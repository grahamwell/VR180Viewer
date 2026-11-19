// StripDecoder: decodes a full-size JPEG on its own thread for VR180Viewer and hands it over in strips,
// so that the GPU upload can be spread over many frames instead of one long frame (Unity's own decode
// uploads the whole 13,888 x 6,944 photo, about 385 MB, in the frame the texture is collected: about
// 200 ms on the Quest 3, felt as a judder while the next photo is being prepared).
//
// How it works: BitmapFactory decodes the whole photo (Android keeps the pixels in native memory, not
// the Java heap). The thread then copies it, a strip of rows at a time, into a small ring of direct
// ByteBuffers that C# reads in place (AndroidJNI.GetDirectByteBuffer: no copy through managed arrays).
// Each strip is turned upside down on the way (Unity textures have their bottom row first), so C# can
// upload it and copy it straight into place. The bitmap is freed as soon as the last strip is copied.
//
// Called from C# (VR180StripDecoder.cs) through AndroidJavaClass:
//   start(path, stripRows, slots, upsideDown)   false if a previous decode is still running
//   getState()                      DECODING, STRIPS, FINISHED, FAILED (IDLE before the first start)
//   getWidth(), getHeight(), getStripCount(), getStripRows(), getSlots(), getDecodeMillis(), getMessage()
//   getBuffer(slot)                 the direct ByteBuffer for that slot (valid once STRIPS)
//   nextStrip()                     the next strip index, in order, if it's ready in slot index % slots; else -1
//   release(index)                  C# has taken that strip; its slot can be refilled
//   cancel()                        stop; the thread frees everything and ends
// Logcat tag: VR180Strips.

package com.vr180viewer;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Matrix;
import android.util.Log;

import java.nio.ByteBuffer;

public class StripDecoder {
    static final String TAG = "VR180Strips";
    public static final int IDLE = 0, DECODING = 1, STRIPS = 2, FINISHED = 3, FAILED = 4;

    static final Object lock = new Object();
    static Thread worker;
    static volatile int state = IDLE;
    static volatile boolean cancelled;
    static volatile boolean turnOver = true;   // each strip upside down (Unity's bottom-row-first order)
    static volatile int width, height, stripRows, stripCount, slots;
    static volatile long decodeMillis;
    static volatile String message = "";
    static ByteBuffer[] buffers = new ByteBuffer[0];
    static int[] slotStrip = new int[0];   // which strip each slot holds (-1 = free); guarded by lock
    static int nextToHand;                  // guarded by lock

    public static int getState() { return state; }
    public static int getWidth() { return width; }
    public static int getHeight() { return height; }
    public static int getStripRows() { return stripRows; }
    public static int getStripCount() { return stripCount; }
    public static int getSlots() { return slots; }
    public static long getDecodeMillis() { return decodeMillis; }
    public static String getMessage() { return message; }

    public static ByteBuffer getBuffer(int slot) {
        synchronized (lock) {
            return slot >= 0 && slot < buffers.length ? buffers[slot] : null;
        }
    }

    /** Starts decoding path; false if the previous decode's thread hasn't ended yet. */
    public static boolean start(final String path, final int rows, final int slotCount, final boolean upsideDown) {
        synchronized (lock) {
            if (worker != null && worker.isAlive()) return false;
            cancelled = false;
            turnOver = upsideDown;
            state = DECODING;
            message = "";
            stripRows = Math.max(8, rows);
            slots = Math.max(2, slotCount);
            nextToHand = 0;
            worker = new Thread(new Runnable() {
                public void run() { work(path); }
            }, "VR180StripDecode");
            worker.start();
            return true;
        }
    }

    public static void cancel() {
        synchronized (lock) {
            cancelled = true;
            lock.notifyAll();
        }
    }

    public static int nextStrip() {
        synchronized (lock) {
            if (state != STRIPS && state != FINISHED) return -1;
            if (nextToHand >= stripCount) return -1;
            int slot = nextToHand % slots;
            if (slotStrip[slot] != nextToHand) return -1;
            return nextToHand++;
        }
    }

    public static void release(int index) {
        synchronized (lock) {
            if (slots > 0 && index >= 0 && slotStrip[index % slots] == index) slotStrip[index % slots] = -1;
            lock.notifyAll();
        }
    }

    static void work(String path) {
        long t0 = System.currentTimeMillis();
        Bitmap bmp = null;
        try {
            BitmapFactory.Options o = new BitmapFactory.Options();
            o.inPreferredConfig = Bitmap.Config.ARGB_8888;
            o.inScaled = false;
            bmp = BitmapFactory.decodeFile(path, o);
            if (bmp == null) { fail("couldn't decode " + path); return; }
            if (bmp.getConfig() != Bitmap.Config.ARGB_8888) {
                Bitmap c = bmp.copy(Bitmap.Config.ARGB_8888, false);
                bmp.recycle();
                bmp = c;
                if (bmp == null) { fail("couldn't convert " + path + " to ARGB_8888"); return; }
            }
            if (cancelled) { state = IDLE; return; }
            int w = bmp.getWidth(), h = bmp.getHeight();
            decodeMillis = System.currentTimeMillis() - t0;

            int rows = stripRows;
            int bytes = w * rows * 4;
            synchronized (lock) {
                // The buffers are kept between photos (C# fetches them afresh for each one)
                if (buffers.length != slots || buffers[0].capacity() != bytes) {
                    buffers = new ByteBuffer[slots];
                    for (int i = 0; i < slots; i++) buffers[i] = ByteBuffer.allocateDirect(bytes);
                }
                slotStrip = new int[slots];
                for (int i = 0; i < slots; i++) slotStrip[i] = -1;
                width = w;
                height = h;
                stripCount = (h + rows - 1) / rows;
                state = STRIPS;
            }

            Matrix flip = new Matrix();
            flip.setScale(1f, -1f);
            for (int s = 0; s < stripCount; s++) {
                int slot = s % slots;
                long waitStart = System.currentTimeMillis();
                synchronized (lock) {
                    while (slotStrip[slot] != -1 && !cancelled) {
                        lock.wait(100);
                        // Nobody collecting (C# gone away without cancelling): give up rather than hold the photo
                        if (System.currentTimeMillis() - waitStart > 20000) {
                            Log.w(TAG, "no strip collected for 20 s; giving up");
                            cancelled = true;
                        }
                    }
                }
                if (cancelled) break;
                int y0 = s * rows;
                int n = Math.min(rows, h - y0);
                // Rows y0 .. y0+n-1, upside down, so the buffer starts with the strip's bottom row
                Bitmap part = turnOver ? Bitmap.createBitmap(bmp, 0, y0, w, n, flip, false)
                                       : Bitmap.createBitmap(bmp, 0, y0, w, n);
                ByteBuffer b = buffers[slot];
                b.clear();
                part.copyPixelsToBuffer(b);   // ARGB_8888 is stored as R, G, B, A bytes
                part.recycle();
                synchronized (lock) { slotStrip[slot] = s; }
                if (s == stripCount - 1) { bmp.recycle(); bmp = null; }   // the photo's memory back at once
            }
            if (cancelled) { state = IDLE; return; }
            state = FINISHED;
            Log.i(TAG, "decoded " + w + "x" + h + " in " + decodeMillis + " ms, " + stripCount + " strips of " + rows +
                       " rows copied out by " + (System.currentTimeMillis() - t0) + " ms");
        } catch (Throwable e) {
            fail(e.toString());
        } finally {
            if (bmp != null) bmp.recycle();
        }
    }

    static void fail(String why) {
        message = why;
        Log.w(TAG, why);
        state = FAILED;
    }
}
