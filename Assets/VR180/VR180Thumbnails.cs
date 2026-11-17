// VR180Thumbnails: makes, stores and loads the picker's thumbnails. Created by VR180Viewer.
//
// A thumbnail is a small side-by-side stereo picture (EyeWidth x Height per eye, JPEG): an
// ordinary 2D view straight ahead and level, rendered from the photo by Hidden/VR180/Thumbnail.
// Raw QooCam fisheye photos are converted on the way, as when viewing.
//
// Making one: on the Quest, Android decodes the JPEG at 1/8 size (Plugins/Android/ThumbDecoder.java),
// far quicker than a full decode; on PC, Unity decodes it in full and it's halved on the GPU.
// A photo that has just been shown in the viewer gets its thumbnail from the viewer's copy, at
// almost no cost (FromViewed).
//
// Storage: persistentDataPath/thumbs, one JPEG per photo (named from a hash of its path), and
// index.json recording each photo's size and modified time when its thumbnail was made; a photo
// whose size or time has changed gets a new one. Nothing is written into the photo folders.
// The picker removes the entries of photos that have gone from a folder when it opens it (Prune).
//
// The picker asks for the thumbnails it lacks with SetQueue (nearest the visible part of the
// grid first); they are made one at a time while Running is true, and Ready reports each.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class VR180Thumbnails : MonoBehaviour
{
    public const int EyeWidth = 240, Height = 300;   // per eye; the tiles are 4:5
    const float VerticalFovDeg = 95f;
    const int JpegQuality = 88;
    const string Format = "v1 240x300 95";             // change to remake every thumbnail

    public static VR180Thumbnails Instance { get; private set; }

    /// Set by the viewer: flips thumbnails vertically if they come out upside down
    public static bool FlipY;

    /// True while the picker is open: the queue is only worked through then
    public static bool Running;

    /// A thumbnail has been made (full path of its photo)
    public static event Action<string> Ready;

    [Serializable] class Entry { public string path; public long size; public long time; }
    [Serializable] class IndexFile { public string format; public List<Entry> entries = new List<Entry>(); }

    static string folder;
    static readonly Dictionary<string, Entry> index = new Dictionary<string, Entry>();
    static bool dirty;
    static float saveAt = -1f;

    readonly List<string> queue = new List<string>();
    Material material;
    int made;
    float madeSeconds;

    // ------------------------------------------------------------------ setup

    void Awake()
    {
        Instance = this;
        folder = Path.Combine(Application.persistentDataPath, "thumbs");
        try { Directory.CreateDirectory(folder); }
        catch (Exception e) { Debug.LogWarning($"VR180Viewer [thumbs]: can't create {folder}: {e.Message}"); }
        LoadIndex();
        StartCoroutine(Worker());
    }

    void OnDestroy()
    {
        SaveIndex();
        if (material != null) Destroy(material);
        if (Instance == this) Instance = null;
    }

    void OnApplicationPause(bool paused) { if (paused) SaveIndex(); }

    void Update()
    {
        if (dirty && saveAt >= 0f && Time.unscaledTime >= saveAt) SaveIndex();
    }

    static void LoadIndex()
    {
        index.Clear();
        string file = Path.Combine(folder, "index.json");
        try
        {
            if (File.Exists(file))
            {
                var data = JsonUtility.FromJson<IndexFile>(File.ReadAllText(file));
                if (data != null && data.format == Format)
                {
                    foreach (var e in data.entries) if (e != null && e.path != null) index[Key(e.path)] = e;
                }
                else
                {
                    // Made with other settings: start again
                    foreach (string f in Directory.GetFiles(folder, "*.jpg")) TryDelete(f);
                    Debug.Log("VR180Viewer [thumbs]: thumbnail format changed; they'll be made again");
                }
            }
        }
        catch (Exception e) { Debug.LogWarning($"VR180Viewer [thumbs]: couldn't read the index: {e.Message}"); }
        Debug.Log($"VR180Viewer [thumbs]: {index.Count} thumbnail(s) on record in {folder}");
    }

    static void SaveIndex()
    {
        if (!dirty || folder == null) return;
        dirty = false;
        saveAt = -1f;
        var data = new IndexFile { format = Format, entries = new List<Entry>(index.Values) };
        string file = Path.Combine(folder, "index.json");
        try
        {
            File.WriteAllText(file + ".tmp", JsonUtility.ToJson(data));
            if (File.Exists(file)) File.Delete(file);
            File.Move(file + ".tmp", file);
        }
        catch (Exception e) { Debug.LogWarning($"VR180Viewer [thumbs]: couldn't save the index: {e.Message}"); }
    }

    static void MarkDirty()
    {
        dirty = true;
        if (saveAt < 0f) saveAt = Time.unscaledTime + 3f;
    }

    // ------------------------------------------------------------------ lookups

    static string Key(string path) => Path.GetFullPath(path).ToLowerInvariant();

    static string ThumbFile(string path)
    {
        // FNV-1a, 64 bit, of the normalised path
        ulong h = 14695981039346656037UL;
        foreach (char c in Key(path)) { h ^= c; h *= 1099511628211UL; }
        return Path.Combine(folder, h.ToString("x16") + ".jpg");
    }

    static bool Stamp(string path, out long size, out long time)
    {
        try
        {
            var fi = new FileInfo(path);
            size = fi.Length;
            time = fi.LastWriteTimeUtc.Ticks;
            return fi.Exists;
        }
        catch { size = time = 0; return false; }
    }

    /// True if the photo has a thumbnail made since it last changed
    public static bool Has(string path)
    {
        if (folder == null || !index.TryGetValue(Key(path), out var e)) return false;
        return Stamp(path, out long size, out long time) && e.size == size && e.time == time;
    }

    /// The photo's thumbnail as a texture (the caller destroys it), or null if there isn't a current one
    public static Texture2D Load(string path)
    {
        if (!Has(path)) return null;
        string file = ThumbFile(path);
        try
        {
            if (!File.Exists(file)) { index.Remove(Key(path)); MarkDirty(); return null; }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "VR180 thumbnail" };
            if (!tex.LoadImage(File.ReadAllBytes(file), false)) { Destroy(tex); return null; }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.Apply(true, true);   // mips, then free the CPU copy
            return tex;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [thumbs]: couldn't load the thumbnail of {path}: {e.Message}");
            return null;
        }
    }

    /// Forgets the thumbnails of photos in this folder that are no longer in it
    public static void Prune(string dir, ICollection<string> present)
    {
        if (folder == null) return;
        var keep = new HashSet<string>();
        foreach (string p in present) keep.Add(Key(p));
        string d = Key(dir).TrimEnd('/', '\\');
        var gone = new List<string>();
        foreach (var kv in index)
        {
            string parent = Path.GetDirectoryName(kv.Key)?.TrimEnd('/', '\\');
            if (parent == d && !keep.Contains(kv.Key)) gone.Add(kv.Value.path);
        }
        Forget(gone);
    }

    /// Forgets (and deletes) the thumbnails of these photos
    public static void Forget(IEnumerable<string> paths)
    {
        if (folder == null) return;
        int n = 0;
        foreach (string p in paths)
        {
            if (index.Remove(Key(p))) n++;
            TryDelete(ThumbFile(p));
        }
        if (n > 0)
        {
            MarkDirty();
            Debug.Log($"VR180Viewer [thumbs]: forgot {n} thumbnail(s) of photos that have gone");
        }
    }

    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch { }
    }

    // ------------------------------------------------------------------ making them

    /// The photos to make thumbnails for, most wanted first (replaces the previous list)
    public void SetQueue(List<string> paths)
    {
        queue.Clear();
        queue.AddRange(paths);
    }

    IEnumerator Worker()
    {
        while (true)
        {
            if (!Running || queue.Count == 0) { yield return null; continue; }
            string path = queue[0];
            queue.RemoveAt(0);
            if (Has(path)) continue;
            float t0 = Time.realtimeSinceStartup;
            yield return Make(path);
            made++;
            madeSeconds += Time.realtimeSinceStartup - t0;
            if (made % 10 == 0)
                Debug.Log($"VR180Viewer [thumbs]: {made} made this session, {madeSeconds / made:F2}s each on average");
        }
    }

    IEnumerator Make(string path)
    {
        if (!Stamp(path, out long size, out long time)) yield break;
        var info = VR180Viewer.ReadMetadata(path);
        RenderTexture photo = null;
        float t0 = Time.realtimeSinceStartup;
        double decodeMs = 0;

#if UNITY_ANDROID && !UNITY_EDITOR
        // Android's decoder at 1/8 size, on its own thread
        using (var decoder = new AndroidJavaClass("com.vr180viewer.ThumbDecoder"))
        {
            if (!decoder.CallStatic<bool>("start", path, 8)) yield break;
            int state;
            while ((state = decoder.CallStatic<int>("getState")) == 1) yield return null;
            if (state != 2)
            {
                Debug.LogWarning($"VR180Viewer [thumbs]: couldn't decode {Path.GetFileName(path)}: {decoder.CallStatic<string>("getMessage")}");
                decoder.CallStatic("reset");
                yield break;
            }
            int w = decoder.CallStatic<int>("getWidth"), h = decoder.CallStatic<int>("getHeight");
            decodeMs = decoder.CallStatic<long>("getMillis");
            sbyte[] raw = decoder.CallStatic<sbyte[]>("takePixels");
            if (raw == null || raw.Length < w * h * 4) yield break;
            var bytes = new byte[w * h * 4];
            Buffer.BlockCopy(raw, 0, bytes, 0, bytes.Length);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(bytes);
            tex.Apply(false, true);
            // Android's rows run top to bottom, Unity's bottom to top: turn it the right way up
            var upright = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, upright, new Vector2(1f, -1f), new Vector2(0f, 1f));
            Destroy(tex);
            photo = Prepare(upright, 0, info);
            RenderTexture.ReleaseTemporary(upright);
        }
#else
        // Unity's decoder (full size), then halved three times on the GPU
        using (var req = UnityWebRequestTexture.GetTexture(new Uri(path).AbsoluteUri, true))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"VR180Viewer [thumbs]: couldn't load {path}: {req.error}");
                yield break;
            }
            decodeMs = (Time.realtimeSinceStartup - t0) * 1000.0;
            Texture2D src = DownloadHandlerTexture.GetContent(req);
            photo = Prepare(src, 3, info);
            Destroy(src);
        }
#endif
        if (photo == null) yield break;
        bool ok = Render(path, photo, info.coverage, info.sideBySide, info.tilt, size, time);
        photo.Release();
        Destroy(photo);
        if (ok)
            Debug.Log($"VR180Viewer [thumbs]: {Path.GetFileName(path)} in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms (decode {decodeMs:F0})");
    }

    // The decoded photo, halved and (raw fisheye photos) converted, with mips
    static RenderTexture Prepare(Texture src, int halvings, VR180Viewer.PhotoInfo info)
    {
        bool fisheye = info.fisheye;
        if (fisheye && !VR180Fisheye.PlausibleSize(src.width, src.height)) fisheye = false;
        return VR180Viewer.MakeMipmapped(src, halvings, fisheye, info.raw);
    }

    /// Called by the viewer once a photo is showing: makes its thumbnail from the viewer's own copy
    /// (equirectangular, already converted, with mips) if it hasn't one yet. Cheap.
    public static void FromViewed(string path, Texture equirect, Vector4 coverage, bool sideBySide, Quaternion tilt)
    {
        if (Instance == null || equirect == null || Has(path)) return;
        if (!Stamp(path, out long size, out long time)) return;
        if (Instance.Render(path, equirect, coverage, sideBySide, tilt, size, time))
            Debug.Log($"VR180Viewer [thumbs]: {Path.GetFileName(path)} from the viewer's copy");
    }

    bool Render(string path, Texture photo, Vector4 coverage, bool sideBySide, Quaternion tilt, long size, long time)
    {
        if (material == null)
        {
            var shader = Resources.Load<Shader>("VR180Thumbnail");
            if (shader == null) { Debug.LogError("VR180Viewer [thumbs]: Resources/VR180Thumbnail.shader is missing"); return false; }
            material = new Material(shader);
        }
        if (tilt.x == 0f && tilt.y == 0f && tilt.z == 0f && tilt.w == 0f) tilt = Quaternion.identity;

        int outW = EyeWidth * 2;
        float tanV = Mathf.Tan(VerticalFovDeg * 0.5f * Mathf.Deg2Rad);
        float tanH = tanV * EyeWidth / Height;
        material.SetVector("_Coverage", coverage);
        material.SetFloat("_Stereo", sideBySide ? 1f : 0f);
        material.SetVector("_View", new Vector4(tanH, tanV, (coverage.x + coverage.y) * 0.5f, outW));
        material.SetMatrix("_LevelToImage", Matrix4x4.Rotate(Quaternion.Inverse(tilt)));
        material.SetFloat("_FlipY", FlipY ? 1f : 0f);

        var rt = RenderTexture.GetTemporary(outW, Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(photo, rt, material);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(outW, Height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, outW, Height), 0, 0);
        tex.Apply(false, false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        byte[] jpg = tex.EncodeToJPG(JpegQuality);
        Destroy(tex);

        try { File.WriteAllBytes(ThumbFile(path), jpg); }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [thumbs]: couldn't save the thumbnail of {path}: {e.Message}");
            return false;
        }
        index[Key(path)] = new Entry { path = Path.GetFullPath(path), size = size, time = time };
        MarkDirty();
        Ready?.Invoke(path);
        return true;
    }
}
