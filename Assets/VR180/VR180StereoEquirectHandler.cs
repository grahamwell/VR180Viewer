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
//
// Fade (17 November 2026): XR_KHR_composition_layer_color_scale_bias (requested by Unity's
// Composition Layers feature) lets the compositor scale a layer's colour. While LayerAlpha < 1 an
// XrCompositionLayerColorScaleBiasKHR is chained on as well, so the viewer can fade the sharp
// layer in over the sphere instead of switching it on in one frame.
//
// Single-image swapchains (18 November 2026, needs OpenXR 1.19): with StaticSwapchains on, each
// layer's swapchain is created with XR_SWAPCHAIN_CREATE_STATIC_IMAGE_BIT, so it has one image that
// may be acquired once only. We ask for it once, write the photo into it, and never ask again
// (OpenXR 1.19 only acquires when asked; 1.16 needed it every frame). The image can't be rewritten,
// so every new photo gets a new swapchain: SetImage replaces a spent one (via the base class's own
// RemoveLayer / CreateSwapchainAsync), and DiscardSwapchain lets the viewer free the old one early,
// before the next photo is decoded. About 0.4 GB for both eyes instead of 1.16 GB with 3 images.
// Timing matters: the provider calls ActiveNativeLayer (where we ask) from onBeforeRender, then
// OnUpdate in the same callback; the image is only ours until the end of that frame, so the write
// happens in our OnUpdate override (as Unity's own handler does in 1.19). If the image ever
// arrives too late to write, the swapchain is recreated and tried again; after 3 failures in a row
// the handler goes back to multi-image swapchains for the rest of the session.
// Unity's native layer code acquires and releases image 0 of every new layer swapchain straight after
// creating it, which would use up a single image's one acquire: VR180StaticSwapchainFeature (an OpenXR
// feature, ticked on the Android tab) absorbs that round trip. Without it, multi-image swapchains are used.
// The image id reaches us a frame late (requests are served on the render thread, which runs a frame
// behind), after Unity has released the image; the feature holds that release back until our blit has
// gone through (ReleaseAfterWrite), and the layer is only submitted once IsReleased says so.
//
// Kept photos (19 November 2026): the viewer keeps the previous photo and prepares the next one in
// extra pairs of layers, so switching is a crossfade between two pairs already written. SetHeld lets a
// layer be switched on (so its single image is requested and written) without ever being submitted;
// SetAlpha gives each layer its own fade (each has its own colour scale struct), so one pair can fade
// over another; DiscardSwapchain now also frees a swapchain that was still being created.
//
// Visibility (both kinds, 18 November 2026): IsVisible needs the layer to have been submitted for
// SubmittedFramesBeforeVisible frames in a row since it was last shown, so the sphere doesn't step aside
// before the headset is really showing a re-shown layer (it flickered black on closing the picker).

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
    const int SubmittedFramesBeforeVisible = 3;

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
        // Single-image (static) swapchains
        public CompositionLayerManager.LayerInfo info;   // kept so we can recreate the swapchain ourselves
        public bool hasInfo;
        public bool isStatic;               // the current swapchain was created with the static flag
        public bool creating;               // swapchain asked for, not created yet
        public bool hasSwapchain;           // created (the base class has its native layer)
        public bool recreateAfterCreate;    // a newer photo arrived while it was being created
        public bool discarded;              // freed on purpose; the next SetImage makes a new one
        public bool acquired;               // its one image has been taken and the photo written into it
        public bool pending;                // the image was asked for and hasn't been dealt with yet
        public int requestFrame = -1, writeFrame = -1, lastActiveFrame = -1000;
        public ulong swapHandle;            // the XrSwapchain, for VR180StaticSwapchainFeature
        // Submission (both kinds): frames in a row the layer has been handed to the headset
        public int submittedSince = -1, lastSubmittedFrame = -1000;
        public int failures;                // writes that failed in a row (each one recreates the swapchain)
        public float createStart;
        // Kept photos (19 November 2026)
        public bool held;                   // switched on only to be written: never submitted
        public bool discardAfterCreate;     // DiscardSwapchain came while it was being created
        public float alpha = 1f;            // this layer's fade (1 = no colour scale sent)
        public IntPtr colour;               // its own XrCompositionLayerColorScaleBiasKHR (48 bytes)
        public bool loggedFading;
        public int imageSerial;             // bumped by every SetImage
        public bool loggedStale;            // logged a submission refused because the layer was switched off
    }

    static readonly Dictionary<CompositionLayer, LayerState> s_States = new Dictionary<CompositionLayer, LayerState>();
    static readonly Dictionary<int, uint> s_CurrentTexIds = new Dictionary<int, uint>();   // filled on the graphics thread
    static readonly object s_TexIdLock = new object();
    static readonly OpenXRLayerUtility.RenderTextureIdCallbackDelegate s_OnTexId = OnRenderTextureId;
    static bool s_Hooked, s_RegisteredThisSession, s_BeforeRenderHooked;

    // ------------------------------------------------------------------ single-image swapchains

    /// Each photo gets a new swapchain with a single image, written once (see the header). Set by the
    /// viewer before Install(); applies to swapchains created from then on. Turned off by the handler
    /// itself if writing keeps failing.
    public static bool StaticSwapchains = true;

    const ulong XR_SWAPCHAIN_CREATE_STATIC_IMAGE_BIT = 0x2;
    const int StaticMaxFailures = 3;
    static VR180StereoEquirectHandler s_Instance;                              // this session's handler
    static readonly Dictionary<int, uint> s_Arrived = new Dictionary<int, uint>();   // image ids delivered since the last request (graphics thread)

    /// " (system free N MB)" on the Quest, "" elsewhere. Cheap enough for the odd log line.
    public static string MemoryNote()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var am = activity.Call<AndroidJavaObject>("getSystemService", "activity"))
            using (var info = new AndroidJavaObject("android.app.ActivityManager$MemoryInfo"))
            {
                am.Call("getMemoryInfo", info);
                return $" (system free {info.Get<long>("availMem") / (1024 * 1024)} MB)";
            }
        }
        catch (Exception) { return ""; }
#else
        return "";
#endif
    }

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

    // ------------------------------------------------------------------ fading (colour scale)

    public const string ColourScaleExtension = "XR_KHR_composition_layer_color_scale_bias";

    /// True if the runtime enabled XR_KHR_composition_layer_color_scale_bias for this session
    public static bool ColourScaleSupported { get; private set; }

    /// Sets one layer's opacity, 0..1 (1 = no colour scale sent). Each layer has its own, so one pair of
    /// layers can fade in over (or out from) another.
    public static void SetAlpha(CompositionLayer layer, float alpha)
    {
        if (layer != null) State(layer).alpha = Mathf.Clamp01(alpha);
    }

    /// Diagnostic: for this many frames, log every layer the provider asks about (order, fade, submitted or why
    /// not), one line per frame. Set by the viewer at a push; counts down by itself.
    public static int TraceFrames;
    static readonly List<string> s_TraceItems = new List<string>();

    static void Trace(CompositionLayer layer, LayerState s, bool submit)
    {
        if (TraceFrames <= 0 || layer == null) return;
        string why = submit ? "" :
            s.held ? " held" : !layer.isActiveAndEnabled ? " off" : s.isStatic && !StaticReleased(s) ? " not released" : " not written";
        s_TraceItems.Add($"{layer.name}[{layer.Order}] a={s.alpha:F2} {(submit ? "IN" : "out" + why)}");
    }

    /// A held layer may be switched on (so its swapchain is made and its single image written) but is never
    /// handed to the headset; the viewer uses this to prepare the next photo out of sight.
    public static void SetHeld(CompositionLayer layer, bool held)
    {
        if (layer != null) State(layer).held = held;
    }

    /// Fade by scaling the colour channels too, not only alpha. Which is right depends on whether the
    /// compositor applies the scale before or after premultiplying an unpremultiplied layer: if the
    /// fade looks too dark or too bright half-way, flip this (a viewer setting).
    public static bool FadeScalesColour;

    const uint XR_TYPE_COMPOSITION_LAYER_COLOR_SCALE_BIAS_KHR = 1000034000;

    // Builds our extension chain (colour scale, then layer settings, either optional) and hangs it
    // on the layer's next pointer; leaves alone a chain that isn't ours
    static void ApplySettings(ref XrCompositionLayerEquirect2KHR layer, bool premultiplied, LayerState s, string name)
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
        float alpha = Mathf.Clamp01(s.alpha);
        bool fading = ColourScaleSupported && alpha < 1f;

        if (next != IntPtr.Zero && next != s_Settings && next != s.colour)
        {
            if (!s_ChainWarned && (flags != 0 || fading))
            {
                s_ChainWarned = true;
                Debug.LogWarning("VR180Viewer [layer]: the layer already has an extension chain; not adding our settings or fade");
            }
            return;
        }

        IntPtr head = IntPtr.Zero;
        if (flags != 0)
        {
            if (s_Settings == IntPtr.Zero)
            {
                s_Settings = Marshal.AllocHGlobal(24);
                Marshal.WriteInt32(s_Settings, 0, (int)XR_TYPE_COMPOSITION_LAYER_SETTINGS_FB);
                Marshal.WriteInt32(s_Settings, 4, 0);
            }
            Marshal.WriteIntPtr(s_Settings, 8, IntPtr.Zero);
            Marshal.WriteInt64(s_Settings, 16, (long)flags);
            head = s_Settings;
        }
        if (fading)
        {
            if (s.colour == IntPtr.Zero)
            {
                s.colour = Marshal.AllocHGlobal(48);   // kept for the app's life (one per layer)
                Marshal.WriteInt32(s.colour, 0, (int)XR_TYPE_COMPOSITION_LAYER_COLOR_SCALE_BIAS_KHR);
                Marshal.WriteInt32(s.colour, 4, 0);
                for (int i = 32; i < 48; i += 4) Marshal.WriteInt32(s.colour, i, 0);   // colour bias 0
            }
            // Colour scale (r, g, b, a). An unpremultiplied layer normally fades by alpha alone;
            // a premultiplied one needs all four scaled.
            float c = premultiplied || FadeScalesColour ? alpha : 1f;
            WriteFloat(s.colour, 16, c);
            WriteFloat(s.colour, 20, c);
            WriteFloat(s.colour, 24, c);
            WriteFloat(s.colour, 28, alpha);
            Marshal.WriteIntPtr(s.colour, 8, head);
            head = s.colour;
        }
        if (head != next) MemoryMarshal.Write(nextBytes, ref head);

        if (fading != s.loggedFading)
        {
            s.loggedFading = fading;
            Debug.Log($"VR180Viewer [layer]: {name} fade {(fading ? "started (colour scale chained)" : "finished")}");
        }

        if (flags != s_LoggedFlags)
        {
            s_LoggedFlags = flags;
            Debug.Log($"VR180Viewer [layer]: layer settings {(flags == 0 ? "off" : "0x" + flags.ToString("X"))}");
        }
    }

    static void WriteFloat(IntPtr p, int offset, float v) =>
        Marshal.WriteInt32(p, offset, BitConverter.SingleToInt32Bits(v));

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
        s.discarded = false;
        s.discardAfterCreate = false;
        s.failures = 0;
        s.imageSerial++;
        if (Active) PrepareSwapchain(layer, s);
    }

    /// Changes whenever the layer is given a new photo (SetImage), so a deferred discard can tell it's no longer wanted
    public static int ImageSerial(CompositionLayer layer) =>
        layer != null && s_States.TryGetValue(layer, out var s) ? s.imageSerial : 0;

    /// Single-image swapchains: frees the layer's swapchain now, so its memory is back before the next
    /// photo is decoded. Only when the layer has been hidden for a few frames (the compositor may still
    /// be showing the last frame it was given); SetImage makes a new one either way. Does nothing for
    /// multi-image swapchains, which are reused. Returns false only if it couldn't yet because the layer was
    /// shown too recently (try again a few frames later); true if freed or there was nothing to free.
    /// A swapchain still being created is freed as soon as it exists.
    public static bool DiscardSwapchain(CompositionLayer layer)
    {
        if (layer == null || !Active || s_Instance == null || !s_States.TryGetValue(layer, out var s)) return true;
        if (!s.isStatic || !s.hasInfo) return true;
        if (s.creating && !s.hasSwapchain) { s.discardAfterCreate = true; return true; }
        if (!s.hasSwapchain) return true;
        if (layer.isActiveAndEnabled || Time.frameCount - s.lastActiveFrame < 3) return false;
        int w = s.swapW, h = s.swapH;
        s_Instance.RemoveLayer(s.info.Id);
        ForgetSwapchain(s);
        s.discarded = true;
        Debug.Log($"VR180Viewer [layer]: {layer.name} single-image swapchain {w}x{h} freed{MemoryNote()}");
        return true;
    }

    // A swapchain whose one image has been used can't take a new photo, so it is replaced; a fresh one
    // is kept if it's the right size and kind. Multi-image swapchains are reused as before.
    static void PrepareSwapchain(CompositionLayer layer, LayerState s)
    {
        bool exists = s.hasSwapchain || s.creating;
        bool wrongKind = exists && s.isStatic != StaticSwapchains;
        if (!StaticSwapchains && !wrongKind) return;
        bool wrongSize = exists && (s.swapW != s.width || s.swapH != s.height);
        bool spent = s.hasSwapchain && s.isStatic && (s.acquired || s.pending);
        if (!exists) Recreate(layer, s, "new photo");
        else if (wrongKind) Recreate(layer, s, "switching swapchain kind");
        else if (spent || wrongSize)
        {
            if (s.creating) s.recreateAfterCreate = true;   // replaced as soon as it exists (see WriteStaticImages)
            else Recreate(layer, s, spent ? "new photo; the old single image is spent" : "photo size changed");
        }
    }

    // Destroys the layer's swapchain (if any) and asks for a new one, of the kind StaticSwapchains says
    static void Recreate(CompositionLayer layer, LayerState s, string why)
    {
        // Before the provider has shown us the layer we can't; it creates the swapchain itself then
        if (s_Instance == null || !s.hasInfo || s.info.Layer != layer) return;
        if (s.hasSwapchain || s.creating) s_Instance.RemoveLayer(s.info.Id);
        ForgetSwapchain(s);
        Debug.Log($"VR180Viewer [layer]: {layer.name}: new {(StaticSwapchains ? "single-image" : "multi-image")} swapchain " +
                  $"{s.width}x{s.height} ({why})");
        s_Instance.CreateSwapchainAsync(s.info);
    }

    static void ForgetSwapchain(LayerState s)
    {
        s.hasSwapchain = s.creating = s.acquired = s.pending = s.recreateAfterCreate = s.discardAfterCreate = false;
        s.requestFrame = s.writeFrame = -1;
        s.swapHandle = 0;
        s.written.Clear();
        s.quietFrames = 0;
        if (s.hasInfo)
            lock (s_TexIdLock) { s_Arrived.Remove(s.info.Id); s_CurrentTexIds.Remove(s.info.Id); }
    }

    static bool StaticReleased(LayerState s) => s.acquired && VR180StaticSwapchainFeature.IsReleased(s.swapHandle);
    static bool StaticVisible(LayerState s) => StaticReleased(s);
    static bool StaticResident(LayerState s) => StaticReleased(s) && Time.frameCount >= s.writeFrame + 3;

    // Handed to the headset for a few frames in a row since it was last shown
    static bool SubmittedSteadily(LayerState s) =>
        s.lastSubmittedFrame >= Time.frameCount - 1 && s.submittedSince >= 0 &&
        s.lastSubmittedFrame - s.submittedSince + 1 >= SubmittedFramesBeforeVisible;

    static bool NoteSubmission(LayerState s, bool submit)
    {
        if (submit)
        {
            if (s.lastSubmittedFrame != Time.frameCount - 1 && s.lastSubmittedFrame != Time.frameCount)
                s.submittedSince = Time.frameCount;
            s.lastSubmittedFrame = Time.frameCount;
        }
        return submit;
    }

    /// True once every swapchain image holds the current photo, so its source can be freed.
    public static bool IsResident(CompositionLayer layer) =>
        layer != null && s_States.TryGetValue(layer, out var s) &&
        (s.isStatic ? StaticResident(s) : s.written.Count > 0 && s.quietFrames >= FramesBeforeResident);

    /// True once the layer is actually being submitted to the headset with the current photo.
    public static bool IsVisible(CompositionLayer layer) =>
        layer != null && layer.isActiveAndEnabled && s_States.TryGetValue(layer, out var s) &&
        (s.isStatic ? StaticVisible(s) : s.written.Count > 0 && s.quietFrames >= FramesBeforeShowing) &&
        SubmittedSteadily(s);

    /// True if the layer can show the current photo (its swapchain holds it, source or not).
    public static bool HasImage(CompositionLayer layer) =>
        layer != null && s_States.TryGetValue(layer, out var s) && s.width > 0 && !s.discarded &&
        (s.image != null || s.written.Count > 0);

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
        ColourScaleSupported = false;
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
            kv.Value.hasSwapchain = kv.Value.creating = kv.Value.acquired = kv.Value.pending = false;
            kv.Value.recreateAfterCreate = kv.Value.discarded = kv.Value.discardAfterCreate = false;
            kv.Value.requestFrame = kv.Value.writeFrame = -1;
        }
        foreach (var k in dead) s_States.Remove(k);
        lock (s_TexIdLock) { s_CurrentTexIds.Clear(); s_Arrived.Clear(); }
        s_Instance = null;
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
        if (StaticSwapchains && !VR180StaticSwapchainFeature.Hooked)
        {
            StaticSwapchains = false;
            Debug.LogWarning("VR180Viewer [layer]: single-image swapchains need the \"VR180 Single-Image Swapchains\" OpenXR feature " +
                             "(Project Settings > XR Plug-in Management > OpenXR > Android); using multi-image swapchains");
        }
        s_Instance = new VR180StereoEquirectHandler();
        OpenXRLayerProvider.RegisterLayerHandler(typeof(EquirectMeshLayerData), s_Instance);
        if (!s_BeforeRenderHooked)
        {
            Application.onBeforeRender += WriteSwapchainImages;
            s_BeforeRenderHooked = true;
        }
        Active = true;
        Debug.Log("VR180Viewer [layer]: stereo equirect handler installed (" +
                  (StaticSwapchains ? "single-image swapchains, written once per photo" : "write-once multi-image swapchains") + ")");
        SettingsSupported = OpenXRRuntime.IsExtensionEnabled(LayerSettingsExtension);
        Debug.Log(SettingsSupported
            ? "VR180Viewer [layer]: " + LayerSettingsExtension + " enabled, so the layers can be sharpened/supersampled"
            : "VR180Viewer [layer]: " + LayerSettingsExtension + " not enabled (tick \"VR180 Layer Sharpening\" in the Android OpenXR features, " +
              "or the runtime doesn't have it); layer filtering unavailable");
        ColourScaleSupported = OpenXRRuntime.IsExtensionEnabled(ColourScaleExtension);
        Debug.Log("VR180Viewer [layer]: " + ColourScaleExtension + (ColourScaleSupported
            ? " enabled, so the photo layer can fade in"
            : " not enabled; the photo layer appears without a fade"));
    }

    // ------------------------------------------------------------------ swapchain writing

    [AOT.MonoPInvokeCallback(typeof(OpenXRLayerUtility.RenderTextureIdCallbackDelegate))]
    static void OnRenderTextureId(int layerId, uint texId)
    {
        lock (s_TexIdLock)
        {
            s_CurrentTexIds[layerId] = texId;
            s_Arrived[layerId] = texId;
        }
    }

    // Runs every frame before rendering: writes the photo into any swapchain image that
    // doesn't hold it yet, and counts quiet frames once all of them do.
    static void WriteSwapchainImages()
    {
        if (!Active) return;
        foreach (var kv in s_States)
        {
            var s = kv.Value;
            if (s.isStatic) continue;   // written in OnUpdate instead (WriteStaticImages)
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
        if (layerInfo.Layer != null)
        {
            var known = State(layerInfo.Layer);
            known.info = layerInfo;
            known.hasInfo = true;
        }
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
        st.isStatic = StaticSwapchains;
        st.creating = true;
        st.hasSwapchain = st.acquired = st.pending = false;
        st.requestFrame = st.writeFrame = -1;
        st.createStart = Time.realtimeSinceStartup;

        swapchainCreateInfo = new XrSwapchainCreateInfo
        {
            Type = (uint)XrStructureType.XR_TYPE_SWAPCHAIN_CREATE_INFO,
            CreateFlags = st.isStatic ? XR_SWAPCHAIN_CREATE_STATIC_IMAGE_BIT : 0,
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

        var st = State(layerInfo.Layer);
        st.creating = false;
        st.hasSwapchain = true;
        st.swapHandle = swapchainOutput.handle;
        st.info = layerInfo;
        st.hasInfo = true;
        string kind = st.isStatic
            ? $"single-image swapchain ready in {(Time.realtimeSinceStartup - st.createStart) * 1000f:F0} ms"
            : "multi-image swapchain";
        Debug.Log($"VR180Viewer [layer]: native equirect2 layer created for {layerInfo.Layer.name}, eye {eye}, " +
                  $"{w}x{h}, {kind}, upper {data.UpperVerticalAngle * Mathf.Rad2Deg:F1}, " +
                  $"lower {data.LowerVerticalAngle * Mathf.Rad2Deg:F1}, radius {data.Radius}{(st.isStatic ? MemoryNote() : "")}");
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
        var blend = ((EquirectMeshLayerData)layerInfo.Layer.LayerData).BlendType;
        ApplySettings(ref nativeLayer, blend == BlendType.Premultiply, s, layerInfo.Layer.name);
        s.layerId = layerInfo.Id;
        s.info = layerInfo;
        s.hasInfo = true;
        s.lastActiveFrame = Time.frameCount;
        s.requestedThisFrame = true;

        if (s.isStatic)
        {
            // One image, which may be taken once: ask for it only until the photo is in it. The
            // write follows in OnUpdate, later in this same onBeforeRender callback.
            if (!s.acquired && !s.pending && s.image != null)
            {
                lock (s_TexIdLock) s_Arrived.Remove(layerInfo.Id);
                s.pending = true;
                s.requestFrame = Time.frameCount;
                OpenXRLayerUtility.RequestRenderTextureId(layerInfo.Id, s_OnTexId);
            }
            // Submitted once the image is really released, i.e. after the render frame that ran the blit
            // (never while held: it's only on so that the photo can be written)
            bool submitStatic = !s.held && StillOn(layerInfo.Layer, s) && StaticReleased(s);
            Trace(layerInfo.Layer, s, submitStatic);
            return NoteSubmission(s, submitStatic);
        }

        OpenXRLayerUtility.RequestRenderTextureId(layerInfo.Id, s_OnTexId);

        // Hold the layer back until every swapchain image has the current photo, so the
        // headset never shows an old or empty image while the new one is being written
        bool submit = !s.held && StillOn(layerInfo.Layer, s) && s.written.Count > 0 && s.quietFrames >= FramesBeforeShowing;
        Trace(layerInfo.Layer, s, submit);
        return NoteSubmission(s, submit);
    }

    // The provider is handed the list of active layers during Update, so a layer switched off later in the
    // same frame (by a coroutine, say) still comes through here that frame. Never submit one: it flashed up
    // for a frame when a prepared photo's layers were switched off (19 November 2026).
    static bool StillOn(CompositionLayer layer, LayerState s)
    {
        if (layer != null && layer.isActiveAndEnabled) return true;
        if (!s.loggedStale)
        {
            s.loggedStale = true;
            Debug.Log($"VR180Viewer [layer]: {(layer != null ? layer.name : "a layer")} came through after being switched off; " +
                      "not submitted (logged once per layer)");
        }
        return false;
    }

    // Called by the provider every frame, straight after the SetActiveLayer pass (both from its
    // onBeforeRender callback): the moment the image asked for in ActiveNativeLayer is ours to write.
    public override void OnUpdate()
    {
        if (TraceFrames > 0)
        {
            Debug.Log($"VR180Viewer [trace]: frame {Time.frameCount}: " +
                      (s_TraceItems.Count == 0 ? "no photo layers asked about" : string.Join(", ", s_TraceItems)));
            s_TraceItems.Clear();
            TraceFrames--;
        }
        if (Active) WriteStaticImages();
        base.OnUpdate();   // our layers' native structs go to the frame from here
    }

    static void WriteStaticImages()
    {
        foreach (var kv in s_States)
        {
            var layer = kv.Key;
            var s = kv.Value;
            if (layer == null || !s.isStatic) continue;
            bool active = s.requestedThisFrame;
            s.requestedThisFrame = false;

            if (s.discardAfterCreate && s.hasSwapchain && !layer.isActiveAndEnabled)
            {
                int w = s.swapW, h = s.swapH;
                s_Instance?.RemoveLayer(s.info.Id);
                ForgetSwapchain(s);
                s.discarded = true;
                Debug.Log($"VR180Viewer [layer]: {layer.name} single-image swapchain {w}x{h} freed as soon as it was made{MemoryNote()}");
                continue;
            }
            if (s.recreateAfterCreate && s.hasSwapchain)
            {
                s.recreateAfterCreate = false;
                Recreate(layer, s, "a newer photo arrived while it was being created");
                continue;
            }

            if (s.acquired)
            {
                if (!s.loggedResident && StaticResident(s))
                {
                    s.loggedResident = true;
                    Debug.Log($"VR180Viewer [layer]: {layer.name} photo resident in its single image{MemoryNote()}");
                }
                continue;
            }
            // Only while the layer is on: a request left pending by a layer that was hidden meanwhile
            // is dealt with when it's shown again, or replaced by the next photo's swapchain
            if (!s.pending || !active) continue;

            uint texId;
            bool arrived;
            lock (s_TexIdLock)
            {
                arrived = s_Arrived.TryGetValue(s.info.Id, out texId);
                if (arrived) s_Arrived.Remove(s.info.Id);
            }

            // The id normally arrives a frame after asking (the render thread runs a frame behind); the
            // feature holds the image until our blit has gone through, so a late id is still ours to write
            if (arrived)
            {
                s.pending = false;
                RenderTexture target = texId != 0 ? OpenXRLayerUtility.FindRenderTexture(texId) : null;
                if (target == null) { Failed(layer, s, $"no render texture for image id {texId}"); continue; }
                if (s.image == null) { Failed(layer, s, "nothing to write (the eye texture was already released)"); continue; }
                Graphics.Blit(s.image, target);
                VR180StaticSwapchainFeature.ReleaseAfterWrite(s.swapHandle);
                s.acquired = true;
                s.writeFrame = Time.frameCount;
                s.written.Add(texId);
                s.failures = 0;
                Debug.Log($"VR180Viewer [layer]: {layer.name} photo written into its single image " +
                          $"{(Time.realtimeSinceStartup - s.createStart) * 1000f:F0} ms after asking for the swapchain, " +
                          $"id arrived {Time.frameCount - s.requestFrame} frame(s) after asking (so far: {VR180StaticSwapchainFeature.Absorbed} " +
                          $"creation round trips absorbed, {VR180StaticSwapchainFeature.HeldReleases} releases held for the write)");
            }
            else if (Time.frameCount - s.requestFrame >= 30)
            {
                s.pending = false;
                Failed(layer, s, "no image 30 frames after asking for it");
            }
        }
    }

    // The single image was taken but couldn't be written, so this swapchain is spent: make another.
    // After StaticMaxFailures in a row, go back to multi-image swapchains for the rest of the session.
    static void Failed(CompositionLayer layer, LayerState s, string why)
    {
        s.failures++;
        if (s.failures >= StaticMaxFailures && StaticSwapchains)
        {
            StaticSwapchains = false;
            Debug.LogError($"VR180Viewer [layer]: {layer.name}: {why}. Failed {s.failures} times in a row, so going back to " +
                           "multi-image swapchains for the rest of this session");
        }
        else
        {
            Debug.LogWarning($"VR180Viewer [layer]: {layer.name}: {why}; trying again with a new swapchain " +
                             $"({s.failures} of {StaticMaxFailures})");
        }
        Recreate(layer, s, "retry after a failed write");
    }

    // Unity's base class creates a swapchain whenever a layer without one has its textures changed.
    // With single-image swapchains we decide when (SetImage), so don't let it make a second one.
    public override void ModifyLayer(CompositionLayerManager.LayerInfo layerInfo)
    {
        if (layerInfo.Layer != null && s_States.TryGetValue(layerInfo.Layer, out var s) && (StaticSwapchains || s.isStatic))
        {
            s.info = layerInfo;
            s.hasInfo = true;
            if (!m_nativeLayers.ContainsKey(layerInfo.Id))
            {
                var tex = layerInfo.Layer.GetComponent<TexturesExtension>();
                if (tex != null) tex.TextureAdded = false;
                // A layer that has a photo to show but no swapchain (for example after the headset woke
                // and the session restarted) gets one now; not one freed on purpose, or one on its way
                if (s_Instance == this && !s.creating && !s.discarded && s.image != null && s.width > 0)
                    Recreate(layerInfo.Layer, s, "the layer has a photo but no swapchain");
                return;
            }
        }
        base.ModifyLayer(layerInfo);
    }
}
