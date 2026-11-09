// VR180FlatExport: frame part of the current VR180 photo and save it as an ordinary flat
// JPEG for sharing (WhatsApp etc.). Created at runtime by VR180Viewer.
//
// Left trigger: enter framing mode. A 16:9 frame follows your gaze; outside it is dimmed.
//   Left thumbstick up/down zooms the frame (narrower/wider field of view).
//   Left trigger again: capture. Left grip: cancel.
//   (Editor: F frame/capture, Escape cancel, = and - zoom.)
//
// The export is rendered from the full-resolution photo (re-decoded from disk), not from
// the headset view: the left eye, through a level virtual camera shifted up or down to
// where you're looking, so vertical lines stay vertical. Pictures go to Pictures/VR180 Flat
// on the Quest (visible over USB) or to "VR180 Flat" beside the PC photo folder.

using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.UI;

public class VR180FlatExport : MonoBehaviour
{
    const float Aspect = 9f / 16f;           // height / width
    const float FrameDistance = 3f;          // metres; where the viewfinder frame is drawn
    const float MinFov = 25f, MaxFov = 110f; // horizontal field of view limits (degrees)
    const float MaxPitch = 50f;              // furthest up/down the shifted frame can go
    const int MinWidth = 1920, MaxWidth = 4096;
    const int JpegQuality = 92;

    public float FieldOfView { get; private set; } = 70f;
    public bool IsActive => state != State.Idle;

    enum State { Idle, Framing, Saving }
    State state = State.Idle;

    VR180Viewer viewer;
    string outputFolder;
    Material exportMaterial;
    InputAction frameAction, cancelAction, zoomAction;

    Canvas canvas;
    RectTransform frameRect;
    TextMeshProUGUI infoText;
    float messageUntil;

    // Framing captured at the moment of the shot
    float shotYaw, shotShift;

    // ------------------------------------------------------------------ setup

    public void Init(VR180Viewer owner, string folder)
    {
        viewer = owner;
        outputFolder = folder;

        var shader = Resources.Load<Shader>("VR180FlatExport");
        if (shader == null) Debug.LogError("VR180Viewer [flat]: Hidden/VR180/FlatExport shader not found in a Resources folder.");
        else exportMaterial = new Material(shader);

        frameAction = new InputAction("FlatFrame", InputActionType.Button);
        frameAction.AddBinding("<XRController>{LeftHand}/{TriggerButton}");
        frameAction.AddBinding("<Keyboard>/f");
        cancelAction = new InputAction("FlatCancel", InputActionType.Button);
        cancelAction.AddBinding("<XRController>{LeftHand}/{GripButton}");
        cancelAction.AddBinding("<Keyboard>/escape");
        zoomAction = new InputAction("FlatZoom", InputActionType.Value);
        zoomAction.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
        frameAction.Enable();
        cancelAction.Enable();
        zoomAction.Enable();

        BuildOverlay();
        canvas.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        frameAction?.Dispose();
        cancelAction?.Dispose();
        zoomAction?.Dispose();
        if (exportMaterial != null) Destroy(exportMaterial);
        if (canvas != null) Destroy(canvas.gameObject);
    }

    // ------------------------------------------------------------------ framing

    void Update()
    {
        bool framePressed = frameAction.WasPressedThisFrame();
        if (framePressed) Debug.Log($"VR180Viewer [input]: FlatFrame pressed via {frameAction.activeControl?.path}");

        switch (state)
        {
            case State.Idle:
                if (framePressed && viewer.CanStartFraming && exportMaterial != null) BeginFraming();
                break;

            case State.Framing:
                float zoom = zoomAction.ReadValue<Vector2>().y;
                var kb = Keyboard.current;
                if (kb != null)
                {
                    if (kb.equalsKey.isPressed) zoom += 1f;
                    if (kb.minusKey.isPressed) zoom -= 1f;
                }
                if (Mathf.Abs(zoom) > 0.15f)
                    FieldOfView = Mathf.Clamp(FieldOfView * Mathf.Pow(0.5f, zoom * Time.deltaTime), MinFov, MaxFov);   // up = zoom in

                UpdateFrame();
                if (Time.unscaledTime > messageUntil)
                    infoText.text = $"{FieldOfView:F0}°   trigger: save   grip: cancel   stick: zoom";

                if (cancelAction.WasPressedThisFrame()) EndFraming();
                else if (framePressed) StartCoroutine(Capture());
                break;
        }
    }

    void BeginFraming()
    {
        state = State.Framing;
        viewer.SetFraming(true);
        canvas.gameObject.SetActive(true);
        messageUntil = 0f;
        UpdateFrame();
        Debug.Log("VR180Viewer [flat]: framing");
    }

    void EndFraming()
    {
        state = State.Idle;
        canvas.gameObject.SetActive(false);
        viewer.SetFraming(false);
    }

    // Current level camera direction (yaw) and vertical shift from where the head points
    void GetFraming(out float yawDeg, out float shift)
    {
        Vector3 f = viewer.Head.forward;
        yawDeg = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg, -MaxPitch, MaxPitch);
        shift = Mathf.Tan(pitch * Mathf.Deg2Rad);
    }

    // Places the frame where the exported picture's edges are: a rectangle on a vertical
    // plane straight ahead (level), shifted up or down to the gaze
    void UpdateFrame()
    {
        GetFraming(out float yawDeg, out float shift);
        Quaternion cam = Quaternion.Euler(0f, yawDeg, 0f);
        float halfW = Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) * FrameDistance;
        Vector3 centre = viewer.Head.position + cam * new Vector3(0f, shift, 1f) * FrameDistance;

        canvas.transform.SetPositionAndRotation(centre, cam);
        frameRect.sizeDelta = new Vector2(halfW * 2f, halfW * 2f * Aspect) * 1000f;   // canvas units are mm
    }

    // ------------------------------------------------------------------ capture

    IEnumerator Capture()
    {
        state = State.Saving;
        GetFraming(out shotYaw, out shotShift);
        float fov = FieldOfView;
        string source = viewer.CurrentPhotoPath;
        Vector4 cov = viewer.CurrentCoverage;
        bool sbs = viewer.CurrentSideBySide;
        Quaternion camToImage = Quaternion.Inverse(viewer.ImageToWorld) * Quaternion.Euler(0f, shotYaw, 0f);
        ShowMessage("Saving...", 60f);
        float t0 = Time.realtimeSinceStartup;

        // Full-resolution photo, decoded off the main thread
        Texture src;
        using (var req = UnityWebRequestTexture.GetTexture(new Uri(source).AbsoluteUri, true))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Fail($"couldn't read {Path.GetFileName(source)}: {req.error}");
                yield break;
            }
            src = VR180Fisheye.ConvertIfRaw(DownloadHandlerTexture.GetContent(req), source);   // raw QooCam photos
        }

        // Size the picture so its centre has about one output pixel per source pixel
        float tanHalf = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        float eyeWidth = sbs ? src.width * 0.5f : src.width;
        float pixelsPerRadian = eyeWidth / Mathf.Max(cov.y - cov.x, 0.01f);
        int w = Mathf.Clamp(Mathf.RoundToInt(2f * tanHalf * pixelsPerRadian / 2f) * 2, MinWidth, MaxWidth);
        int h = Mathf.RoundToInt(w * Aspect / 2f) * 2;

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        exportMaterial.SetMatrix("_CamToImage", Matrix4x4.Rotate(camToImage));
        exportMaterial.SetVector("_Coverage", cov);
        exportMaterial.SetFloat("_StereoSBS", sbs ? 1f : 0f);
        exportMaterial.SetFloat("_TanHalf", tanHalf);
        exportMaterial.SetFloat("_Aspect", Aspect);
        exportMaterial.SetFloat("_Shift", shotShift);
        exportMaterial.SetFloat("_FlipY", viewer.flipFlatExport ? 1f : 0f);
        var adj = viewer.CurrentAdjustments;   // the photo's saved brightness, contrast and saturation
        exportMaterial.SetFloat("_Exposure", adj.exposure);
        exportMaterial.SetFloat("_Contrast", adj.contrast);
        exportMaterial.SetFloat("_Saturation", adj.saturation);
        Graphics.Blit(src, rt, exportMaterial);
        Destroy(src);

        // Read the pixels back without stalling the headset
        var readback = AsyncGPUReadback.Request(rt);
        while (!readback.done) yield return null;
        RenderTexture.ReleaseTemporary(rt);
        if (readback.hasError)
        {
            Fail("couldn't read the rendered picture back from the GPU");
            yield break;
        }
        byte[] raw = readback.GetData<byte>().ToArray();

        // Encode and write on a background thread
        string path = NextFileName(source);
        var task = Task.Run(() =>
        {
            byte[] jpg = ImageConversion.EncodeArrayToJPG(raw, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, JpegQuality);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, jpg);
        });
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted)
        {
            Fail(task.Exception?.GetBaseException().Message ?? "couldn't save");
            yield break;
        }

        AnnounceToGallery(path);
        Debug.Log($"VR180Viewer [flat]: saved {path} ({w}x{h}, {fov:F0} deg, shift {shotShift:F2}) in {Time.realtimeSinceStartup - t0:F2}s");
        ShowMessage("Saved " + Path.GetFileName(path), 2.5f);
        yield return new WaitForSecondsRealtime(2.5f);
        EndFraming();
    }

    void Fail(string why)
    {
        Debug.LogWarning("VR180Viewer [flat]: " + why);
        ShowMessage("Couldn't save: " + why, 4f);
        state = State.Framing;
    }

    void ShowMessage(string text, float seconds)
    {
        infoText.text = text;
        messageUntil = Time.unscaledTime + seconds;
    }

    // <photo name>_flat_01.jpg, _02 ... in the output folder
    string NextFileName(string source)
    {
        string stem = Path.GetFileNameWithoutExtension(source);
        for (int n = 1; ; n++)
        {
            string path = Path.Combine(outputFolder, $"{stem}_flat_{n:00}.jpg");
            if (!File.Exists(path)) return path;
        }
    }

    // Makes the new picture show up straight away over USB and in gallery apps
    static void AnnounceToGallery(string path)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var scanner = new AndroidJavaClass("android.media.MediaScannerConnection"))
                scanner.CallStatic("scanFile", UnityEngine.Android.AndroidApplication.currentActivity,
                    new[] { path }, new[] { "image/jpeg" }, new ScanListener());
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [flat]: media scan failed (the file is still saved): {e.Message}");
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

    // ------------------------------------------------------------------ viewfinder overlay

    void BuildOverlay()
    {
        var go = new GameObject("VR180 Flat Viewfinder", typeof(RectTransform), typeof(Canvas));
        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        var root = (RectTransform)go.transform;
        root.sizeDelta = Vector2.zero;
        root.localScale = Vector3.one * 0.001f;

        frameRect = NewRect("Frame", root);
        frameRect.anchorMin = frameRect.anchorMax = new Vector2(0.5f, 0.5f);

        // Dim everything outside the frame (big panels around it)
        const float big = 40000f;
        var dim = new Color(0f, 0f, 0f, 0.55f);
        Panel(frameRect, new Vector2(0, 1), new Vector2(1, 1), new Vector2(-big, 0), new Vector2(big, big), dim);   // above
        Panel(frameRect, new Vector2(0, 0), new Vector2(1, 0), new Vector2(-big, -big), new Vector2(big, 0), dim);  // below
        Panel(frameRect, new Vector2(0, 0), new Vector2(0, 1), new Vector2(-big, 0), new Vector2(0, 0), dim);       // left
        Panel(frameRect, new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 0), new Vector2(big, 0), dim);        // right

        // Thin white border just inside the frame
        const float t = 10f;
        var line = new Color(1f, 1f, 1f, 0.9f);
        Panel(frameRect, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -t), new Vector2(0, 0), line);
        Panel(frameRect, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, t), line);
        Panel(frameRect, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0), new Vector2(t, 0), line);
        Panel(frameRect, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-t, 0), new Vector2(0, 0), line);

        // Instructions and messages, just below the frame
        infoText = NewRect("Info", frameRect).gameObject.AddComponent<TextMeshProUGUI>();
        var r = infoText.rectTransform;
        r.anchorMin = new Vector2(0, 0);
        r.anchorMax = new Vector2(1, 0);
        r.pivot = new Vector2(0.5f, 1f);
        r.offsetMin = new Vector2(0, -160);
        r.offsetMax = new Vector2(0, -30);
        infoText.fontSize = 80;
        infoText.alignment = TextAlignmentOptions.Top;
        infoText.color = Color.white;
        infoText.raycastTarget = false;
    }

    static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    static void Panel(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color colour)
    {
        var rt = NewRect("Panel", parent);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        var img = rt.gameObject.AddComponent<Image>();
        img.color = colour;
        img.raycastTarget = false;
    }
}
