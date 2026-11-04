// VR180Adjustments: per-photo picture adjustments (brightness, contrast, saturation, convergence),
// remembered by file name in adjustments.json in the app's own storage, so a photo looks
// the same every time it's shown. Photos with no saved entry use the neutral defaults.
//
// Convergence has a default for all photos (kept in PlayerPrefs). A photo follows that default
// unless it has its own value (ownConvergence), set by unticking "Use for all photos".
//
// The file lives in Application.persistentDataPath (on the Quest:
// Android/data/<package>/files/adjustments.json), so uninstalling the app deletes it.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public static class VR180Adjustments
{
    [Serializable]
    public struct Values
    {
        public float exposure;      // stops (-2..2)
        public float contrast;      // 0.5..1.5, 1 = unchanged
        public float saturation;    // 0..2, 1 = unchanged
        public float convergence;   // degrees between the eyes' images, + = crossed (nearer)
        public bool ownConvergence; // false: this photo follows the default convergence

        /// Fully neutral (convergence 0); used to leave the material asset as it was
        public static Values Default => new Values { exposure = 0f, contrast = 1f, saturation = 1f, convergence = 0f };

        /// What a photo with nothing saved gets: neutral picture, the default convergence
        public static Values ForNewPhoto => new Values { exposure = 0f, contrast = 1f, saturation = 1f, convergence = DefaultConvergence };

        /// Nothing worth saving for this photo
        public bool IsDefault => SameImage(this, Default) && !ownConvergence;

        public static bool Same(Values a, Values b) =>
            SameImage(a, b) && Mathf.Abs(a.convergence - b.convergence) < 0.005f && a.ownConvergence == b.ownConvergence;

        /// The settings that change the picture itself (and so are baked into the layer image)
        public static bool SameImage(Values a, Values b) =>
            Mathf.Abs(a.exposure - b.exposure) < 0.005f &&
            Mathf.Abs(a.contrast - b.contrast) < 0.005f &&
            Mathf.Abs(a.saturation - b.saturation) < 0.005f;
    }

    const string DefaultConvergenceKey = "VR180.defaultConvergence";

    /// Convergence for every photo that doesn't have its own
    public static float DefaultConvergence
    {
        get => PlayerPrefs.GetFloat(DefaultConvergenceKey, 0f);
        set { PlayerPrefs.SetFloat(DefaultConvergenceKey, value); PlayerPrefs.Save(); }
    }

    [Serializable]
    class Entry
    {
        public string file;
        public float exposure, contrast, saturation, convergence;
        public bool hasConvergence;
    }
    [Serializable] class Store { public List<Entry> photos = new List<Entry>(); }

    static Dictionary<string, Values> s_Map;   // photos with saved settings; convergence only if own

    static string FilePath => Path.Combine(Application.persistentDataPath, "adjustments.json");

    // Keyed by file name only, so a photo keeps its settings if it's moved to another folder
    static string Key(string photoPath) => Path.GetFileName(photoPath ?? "").ToLowerInvariant();

    public static Values Get(string photoPath)
    {
        Load();
        if (!s_Map.TryGetValue(Key(photoPath), out var v)) return Values.ForNewPhoto;
        if (!v.ownConvergence) v.convergence = DefaultConvergence;
        return v;
    }

    public static void Set(string photoPath, Values v)
    {
        Load();
        string key = Key(photoPath);
        if (string.IsNullOrEmpty(key)) return;
        if (v.IsDefault) s_Map.Remove(key);
        else s_Map[key] = v;
        Save();
    }

    static void Load()
    {
        if (s_Map != null) return;
        s_Map = new Dictionary<string, Values>();
        try
        {
            if (!File.Exists(FilePath)) return;
            var store = JsonUtility.FromJson<Store>(File.ReadAllText(FilePath));
            if (store?.photos == null) return;
            foreach (var e in store.photos)
            {
                if (string.IsNullOrEmpty(e.file)) continue;
                // Entries saved before the default existed have a convergence but no flag
                bool own = e.hasConvergence || Mathf.Abs(e.convergence) > 0.001f;
                s_Map[e.file] = new Values
                {
                    exposure = e.exposure, contrast = e.contrast, saturation = e.saturation,
                    convergence = own ? e.convergence : 0f, ownConvergence = own
                };
            }
            Debug.Log($"VR180Viewer [adjust]: {s_Map.Count} saved adjustment(s) loaded from {FilePath}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [adjust]: couldn't read {FilePath}: {e.Message}");
        }
    }

    static void Save()
    {
        try
        {
            var store = new Store();
            foreach (var kv in s_Map)
                store.photos.Add(new Entry
                {
                    file = kv.Key, exposure = kv.Value.exposure, contrast = kv.Value.contrast, saturation = kv.Value.saturation,
                    convergence = kv.Value.ownConvergence ? kv.Value.convergence : 0f, hasConvergence = kv.Value.ownConvergence
                });
            store.photos.Sort((a, b) => string.CompareOrdinal(a.file, b.file));

            // Write a temporary file first, so a crash mid-write can't lose the existing settings
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(store, true));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temp, FilePath);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [adjust]: couldn't save {FilePath}: {e.Message}");
        }
    }
}
