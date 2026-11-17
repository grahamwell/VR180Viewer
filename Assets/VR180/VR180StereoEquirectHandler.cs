// VR180StereoEquirectHandler: replaces Unity's OpenXR handler for Equirect composition layers.
//
// Why:
//  - In OpenXR 1.16.1, Unity's equirect handler ignores the Source Textures "Individual" setting
//    and sends the left texture to both eyes, so a stereo photo shows in mono. This handler marks
//    each layer as left-eye-only or right-eye-only (OpenXR eyeVisibility); the viewer uses two layers.
//  - Unity's handler also flips the sign of the Lower Vertical Angle, which no longer matches
//    Composition Layers 2.6. This one passes the angles through unchanged.
//  - Unity's base class copies a RenderTexture source into the layer's swapchain every frame.
//    For a still photo that is wasted work, and it forces us to keep two full-size eye textures
//    alive. This handler manages the swapchain images itself: it writes each swapchain image once
//    per photo, then reports the photo as "resident" so the viewer can free its eye textures.
//
// Usage from the viewer:
//   Install() once; SetEye(layer, Eye.Left/Right) for each layer;
//   SetImage(layer, texture) whenever a new photo's eye texture is ready (before showing the layer);
//   IsResident(layer) becomes true once every swapchain image holds the photo;
//   then ReleaseImage(layer) and free the texture.
//
// It is only installed when the runtime supports XR_KHR_composition_layer_equirect2 (the Quest).
//
// Zoom (11 November 2026): the viewer can ask for part of the image over different angles
// (SetZoom); applied every frame in ActiveNativeLayer, so the write-once swapchain is untouched.
// Over Link that extension is missing, so Unity's own handler stays in charge.
//
// Layer settings (experiment, 12 November 2026): XR_FB_composition_layer_settings lets an app ask
// the Quest's compositor to sharpen and/or supersample a layer. When the runtime has enabled the
// extension (the "VR180 Layer Sharpening" OpenXR feature asks for it) and SettingsFlags is not 0,
// an XrCompositionLayerSettingsFB is chained onto each layer's next pointer every frame. Done with
// MemoryMarshal on the layer struct's bytes, so no unsafe code is needed. Whether the Quest honours
// it for equirect layers (it's documented for quad and cylinder layers) is what we're testing.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using Unity.XR.CompositionLayers.Services;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.CompositionLayers;
using UnityEngine.XR.OpenXR.NativeTypes;

public class VR180StereoEquirectHandler : OpenXRCustomLayerHandler<XrCompositionLayerEquirect2KHR>
{
    public enum Eye : uint { Both = 0, Left = 1, Right = 2 }   // OpenXR XrEyeVisibility values

    // Frames with no new swapchain image appearing before we trust that every image has been
    // written (swapchains rotate through a few images, typically 3)
    const int FramesBeforeShowing = 6;
    const int FramesBeforeResident = 30;

    class LayerState
    {
        public Eye eye = Eye.Both;
        public Texture image;               // source for the current photo; null once released
        public int width, height;           // size of the current photo's eye image
        public int swapW, swapH;            // size the swapchain was created at (photos are scaled to fit)
        public readonly HashSet<uint> written = new HashSet<uint>();   // swapchain images holding it
        public int quietFrames;             // frames since a not-yet-written image last appeared
        public int layerId = -1;
        public bool requestedThisFrame;
        public bool loggedResident;
        // Zoom (set by the viewer every frame): show only part of the image, over these angles
        public bool zoomed;
        public Rect crop;                   // part of the image: x from the left, y from the photo's top, 0..1
        public float central, upper, lower; // radians, as in the layer data
    }

    static readonly Dictionary<CompositionLayer, LayerState> s_States = new Dictionary<CompositionLayer, LayerState>();
    static readonly Dictionary<int, uint> s_CurrentTexIds = new Dictionary<int, uint>();   // filled on the graphics thread
    static readonly object s_TexIdLock = new object();
    static readonly OpenXRLayerUtility.RenderTextureIdCallbackDelegate s_OnTexId = OnRenderTextureId;
    static bool s_Hooked, s_RegisteredThisSession, s_BeforeRenderHooked;

    /// True once this handler has replaced Unity's for the current XR session.
    public static bool Active { get; private set; }

    /// The XR session ended (for example the headset went to sleep): the swapchains, and the photo
    /// in them, are gone. The viewer rebuilds the layer once Active is true again.
    public static event Action SessionStopped;

    // ------------------------------------------------------------------ layer settings (experiment)

    public const string LayerSettingsExtension = "XR_FB_composition_layer_settings";

    // XrCompositionLayerSettingsFlagsFB
    public const ulong NormalSuperSampling = 0x1, QualitySuperSampling = 0x2, NormalSharpening = 0x4, QualitySharpening = 0x8;

    /// Flags to ask for on both layers this frame (0 = none); set by the viewer every frame
    public static ulong SettingsFlags;

    /// True if the runtime enabled XR_FB_composition_layer_settings for this session
    public static bool SettingsSupported { get; private set; }

    const uint XR_TYPE_COMPOSITION_LAYER_SETTINGS_FB = 1000204000;
    static IntPtr s_Settings;          // one XrCompositionLayerSettingsFB (24 bytes), shared by both layers, kept for the app's life
    static int s_NextOffset = -1;      // where the layer struct's next pointer is
    static ulong s_LoggedFlags = ulong.MaxValue;
    static bool s_ChainWarned;

    // Puts our settings struct on (or takes it off) the layer's next chain
    static void ApplySettings(ref XrCompositionLayerEquirect2KHR layer)
    {
        if (s_NextOffset < 0)
        {
            try { s_NextOffset = (int)Marshal.OffsetOf(typeof(XrCompositionLayerEquirect2KHR), "Next"); }
            catch (Exception e)
            {
                s_NextOffset = IntPtr.Size == 8 ? 8 : 4;   // after the 32-bit type, aligned
                Debug.LogWarning($"VR180Viewer [layer]: couldn't find the layer's next field ({e.Message}); assuming offset {s_NextOffset}");
            }
        }
        Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref layer, 1));
        Span<byte> nextBytes = bytes.Slice(s_NextOffset, IntPtr.Size);
        IntPtr next = MemoryMarshal.Read<IntPtr>(nextBytes);
        ulong flags = SettingsSupported ? SettingsFlags : 0;

        if (flags != 0)
        {
            if (next != IntPtr.Zero && next != s_Settings)
            {
                if (!s_ChainWarned)
                {
                    s_ChainWarned = true;
                    Debug.LogWarning("VR180Viewer [layer]: the layer already has an extension chain; not adding the layer settings");
                }
                return;
            }
            if (s_Settings == IntPtr.Zero)
            {
                s_Settings = Marshal.AllocHGlobal(24);
                Marshal.WriteInt32(s_Settings, 0, (int)XR_TYPE_COMPOSITION_LAYER_SETTINGS_FB);
                Marshal.WriteInt32(s_Settings, 4, 0);
                Marshal.WriteIntPtr(s_Settings, 8, IntPtr.Zero);
            }
            Marshal.WriteInt64(s_Settings, 16, (long)flags);
            IntPtr ours = s_Settings;
            MemoryMarshal.Write(nextBytes, ref ours);
        }
        else if (next != IntPtr.Zero && next == s_Settings)
        {
            IntPtr none = IntPtr.Zero;
            MemoryMarshal.Write(nextBytes, ref none);
        }

        if (flags != s_LoggedFlags)
        {
            s_LoggedFlags = flags;
            Debug.Log($"VR180Viewer [layer]: layer settings {(flags == 0 ? "off" : "0x" + flags.ToString("X"))}");
        }
    }

    static LayerState State(CompositionLayer layer)
    {
        if (!s_States.TryGetValue(layer, out var s)) s_States[layer] = s = new LayerState();
        return s;
    }

    // ------------------------------------------------------------------ API for the viewer

    /// Which way the swapchain image's rows run relative to the photo: false = the first row is the
    /// photo's top (Vulkan, D3D), true = its bottom (OpenGL ES). Set by the viewer.
    public static bool CropFromBottom;

    /// Zoom: show only the crop part of the photo (x from the left, y from the photo's top, 0..1),
    /// over these angles (radians). Pass zoomed = false for the whole photo with the layer data's angles.
    public static void SetZoom(CompositionLayer layer, bool zoomed, Rect crop, float central, float upper, float lower)
    {
        if (layer == null) return;
        var s = State(layer);
        s.zoomed = zoomed;
        s.crop = crop;
        s.central = central;
        s.upper = upper;
        s.lower = lower;
    }

    public static void SetEye(CompositionLayer layer, Eye eye)
    {
        if (layer != null) State(layer).eye = eye;
    }

    /// Hands over a new photo's eye image. It is written into every swapchain image over the next
    /// few frames while the layer is showing; the layer itself is held back until that's done.
    public static void SetImage(CompositionLayer layer, Texture image)
    {
        if (layer == null || image == null) return;
        var s = State(layer);
        s.image = image;
        s.width = image.width;
        s.height = image.height;
        s.written.Clear();
        s.quietFrames = 0;
        s.loggedResident = false;
    }

    /// True once every swapchain image holds the current photo, so its source can be freed.
    public static bool IsResident(CompositionLayer layer) =>
        layer != null && s_States.TryGetValue(layer, out var s) && s.written.Count > 0 && s.quietFrames >= FramesBeforeResident;

    /// True once the layer is actually being submitted to the headset with the current photo.
    public static bool IsVisible(CompositionLayer layer) =>
        layer != null && layer.isActiveAndEnabled && s_States.TryGetValue(layer, out var s) &&
        s.written.Count > 0 && s.quietFrames >= FramesBeforeShowing;

    /// True if the layer can show the current photo (its swapchain holds it, source or not).
    public static bool HasImage(CompositionLayer layer) =>
        layer != null && s_States.TryGetValue(layer, out var s) && s.width > 0 && (s.image != null || s.written.Count > 0);

    /// Forgets the source texture (call before destroying it). The swapchain keeps the photo.
    public static void ReleaseImage(CompositionLayer layer)
    {
        if (layer != null && s_States.TryGetValue(layer, out var s)) s.image = null;
    }

    // ------------------------------------------------------------------ installation

    public static void Install()
    {
        if (!s_Hooked)
        {
            OpenXRLayerProvider.Started += Register;
            OpenXRLayerProvider.Stopped += OnStopped;
            s_Hooked = true;
        }
        if (OpenXRLayerProvider.isStarted) Register();
    }

    // The session ended (for example the headset went to sleep): its swapchains are gone, so
    // forget what was written to them. Eye assignments are kept for the next session.
    static void OnStopped()
    {
        Active = false;
        SettingsSupported = false;
        s_LoggedFlags = ulong.MaxValue;
        s_RegisteredThisSession = false;
        var dead = new List<CompositionLayer>();
        foreach (var kv in s_States)
        {
            if (kv.Key == null) { dead.Add(kv.Key); continue; }
            kv.Value.written.Clear();
            kv.Value.quietFrames = 0;
            kv.Value.swapW = kv.Value.swapH = 0;
            kv.Value.layerId = -1;
        }
        foreach (var k in dead) s_States.Remove(k);
        lock (s_TexIdLock) s_CurrentTexIds.Clear();
        Debug.Log("VR180Viewer [layer]: XR layer provider stopped");
        SessionStopped?.Invoke();
    }

    static void Register()
    {
        if (s_RegisteredThisSession) return;
        s_RegisteredThisSession = true;

        if (!OpenXRRuntime.IsExtensionEnabled("XR_KHR_composition_layer_equirect2"))
        {
            Active = false;
            Debug.Log("VR180Viewer [layer]: equirect2 not supported by this runtime; keeping Unity's equirect handler (mono)");
            return;
        }
        // Must be created after Unity's own handler: the base class routes swapchain callbacks
        // to the most recently created handler of this layer type.
        OpenXRLayerProvider.RegisterLayerHandler(typeof(EquirectMeshLayerData), new VR180StereoEquirectHandler());
        if (!s_BeforeRenderHooked)
        {
            Application.onBeforeRender += WriteSwapchainImages;
            s_BeforeRenderHooked = true;
        }
        Active = true;
        Debug.Log("VR180Viewer [layer]: stereo equirect handler installed (write-once swapchains)");
        SettingsSupported = OpenXRRuntime.IsExtensionEnabled(LayerSettingsExtension);
        Debug.Log(SettingsSupported
            ? "VR180Viewer [layer]: " + LayerSettingsExtension + " enabled, so the layers can be sharpened/supersampled"
            : "VR180Viewer [layer]: " + LayerSettingsExtension + " not enabled (tick \"VR180 Layer Sharpening\" in the Android OpenXR features, " +
              "or the runtime doesn't have it); layer filtering unavailable");
    }

    // ------------------------------------------------------------------ swapchain writing

    [AOT.MonoPInvokeCallback(typeof(OpenXRLayerUtility.RenderTextureIdCallbackDelegate))]
    static void OnRenderTextureId(int layerId, uint texId)
    {
        lock (s_TexIdLock) s_CurrentTexIds[layerId] = texId;
    }

    // Runs every frame before rendering: writes the photo into any swapchain image that
    // doesn't hold it yet, and counts quiet frames once all of them do.
    static void WriteSwapchainImages()
    {
        if (!Active) return;
        foreach (var kv in s_States)
        {
            var s = kv.Value;
            if (!s.requestedThisFrame || s.layerId < 0 || s.width == 0) continue;
            s.requestedThisFrame = false;

            uint texId;
            lock (s_TexIdLock) s_CurrentTexIds.TryGetValue(s.layerId, out texId);
            if (texId == 0) continue;

            if (s.written.Contains(texId))
            {
                s.quietFrames++;
                if (!s.loggedResident && s.quietFrames >= FramesBeforeResident)
                {
                    s.loggedResident = true;
                    Debug.Log($"VR180Viewer [layer]: {kv.Key.name} photo resident in {s.written.Count} swapchain image(s)");
                }
                continue;
            }

            if (s.image == null) continue;   // released too early: nothing to write with
            RenderTexture target = OpenXRLayerUtility.FindRenderTexture(texId);
            if (target == null) continue;
            Graphics.Blit(s.image, target);
            s.written.Add(texId);
            s.quietFrames = 0;
        }
    }

    // ------------------------------------------------------------------ handler overrides

    static Eye EyeOf(CompositionLayer layer) =>
        layer != null && s_States.TryGetValue(layer, out var s) ? s.eye : Eye.Both;

    // Size from our own state if the viewer has set an image, otherwise from Source Textures
    static bool TryGetSize(CompositionLayerManager.LayerInfo info, out int w, out int h)
    {
        w = h = 0;
        if (info.Layer == null) return false;
        if (s_States.TryGetValue(info.Layer, out var s) && s.width > 0) { w = s.width; h = s.height; return true; }
        var tex = info.Layer.GetComponent<TexturesExtension>();
        if (tex != null && tex.enabled && tex.LeftTexture != null) { w = tex.LeftTexture.width; h = tex.LeftTexture.height; return true; }
        return false;
    }

    // The swapchain's own size once created; before that, the photo's
    static bool TryGetSwapSize(CompositionLayerManager.LayerInfo info, out int w, out int h)
    {
        if (info.Layer != null && s_States.TryGetValue(info.Layer, out var s) && s.swapW > 0)
        {
            w = s.swapW; h = s.swapH;
            return true;
        }
        return TryGetSize(info, out w, out h);
    }

    static XrPosef PoseOf(CompositionLayerManager.LayerInfo info)
    {
        Pose p = OpenXRUtility.ComputePoseToWorldSpace(info.Layer.transform, CompositionLayerManager.mainCameraCache);
        return new XrPosef(p.position, p.rotation);
    }

    protected override bool CreateSwapchain(CompositionLayerManager.LayerInfo layerInfo, out SwapchainCreateInfo swapchainCreateInfo)
    {
        if (!TryGetSize(layerInfo, out int w, out int h))
        {
            swapchainCreateInfo = default;
            return false;
        }
        var st = State(layerInfo.Layer);
        st.swapW = w;
        st.swapH = h;
        st.written.Clear();   // a new swapchain holds nothing yet
        st.quietFrames = 0;

        swapchainCreateInfo = new XrSwapchainCreateInfo
        {
            Type = (uint)XrStructureType.XR_TYPE_SWAPCHAIN_CREATE_INFO,
            CreateFlags = 0,
            UsageFlags = (ulong)(XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_SAMPLED_BIT | XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT),
            Format = OpenXRLayerUtility.GetDefaultColorFormat(),
            SampleCount = 1,
            Width = (uint)w,
            Height = (uint)h,
            FaceCount = 1,
            ArraySize = 1,
            MipCount = 1,
        };
        return true;
    }

    protected override bool CreateNativeLayer(CompositionLayerManager.LayerInfo layerInfo, SwapchainCreatedOutput swapchainOutput, out XrCompositionLayerEquirect2KHR nativeLayer)
    {
        if (!TryGetSwapSize(layerInfo, out int w, out int h))
        {
            nativeLayer = default;
            return false;
        }

        var data = (EquirectMeshLayerData)layerInfo.Layer.LayerData;
        Eye eye = EyeOf(layerInfo.Layer);

        nativeLayer = new XrCompositionLayerEquirect2KHR
        {
            Type = (uint)XrStructureType.XR_TYPE_COMPOSITION_LAYER_EQUIRECT2_KHR,
            LayerFlags = data.BlendType == BlendType.Premultiply
                ? XrCompositionLayerFlags.SourceAlpha
                : XrCompositionLayerFlags.SourceAlpha | XrCompositionLayerFlags.UnPremultipliedAlpha,
            Space = OpenXRLayerUtility.GetCurrentAppSpace(),
            EyeVisibility = (uint)eye,
            SubImage = new XrSwapchainSubImage
            {
                Swapchain = swapchainOutput.handle,
                ImageRect = new XrRect2Di
                {
                    Offset = new XrOffset2Di { X = 0, Y = 0 },
                    Extent = new XrExtent2Di { Width = w, Height = h }
                },
                ImageArrayIndex = 0
            },
            Pose = PoseOf(layerInfo),
            Radius = data.Radius,
            CentralHorizontalAngle = data.CentralHorizontalAngle,
            UpperVerticalAngle = data.UpperVerticalAngle,
            LowerVerticalAngle = data.LowerVerticalAngle
        };

        Debug.Log($"VR180Viewer [layer]: native equirect2 layer created for {layerInfo.Layer.name}, eye {eye}, " +
                  $"{w}x{h}, upper {data.UpperVerticalAngle * Mathf.Rad2Deg:F1}, " +
                  $"lower {data.LowerVerticalAngle * Mathf.Rad2Deg:F1}, radius {data.Radius}");
        return true;
    }

    protected override bool ModifyNativeLayer(CompositionLayerManager.LayerInfo layerInfo, ref XrCompositionLayerEquirect2KHR nativeLayer)
    {
        if (!TryGetSwapSize(layerInfo, out int w, out int h)) return false;

        var data = (EquirectMeshLayerData)layerInfo.Layer.LayerData;
        nativeLayer.SubImage.ImageRect.Extent = new XrExtent2Di { Width = w, Height = h };
        nativeLayer.EyeVisibility = (uint)EyeOf(layerInfo.Layer);
        nativeLayer.Pose = PoseOf(layerInfo);
        nativeLayer.Radius = data.Radius;
        nativeLayer.CentralHorizontalAngle = data.CentralHorizontalAngle;
        nativeLayer.UpperVerticalAngle = data.UpperVerticalAngle;
        nativeLayer.LowerVerticalAngle = data.LowerVerticalAngle;
        return true;
    }

    // Called every frame while the layer is active. Deliberately does NOT call the base version,
    // which would copy the source texture into the swapchain every frame. Instead we ask for the
    // current swapchain image and write it ourselves (once) in WriteSwapchainImages.
    protected override bool ActiveNativeLayer(CompositionLayerManager.LayerInfo layerInfo, ref XrCompositionLayerEquirect2KHR nativeLayer)
    {
        nativeLayer.Space = OpenXRLayerUtility.GetCurrentAppSpace();
        nativeLayer.Pose = PoseOf(layerInfo);

        var s = State(layerInfo.Layer);

        // Zoom: part of the image over different angles; otherwise all of it over the layer data's angles
        if (TryGetSwapSize(layerInfo, out int sw, out int sh))
        {
            var data = (EquirectMeshLayerData)layerInfo.Layer.LayerData;
            if (s.zoomed)
            {
                int x0 = Mathf.Clamp(Mathf.RoundToInt(s.crop.xMin * sw), 0, sw - 1);
                int x1 = Mathf.Clamp(Mathf.RoundToInt(s.crop.xMax * sw), x0 + 1, sw);
                float top = CropFromBottom ? 1f - s.crop.yMax : s.crop.yMin;
                float bottom = CropFromBottom ? 1f - s.crop.yMin : s.crop.yMax;
                int y0 = Mathf.Clamp(Mathf.RoundToInt(top * sh), 0, sh - 1);
                int y1 = Mathf.Clamp(Mathf.RoundToInt(bottom * sh), y0 + 1, sh);
                nativeLayer.SubImage.ImageRect = new XrRect2Di
                {
                    Offset = new XrOffset2Di { X = x0, Y = y0 },
                    Extent = new XrExtent2Di { Width = x1 - x0, Height = y1 - y0 }
                };
                nativeLayer.CentralHorizontalAngle = s.central;
                nativeLayer.UpperVerticalAngle = s.upper;
                nativeLayer.LowerVerticalAngle = s.lower;
            }
            else
            {
                nativeLayer.SubImage.ImageRect = new XrRect2Di
                {
                    Offset = new XrOffset2Di { X = 0, Y = 0 },
                    Extent = new XrExtent2Di { Width = sw, Height = sh }
                };
                nativeLayer.CentralHorizontalAngle = data.CentralHorizontalAngle;
                nativeLayer.UpperVerticalAngle = data.UpperVerticalAngle;
                nativeLayer.LowerVerticalAngle = data.LowerVerticalAngle;
            }
            nativeLayer.Radius = data.Radius;
        }
        ApplySettings(ref nativeLayer);
        s.layerId = layerInfo.Id;
        s.requestedThisFrame = true;
        OpenXRLayerUtility.RequestRenderTextureId(layerInfo.Id, s_OnTexId);

        // Hold the layer back until every swapchain image has the current photo, so the
        // headset never shows an old or empty image while the new one is being written
        return s.written.Count > 0 && s.quietFrames >= FramesBeforeShowing;
    }
}
