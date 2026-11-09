// VR180PhotoPicker: a simple in-headset folder picker, created at runtime by VR180Viewer.
//
// Shows the subfolders of the current folder (below a fixed root), with photo counts (each
// folder's own photos, plus any further down in its subfolders),
// "Up one level", "Favourites" (all favourite photos, from every folder), "View the N photos
// here", "Tidy" (delete the non-favourites in a folder, after a confirmation) and, fixed at the
// bottom, "Exit" (closes the app). On PC only, "Copy favourites" copies every favourite into one
// folder (ready to transfer to the Quest) and clears their stars, ready for the next selection. Point with a controller ray and pull the trigger to choose. No scene setup needed: the canvas and its buttons are built in code.
//
// On Android it also checks for "All files access" (MANAGE_EXTERNAL_STORAGE), which is needed
// to read shared folders such as Pictures. If it's missing, the panel offers to open the
// Quest's settings page for this app, and shows the adb command that grants it from a PC.
//
// On the Quest, the top level also offers "Import from camera": copies JPEGs from a camera plugged
// into the USB-C port (MTP, via VR180CameraImport and Plugins/Android/CameraImport.java) into
// Pictures/QooCam Import, skipping ones already there.

using System;
using System.Collections.Generic;
using System.IO;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

public class VR180PhotoPicker : MonoBehaviour
{
    const float PanelWidth = 900f, PanelHeight = 1250f;   // canvas units (1 unit = 1 mm)
    const int RowsPerPage = 7;

    VR180Viewer viewer;
    string root, folder;
    int page;

    Canvas canvas;
    RectTransform listArea;
    TextMeshProUGUI titleText, statusText;
    readonly List<GameObject> rows = new List<GameObject>();
    List<string> tidyList;   // photos waiting for the delete confirmation, or null
    List<string> copyList;   // favourites waiting for the copy confirmation, or null (PC only)
    bool copying;
    int copyDone, copyTotal;
    string notice;           // a one-off message for the status line

    static readonly Color DangerColour = new Color(0.42f, 0.14f, 0.14f);
    static readonly Color FavouriteColour = new Color(0.50f, 0.40f, 0.13f);   // a muted version of the star's gold

    // Photo counts per folder, worked out once per opening of the picker
    struct FolderInfo { public int here, below, subfolders; }
    readonly Dictionary<string, FolderInfo> infoCache = new Dictionary<string, FolderInfo>(StringComparer.OrdinalIgnoreCase);
    const int MaxCountDepth = 6;

    /// A folder not to list (the flat export folder: its pictures aren't VR180)
    public static string ExcludedFolder;

    public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;
    public string Root => root;

    // ------------------------------------------------------------------ setup

    public void Init(VR180Viewer owner, string rootFolder)
    {
        viewer = owner;
        root = Path.GetFullPath(rootFolder);
        BuildCanvas();
        canvas.gameObject.SetActive(false);

        if (FindFirstObjectByType<XRUIInputModule>() == null)
            Debug.LogWarning("VR180Viewer [picker]: no XR UI Input Module in the scene's EventSystem, so the controller rays can't click the picker.");
    }

    public void Open(string startFolder, Transform head)
    {
        folder = startFolder != null && IsUnderRoot(startFolder) && Directory.Exists(startFolder) ? Path.GetFullPath(startFolder) : root;
        page = 0;
        tidyList = null;
        copyList = null;
        infoCache.Clear();   // photos may have been copied in since last time
        PlaceInFrontOf(head);
        canvas.gameObject.SetActive(true);
        Refresh();
    }

    public void Close()
    {
        if (canvas != null) canvas.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (canvas != null) Destroy(canvas.gameObject);
    }

    // Re-check permission when returning from the Quest's settings page
    void OnApplicationFocus(bool focus)
    {
        if (focus && IsOpen) { infoCache.Clear(); Refresh(); }
    }

    void PlaceInFrontOf(Transform head)
    {
        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
        f.Normalize();
        Vector3 pos = head.position + f * 1.3f + Vector3.down * 0.15f;
        canvas.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(f, Vector3.up));
    }

    // ------------------------------------------------------------------ contents

    void Refresh()
    {
        foreach (var r in rows) Destroy(r);
        rows.Clear();

        if (!HasAllFilesAccess())
        {
            titleText.text = "Photo folders";
            statusText.text = "This app needs \"All files access\" to read your photos in " + root + ".\n" +
                              "After allowing it, close the Settings window to come back here.\n\n" +
                              "Or grant it from the PC:\nadb shell appops set --uid " + Application.identifier +
                              " MANAGE_EXTERNAL_STORAGE allow";
            AddRow("Open settings to allow All files access", OpenAllFilesAccessSettings, true);
            AddRow("Close", () => viewer.ClosePicker());
            return;
        }

        try { Directory.CreateDirectory(root); }
        catch (Exception e) { Debug.LogWarning($"VR180Viewer [picker]: can't create {root}: {e.Message}"); }

        if (copying) { ShowCopyProgress(); return; }
        if (importStage != ImportStage.None) { ShowImport(); return; }
        if (tidyList != null) { ShowTidyConfirmation(); return; }
        if (copyList != null) { ShowCopyConfirmation(); return; }

        titleText.text = DisplayPath(folder);

        // All favourites: always the first row, on every page, in the star's gold
        int favourites = VR180Favourites.Count;
        if (favourites > 0)
        {
            AddRow("All favourites", () => viewer.LoadFavourites(), false,
                   $"{favourites} photo{(favourites == 1 ? "" : "s")} from all folders");
            Tint(rows[rows.Count - 1], FavouriteColour);
        }

        var entries = new List<(string label, Action action, bool highlight, string detail)>();
        if (VR180Viewer.OnQuest && SamePath(folder, root))
        {
            string cam = VR180CameraImport.FindCamera();
            cameraShown = cam != null;
            entries.Add((cam != null ? "Import from camera" : "Import from camera: not connected",
                         () => { if (importStage == ImportStage.None) StartCoroutine(ScanCamera()); }, false,
                         cam ?? "plug the camera into the Quest's USB-C port and switch it on"));
        }
        if (!SamePath(folder, root))
            entries.Add(("< Up one level", () => Navigate(Path.GetDirectoryName(folder)), false, null));

        if (favourites > 0)
        {
            if (!VR180Viewer.OnQuest)
                entries.Add(("Copy favourites to the Favourites folder", () => { copyList = VR180Favourites.Resolve(root); Refresh(); }, false,
                             "ready to transfer to the Quest; then their stars are cleared"));
        }

        List<string> photosHere = PhotosIn(folder);
        int here = photosHere.Count;
        int favHere = photosHere.Count(VR180Favourites.IsFavourite);
        if (here > 0)
            entries.Add(($"View the {here} photo{(here == 1 ? "" : "s")} here", () => viewer.LoadFolder(folder), true,
                         favHere > 0 ? $"{favHere} favourite{(favHere == 1 ? "" : "s")}" : null));

        // Only offered where at least one photo has been kept, so a folder can't be emptied by accident
        int others = here - favHere;
        if (favHere > 0 && others > 0)
        {
            var doomed = photosHere.Where(p => !VR180Favourites.IsFavourite(p)).ToList();
            entries.Add(($"Tidy: delete the {others} non-favourite{(others == 1 ? "" : "s")} here", () => { tidyList = doomed; Refresh(); }, false,
                         $"{SizeText(TotalBytes(doomed))}; the {favHere} {(favHere == 1 ? "favourite stays" : "favourites stay")}"));
        }

        foreach (string d in Subfolders(folder))
        {
            string dir = d;
            entries.Add((Path.GetFileName(d), () => Navigate(dir), false, Describe(Info(d))));
        }

        int pages = Mathf.Max(1, Mathf.CeilToInt(entries.Count / (float)RowsPerPage));
        page = Mathf.Clamp(page, 0, pages - 1);
        foreach (var e in entries.Skip(page * RowsPerPage).Take(RowsPerPage))
            AddRow(e.label, e.action, e.highlight, e.detail);

        if (pages > 1)
        {
            if (page > 0) AddRow("Previous page", () => { page--; Refresh(); });
            if (page < pages - 1) AddRow("Next page", () => { page++; Refresh(); });
        }

        if (notice != null)
            statusText.text = notice;
        else if (entries.Count == 0)
            statusText.text = "No photos or folders here yet.\nCopy photos over USB into " + root;
        else
            statusText.text = pages > 1 ? $"Page {page + 1} of {pages}" : "";
        notice = null;
    }

    // ------------------------------------------------------------------ tidy (delete non-favourites)

    void ShowTidyConfirmation()
    {
        int n = tidyList.Count;
        string photos = $"{n} photo{(n == 1 ? "" : "s")}";
        titleText.text = $"Delete {photos}?";
        statusText.text = $"These {photos} in {DisplayPath(folder)} aren't favourites. The favourites stay.\n" +
                          "Deleting can't be undone: there's no recycle bin on the Quest. " +
                          "Only do this if you have copies elsewhere, such as on your PC.";
        AddRow($"Delete {photos} ({SizeText(TotalBytes(tidyList))})", DeleteTidyList);
        Tint(rows[rows.Count - 1], DangerColour);
        AddRow("Cancel", () => { tidyList = null; Refresh(); }, true);
    }

    void DeleteTidyList()
    {
        var deleted = new List<string>();
        int failed = 0;
        foreach (string p in tidyList)
        {
            try { File.Delete(p); deleted.Add(p); }
            catch (Exception e) { failed++; Debug.LogWarning($"VR180Viewer [picker]: couldn't delete {p}: {e.Message}"); }
        }
        Debug.Log($"VR180Viewer [picker]: tidy deleted {deleted.Count} photo(s) in {folder}{(failed > 0 ? $", {failed} failed" : "")}");
        ForgetInGallery(deleted);
        viewer.ForgetFiles(deleted);
        tidyList = null;
        infoCache.Clear();
        notice = $"Deleted {deleted.Count} photo{(deleted.Count == 1 ? "" : "s")}." + (failed > 0 ? $" {failed} couldn't be deleted." : "");
        Refresh();
    }

    // ------------------------------------------------------------------ copy favourites (PC only)

    void ShowCopyConfirmation()
    {
        string dest = viewer.PcFavouritesFolder;
        int n = copyList.Count;
        string photos = $"{n} favourite{(n == 1 ? "" : "s")}";
        if (n == 0)
        {
            titleText.text = "Copy favourites";
            statusText.text = "None of the favourites could be found under " + root + ".";
            AddRow("Back", () => { copyList = null; Refresh(); }, true);
            return;
        }
        titleText.text = $"Copy {photos}?";
        statusText.text = $"They'll be copied to {dest}. The originals stay where they are, and their " +
                          "favourite stars are cleared, ready for your next selection.\n" +
                          "Then copy that folder into the Quest's Pictures folder over USB.";
        AddRow($"Copy {photos} ({SizeText(TotalBytes(copyList))})", StartCopy, true);
        AddRow("Cancel", () => { copyList = null; Refresh(); });
    }

    void ShowCopyProgress()
    {
        titleText.text = "Copying favourites";
        statusText.text = $"Copied {copyDone} of {copyTotal}...";
    }

    void StartCopy()
    {
        var list = copyList;
        copyList = null;
        StartCoroutine(CopyFavourites(list, viewer.PcFavouritesFolder));
    }

    // Copies on a background thread (photos are large); the status line shows progress
    IEnumerator CopyFavourites(List<string> sources, string dest)
    {
        copying = true;
        copyDone = 0;
        copyTotal = sources.Count;
        Refresh();

        var copied = new List<string>();   // favourites now safely in the folder (to unmark)
        int already = 0, conflicts = 0, failed = 0;
        var task = Task.Run(() =>
        {
            Directory.CreateDirectory(dest);
            foreach (string src in sources)
            {
                try
                {
                    string target = Path.Combine(dest, Path.GetFileName(src));
                    if (SamePath(src, target)) { copied.Add(src); already++; }
                    else if (File.Exists(target))
                    {
                        // Same name and size: taken to be the same photo, copied before
                        if (new FileInfo(target).Length == new FileInfo(src).Length) { copied.Add(src); already++; }
                        else conflicts++;
                    }
                    else
                    {
                        string temp = target + ".copying";
                        File.Copy(src, temp, true);
                        File.Move(temp, target);
                        copied.Add(src);
                    }
                }
                catch (Exception) { failed++; }
                Interlocked.Increment(ref copyDone);
            }
        });
        while (!task.IsCompleted)
        {
            if (IsOpen) ShowCopyProgress();
            yield return null;
        }
        if (task.IsFaulted)
        {
            failed = copyTotal - copied.Count;
            Debug.LogWarning($"VR180Viewer [picker]: copying favourites failed: {task.Exception?.GetBaseException().Message}");
        }

        VR180Favourites.Clear(copied);
        copying = false;
        infoCache.Clear();
        int fresh = copied.Count - already;
        notice = $"Copied {fresh} favourite{(fresh == 1 ? "" : "s")} to {dest}" +
                 (already > 0 ? $" ({already} already there)" : "") +
                 (conflicts > 0 ? $"; {conflicts} skipped: a different file with the same name is there" : "") +
                 (failed > 0 ? $"; {failed} failed" : "") +
                 ". Stars cleared on the copied ones.";
        Debug.Log("VR180Viewer [picker]: " + notice);
        Refresh();
    }

    // ------------------------------------------------------------------ import from camera (Quest only)

    enum ImportStage { None, Permission, Scanning, Choice, Importing }
    ImportStage importStage;
    string importCamera;
    const int TestImportCount = 5;
    string ImportFolder => Path.Combine(root, "QooCam Import");

    void ShowImport()
    {
        titleText.text = "Import from camera";
        switch (importStage)
        {
            case ImportStage.Permission:
                statusText.text = $"Allow this app to use {importCamera} in the dialog Android shows " +
                                  "(it may appear as a separate window).";
                AddRow("Cancel", CancelImport);
                break;

            case ImportStage.Scanning:
                statusText.text = $"Looking at the photos on {importCamera}...";
                break;

            case ImportStage.Choice:
                int all = VR180CameraImport.PhotoCount, n = VR180CameraImport.NewCount;
                statusText.text = $"{importCamera}: {all} photo{(all == 1 ? "" : "s")} ({SizeText(VR180CameraImport.PhotoBytes)}), " +
                                  $"{n} not yet imported ({SizeText(VR180CameraImport.NewBytes)}).\n" +
                                  $"They go into {DisplayPath(ImportFolder)}, and are converted from the camera's " +
                                  "fisheye format each time they're shown (the originals aren't changed).";
                if (n > TestImportCount)
                    AddRow($"Import the newest {TestImportCount} (test)", () => StartImport(TestImportCount), true);
                if (n > 0)
                    AddRow($"Import all {n} new photo{(n == 1 ? "" : "s")}", () => StartImport(n), n <= TestImportCount);
                AddRow(n > 0 ? "Cancel" : "Back", CancelImport);
                break;

            case ImportStage.Importing:
                statusText.text = ImportProgress();
                break;
        }
    }

    // The camera may be plugged in or unplugged while the picker is open: keep the
    // "Import from camera" row up to date (checked once a second, top level only)
    float nextCameraCheck;
    bool cameraShown;

    void Update()
    {
        if (!VR180Viewer.OnQuest || !IsOpen || importStage != ImportStage.None || copying ||
            tidyList != null || copyList != null || folder == null || !SamePath(folder, root)) return;
        if (Time.unscaledTime < nextCameraCheck) return;
        nextCameraCheck = Time.unscaledTime + 1f;
        bool present = VR180CameraImport.FindCamera() != null;
        if (present != cameraShown) Refresh();
    }

    static string ImportProgress() => $"Imported {VR180CameraImport.Done} of {VR180CameraImport.Total}...";

    // Finds the camera, gets USB permission if needed, then lists its photos
    IEnumerator ScanCamera()
    {
        importCamera = VR180CameraImport.FindCamera();
        if (importCamera == null)
        {
            notice = "No camera found. Plug it into the Quest's USB-C port, switch it on, and try again.";
            Refresh();
            yield break;
        }

        if (!VR180CameraImport.HasPermission())
        {
            importStage = ImportStage.Permission;
            Refresh();
            VR180CameraImport.RequestPermission();
            float start = Time.realtimeSinceStartup;
            while (!VR180CameraImport.HasPermission())
            {
                if (importStage != ImportStage.Permission) yield break;   // cancelled
                if (Time.realtimeSinceStartup - start > 120f) { EndImport("No permission to use the camera was given."); yield break; }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            Debug.Log("VR180Viewer [import]: USB permission granted");
        }

        importStage = ImportStage.Scanning;
        Refresh();
        VR180CameraImport.StartScan(ImportFolder);
        float t0 = Time.realtimeSinceStartup;
        while (VR180CameraImport.State == VR180CameraImport.Scanning)
            yield return new WaitForSecondsRealtime(0.1f);
        if (VR180CameraImport.State != VR180CameraImport.Scanned) { EndImport(VR180CameraImport.Message); yield break; }

        Debug.Log($"VR180Viewer [import]: {VR180CameraImport.PhotoCount} photos on {importCamera}, {VR180CameraImport.NewCount} new " +
                  $"(listed in {Time.realtimeSinceStartup - t0:F1} s)");
        importStage = ImportStage.Choice;
        Refresh();
    }

    void StartImport(int max)
    {
        if (importStage != ImportStage.Choice) return;
        StartCoroutine(ImportPhotos(max));
    }

    IEnumerator ImportPhotos(int max)
    {
        importStage = ImportStage.Importing;
        VR180CameraImport.StartImport(ImportFolder, max);
        Refresh();
        while (VR180CameraImport.State == VR180CameraImport.Importing)
        {
            if (IsOpen) statusText.text = ImportProgress();
            yield return new WaitForSecondsRealtime(0.25f);
        }
        EndImport(VR180CameraImport.Message);
    }

    void CancelImport()
    {
        importStage = ImportStage.None;
        VR180CameraImport.Reset();
        Refresh();
    }

    void EndImport(string message)
    {
        Debug.Log("VR180Viewer [import]: " + message);
        importStage = ImportStage.None;
        VR180CameraImport.Reset();
        infoCache.Clear();
        notice = string.IsNullOrEmpty(message) ? "Import stopped." : message;
        Refresh();
    }

    static List<string> PhotosIn(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Where(IsPhotoFile).ToList(); }
        catch { return new List<string>(); }
    }

    static long TotalBytes(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (string p in paths)
        {
            try { total += new FileInfo(p).Length; }
            catch { }
        }
        return total;
    }

    static string SizeText(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    // Tells Android's media index the files have gone, so they vanish from USB and gallery views
    static void ForgetInGallery(List<string> paths)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (paths.Count == 0) return;
        try
        {
            using (var scanner = new AndroidJavaClass("android.media.MediaScannerConnection"))
                scanner.CallStatic("scanFile", UnityEngine.Android.AndroidApplication.currentActivity,
                    paths.ToArray(), paths.Select(_ => "image/jpeg").ToArray(), new ScanListener());
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [picker]: media scan after deleting failed: {e.Message}");
        }
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    class ScanListener : AndroidJavaProxy
    {
        public ScanListener() : base("android.media.MediaScannerConnection$OnScanCompletedListener") { }
        public void onScanCompleted(string path, AndroidJavaObject uri) { }
    }
#endif

    static void Tint(GameObject row, Color colour)
    {
        var button = row.GetComponent<Button>();
        var colours = button.colors;
        colours.normalColor = colour;
        colours.highlightedColor = colour * 1.45f;
        colours.selectedColor = colour;
        colours.pressedColor = colour * 0.7f;
        button.colors = colours;
    }

    static void QuitApp()
    {
        Debug.Log("VR180Viewer [picker]: exit chosen");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void Navigate(string target)
    {
        if (target == null || !IsUnderRoot(target)) target = root;
        folder = Path.GetFullPath(target);
        page = 0;
        tidyList = null;
        copyList = null;
        Refresh();
    }

    static int CountPhotos(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Count(IsPhotoFile); }
        catch { return 0; }
    }

    static bool IsPhotoFile(string f)
    {
        string e = Path.GetExtension(f).ToLowerInvariant();
        return e == ".jpg" || e == ".jpeg";
    }

    // Subfolders worth showing: not hidden (".thumbnails" etc.) and not the flat export folder
    static string[] Subfolders(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir)
                .Where(d => !Path.GetFileName(d).StartsWith("."))
                .Where(d => string.IsNullOrEmpty(ExcludedFolder) || !SamePath(d, ExcludedFolder))
                .Where(d => !string.Equals(Path.GetFileName(d), VR180Fisheye.CalibrationFolderName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [picker]: can't list {dir}: {e.Message}");
            return Array.Empty<string>();
        }
    }

    FolderInfo Info(string dir)
    {
        if (infoCache.TryGetValue(dir, out var info)) return info;
        string[] subs = Subfolders(dir);
        info = new FolderInfo { here = CountPhotos(dir), subfolders = subs.Length };
        foreach (string s in subs) info.below += CountAllPhotos(s, 1);
        infoCache[dir] = info;
        return info;
    }

    // Photos in a folder and everything below it (to a sensible depth)
    int CountAllPhotos(string dir, int depth)
    {
        if (infoCache.TryGetValue(dir, out var known)) return known.here + known.below;
        int n = CountPhotos(dir);
        if (depth < MaxCountDepth)
            foreach (string s in Subfolders(dir)) n += CountAllPhotos(s, depth + 1);
        return n;
    }

    static string Describe(FolderInfo f)
    {
        string Photos(int n) => $"{n} photo{(n == 1 ? "" : "s")}";
        string Folders(int n) => $"{n} subfolder{(n == 1 ? "" : "s")}";
        if (f.here == 0 && f.below == 0)
            return f.subfolders > 0 ? $"no photos ({Folders(f.subfolders)}, all empty)" : "empty";
        if (f.below == 0) return Photos(f.here);
        string below = $"{Photos(f.below)} in {Folders(f.subfolders)}";
        return f.here == 0 ? $"no photos here; {below}" : $"{Photos(f.here)} + {below}";
    }

    string DisplayPath(string path)
    {
        string rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (SamePath(path, root)) return rootName;
        string rel = Path.GetFullPath(path).Substring(root.Length).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rootName + " / " + rel.Replace(Path.DirectorySeparatorChar, '/').Replace("/", " / ");
    }

    bool IsUnderRoot(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(r, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Android permission

    public static bool HasAllFilesAccess()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var env = new AndroidJavaClass("android.os.Environment"))
                return env.CallStatic<bool>("isExternalStorageManager");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [picker]: couldn't check All files access: {e.Message}");
            return false;
        }
#else
        return true;
#endif
    }

    static void OpenAllFilesAccessSettings()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            var activity = UnityEngine.Android.AndroidApplication.currentActivity;
            using (var uriClass = new AndroidJavaClass("android.net.Uri"))
            using (var uri = uriClass.CallStatic<AndroidJavaObject>("parse", "package:" + Application.identifier))
            using (var intent = new AndroidJavaObject("android.content.Intent", "android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION", uri))
                activity.Call("startActivity", intent);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [picker]: couldn't open the All files access settings: {e.Message}");
        }
#endif
    }

    // ------------------------------------------------------------------ building the UI

    void BuildCanvas()
    {
        var go = new GameObject("VR180 Photo Picker", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(TrackedDeviceGraphicRaycaster));
        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        rt.localScale = Vector3.one * 0.001f;
        go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 2f;

        var bg = NewUI("Background", rt);
        Stretch(bg);
        var bgImage = bg.gameObject.AddComponent<Image>();
        bgImage.color = new Color(0.07f, 0.08f, 0.10f, 0.94f);
        var layout = bg.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(36, 36, 30, 30);
        layout.spacing = 16;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        titleText = NewText("Title", bg, 46, FontStyles.Bold, new Color(0.95f, 0.95f, 0.95f));
        titleText.gameObject.AddComponent<LayoutElement>().preferredHeight = 64;

        listArea = NewUI("List", bg);
        var listLayout = listArea.gameObject.AddComponent<VerticalLayoutGroup>();
        listLayout.spacing = 10;
        listLayout.childControlWidth = listLayout.childControlHeight = true;
        listLayout.childForceExpandWidth = true;
        listLayout.childForceExpandHeight = false;
        listArea.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1;

        statusText = NewText("Status", bg, 30, FontStyles.Normal, new Color(0.7f, 0.72f, 0.76f));
        statusText.textWrappingMode = TextWrappingModes.Normal;
        statusText.gameObject.AddComponent<LayoutElement>().minHeight = 40;

        // Exit always sits at the very bottom, whatever the list above holds
        var exit = MakeRow(bg, "Exit VR180 Viewer", QuitApp, false, null);
        Tint(exit, DangerColour);
    }

    // With a detail, the row shows the label above a smaller grey line and a ">" on the right
    void AddRow(string label, Action onClick, bool highlight = false, string detail = null) =>
        rows.Add(MakeRow(listArea, label, onClick, highlight, detail));

    GameObject MakeRow(Transform parent, string label, Action onClick, bool highlight, string detail)
    {
        var row = NewUI("Row", parent);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 84;
        var image = row.gameObject.AddComponent<Image>();
        Color baseColour = highlight ? new Color(0.20f, 0.36f, 0.55f) : new Color(0.17f, 0.19f, 0.24f);
        image.color = Color.white;
        var button = row.gameObject.AddComponent<Button>();
        var colours = button.colors;
        colours.normalColor = baseColour;
        colours.highlightedColor = baseColour * 1.45f;
        colours.selectedColor = baseColour;
        colours.pressedColor = baseColour * 0.7f;
        colours.colorMultiplier = 1f;
        button.colors = colours;
        button.onClick.AddListener(() => onClick?.Invoke());

        var text = NewText("Label", row, detail == null ? 38 : 34, FontStyles.Normal, Color.white);
        Stretch(text.rectTransform);
        text.rectTransform.offsetMin = new Vector2(28, 0);
        text.rectTransform.offsetMax = new Vector2(detail == null ? -28 : -70, 0);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.text = label;

        if (detail != null)
        {
            text.rectTransform.anchorMin = new Vector2(0f, 0.42f);
            text.rectTransform.offsetMax = new Vector2(-70, -4);

            var sub = NewText("Detail", row, 26, FontStyles.Normal, new Color(0.72f, 0.75f, 0.80f));
            Stretch(sub.rectTransform);
            sub.rectTransform.anchorMax = new Vector2(1f, 0.46f);
            sub.rectTransform.offsetMin = new Vector2(28, 6);
            sub.rectTransform.offsetMax = new Vector2(-70, 0);
            sub.alignment = TextAlignmentOptions.TopLeft;
            sub.textWrappingMode = TextWrappingModes.NoWrap;
            sub.overflowMode = TextOverflowModes.Ellipsis;
            sub.text = detail;

            var arrow = NewText("Arrow", row, 40, FontStyles.Bold, Color.white);
            Stretch(arrow.rectTransform);
            arrow.rectTransform.offsetMax = new Vector2(-28, 0);
            arrow.alignment = TextAlignmentOptions.MidlineRight;
            arrow.text = ">";
        }

        return row.gameObject;
    }

    static RectTransform NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    static TextMeshProUGUI NewText(string name, Transform parent, float size, FontStyles style, Color colour)
    {
        var t = NewUI(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        t.fontSize = size;
        t.fontStyle = style;
        t.color = colour;
        t.raycastTarget = false;
        return t;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
