// CameraImport: copies photos from a camera plugged into the Quest's USB-C port (MTP), for
// VR180Viewer. Called from C# (VR180CameraImport.cs) through AndroidJavaClass; the work runs on
// background threads and C# polls the state.
//
// Flow: findCamera -> (requestPermission, then poll hasPermission) -> startScan -> SCANNED
// (counts available) -> startImport(dest, max) -> FINISHED or FAILED (see getMessage).
// Logcat tag: VR180Import.

package com.vr180viewer;

import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.hardware.usb.UsbConstants;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbDeviceConnection;
import android.hardware.usb.UsbInterface;
import android.hardware.usb.UsbManager;
import android.media.MediaScannerConnection;
import android.mtp.MtpConstants;
import android.mtp.MtpDevice;
import android.mtp.MtpDeviceInfo;
import android.mtp.MtpObjectInfo;
import android.os.Build;
import android.util.Log;

import java.io.File;
import java.io.FileOutputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;

public class CameraImport {
    static final String TAG = "VR180Import";
    static final String ACTION_PERMISSION = "com.vr180viewer.USB_PERMISSION";
    static final int FLAG_MUTABLE = 0x02000000;   // PendingIntent.FLAG_MUTABLE (API 31)
    static final int ROOT = 0xFFFFFFFF;            // MTP parent handle for a storage's top level
    static final int MAX_DEPTH = 8;

    public static final int IDLE = 0, SCANNING = 1, SCANNED = 2, IMPORTING = 3, FINISHED = 4, FAILED = 5;

    static volatile int state = IDLE;
    static volatile String message = "";
    static volatile int photoCount, newCount, done, total;
    static volatile long photoBytes, newBytes;

    static class Item { int handle; String name; long size; long modified; }
    static final List<Item> fresh = new ArrayList<>();   // photos not yet in the destination, newest first

    // ------------------------------------------------------------------ getters for C#

    public static int getState() { return state; }
    public static String getMessage() { return message; }
    public static int getPhotoCount() { return photoCount; }
    public static long getPhotoBytes() { return photoBytes; }
    public static int getNewCount() { return newCount; }
    public static long getNewBytes() { return newBytes; }
    public static int getDone() { return done; }
    public static int getTotal() { return total; }

    /** Back to IDLE after a finished, failed or abandoned scan (not while busy). */
    public static void reset() {
        if (state != SCANNING && state != IMPORTING) { state = IDLE; message = ""; }
    }

    // ------------------------------------------------------------------ finding the camera

    static UsbManager usb(Context ctx) { return (UsbManager) ctx.getSystemService(Context.USB_SERVICE); }

    static UsbDevice findDevice(Context ctx) {
        UsbManager um = usb(ctx);
        if (um == null) return null;
        for (UsbDevice d : um.getDeviceList().values())
            if (isCamera(d)) return d;
        return null;
    }

    // An MTP or PTP interface: Android-style MTP (vendor class 255/255/0, often named "MTP")
    // as on the QooCam, or the standard still-image class 6/1/1
    static boolean isCamera(UsbDevice d) {
        for (int i = 0; i < d.getInterfaceCount(); i++) {
            UsbInterface ui = d.getInterface(i);
            int c = ui.getInterfaceClass(), s = ui.getInterfaceSubclass(), p = ui.getInterfaceProtocol();
            if (c == UsbConstants.USB_CLASS_STILL_IMAGE && s == 1 && p == 1) return true;
            if ("MTP".equalsIgnoreCase(ui.getName())) return true;
            if (c == UsbConstants.USB_CLASS_VENDOR_SPEC && s == 255 && p == 0) return true;
        }
        return false;
    }

    /** A description of the connected camera, or null if there isn't one. */
    public static String findCamera(Context ctx) {
        try {
            UsbDevice d = findDevice(ctx);
            if (d == null) return null;
            String m = d.getManufacturerName(), p = d.getProductName();
            String name = ((m != null ? m + " " : "") + (p != null ? p : "")).trim();
            return name.isEmpty() ? "USB camera" : name;
        } catch (Throwable e) {
            Log.w(TAG, "findCamera: " + e);
            return null;
        }
    }

    public static boolean hasPermission(Context ctx) {
        try {
            UsbDevice d = findDevice(ctx);
            return d != null && usb(ctx).hasPermission(d);
        } catch (Throwable e) {
            Log.w(TAG, "hasPermission: " + e);
            return false;
        }
    }

    /** Shows Android's "allow access to the USB device" dialog; C# polls hasPermission afterwards. */
    public static void requestPermission(Context ctx) {
        try {
            UsbDevice d = findDevice(ctx);
            if (d == null) return;
            Intent intent = new Intent(ACTION_PERMISSION);
            intent.setPackage(ctx.getPackageName());   // Android 14 refuses mutable implicit intents
            int flags = Build.VERSION.SDK_INT >= 31 ? FLAG_MUTABLE : 0;
            PendingIntent pi = PendingIntent.getBroadcast(ctx, 0, intent, flags);
            usb(ctx).requestPermission(d, pi);
            Log.i(TAG, "requested USB permission for " + d.getDeviceName());
        } catch (Throwable e) {
            Log.e(TAG, "requestPermission failed", e);
        }
    }

    static MtpDevice open(Context ctx) throws Exception {
        UsbManager um = usb(ctx);
        UsbDevice d = findDevice(ctx);
        if (d == null) throw new Exception("The camera isn't connected.");
        if (!um.hasPermission(d)) throw new Exception("No permission to use the camera over USB.");
        UsbDeviceConnection c = um.openDevice(d);
        if (c == null) throw new Exception("Couldn't open the USB device.");
        MtpDevice mtp = new MtpDevice(d);
        if (!mtp.open(c)) {
            c.close();
            throw new Exception("Couldn't start an MTP session with the camera (another app may be using it).");
        }
        MtpDeviceInfo info = mtp.getDeviceInfo();
        if (info != null)
            Log.i(TAG, "MTP session open: " + info.getManufacturer() + " " + info.getModel() + " version " + info.getVersion());
        return mtp;
    }

    // ------------------------------------------------------------------ scanning

    public static void startScan(final Context ctx, final String destDir) {
        if (state == SCANNING || state == IMPORTING) return;
        state = SCANNING;
        message = "Looking at the camera's photos...";
        new Thread(() -> {
            MtpDevice mtp = null;
            long t0 = System.currentTimeMillis();
            try {
                mtp = open(ctx);
                List<Item> all = new ArrayList<>();
                int[] storages = mtp.getStorageIds();
                if (storages == null || storages.length == 0) throw new Exception("The camera reported no storage (is it switched on and unlocked?).");
                for (int sid : storages) walk(mtp, sid, ROOT, 0, all);

                Collections.sort(all, (a, b) -> Long.compare(b.modified, a.modified));
                File dir = new File(destDir);
                long bytes = 0, nbytes = 0;
                synchronized (fresh) {
                    fresh.clear();
                    for (Item it : all) {
                        bytes += Math.max(0, it.size);
                        File target = new File(dir, it.name);
                        if (target.exists() && (it.size <= 0 || target.length() == it.size)) continue;
                        fresh.add(it);
                        nbytes += Math.max(0, it.size);
                    }
                    newCount = fresh.size();
                }
                photoCount = all.size();
                photoBytes = bytes;
                newBytes = nbytes;
                message = "";
                Log.i(TAG, "scan: " + photoCount + " JPEGs on the camera, " + newCount + " new, in " + (System.currentTimeMillis() - t0) + " ms");
                state = SCANNED;
            } catch (Throwable e) {
                Log.e(TAG, "scan failed", e);
                message = e.getMessage() != null ? e.getMessage() : e.toString();
                state = FAILED;
            } finally {
                if (mtp != null) mtp.close();
            }
        }).start();
    }

    static void walk(MtpDevice mtp, int storage, int parent, int depth, List<Item> out) {
        int[] handles = mtp.getObjectHandles(storage, 0, parent);
        if (handles == null) return;
        for (int h : handles) {
            MtpObjectInfo info = mtp.getObjectInfo(h);
            if (info == null) continue;
            String name = info.getName();
            if (info.getFormat() == MtpConstants.FORMAT_ASSOCIATION) {
                if (depth < MAX_DEPTH) walk(mtp, storage, h, depth + 1, out);
                continue;
            }
            if (name == null) continue;
            String lower = name.toLowerCase(Locale.ROOT);
            if (!lower.endsWith(".jpg") && !lower.endsWith(".jpeg")) continue;
            Item it = new Item();
            it.handle = h;
            it.name = name;
            it.size = info.getCompressedSizeLong();
            it.modified = info.getDateModified();
            out.add(it);
        }
    }

    // ------------------------------------------------------------------ importing

    /** Copies up to max of the new photos (newest first) into destDir. */
    public static void startImport(final Context ctx, final String destDir, final int max) {
        if (state != SCANNED) return;
        final List<Item> list;
        synchronized (fresh) { list = new ArrayList<>(fresh.subList(0, Math.min(Math.max(max, 0), fresh.size()))); }
        state = IMPORTING;
        done = 0;
        total = list.size();
        message = "";
        new Thread(() -> {
            MtpDevice mtp = null;
            List<String> paths = new ArrayList<>();
            int failed = 0;
            long bytes = 0, t0 = System.currentTimeMillis();
            try {
                mtp = open(ctx);
                File dir = new File(destDir);
                if (!dir.isDirectory() && !dir.mkdirs()) throw new Exception("Couldn't create " + destDir);
                for (Item it : list) {
                    File target = new File(dir, it.name);
                    File temp = new File(dir, it.name + ".importing");
                    try {
                        temp.delete();
                        long t1 = System.currentTimeMillis();
                        boolean ok = mtp.importFile(it.handle, temp.getPath());
                        if (!ok && it.size > 0 && it.size < Integer.MAX_VALUE) {
                            Log.w(TAG, "importFile failed for " + it.name + "; trying getObject");
                            byte[] data = mtp.getObject(it.handle, (int) it.size);
                            if (data != null) {
                                try (FileOutputStream o = new FileOutputStream(temp)) { o.write(data); }
                                ok = true;
                            }
                        }
                        if (ok && it.size > 0 && temp.length() != it.size) {
                            Log.w(TAG, it.name + ": got " + temp.length() + " bytes, expected " + it.size);
                            ok = false;
                        }
                        if (ok) {
                            target.delete();   // an incomplete earlier copy
                            ok = temp.renameTo(target);
                        }
                        if (ok) {
                            if (it.modified > 0) target.setLastModified(it.modified);
                            paths.add(target.getPath());
                            bytes += target.length();
                            Log.i(TAG, "imported " + it.name + " (" + target.length() / 1024 + " KB) in " + (System.currentTimeMillis() - t1) + " ms");
                        } else {
                            failed++;
                            temp.delete();
                        }
                    } catch (Throwable e) {
                        failed++;
                        temp.delete();
                        Log.e(TAG, "importing " + it.name + " failed", e);
                    }
                    done++;
                }
                if (!paths.isEmpty())
                    MediaScannerConnection.scanFile(ctx, paths.toArray(new String[0]), null, null);
                double secs = Math.max(0.001, (System.currentTimeMillis() - t0) / 1000.0);
                double mb = bytes / 1048576.0;
                message = String.format(Locale.UK, "Imported %d photo%s (%.0f MB) in %.1f s, %.1f MB/s%s.",
                        paths.size(), paths.size() == 1 ? "" : "s", mb, secs, mb / secs,
                        failed > 0 ? "; " + failed + " failed (see logcat, tag VR180Import)" : "");
                Log.i(TAG, message);
                state = FINISHED;
            } catch (Throwable e) {
                Log.e(TAG, "import failed", e);
                message = (e.getMessage() != null ? e.getMessage() : e.toString()) +
                        (paths.isEmpty() ? "" : " (" + paths.size() + " photos were imported first)");
                state = FAILED;
            } finally {
                if (mtp != null) mtp.close();
            }
        }).start();
    }
}
