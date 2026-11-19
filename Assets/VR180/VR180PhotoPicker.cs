// VR180PhotoPicker: the in-headset photo picker, created at runtime by VR180Viewer.
//
// Layout (built in code; no scene setup needed):
// - The main area: the current folder's path, then a scrolling grid of tiles: "Up" (below the
//   root), the subfolders (each with its first photo as a cover, and its photo counts), then the
//   photos, each with a stereo thumbnail (see VR180Thumbnails and Hidden/VR180/StereoThumb).
//   Choosing a photo starts the slideshow there, in the folder's order; reopening the picker shows
//   the photo being viewed, outlined. Either thumbstick up/down scrolls the grid. The tile under the
//   ray lifts towards you.
// - The side column, a little nearer than the grid and set off by a bar: the actions that aren't
//   browsing: "All favourites" (every favourite, from every folder) and its Shuffle switch, "View
//   all N from the start", on the Quest's top level "Import from camera", on PC "Copy favourites",
//   "Tidy" (delete the non-favourites in a folder, after a confirmation), and, fixed at the
//   bottom, "Exit" (closes the app).
// - Confirmations and progress (tidy, copy, import, permission) replace the grid with a list.
//
// On Android it also checks for "All files access" (MANAGE_EXTERNAL_STORAGE), which is needed
// to read shared folders such as Pictures. If it's missing, the panel offers to open the
// Quest's settings page for this app, and shows the adb command that grants it from a PC.
//
// "Import from camera" copies JPEGs from a camera plugged into the Quest's USB-C port (MTP, via
// VR180CameraImport and Plugins/Android/CameraImport.java) into Pictures/QooCam Import, skipping
// ones already there.

using System;
using System.Collections.Generic;
using System.IO;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

public class VR180PhotoPicker : MonoBehaviour
{
    // Sizes in canvas units (1 unit = 1 mm before the UI Scale)
    // (15 November: side column 400 -> 600, the panel widened to match so the grid keeps its size)
    const float PanelWidth = 1800f, PanelHeight = 1080f;
    const float SideWidth = 600f, SeparatorGap = 34f, Pad = 30f;
    const float TitleHeight = 66f, StatusHeight = 50f;
    const int Columns = 5;
    const float TileW = 196f, ThumbH = 245f, LabelH = 76f, GapX = 24f, GapY = 24f;
    const float CellH = ThumbH + LabelH, RowStep = CellH + GapY;
    const float SideDepth = 30f;          // how much nearer the side column is (units)
    const float HoverLift = 34f, HoverScale = 1.06f;
    const float ScrollSpeed = 1500f;      // units per second at a full push
    const float StickDeadZone = 0.2f;
    const int KeepRowsAround = 3;         // thumbnails kept loaded this many rows beyond the visible ones

    VR180Viewer viewer;
    string root, folder;
    string highlightPath;                 // the photo being viewed, outlined in the grid

    Canvas canvas;
    RectTransform sideList, mainArea, gridViewport, gridContent, listArea, scrollTrack, scrollThumb;
    TextMeshProUGUI titleText, statusText;
    readonly List<GameObject> rows = new List<GameObject>();       // the list (confirmations etc.)
    readonly List<GameObject> sideRows = new List<GameObject>();
    List<string> tidyList;   // photos waiting for the delete confirmation, or null
    List<string> deleteList; // marked photos waiting for the delete confirmation, or null

    // Marked photos, for deleting (any folder, this session only). Hold grip and point: the photos the ray
    // passes over are marked, or unmarked if the sweep started on a marked one.
    readonly HashSet<string> marked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    InputAction markAction;
    string hoveredPhoto;        // the photo tile under a ray, or null
    string lastPainted;         // the last photo this grip hold changed (or found already right)
    bool? paintMarks;           // this grip hold: marking (true) or unmarking (false); null until the first photo
    List<string> photosShown = new List<string>();   // the folder's photos, as last listed
    static readonly Color MarkColour = new Color(0.95f, 0.30f, 0.26f);
    List<string> copyList;   // favourites waiting for the copy confirmation, or null (PC only)
    bool copying;
    int copyDone, copyTotal;
    string notice;           // a one-off message for the status line

    static readonly Color DangerColour = new Color(0.42f, 0.14f, 0.14f);
    static readonly Color FavouriteColour = new Color(0.50f, 0.40f, 0.13f);   // a muted version of the star's gold
    static readonly Color AccentColour = new Color(0.33f, 0.62f, 0.95f);
    static readonly Color TileColour = new Color(0.16f, 0.18f, 0.22f);
    static readonly Color FolderColour = new Color(0.30f, 0.34f, 0.42f);

    // Photo counts per folder, worked out once per opening of the picker
    struct FolderInfo { public int here, below, subfolders; }
    readonly Dictionary<string, FolderInfo> infoCache = new Dictionary<string, FolderInfo>(StringComparer.OrdinalIgnoreCase);
    const int MaxCountDepth = 6;

    /// A folder not to list (the flat export folder: its pictures aren't VR180)
    public static string ExcludedFolder;

    /// Set by the viewer (Inspector): thumbnails in stereo, and how far behind the panel distant scenery sits
    public static bool StereoThumbnails = true;
    public static float ThumbnailDepth = 0.015f;

    public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;
    public string Root => root;

    // ------------------------------------------------------------------ the grid's tiles

    class Tile
    {
        public RectTransform rect;
        public RawImage thumb;          // the stereo thumbnail (photo, or a folder's cover)
        public string thumbOf;          // whose thumbnail it shows (null: none)
        public bool hasThumb;           // a current thumbnail exists on disk
        public Texture2D tex;           // loaded while near the visible part of the grid
        public int row;
        public string photo;            // photo tiles: the photo's path
        public GameObject markOutline, markTint;
    }
    readonly List<Tile> tiles = new List<Tile>();
    float scroll, maxScroll;            // units scrolled down from the top
    int lastQueueRow = int.MinValue;
    readonly Dictionary<string, float> folderScroll = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Material> roundMaterials = new Dictionary<string, Material>();
    Texture2D starTexture;
    Texture2D upArrowTexture;
    float upArrowDepth = -1f;           // the Thumbnail Depth it was drawn for
    bool gridMode;
    string gridFolder;                  // the folder the grid is showing (its scroll is remembered)

    // ------------------------------------------------------------------ setup

    public void Init(VR180Viewer owner, string rootFolder)
    {
        viewer = owner;
        root = Path.GetFullPath(rootFolder);
        BuildCanvas();
        canvas.gameObject.SetActive(false);
        VR180Thumbnails.Ready += OnThumbnailReady;

        markAction = new InputAction("MarkPhotos", InputActionType.Button);
        markAction.AddBinding("<XRController>{RightHand}/{GripButton}");
        markAction.AddBinding("<XRController>{LeftHand}/{GripButton}");
        markAction.AddBinding("<Keyboard>/g");   // Editor: hold G and point with the mouse
        markAction.Enable();

        if (FindFirstObjectByType<XRUIInputModule>() == null)
            Debug.LogWarning("VR180Viewer [picker]: no XR UI Input Module in the scene's EventSystem, so the controller rays can't click the picker.");
    }

    /// Opens at startFolder (or the root), showing currentPhoto outlined if it's there
    public void Open(string startFolder, Transform head, string currentPhoto = null)
    {
        folder = startFolder != null && IsUnderRoot(startFolder) && Directory.Exists(startFolder) ? Path.GetFullPath(startFolder) : root;
        highlightPath = currentPhoto;
        tidyList = null;
        deleteList = null;
        copyList = null;
        infoCache.Clear();   // photos may have been copied in since last time
        PlaceInFrontOf(head);
        foreach (var m in roundMaterials.Values)
            if (m != null) { m.SetFloat("_Stereo", StereoThumbnails ? 1f : 0f); m.SetFloat("_Window", ThumbnailDepth); }
        canvas.gameObject.SetActive(true);
        VR180Thumbnails.Running = true;
        Refresh(true);
    }

    public void Close()
    {
        if (canvas != null) canvas.gameObject.SetActive(false);
        VR180Thumbnails.Running = false;
        if (gridMode && gridFolder != null) folderScroll[gridFolder] = scroll;
        ClearTiles();   // frees the thumbnails' memory
    }

    void OnDestroy()
    {
        VR180Thumbnails.Ready -= OnThumbnailReady;
        markAction?.Dispose();
        ClearTiles();
        foreach (var m in roundMaterials.Values) if (m != null) Destroy(m);
        if (starTexture != null) Destroy(starTexture);
        if (upArrowTexture != null) Destroy(upArrowTexture);
        if (canvas != null) Destroy(canvas.gameObject);
    }

    // Re-check permission when returning from the Quest's settings page
    void OnApplicationFocus(bool focus)
    {
        if (focus && IsOpen) { infoCache.Clear(); Refresh(); }   // (keeps the grid's scroll)
    }

    void PlaceInFrontOf(Transform head)
    {
        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
        f.Normalize();
        float k = VR180Viewer.UIScale;
        canvas.transform.localScale = Vector3.one * 0.001f * k;
        Vector3 pos = head.position + f * 1.3f + Vector3.down * (0.12f * k);
        canvas.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(f, Vector3.up));
    }

    // ------------------------------------------------------------------ contents

    void Refresh(bool opening = false)
    {
        if (gridMode && gridFolder != null) folderScroll[gridFolder] = scroll;
        foreach (var r in rows) Destroy(r);
        rows.Clear();
        foreach (var r in sideRows) Destroy(r);
        sideRows.Clear();
        ClearTiles();
        SetGridMode(false);

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
        if (deleteList != null) { ShowDeleteConfirmation(); return; }
        if (copyList != null) { ShowCopyConfirmation(); return; }

        titleText.text = DisplayPath(folder);
        List<string> photosHere = SortedPhotos(folder);
        VR180Thumbnails.Prune(folder, photosHere);
        photosShown = photosHere;
        marked.RemoveWhere(p => !File.Exists(p));   // (deleted or moved meanwhile)

        BuildSide(photosHere);
        BuildGrid(photosHere, opening);

        int folders = Subfolders(folder).Length;
        if (notice != null)
            statusText.text = notice;
        else if (photosHere.Count == 0 && folders == 0)
            statusText.text = "No photos or folders here yet. Copy photos over USB into " + root;
        else
            statusText.text = Summary(folders, photosHere.Count);
        notice = null;
    }

    string Summary(int folders, int photos)
    {
        var parts = new List<string>();
        if (folders > 0) parts.Add($"{folders} folder{(folders == 1 ? "" : "s")}");
        if (photos > 0) parts.Add($"{photos} photo{(photos == 1 ? "" : "s")}");
        int missing = tiles.Count(t => t.thumbOf != null && !t.hasThumb);
        string s = string.Join(", ", parts);
        if (missing > 0) s += $"   (making thumbnails: {missing} to go)";
        if (marked.Count > 0) s += $"   \u00b7   {marked.Count} marked";
        else if (photos > 0) s += "   \u00b7   hold grip and point to mark photos";
        return s;
    }

    // The side column: everything that isn't browsing
    void BuildSide(List<string> photosHere)
    {
        int favourites = VR180Favourites.Count;
        if (favourites > 0)
        {
            AddSideRow("All favourites", () => viewer.LoadFavourites(), false,
                       $"{favourites} photo{(favourites == 1 ? "" : "s")} from all folders");
            Tint(sideRows[sideRows.Count - 1], FavouriteColour);
            bool shuffle = VR180Viewer.ShuffleFavourites;
            AddSideRow(shuffle ? "Shuffle: on" : "Shuffle: off", () => { VR180Viewer.ShuffleFavourites = !VR180Viewer.ShuffleFavourites; RefreshKeepingScroll(); }, false,
                       shuffle ? "no repeats until all are seen" : "favourites in name order");
            if (shuffle) Tint(sideRows[sideRows.Count - 1], Color.Lerp(FavouriteColour, Color.black, 0.25f));
            AddSideDivider();   // the favourites, set apart from the browsing actions below
        }

        int here = photosHere.Count;
        int favHere = photosHere.Count(VR180Favourites.IsFavourite);
        if (here > 0)
            AddSideRow($"View all {here} from the start", () => viewer.LoadFolder(folder), true,
                       favHere > 0 ? $"{favHere} favourite{(favHere == 1 ? "" : "s")} here" : "or choose a photo to start there");

        if (VR180Viewer.OnQuest && SamePath(folder, root))
        {
            string cam = VR180CameraImport.FindCamera();
            cameraShown = cam != null;
            AddSideRow(cam != null ? "Import from camera" : "Import from camera: not connected",
                       () => { if (importStage == ImportStage.None) StartCoroutine(ScanCamera()); }, false,
                       cam ?? "plug the camera into the USB-C port");
        }

        if (favourites > 0 && !VR180Viewer.OnQuest)
            AddSideRow("Copy favourites", () => { copyList = VR180Favourites.Resolve(root); Refresh(); }, false,
                       "to the Favourites folder, for the Quest");

        // Only offered where at least one photo has been kept, so a folder can't be emptied by accident
        int others = here - favHere;
        if (favHere > 0 && others > 0)
        {
            var doomed = photosHere.Where(p => !VR180Favourites.IsFavourite(p)).ToList();
            AddSideRow($"Tidy: delete {others} non-favourite{(others == 1 ? "" : "s")}", () => { tidyList = doomed; Refresh(); }, false,
                       $"{SizeText(TotalBytes(doomed))}; the {favHere} {(favHere == 1 ? "favourite stays" : "favourites stay")}");
        }

        // Marked photos (from any folder)
        if (marked.Count > 0)
        {
            int n = marked.Count;
            int markedHere = photosHere.Count(p => marked.Contains(p));
            string where = markedHere == n ? (n == 1 ? "in this folder" : "all in this folder")
                         : markedHere == 0 ? "none in this folder" : $"{markedHere} in this folder";
            AddSideRow($"Delete {n} marked photo{(n == 1 ? "" : "s")}",
                       () => { deleteList = marked.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); Refresh(); }, false,
                       $"{SizeText(TotalBytes(marked))}, {where}");
            Tint(sideRows[sideRows.Count - 1], DangerColour);
            AddSideRow("Clear the marks", () => { marked.Clear(); UpdateMarks(); }, false, "hold grip and point to mark photos");
        }
    }

    // ------------------------------------------------------------------ marking photos (hold grip and point)

    void UpdateMarking()
    {
        if (markAction == null) return;
        if (!markAction.IsPressed()) { paintMarks = null; lastPainted = null; return; }
        if (hoveredPhoto == null || string.Equals(hoveredPhoto, lastPainted, StringComparison.OrdinalIgnoreCase)) return;
        lastPainted = hoveredPhoto;
        if (paintMarks == null) paintMarks = !marked.Contains(hoveredPhoto);   // the first photo decides
        bool changed = paintMarks.Value ? marked.Add(hoveredPhoto) : marked.Remove(hoveredPhoto);
        if (!changed) return;
        VR180FavouriteBadge.Tap();
        UpdateMarks();
    }

    // The tiles' marks, the side column's rows and the status line, after the marks changed
    void UpdateMarks()
    {
        foreach (var t in tiles)
            if (t.photo != null) SetMarkVisual(t, marked.Contains(t.photo));
        foreach (var r in sideRows) Destroy(r);
        sideRows.Clear();
        BuildSide(photosShown);
        if (notice == null && gridMode) statusText.text = Summary(Subfolders(folder).Length, photosShown.Count);
    }

    static void SetMarkVisual(Tile t, bool on)
    {
        if (t.markOutline != null) t.markOutline.SetActive(on);
        if (t.markTint != null) t.markTint.SetActive(on);
    }

    void RefreshKeepingScroll() => Refresh();

    // ------------------------------------------------------------------ the grid

    void BuildGrid(List<string> photosHere, bool opening)
    {
        SetGridMode(true);
        gridFolder = folder;
        int i = 0;
        if (!SamePath(folder, root))
            AddUpTile(i++);
        foreach (string d in Subfolders(folder))
            AddFolderTile(i++, d);
        int highlightIndex = -1;
        foreach (string p in photosHere)
        {
            if (highlightPath != null && SamePath(p, highlightPath)) highlightIndex = i;
            AddPhotoTile(i++, p, highlightIndex == i - 1);
        }

        int rowsUsed = Mathf.CeilToInt(i / (float)Columns);
        float contentH = rowsUsed > 0 ? rowsUsed * CellH + (rowsUsed - 1) * GapY : 0f;
        gridContent.sizeDelta = new Vector2(gridContent.sizeDelta.x, contentH);
        maxScroll = Mathf.Max(0f, contentH - gridViewport.rect.height);

        // Where to start: the photo being viewed (when opening), else where this folder was left
        if (opening && highlightIndex >= 0)
            scroll = (highlightIndex / Columns) * RowStep - (gridViewport.rect.height - CellH) * 0.5f;
        else
            scroll = folderScroll.TryGetValue(folder, out float s) ? s : 0f;
        scroll = Mathf.Clamp(scroll, 0f, maxScroll);
        lastQueueRow = int.MinValue;
        ApplyScroll();
    }

    /// Called by the viewer every frame while the picker is open: either thumbstick up/down scrolls
    public void Scroll(Vector2 stick)
    {
        if (!gridMode || maxScroll <= 0f) return;
        float y = stick.y;
        if (Mathf.Abs(y) < StickDeadZone || Mathf.Abs(y) < Mathf.Abs(stick.x)) return;
        float push = (Mathf.Abs(y) - StickDeadZone) / (1f - StickDeadZone);
        scroll = Mathf.Clamp(scroll - Mathf.Sign(y) * push * push * ScrollSpeed * Time.unscaledDeltaTime, 0f, maxScroll);
        ApplyScroll();
    }

    void ApplyScroll()
    {
        gridContent.anchoredPosition = new Vector2(0f, scroll);
        float viewH = gridViewport.rect.height;
        bool scrollable = maxScroll > 0f;
        scrollTrack.gameObject.SetActive(scrollable);
        if (scrollable)
        {
            float trackH = scrollTrack.rect.height;
            float contentH = maxScroll + viewH;
            float thumbH = Mathf.Max(60f, trackH * viewH / contentH);
            scrollThumb.sizeDelta = new Vector2(scrollThumb.sizeDelta.x, thumbH);
            scrollThumb.anchoredPosition = new Vector2(0f, -(trackH - thumbH) * (scroll / maxScroll));
        }
    }

    void Update()
    {
        UpdateCameraRow();
        if (!IsOpen || !gridMode) return;
        UpdateMarking();

        // Load the thumbnails near the visible rows (a few per frame), free those far away
        float viewH = gridViewport.rect.height;
        int firstRow = Mathf.FloorToInt(scroll / RowStep);
        int lastRow = Mathf.FloorToInt((scroll + viewH) / RowStep);
        float budgetEnd = Time.realtimeSinceStartup + 0.006f;
        bool loadedOne = false;
        foreach (var t in tiles)
        {
            if (t.thumbOf == null) continue;
            bool near = t.row >= firstRow - 1 && t.row <= lastRow + 1;
            bool keep = t.row >= firstRow - KeepRowsAround && t.row <= lastRow + KeepRowsAround;
            if (t.tex == null && near && t.hasThumb && (!loadedOne || Time.realtimeSinceStartup < budgetEnd))
            {
                t.tex = VR180Thumbnails.Load(t.thumbOf);
                loadedOne = true;
                if (t.tex == null) t.hasThumb = false;
                else { t.thumb.texture = t.tex; t.thumb.color = Color.white; }
            }
            else if (t.tex != null && !keep)
            {
                t.thumb.texture = Texture2D.whiteTexture;
                t.thumb.color = TileColour * 1.3f;
                Destroy(t.tex);
                t.tex = null;
            }
        }

        // Ask for the missing thumbnails, nearest the visible rows first
        int centreRow = (firstRow + lastRow) / 2;
        if (centreRow != lastQueueRow)
        {
            lastQueueRow = centreRow;
            var wanted = tiles.Where(t => t.thumbOf != null && !t.hasThumb)
                              .OrderBy(t => Mathf.Abs(t.row - centreRow))
                              .Select(t => t.thumbOf).ToList();
            if (VR180Thumbnails.Instance != null) VR180Thumbnails.Instance.SetQueue(wanted);
        }
    }

    void OnThumbnailReady(string path)
    {
        bool any = false;
        foreach (var t in tiles)
            if (t.thumbOf != null && SamePath(t.thumbOf, path)) { t.hasThumb = true; any = true; }
        if (any && gridMode && notice == null && IsOpen)
            statusText.text = Summary(Subfolders(folder).Length, tiles.Count(t => t.rect.name == "Photo"));
    }

    void ClearTiles()
    {
        foreach (var t in tiles)
        {
            if (t.tex != null) Destroy(t.tex);
            if (t.rect != null) Destroy(t.rect.gameObject);
        }
        tiles.Clear();
        hoveredPhoto = null;
        if (VR180Thumbnails.Instance != null) VR180Thumbnails.Instance.SetQueue(new List<string>());
    }

    // The grid, or the list (confirmations etc.) with room below it for a longer explanation
    void SetGridMode(bool on)
    {
        gridMode = on;
        gridViewport.gameObject.SetActive(on);
        listArea.gameObject.SetActive(!on);
        if (!on) scrollTrack.gameObject.SetActive(false);
        const float ListStatusHeight = 400f;
        if (on)
        {
            Place(statusText.rectTransform, bottom: Pad * 0.5f, height: StatusHeight + Pad * 0.5f, left: Pad, right: Pad);
            statusText.alignment = TextAlignmentOptions.MidlineLeft;
        }
        else
        {
            listArea.offsetMin = new Vector2(Pad, Pad + ListStatusHeight + 20f);
            Place(statusText.rectTransform, bottom: Pad, height: ListStatusHeight, left: Pad, right: Pad);
            statusText.alignment = TextAlignmentOptions.TopLeft;
        }
    }

    static List<string> SortedPhotos(string dir)
    {
        try { return VR180Viewer.ListPhotos(dir); }
        catch { return new List<string>(); }
    }

    // ------------------------------------------------------------------ tiles

    // A tile: an invisible hit area (the button) holding a "Lift" that rises towards you on hover
    Tile NewTile(int i, string kind, Action onClick)
    {
        var rt = NewUI(kind, gridContent);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(TileW, CellH);
        int col = i % Columns, row = i / Columns;
        rt.anchoredPosition = new Vector2(col * (TileW + GapX) + TileW * 0.5f, -(row * RowStep + CellH * 0.5f));

        var hit = rt.gameObject.AddComponent<Image>();
        hit.color = new Color(0f, 0f, 0f, 0f);
        var button = rt.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(() => onClick?.Invoke());

        // A soft shadow that appears under the tile as it lifts (stays on the panel)
        var shadow = NewRounded("Shadow", rt, Color.clear, ThumbArea(8f, -10f));

        var lift = NewUI("Lift", rt);
        Stretch(lift);
        var hover = rt.gameObject.AddComponent<TileHover>();
        hover.lift = lift;
        hover.shadow = shadow;

        var tile = new Tile { rect = rt, row = row };
        tiles.Add(tile);
        return tile;
    }

    // The thumbnail's rectangle within a tile, grown by g on each side and moved down by dy
    static Rect ThumbArea(float g = 0f, float dy = 0f) =>
        new Rect(-g, LabelH - g + dy, TileW + 2f * g, ThumbH + 2f * g);

    void AddPhotoTile(int i, string path, bool current)
    {
        var tile = NewTile(i, "Photo", () => viewer.LoadFolder(folder, path));
        var lift = (RectTransform)tile.rect.Find("Lift");
        tile.photo = path;
        tile.markOutline = NewRounded("Marked", lift, MarkColour, ThumbArea(11f)).gameObject;   // (outside the blue outline)
        if (current) NewRounded("Current", lift, AccentColour, ThumbArea(7f));
        tile.thumb = NewRounded("Thumb", lift, TileColour * 1.3f, ThumbArea());
        tile.markTint = NewRounded("MarkedTint", lift, new Color(0.85f, 0.12f, 0.10f, 0.38f), ThumbArea()).gameObject;
        SetMarkVisual(tile, marked.Contains(path));
        var hover = tile.rect.GetComponent<TileHover>();
        hover.changed = on =>
        {
            if (on) hoveredPhoto = path;
            else if (string.Equals(hoveredPhoto, path, StringComparison.OrdinalIgnoreCase)) hoveredPhoto = null;
        };
        tile.thumbOf = path;
        tile.hasThumb = VR180Thumbnails.Has(path);
        if (VR180Favourites.IsFavourite(path)) AddStar(lift);
        AddLabel(lift, Path.GetFileNameWithoutExtension(path), null, current);
    }

    void AddFolderTile(int i, string dir)
    {
        var tile = NewTile(i, "Folder", () => Navigate(dir));
        var lift = (RectTransform)tile.rect.Find("Lift");
        // A folder: a tab on top of a body, with the folder's first photo inside
        var area = ThumbArea();
        NewRounded("Tab", lift, FolderColour, new Rect(area.x, area.yMax - 30f, TileW * 0.42f, 30f));
        NewRounded("Body", lift, FolderColour, new Rect(area.x, area.y, area.width, area.height - 20f));
        string cover = SortedPhotos(dir).FirstOrDefault();
        if (cover != null)
        {
            // The cover keeps the thumbnails' shape, centred in the body
            float h = area.height - 44f, w = h * VR180Thumbnails.EyeWidth / VR180Thumbnails.Height;
            tile.thumb = NewRounded("Thumb", lift, TileColour * 1.3f, new Rect(area.x + (area.width - w) * 0.5f, area.y + 12f, w, h));
            tile.thumbOf = cover;
            tile.hasThumb = VR180Thumbnails.Has(cover);
        }
        AddLabel(lift, Path.GetFileName(dir), Describe(Info(dir)), false);
    }

    void AddUpTile(int i)
    {
        var tile = NewTile(i, "Up", () => Navigate(Path.GetDirectoryName(folder)));
        var lift = (RectTransform)tile.rect.Find("Lift");
        var arrow = NewRounded("Arrow", lift, Color.white, ThumbArea());
        arrow.texture = UpArrowTexture();
        AddLabel(lift, "Up one level", Path.GetFileName(Path.GetDirectoryName(folder)?.TrimEnd('/', '\\') ?? ""), false);
    }

    void AddLabel(RectTransform lift, string name, string detail, bool current)
    {
        var label = NewText("Name", lift, 24, current ? FontStyles.Bold : FontStyles.Normal, current ? AccentColour : new Color(0.92f, 0.93f, 0.95f));
        var r = label.rectTransform;
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 0f);
        r.anchoredPosition = new Vector2(-6f, 0f);
        r.sizeDelta = new Vector2(TileW + 12f, LabelH - 6f);
        label.alignment = TextAlignmentOptions.Top;
        label.textWrappingMode = TextWrappingModes.Normal;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.maxVisibleLines = 2;
        label.text = detail == null ? name : $"{name}\n<size=80%><color=#A8AEB8>{detail}</color></size>";
    }

    void AddStar(RectTransform lift)
    {
        if (starTexture == null) starTexture = MakeStarTexture(96);
        var star = NewUI("Star", lift).gameObject.AddComponent<RawImage>();
        star.texture = starTexture;
        star.raycastTarget = false;
        var r = star.rectTransform;
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 0f);
        r.sizeDelta = new Vector2(46f, 46f);
        r.anchoredPosition = new Vector2(TileW - 52f, LabelH + ThumbH - 52f);
    }

    // A rounded rectangle (the thumbnail shader with a plain texture), positioned from the tile's bottom-left
    RawImage NewRounded(string name, RectTransform parent, Color colour, Rect area)
    {
        var img = NewUI(name, parent).gameObject.AddComponent<RawImage>();
        img.texture = Texture2D.whiteTexture;
        img.color = colour;
        img.material = RoundedMaterial(area.width / area.height, Mathf.Min(CornerRadius / area.height, 0.5f));
        img.raycastTarget = false;
        var r = img.rectTransform;
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 0f);
        r.anchoredPosition = area.position;
        r.sizeDelta = area.size;
        return img;
    }

    const float CornerRadius = 14f;   // units, the same on every rounded shape

    Material RoundedMaterial(float aspect, float corner)
    {
        aspect = Mathf.Round(aspect * 100f) / 100f;
        corner = Mathf.Round(corner * 1000f) / 1000f;
        string key = $"{aspect:F2}/{corner:F3}";
        if (roundMaterials.TryGetValue(key, out var m) && m != null) return m;
        var shader = Resources.Load<Shader>("VR180StereoThumb");
        if (shader == null)
        {
            Debug.LogError("VR180Viewer [picker]: Resources/VR180StereoThumb.shader is missing");
            return null;
        }
        m = new Material(shader) { name = "VR180 tile " + key };
        m.SetFloat("_Aspect", aspect);
        m.SetFloat("_Corner", corner);
        m.SetFloat("_Stereo", StereoThumbnails ? 1f : 0f);
        m.SetFloat("_Window", ThumbnailDepth);
        roundMaterials[key] = m;
        return m;
    }

    static Texture2D MakeStarTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "VR180 star", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[size * size];
        var gold = new Color(1f, 0.80f, 0.25f);
        // Five points, inner radius 0.45 of the outer; 4x4 samples per pixel; a dark rim for contrast
        var outer = new Vector2[10];
        for (int k = 0; k < 10; k++)
        {
            float a = Mathf.PI / 2f + k * Mathf.PI / 5f;
            float rad = k % 2 == 0 ? 0.48f : 0.22f;
            outer[k] = new Vector2(0.5f + rad * Mathf.Cos(a), 0.47f + rad * Mathf.Sin(a));
        }
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                        if (InPolygon(new Vector2((x + (sx + 0.5f) / 4f) / size, (y + (sy + 0.5f) / 4f) / size), outer)) inside++;
                float a = inside / 16f;
                px[y * size + x] = new Color(gold.r, gold.g, gold.b, a);
            }
        tex.SetPixels32(px);
        tex.Apply(true, true);
        return tex;
    }

    // ------------------------------------------------------------------ the stereo "Up" arrow

    // The "Up" tile's picture: a side-by-side stereo pair, like a thumbnail, drawn here once. An
    // arrow leaning towards you (its base on the panel's surface, its tip a few centimetres in front),
    // casting a soft shadow on a faintly dotted background at the depth of a photo's far distance.
    // Disparities are in pixels of each eye's picture; the StereoThumb shader's window shift moves
    // everything back by 2 x Thumbnail Depth x the width, so the arrow's base cancels that exactly.
    Texture2D UpArrowTexture()
    {
        if (upArrowTexture != null && Mathf.Approximately(upArrowDepth, ThumbnailDepth)) return upArrowTexture;
        if (upArrowTexture != null) Destroy(upArrowTexture);
        upArrowDepth = ThumbnailDepth;

        int ew = VR180Thumbnails.EyeWidth, h = VR180Thumbnails.Height, w = ew * 2;
        float windowPx = StereoThumbnails ? 2f * ThumbnailDepth * ew : 0f;
        float baseDisp = windowPx, tipDisp = windowPx + 7f;   // crossed disparity (left eye's picture to the right)
        const float ShaftBottom = 62f, HeadBase = 150f, Tip = 238f;
        float cx = ew * 0.5f;
        var shape = new[]
        {
            new Vector2(cx - 24f, ShaftBottom), new Vector2(cx + 24f, ShaftBottom), new Vector2(cx + 24f, HeadBase),
            new Vector2(cx + 68f, HeadBase), new Vector2(cx, Tip), new Vector2(cx - 68f, HeadBase), new Vector2(cx - 24f, HeadBase)
        };
        var shadowOffset = new Vector2(10f, -13f);
        var light = new Vector2(-0.6f, 0.8f);   // from the upper left
        Color bgLow = new Color(0.10f, 0.115f, 0.14f), bgHigh = new Color(0.19f, 0.21f, 0.26f);
        Color arrowBase = new Color(0.47f, 0.58f, 0.74f), arrowTip = new Color(0.80f, 0.88f, 0.98f);

        var px = new Color32[w * h];
        for (int eye = 0; eye < 2; eye++)
            for (int y = 0; y < h; y++)
            {
                float t = Mathf.Clamp01((y + 0.5f - ShaftBottom) / (Tip - ShaftBottom));
                float disp = Mathf.Lerp(baseDisp, tipDisp, t);
                float shift = eye == 0 ? disp * 0.5f : -disp * 0.5f;
                for (int x = 0; x < ew; x++)
                {
                    var c = new Vector2(x + 0.5f, y + 0.5f);

                    // Background: a gentle gradient, a faint dot grid (gives it a depth), and the shadow
                    Color col = Color.Lerp(bgLow, bgHigh, c.y / h);
                    Vector2 g = new Vector2(Mathf.Repeat(c.x - ew * 0.5f, 20f) - 10f, Mathf.Repeat(c.y, 20f) - 10f);
                    col = Color.Lerp(col, Color.white, 0.16f * Mathf.Clamp01(1.6f - g.magnitude));
                    float ds = PolygonDistance(c - shadowOffset, shape);
                    col = Color.Lerp(col, Color.black, 0.5f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(9f, -9f, ds)));

                    // The arrow, shifted for this eye
                    Vector2 pa = c - new Vector2(shift, 0f);
                    float da = PolygonDistance(pa, shape);
                    if (da < 1f)
                    {
                        Color fill = Color.Lerp(arrowBase, arrowTip, t);
                        if (da > -6f)
                        {
                            // A bevelled edge: lighter where it faces the light, darker where it faces away
                            var n = new Vector2(PolygonDistance(pa + Vector2.right, shape) - PolygonDistance(pa - Vector2.right, shape),
                                                PolygonDistance(pa + Vector2.up, shape) - PolygonDistance(pa - Vector2.up, shape));
                            if (n.sqrMagnitude > 1e-6f)
                            {
                                float k = Vector2.Dot(n.normalized, light) * (1f - Mathf.Clamp01(-da / 6f));
                                fill = k > 0f ? Color.Lerp(fill, Color.white, 0.45f * k) : Color.Lerp(fill, Color.black, -0.35f * k);
                            }
                        }
                        col = Color.Lerp(col, fill, Mathf.Clamp01(0.5f - da));
                    }
                    col.a = 1f;
                    px[y * w + eye * ew + x] = col;
                }
            }

        upArrowTexture = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "VR180 up arrow", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
        upArrowTexture.SetPixels32(px);
        upArrowTexture.Apply(true, true);
        return upArrowTexture;
    }

    // Signed distance from p to a polygon's edge (negative inside), in the polygon's units
    static float PolygonDistance(Vector2 p, Vector2[] v)
    {
        float d = Vector2.Dot(p - v[0], p - v[0]);
        float sign = 1f;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i, i++)
        {
            Vector2 e = v[j] - v[i], wv = p - v[i];
            Vector2 b = wv - e * Mathf.Clamp01(Vector2.Dot(wv, e) / Vector2.Dot(e, e));
            d = Mathf.Min(d, Vector2.Dot(b, b));
            bool c1 = p.y >= v[i].y, c2 = p.y < v[j].y, c3 = e.x * wv.y > e.y * wv.x;
            if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) sign = -sign;
        }
        return sign * Mathf.Sqrt(d);
    }

    static bool InPolygon(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }

    // Lifts a tile towards the viewer while the ray is over it
    class TileHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public RectTransform lift;
        public RawImage shadow;
        public Action<bool> changed;    // photo tiles: tells the picker which photo is being pointed at
        bool over;
        float t;

        public void OnPointerEnter(PointerEventData e) { over = true; changed?.Invoke(true); }
        public void OnPointerExit(PointerEventData e) { over = false; changed?.Invoke(false); }
        void OnDisable() { if (over) changed?.Invoke(false); over = false; t = 0f; Apply(); }

        void Update()
        {
            float target = over ? 1f : 0f;
            if (Mathf.Approximately(t, target)) return;
            t = Mathf.MoveTowards(t, target, Time.unscaledDeltaTime / 0.12f);
            Apply();
        }

        void Apply()
        {
            if (lift == null) return;
            float e = Mathf.SmoothStep(0f, 1f, t);
            lift.localPosition = new Vector3(lift.localPosition.x, lift.localPosition.y, -HoverLift * e);
            lift.localScale = Vector3.one * Mathf.Lerp(1f, HoverScale, e);
            if (shadow != null) shadow.color = new Color(0f, 0f, 0f, 0.55f * e);
        }
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
        DeletePhotos(tidyList, $"tidy in {folder}");
        tidyList = null;
        Refresh();
    }

    // Deletes the files, and tells everything that knew about them (gallery, viewer, thumbnails, favourites, marks)
    void DeletePhotos(List<string> paths, string what)
    {
        var deleted = new List<string>();
        int failed = 0;
        foreach (string p in paths)
        {
            try { File.Delete(p); deleted.Add(p); }
            catch (Exception e) { failed++; Debug.LogWarning($"VR180Viewer [picker]: couldn't delete {p}: {e.Message}"); }
        }
        Debug.Log($"VR180Viewer [picker]: {what}: deleted {deleted.Count} photo(s){(failed > 0 ? $", {failed} failed" : "")}");
        ForgetInGallery(deleted);
        viewer.ForgetFiles(deleted);
        VR180Thumbnails.Forget(deleted);
        VR180Favourites.Deleted(deleted, root);
        marked.ExceptWith(deleted);
        infoCache.Clear();
        notice = $"Deleted {deleted.Count} photo{(deleted.Count == 1 ? "" : "s")}." + (failed > 0 ? $" {failed} couldn't be deleted." : "");
    }

    // ------------------------------------------------------------------ delete the marked photos

    void ShowDeleteConfirmation()
    {
        int n = deleteList.Count;
        string photos = $"{n} photo{(n == 1 ? "" : "s")}";
        int favs = deleteList.Count(VR180Favourites.IsFavourite);
        var dirs = deleteList.Select(p => Path.GetDirectoryName(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool onView = highlightPath != null && deleteList.Any(p => SamePath(p, highlightPath));
        titleText.text = $"Delete {photos}?";
        string text = dirs.Count == 1
            ? $"The {(n == 1 ? "photo" : photos)} you marked in {DisplayPath(dirs[0])}."
            : $"The {photos} you marked, in {dirs.Count} folders.";
        if (favs > 0) text += favs == n ? (n == 1 ? " It's a favourite." : " They're all favourites.")
                                        : $" {favs} of them {(favs == 1 ? "is a favourite" : "are favourites")}.";
        if (onView) text += n == 1 ? " It's the photo you're looking at." : " One is the photo you're looking at.";
        text += VR180Viewer.OnQuest
            ? "\nDeleting can't be undone: there's no recycle bin on the Quest. Only do this if you have copies elsewhere, such as on your PC."
            : "\nDeleting can't be undone: the files don't go to the Recycle Bin.";
        statusText.text = text;
        AddRow($"Delete {photos} ({SizeText(TotalBytes(deleteList))})", DeleteMarked);
        Tint(rows[rows.Count - 1], DangerColour);
        AddRow("Cancel (keep the marks)", () => { deleteList = null; Refresh(); }, true);
        AddRow("Cancel and clear the marks", () => { deleteList = null; marked.Clear(); Refresh(); });
    }

    void DeleteMarked()
    {
        DeletePhotos(deleteList, "marked photos");
        deleteList = null;
        if (!Directory.Exists(folder)) folder = root;
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

    void UpdateCameraRow()
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
        tidyList = null;
        deleteList = null;
        copyList = null;
        Refresh();
    }

    static int CountPhotos(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Count(IsPhotoFile); }
        catch { return 0; }
    }

    static bool IsPhotoFile(string f) => VR180Viewer.IsPhotoFile(f);

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

        // The main area (folders and photos), on the right
        mainArea = NewUI("Main", rt);
        Stretch(mainArea);
        mainArea.offsetMin = new Vector2(SideWidth + SeparatorGap, 0f);
        mainArea.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.08f, 0.10f, 0.94f);

        titleText = NewText("Title", mainArea, 44, FontStyles.Bold, new Color(0.95f, 0.95f, 0.95f));
        Place(titleText.rectTransform, top: Pad, height: TitleHeight, left: Pad, right: Pad);
        titleText.alignment = TextAlignmentOptions.MidlineLeft;
        titleText.textWrappingMode = TextWrappingModes.NoWrap;
        titleText.overflowMode = TextOverflowModes.Ellipsis;

        float gridTop = Pad + TitleHeight + 12f, gridBottom = Pad + StatusHeight;
        gridViewport = NewUI("Grid", mainArea);
        Stretch(gridViewport);
        gridViewport.offsetMin = new Vector2(Pad, gridBottom);
        gridViewport.offsetMax = new Vector2(-(Pad + 26f), -gridTop);
        gridViewport.gameObject.AddComponent<RectMask2D>().padding = new Vector4(-20f, -10f, -20f, -10f);   // room for lifted tiles

        gridContent = NewUI("Content", gridViewport);
        gridContent.anchorMin = gridContent.anchorMax = gridContent.pivot = new Vector2(0f, 1f);
        gridContent.sizeDelta = new Vector2(Columns * TileW + (Columns - 1) * GapX, 0f);

        scrollTrack = NewUI("Scroll bar", mainArea);
        scrollTrack.anchorMin = new Vector2(1f, 0f);
        scrollTrack.anchorMax = new Vector2(1f, 1f);
        scrollTrack.pivot = new Vector2(1f, 1f);
        scrollTrack.offsetMin = new Vector2(-(Pad + 8f), gridBottom);
        scrollTrack.offsetMax = new Vector2(-Pad, -gridTop);
        scrollTrack.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);
        scrollThumb = NewUI("Thumb", scrollTrack);
        scrollThumb.anchorMin = new Vector2(0f, 1f);
        scrollThumb.anchorMax = new Vector2(1f, 1f);
        scrollThumb.pivot = new Vector2(0.5f, 1f);
        scrollThumb.sizeDelta = new Vector2(0f, 100f);
        scrollThumb.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.35f);

        // Confirmations and progress: a list in the grid's place
        listArea = NewUI("List", mainArea);
        Stretch(listArea);
        listArea.offsetMin = new Vector2(Pad, gridBottom);
        listArea.offsetMax = new Vector2(-Pad, -gridTop);
        var listLayout = listArea.gameObject.AddComponent<VerticalLayoutGroup>();
        listLayout.spacing = 10;
        listLayout.childControlWidth = listLayout.childControlHeight = true;
        listLayout.childForceExpandWidth = true;
        listLayout.childForceExpandHeight = false;

        statusText = NewText("Status", mainArea, 28, FontStyles.Normal, new Color(0.7f, 0.72f, 0.76f));
        Place(statusText.rectTransform, bottom: Pad * 0.5f, height: StatusHeight + Pad * 0.5f, left: Pad, right: Pad);
        statusText.alignment = TextAlignmentOptions.MidlineLeft;
        statusText.textWrappingMode = TextWrappingModes.Normal;
        statusText.overflowMode = TextOverflowModes.Overflow;

        // The side column (actions), a little nearer, set off by an accent bar
        var side = NewUI("Side", rt);
        Stretch(side);
        side.offsetMax = new Vector2(-(PanelWidth - SideWidth), 0f);
        side.localPosition = new Vector3(side.localPosition.x, side.localPosition.y, -SideDepth);
        side.gameObject.AddComponent<Image>().color = new Color(0.10f, 0.11f, 0.14f, 0.96f);
        var sideLayout = side.gameObject.AddComponent<VerticalLayoutGroup>();
        sideLayout.padding = new RectOffset(22, 22, (int)Pad, (int)Pad);
        sideLayout.spacing = 12;
        sideLayout.childControlWidth = sideLayout.childControlHeight = true;
        sideLayout.childForceExpandWidth = true;
        sideLayout.childForceExpandHeight = false;

        var heading = NewText("Heading", side, 34, FontStyles.Bold, new Color(0.80f, 0.83f, 0.88f));
        heading.text = "VR180 Viewer";
        heading.gameObject.AddComponent<LayoutElement>().preferredHeight = TitleHeight;

        sideList = NewUI("Actions", side);
        var sideListLayout = sideList.gameObject.AddComponent<VerticalLayoutGroup>();
        sideListLayout.spacing = 12;
        sideListLayout.childControlWidth = sideListLayout.childControlHeight = true;
        sideListLayout.childForceExpandWidth = true;
        sideListLayout.childForceExpandHeight = false;
        sideList.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1;

        // Exit always sits at the very bottom of the column
        var exit = MakeRow(side, "Exit VR180 Viewer", QuitApp, false, null, 0.8f);
        Tint(exit, DangerColour);

        var bar = NewUI("Separator", rt);
        bar.anchorMin = new Vector2(0f, 0.08f);
        bar.anchorMax = new Vector2(0f, 0.92f);
        bar.sizeDelta = new Vector2(6f, 0f);
        bar.anchoredPosition = new Vector2(SideWidth + SeparatorGap * 0.5f, 0f);
        bar.localPosition = new Vector3(bar.localPosition.x, bar.localPosition.y, -SideDepth * 0.5f);
        bar.gameObject.AddComponent<Image>().color = new Color(AccentColour.r, AccentColour.g, AccentColour.b, 0.8f);
    }

    // Sets a child's rectangle from its parent's edges (top or bottom, with a height; left and right insets)
    static void Place(RectTransform r, float top = -1f, float bottom = -1f, float height = 0f, float left = 0f, float right = 0f)
    {
        bool fromTop = top >= 0f;
        r.anchorMin = new Vector2(0f, fromTop ? 1f : 0f);
        r.anchorMax = new Vector2(1f, fromTop ? 1f : 0f);
        r.pivot = new Vector2(0.5f, fromTop ? 1f : 0f);
        r.offsetMin = new Vector2(left, 0f);
        r.offsetMax = new Vector2(-right, 0f);
        r.sizeDelta = new Vector2(r.sizeDelta.x, height);
        r.anchoredPosition = new Vector2(r.anchoredPosition.x, fromTop ? -top : bottom);
    }

    // A row in the list (confirmations, progress, permission)
    void AddRow(string label, Action onClick, bool highlight = false, string detail = null) =>
        rows.Add(MakeRow(listArea, label, onClick, highlight, detail));

    // A row in the side column (its text wraps, and the row grows to fit)
    void AddSideRow(string label, Action onClick, bool highlight = false, string detail = null) =>
        sideRows.Add(MakeRow(sideList, label, onClick, highlight, detail, 0.8f, true));

    // Extra space with a thin gold bar across it, between the favourites and the rest
    void AddSideDivider()
    {
        var gap = NewUI("Divider", sideList);
        gap.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
        var bar = NewUI("Bar", gap);
        bar.anchorMin = new Vector2(0.04f, 0.5f);
        bar.anchorMax = new Vector2(0.96f, 0.5f);
        bar.sizeDelta = new Vector2(0f, 4f);
        bar.anchoredPosition = Vector2.zero;
        var image = bar.gameObject.AddComponent<Image>();
        image.color = new Color(FavouriteColour.r * 1.6f, FavouriteColour.g * 1.6f, FavouriteColour.b * 1.6f, 0.9f);
        image.raycastTarget = false;
        sideRows.Add(gap.gameObject);
    }

    // With a detail, the row shows the label above a smaller grey line and a ">" on the right
    // With wrap, the label and detail wrap onto more lines and the row grows to fit them (side column)
    GameObject MakeRow(Transform parent, string label, Action onClick, bool highlight, string detail, float scale = 1f, bool wrap = false)
    {
        var row = NewUI("Row", parent);
        if (wrap) return MakeWrappingRow(row, label, onClick, highlight, detail, scale);
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

        float inset = 28f * scale, arrowRoom = 70f * scale;
        var text = NewText("Label", row, (detail == null ? 38 : 34) * scale, FontStyles.Normal, Color.white);
        Stretch(text.rectTransform);
        text.rectTransform.offsetMin = new Vector2(inset, 0);
        text.rectTransform.offsetMax = new Vector2(detail == null ? -inset : -arrowRoom, 0);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.text = label;

        if (detail != null)
        {
            text.rectTransform.anchorMin = new Vector2(0f, 0.42f);
            text.rectTransform.offsetMax = new Vector2(-arrowRoom, -4);

            var sub = NewText("Detail", row, 26 * scale, FontStyles.Normal, new Color(0.72f, 0.75f, 0.80f));
            Stretch(sub.rectTransform);
            sub.rectTransform.anchorMax = new Vector2(1f, 0.46f);
            sub.rectTransform.offsetMin = new Vector2(inset, 6);
            sub.rectTransform.offsetMax = new Vector2(-arrowRoom, 0);
            sub.alignment = TextAlignmentOptions.TopLeft;
            sub.textWrappingMode = TextWrappingModes.NoWrap;
            sub.overflowMode = TextOverflowModes.Ellipsis;
            sub.text = detail;

            var arrow = NewText("Arrow", row, 40 * scale, FontStyles.Bold, Color.white);
            Stretch(arrow.rectTransform);
            arrow.rectTransform.offsetMax = new Vector2(-inset, 0);
            arrow.alignment = TextAlignmentOptions.MidlineRight;
            arrow.text = ">";
        }

        return row.gameObject;
    }

    // The side column's row: label and detail stacked by a layout group, wrapping at the row's
    // width, so the row is as tall as its text needs (at least the usual 84 units)
    GameObject MakeWrappingRow(RectTransform row, string label, Action onClick, bool highlight, string detail, float scale)
    {
        row.gameObject.AddComponent<LayoutElement>().minHeight = 84;
        var image = row.gameObject.AddComponent<Image>();
        Color baseColour = highlight ? new Color(0.20f, 0.36f, 0.55f) : new Color(0.17f, 0.19f, 0.24f);
        image.color = Color.white;
        var button = row.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var colours = button.colors;
        colours.normalColor = baseColour;
        colours.highlightedColor = baseColour * 1.45f;
        colours.selectedColor = baseColour;
        colours.pressedColor = baseColour * 0.7f;
        colours.colorMultiplier = 1f;
        button.colors = colours;
        button.onClick.AddListener(() => onClick?.Invoke());

        int inset = Mathf.RoundToInt(28f * scale), arrowRoom = Mathf.RoundToInt(70f * scale);
        var layout = row.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(inset, detail == null ? inset : arrowRoom, 14, 14);
        layout.spacing = 4;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var text = NewText("Label", row, (detail == null ? 38 : 34) * scale, FontStyles.Normal, Color.white);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Overflow;
        text.text = label;

        if (detail != null)
        {
            var sub = NewText("Detail", row, 26 * scale, FontStyles.Normal, new Color(0.72f, 0.75f, 0.80f));
            sub.alignment = TextAlignmentOptions.TopLeft;
            sub.textWrappingMode = TextWrappingModes.Normal;
            sub.overflowMode = TextOverflowModes.Overflow;
            sub.text = detail;

            var arrow = NewText("Arrow", row, 40 * scale, FontStyles.Bold, Color.white);
            arrow.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            Stretch(arrow.rectTransform);
            arrow.rectTransform.offsetMax = new Vector2(-inset, 0);
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
