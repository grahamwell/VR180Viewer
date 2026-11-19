// VR180StripDecoder: on the Quest, decodes a photo with Android's decoder on its own thread
// (Plugins/Android/StripDecoder.java) and uploads it to the GPU a strip at a time, a frame or more
// apart, into a RenderTexture that never has a CPU copy. Unity's own decode (UnityWebRequestTexture)
// uploads the whole photo in the frame its texture is collected: about 200 ms for a 13,888 x 6,944
// photo, a judder while the next photo is prepared in the background.
//
// The strips arrive already upside down (Unity's bottom-row-first order) in direct ByteBuffers that
// C# reads in place (AndroidJNI.GetDirectByteBuffer). Each is copied into a small strip texture
// (SetPixelData + Apply: one upload of about 7 MB) and then, on the GPU, into its place in the photo
// (Graphics.CopyTexture). The result is sRGB without mips, like the textures DecodeRequest gives.
//
// Used by VR180Viewer.Prefetch only (the cold path's hitch falls during the blur and loading dots).
// Any failure (no plugin, a decode Android refuses, a decoder still busy) is reported in Result.error
// and the caller falls back to Unity's decode.

using System;
using System.Collections;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public static class VR180StripDecoder
{
    const int IDLE = 0, DECODING = 1, STRIPS = 2, FINISHED = 3, FAILED = 4;
    const int Slots = 3;

    public class Result
    {
        public RenderTexture texture;   // the caller owns it
        public string error;            // set on failure (texture null)
        public bool cancelled;
        public float decodeSeconds;     // Android's decode, on its thread
        public float uploadSeconds;     // from the first strip to the last
        public int strips, frames;
        public float worstMs;           // the most time any one frame spent on strips
        public float totalMs;           // all frames' time on strips
    }

    static AndroidJavaClass cls;
    static bool unavailable;

    public static bool Available
    {
        get
        {
            if (Application.platform != RuntimePlatform.Android || unavailable) return false;
            if (cls != null) return true;
            try { cls = new AndroidJavaClass("com.vr180viewer.StripDecoder"); return true; }
            catch (Exception e)
            {
                unavailable = true;
                Debug.LogWarning($"VR180Viewer [strips]: the strip decoder isn't available ({e.Message}); using Unity's decode");
                return false;
            }
        }
    }

    // stripRows: rows per strip (13,888 wide: 128 rows is about 7 MB); perFrame(): how many strips this
    // frame may upload (read every frame, so the caller can hurry or pause it: 0 = none this frame);
    // cancel(): stop and free everything
    // flip: the whole photo the other way up (in case the Quest's texture copies turn out to store rows the
    // other way round from what is assumed here: Unity's usual bottom row first)
    public static IEnumerator Load(string path, int stripRows, bool flip, Func<int> perFrame, Func<bool> cancel, Result result)
    {
        if (!Available) { result.error = "not available"; yield break; }
        bool started;
        try { started = cls.CallStatic<bool>("start", path, stripRows, Slots, !flip); }
        catch (Exception e) { result.error = e.Message; yield break; }
        if (!started) { result.error = "the previous strip decode is still finishing"; yield break; }

        float t0 = Time.realtimeSinceStartup;
        int state;
        while ((state = cls.CallStatic<int>("getState")) == DECODING)
        {
            if (cancel()) { cls.CallStatic("cancel"); result.cancelled = true; yield break; }
            yield return null;
        }
        if (state == FAILED || state == IDLE)
        {
            result.error = state == FAILED ? cls.CallStatic<string>("getMessage") : "the decode stopped";
            yield break;
        }
        result.decodeSeconds = cls.CallStatic<long>("getDecodeMillis") / 1000f;

        int w = cls.CallStatic<int>("getWidth"), h = cls.CallStatic<int>("getHeight");
        int rows = cls.CallStatic<int>("getStripRows"), count = cls.CallStatic<int>("getStripCount");
        int slots = cls.CallStatic<int>("getSlots");
        if (w > SystemInfo.maxTextureSize || h > SystemInfo.maxTextureSize)
        {
            cls.CallStatic("cancel");
            result.error = $"{w}x{h} is larger than this device's textures can be";
            yield break;
        }

        var buffers = new AndroidJavaObject[slots];
        var views = new NativeArray<byte>[slots];
        RenderTexture rt = null;
        Texture2D strip = null;
        bool ok = false;
        try
        {
            for (int i = 0; i < slots; i++)
            {
                buffers[i] = cls.CallStatic<AndroidJavaObject>("getBuffer", i);
                views[i] = AndroidJNI.GetDirectByteBuffer(buffers[i].GetRawObject());
                if (views[i].Length < w * rows * 4) throw new Exception($"strip buffer {i} is too small ({views[i].Length} bytes)");
            }
            rt = new RenderTexture(w, h, 0, GraphicsFormat.R8G8B8A8_SRGB)
            {
                name = "VR180 photo (uploaded in strips)",
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
            };
            rt.Create();
            strip = new Texture2D(w, rows, TextureFormat.RGBA32, false, false) { name = "VR180 photo strip" };
            ok = true;
        }
        catch (Exception e)
        {
            result.error = e.Message;
        }
        if (!ok)
        {
            cls.CallStatic("cancel");
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            if (strip != null) UnityEngine.Object.Destroy(strip);
            foreach (var b in buffers) b?.Dispose();
            yield break;
        }

        float tFirst = -1f;
        int done = 0;
        while (done < count)
        {
            if (cancel())
            {
                cls.CallStatic("cancel");
                result.cancelled = true;
                break;
            }
            float tFrame = Time.realtimeSinceStartup;
            int allowed = perFrame(), took = 0;
            if (allowed <= 0) { yield return null; continue; }   // (paused this frame)
            while (took < allowed && done < count)
            {
                int s = cls.CallStatic<int>("nextStrip");
                if (s < 0) break;
                if (tFirst < 0f) tFirst = tFrame;
                int y0 = s * rows;                 // from the photo's top
                int n = Mathf.Min(rows, h - y0);
                strip.SetPixelData(views[s % slots], 0);
                cls.CallStatic("release", s);      // its bytes are Unity's now
                strip.Apply(false, false);
                Graphics.CopyTexture(strip, 0, 0, 0, 0, w, n, rt, 0, 0, 0, flip ? y0 : h - y0 - n);
                took++;
                done++;
            }
            if (took > 0)
            {
                float ms = (Time.realtimeSinceStartup - tFrame) * 1000f;
                result.worstMs = Mathf.Max(result.worstMs, ms);
                result.totalMs += ms;
                result.frames++;
            }
            else if (cls.CallStatic<int>("getState") == FAILED)
            {
                result.error = cls.CallStatic<string>("getMessage");
                break;
            }
            if (done < count) yield return null;
        }

        UnityEngine.Object.Destroy(strip);
        foreach (var b in buffers) b?.Dispose();   // (the buffers themselves stay with Java for the next photo)
        if (done < count)
        {
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            yield break;
        }
        result.strips = count;
        result.uploadSeconds = Time.realtimeSinceStartup - (tFirst < 0f ? t0 : tFirst);
        result.texture = rt;
    }
}
