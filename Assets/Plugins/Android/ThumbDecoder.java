// ThumbDecoder: decodes a JPEG at a reduced size for VR180Viewer's picker thumbnails.
// Android's decoder (libjpeg-turbo) can scale by 1/2, 1/4 or 1/8 while decoding (inSampleSize),
// which skips most of the work: a 13,888 x 6,944 photo comes out at 1,736 x 868 in a fraction
// of the time of a full decode, using about 6 MB.
//
// Called from C# (VR180Thumbnails.cs) through AndroidJavaClass. One decode at a time, on a
// background thread; C# polls getState() and then takes the pixels with takePixels().
// Logcat tag: VR180Thumbs.

package com.vr180viewer;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.util.Log;

import java.nio.ByteBuffer;

public class ThumbDecoder {
    static final String TAG = "VR180Thumbs";
    public static final int IDLE = 0, BUSY = 1, DONE = 2, FAILED = 3;

    static volatile int state = IDLE;
    static volatile int width, height;
    static volatile long millis;
    static volatile String message = "";
    static byte[] pixels;   // RGBA, top row first

    public static int getState() { return state; }
    public static int getWidth() { return width; }
    public static int getHeight() { return height; }
    public static long getMillis() { return millis; }
    public static String getMessage() { return message; }

    /** The decoded pixels (RGBA, top row first); the decoder is idle again afterwards. */
    public static byte[] takePixels() {
        byte[] p = pixels;
        pixels = null;
        state = IDLE;
        return p;
    }

    /** Forgets a finished or failed decode (not while busy). */
    public static void reset() {
        if (state != BUSY) { pixels = null; state = IDLE; }
    }

    /** Starts decoding path at 1/sample size (sample 1, 2, 4 or 8). False if already busy. */
    public static boolean start(final String path, final int sample) {
        if (state == BUSY) return false;
        state = BUSY;
        pixels = null;
        message = "";
        new Thread(new Runnable() {
            public void run() { decode(path, sample); }
        }, "VR180ThumbDecode").start();
        return true;
    }

    static void decode(String path, int sample) {
        long t0 = System.currentTimeMillis();
        Bitmap bmp = null;
        try {
            BitmapFactory.Options o = new BitmapFactory.Options();
            o.inSampleSize = sample;
            o.inPreferredConfig = Bitmap.Config.ARGB_8888;
            o.inScaled = false;
            bmp = BitmapFactory.decodeFile(path, o);
            if (bmp == null) {
                message = "couldn't decode " + path;
                Log.w(TAG, message);
                state = FAILED;
                return;
            }
            if (bmp.getConfig() != Bitmap.Config.ARGB_8888) {
                Bitmap c = bmp.copy(Bitmap.Config.ARGB_8888, false);
                bmp.recycle();
                bmp = c;
            }
            int w = bmp.getWidth(), h = bmp.getHeight();
            ByteBuffer buf = ByteBuffer.allocate(w * h * 4);
            bmp.copyPixelsToBuffer(buf);   // ARGB_8888 is stored as R, G, B, A bytes
            pixels = buf.array();
            width = w;
            height = h;
            millis = System.currentTimeMillis() - t0;
            state = DONE;
        } catch (Throwable e) {
            message = e.toString();
            Log.w(TAG, "decoding " + path + ": " + message);
            state = FAILED;
        } finally {
            if (bmp != null) bmp.recycle();
        }
    }
}
