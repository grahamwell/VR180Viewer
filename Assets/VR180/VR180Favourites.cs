// VR180Favourites: the list of favourite photos, kept in favourites.json in the app's own
// storage (persistentDataPath), keyed by file name so a favourite survives being moved to
// another folder. The last known full path is stored too, for the Favourites slideshow;
// if a file isn't there any more, it's looked for by name under the picker's root.
//
// Nothing here touches the photos themselves. (Uninstalling the app deletes the list.)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class VR180Favourites
{
    [Serializable] class Entry { public string file; public string path; }
    [Serializable] class Store { public List<Entry> photos = new List<Entry>(); }

    static Dictionary<string, string> s_Map;   // file name (lower case) -> last known full path
    const int MaxSearchDepth = 8;

    static string FilePath => Path.Combine(Application.persistentDataPath, "favourites.json");
    static string Key(string photoPath) => Path.GetFileName(photoPath ?? "").ToLowerInvariant();

    public static int Count { get { Load(); return s_Map.Count; } }

    public static bool IsFavourite(string photoPath)
    {
        Load();
        return s_Map.ContainsKey(Key(photoPath));
    }

    public static void Set(string photoPath, bool on)
    {
        Load();
        string key = Key(photoPath);
        if (string.IsNullOrEmpty(key)) return;
        if (on) s_Map[key] = Path.GetFullPath(photoPath);
        else if (!s_Map.Remove(key)) return;
        Save();
    }

    /// Unmarks several photos at once (one save)
    public static void Clear(IEnumerable<string> photoPaths)
    {
        Load();
        bool any = false;
        foreach (string p in photoPaths) any |= s_Map.Remove(Key(p));
        if (any) Save();
    }

    /// Returns true if the photo is now a favourite
    public static bool Toggle(string photoPath)
    {
        bool on = !IsFavourite(photoPath);
        Set(photoPath, on);
        return on;
    }

    /// Full paths of the favourites that can be found, sorted by file name. Ones that have moved
    /// are found by name under root, and their stored paths updated.
    public static List<string> Resolve(string root)
    {
        Load();
        var missing = new HashSet<string>(s_Map.Where(kv => !File.Exists(kv.Value)).Select(kv => kv.Key));
        if (missing.Count > 0 && Directory.Exists(root))
        {
            int before = missing.Count;
            Search(root, missing, 0);
            if (missing.Count < before) Save();
            if (missing.Count > 0)
                Debug.LogWarning($"VR180Viewer [favourites]: {missing.Count} favourite(s) not found under {root}: {string.Join(", ", missing.Take(5))}");
        }
        return s_Map.Values.Where(File.Exists)
                    .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    static void Search(string dir, HashSet<string> missing, int depth)
    {
        if (missing.Count == 0 || depth > MaxSearchDepth) return;
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                string key = Key(f);
                if (missing.Remove(key)) s_Map[key] = Path.GetFullPath(f);
            }
            foreach (string d in Directory.EnumerateDirectories(dir))
                if (!Path.GetFileName(d).StartsWith(".")) Search(d, missing, depth + 1);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [favourites]: can't search {dir}: {e.Message}");
        }
    }

    static void Load()
    {
        if (s_Map != null) return;
        s_Map = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(FilePath)) return;
            var store = JsonUtility.FromJson<Store>(File.ReadAllText(FilePath));
            if (store?.photos == null) return;
            foreach (var e in store.photos)
                if (!string.IsNullOrEmpty(e.file)) s_Map[e.file] = e.path ?? "";
            Debug.Log($"VR180Viewer [favourites]: {s_Map.Count} favourite(s) loaded from {FilePath}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [favourites]: couldn't read {FilePath}: {e.Message}");
        }
    }

    static void Save()
    {
        try
        {
            var store = new Store();
            foreach (var kv in s_Map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                store.photos.Add(new Entry { file = kv.Key, path = kv.Value });
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(store, true));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temp, FilePath);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [favourites]: couldn't save {FilePath}: {e.Message}");
        }
    }
}
