// VR180Viewer: milestone 2, with an optional compositor layer.
//
// Loads VR180 side-by-side JPEGs at full resolution, reads their Google VR180 (GPano)
// metadata, and shows them two ways:
//   - on a sphere using the VR180/StereoEquirect shader (the "eye-buffer" path), and
//   - if a composition layer is assigned, as an Equirect compositor layer per eye,
//     which the headset samples directly for the sharpest result. The sphere then
//     just supplies the soft surround underneath it. For stereo this needs two layers
//     (Photo Layer for the left eye, Photo Layer Right for the right) and the
//     VR180StereoEquirectHandler, because Unity's own handler shows one image to both eyes.
//
// Controls:  either thumbstick pushed right = next, left = previous,
//            A = open/close the folder picker (which also has Favourites, Tidy and Exit),
//            B = picture adjustments panel (brightness, contrast, saturation, convergence, sharpness, levelling),
//            right trigger = mark/unmark the photo as a favourite (a buzz confirms: one = added, two = removed),
//            hold the right thumbstick click = recentre (photos otherwise stay where they are; holding
//            the Quest button to recentre the headset also puts the photo straight ahead),
//            X = switch the compositor layer on and off (to compare); while zoomed, switch the
//                layer filter experiment on and off instead (one buzz = on, two = off)
//            Y = switch the layer's radius between its Inspector value and 0 (infinite), for testing;
//                while zoomed, flip the layer's crop (diagnostic)
//            left trigger = frame and save a flat 2D picture (see VR180FlatExport)
//            either thumbstick up/down = zoom in/out (0.5x to 2x, about the direction you were facing
//            when you started, but centred on the horizon vertically unless Zoom Vertical Follow is
//            raised; it pauses at 1x on the way through; resets for each photo). Zooming in is a true
//            telephoto ("squashed sphere") by default, centred where you look: see Zoom Style. On the
//            Quest the sharp layer steps aside while zooming and is rebuilt with the zoom baked in
//            once the stick has been let go (about 2.5 s)
//            (in the Editor: right/left arrow keys, A for adjustments, M for the picker, F for flat export,
//            T for the zoom style, R, L and K)
//
// On PC (Link, Air Link or the Editor) the compositor layer is switched off by default and the
// photo is shown on the sphere at full resolution instead: see Layer On PC.
//
// Orientation: every photo is centred in the same direction, straight ahead as you were facing
// when the first one appeared. It doesn't follow your head from photo to photo. The headset's own
// recentre (hold the Quest button) moves "straight ahead" to where you're facing, and the photo
// with it; holding the right thumbstick click does the same within the app.
//
// Kept photos (19 November 2026): the photo just left is kept, and the next one in the direction
// you're going is prepared while you look at the current one, so stepping to either is a short
// crossfade with no decode (see Keep Previous, Preload Next and the kept photos section below).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Runs early, so the layers' poses (roll compensation) are set before the composition layer
// code reads them in its own Update, avoiding a frame of lag
[DefaultExecutionOrder(-50)]
public class VR180Viewer : MonoBehaviour
{
    [Header("Display")]
    [Tooltip("A material using the VR180/StereoEquirect shader. Slider changes during Play take effect live.")]
    public Material viewerMaterial;

    [Tooltip("Radius of the viewing sphere in metres. It doesn't affect the image; it just has to sit inside the camera's far clip plane.")]
    public float sphereRadius = 50f;

    [Tooltip("Hide the controllers (models, rays and pointers) except while the picker (A) or the adjustments panel (B) is open, the only times pointing is needed.")]
    public bool hideControllers = true;

    [Header("Compositor layer")]
    [Tooltip("An Equirect composition layer with a Source Textures extension set to Target Eye: Individual. Leave empty to use the sphere only.")]
    public CompositionLayer photoLayer;

    [Tooltip("A second Equirect layer, set up like Photo Layer, for the right eye. Needed for stereo on the Quest.")]
    public CompositionLayer photoLayerRight;

    [Tooltip("The Hidden/VR180/EyeExtract shader.")]
    public Shader eyeExtractShader;

    [Tooltip("Start with the compositor layer switched on.")]
    public bool useLayer = true;

    [Tooltip("Quest: give each eye's layer a single-image swapchain, written once per photo, instead of 3 images " +
             "reused from photo to photo. Saves about 0.75 GB while viewing, and the old photo's swapchains are freed " +
             "before the next one is decoded; each photo then gets new swapchains. Needs OpenXR 1.19 or later. " +
             "Untick for the old way (also chosen automatically for the session if writing keeps failing).")]
    public bool singleImageSwapchains = true;

    [Tooltip("Use the compositor layer on PC (Link, Air Link, Editor) as well as on the Quest. Off by default: " +
             "the PC runtime lacks the stereo equirect layer, so the layer shows in mono wrapped round 360 degrees, " +
             "and Unity's handler throws an error every frame after a photo of a different size is loaded. " +
             "With it off, PC shows the photo on the sphere at full resolution.")]
    public bool layerOnPC = false;

    [Tooltip("PC only (Link, Air Link, Editor): renders the headset's eye images at this multiple of the runtime's " +
             "recommended size, by setting the URP asset's Render Scale while the viewer runs (put back on exit). " +
             "The photo on the sphere is already at full resolution on PC; what limits sharpness there is the eye " +
             "image's resolution and Link's video encoding, so supersampling (1.3 to 1.7) can help. Costs GPU time: " +
             "lower it if the view judders. 1 = leave the URP asset alone. The Quest is unaffected.")]
    [Range(1f, 2f)]
    public float pcRenderScale = 1.5f;
    float savedRenderScale = -1f;   // the URP asset's own value, put back in OnDestroy

    [Tooltip("Radius of the layer's sphere in metres. 0 means infinitely far away, which is what we want.")]
    public float layerRadius = 0f;

    [Tooltip("Works around a sign mismatch between Composition Layers 2.6 and OpenXR 1.16 that collapses " +
             "an Equirect layer to zero height on the Quest. Untick if Unity fixes it and the photo disappears.")]
    public bool compensateOpenXRLowerAngle = true;

    [Tooltip("Tick this if the layer's image appears upside down.")]
    public bool flipLayerVertically = false;

    [Header("Layer diagnostics")]
    [Tooltip("Make the layer fully opaque (no feathered edge), to test whether transparency is the problem.")]
    public bool forceOpaqueLayer = false;

    [Tooltip("Give the layer Texture2Ds instead of RenderTextures. Uses more memory while loading.")]
    public bool layerUsesTexture2D = false;

    [Tooltip("Halve the layer's eye textures this many times (1 = 3472 pixels per eye), to test whether size is the problem.")]
    [Range(0, 3)]
    public int layerHalvings = 0;

    [Header("Memory")]
    [Tooltip("When the compositor layer is in use, the sphere only needs a soft surround, so its copy of the photo " +
             "is halved this many times (2 = quarter size). 0 keeps it at full size, which uses far more memory.")]
    [Range(0, 4)]
    public int sphereHalvingsWithLayer = 2;

    [Header("Photos")]
    [Tooltip("Folder of photos used in the Editor and on PC.")]
    public string pcFolder = @"Q:\VR180viewer\Photos";

    [Tooltip("On PC: the folder the picker's \"Copy favourites\" button copies favourites into, ready to transfer to the Quest. " +
             "A plain name is a subfolder of the PC photo folder; a full path is used as it is.")]
    public string pcFavouritesFolder = "Favourites";

    /// Where "Copy favourites" puts them (PC only)
    public string PcFavouritesFolder => Path.IsPathRooted(pcFavouritesFolder) ? pcFavouritesFolder : Path.Combine(pcFolder, pcFavouritesFolder);

    [Tooltip("Sub-folder of the app's own storage, used on Quest until All files access is granted.")]
    public string questSubfolder = "Photos";

    [Tooltip("Top folder of the picker on Quest (needs All files access). Its subfolders can be browsed.")]
    public string questPhotoRoot = "/storage/emulated/0/Pictures";

    [Tooltip("Folder for flat 2D exports on Quest (left trigger). On PC they go to \"VR180 Flat\" beside the PC photo folder.")]
    public string questFlatFolder = "/storage/emulated/0/Pictures/VR180 Flat";

    [Tooltip("Tick if exported flat pictures come out upside down.")]
    public bool flipFlatExport = false;

    [Tooltip("Unprocessed QooCam photos (two fisheye circles, imported from the camera) are converted when shown. " +
             "Tick to level their horizon from the camera's gravity reading, as QooCam Studio does.")]
    public bool levelFisheyePhotos = true;

    [Tooltip("Unprocessed QooCam photos: where one lens sees the other at the edge of its view, show what the other lens sees " +
             "in that direction instead (like QooCam Studio's lens patch).")]
    public bool lensPatch = true;

    [Tooltip("Unprocessed QooCam photos: level the horizon by turning the sphere and layers (on), so a shot taken pointing " +
             "well down or up keeps everything the lenses saw; or inside the conversion as before (off), which cuts off " +
             "whatever was more than 90 degrees above or below the horizon. Takes effect on the next photo.")]
    public bool levelByTurningSphere = true;

    [Header("Roll compensation (looking down and to the side)")]
    [Tooltip("Roll compensation starts when the gaze is this far below the horizon (degrees)...")]
    public float rollDownStart = 40f;
    [Tooltip("...and reaches the panel's slider value (its maximum) this far down.")]
    public float rollDownFull = 60f;
    [Tooltip("It also needs the gaze turned this far left or right of the photo's centre (degrees)...")]
    public float rollSideStart = 30f;
    [Tooltip("...and is at full strength this far round. The two are multiplied together.")]
    public float rollSideFull = 45f;

    [Header("Zoom (either thumbstick up/down)")]
    [Tooltip("Smallest magnification (zoomed right out: the whole photo framed by the surround)")]
    public float zoomMin = 0.5f;
    [Tooltip("Largest magnification")]
    public float zoomMax = 2f;
    [Tooltip("How fast the zoom changes with the stick pushed fully, in doublings per second")]
    public float zoomSpeed = 1f;
    [Tooltip("Diagnostic: if zooming in while looking up or down on the Quest shows the wrong part of the photo, tick this " +
             "(or press Y while zoomed) to crop the layer's image the other way up.")]
    public bool zoomCropFlipY = false;
    [Tooltip("How far the zoom's centre follows your gaze up and down: 0 = always on the horizon (keeps the horizon level " +
             "and straight, but at 2x you can only reach 45 degrees above or below it), 1 = exactly where you look " +
             "(the horizon curves when you zoom looking well up or down). Left and right always follow the gaze.")]
    [Range(0f, 1f)]
    public float zoomVerticalFollow = 0f;

    public enum ZoomStyle { Map, Telephoto }
    [Tooltip("How zooming in works (zooming out always uses Map). Map: longitude and latitude scaled about the centre " +
             "(what the Quest's layer can do; bends things above and below the horizon). Telephoto: the \"squashed sphere\": " +
             "a true telephoto view about where you look, straight lines stay straight and the horizon stays level, " +
             "magnification strongest at the centre. On the Quest the sharp layer steps aside while the zoom changes " +
             "(the softer sphere shows it) and is rebuilt with the zoom baked into its image once the stick has been " +
             "let go for Zoom Settle Seconds (a re-decode, about 2.5 s). T in the Editor switches between the two.")]
    public ZoomStyle zoomStyle = ZoomStyle.Telephoto;
    [Tooltip("Quest, Telephoto: how long after the zoom stops changing before the sharp layer is rebuilt with it")]
    public float zoomSettleSeconds = 0.5f;
    bool telephotoHidLayer;   // the layer was stepped aside for a telephoto zoom
    float layerBakeZoom = 1f;          // the telephoto zoom baked into the layer's image (1 = none)
    Vector2 layerBakeCentre;
    float lastZoomTime = -10f;         // when the zoom last changed
    static readonly int TeleZoomId = Shader.PropertyToID("_TeleZoom");

    [Header("Tilt hint")]
    [Tooltip("For a photo levelled by turning the sphere whose centre is at least this far above or below both the " +
             "horizon and where you're looking (degrees), a brief arrow points that way as it sharpens in. 0 = never.")]
    public float tiltHintDegrees = 15f;
    VR180TiltHint tiltHint;

    public enum LayerFilter { Off, Sharpen, QualitySharpen, SuperSample, QualitySuperSample, SharpenAndSuperSample, QualityBoth }

    [Header("Layer filtering experiment (Quest; needs the VR180 Layer Sharpening OpenXR feature)")]
    [Tooltip("What to ask the Quest's compositor to do to the photo layers (XR_FB_composition_layer_settings). " +
             "While zoomed, the left X button switches it on and off to compare (one buzz = on, two = off).")]
    public LayerFilter layerFilter = LayerFilter.QualitySharpen;
    [Tooltip("Only use the filter above this magnification (1 = only when zoomed in; 0 = always)")]
    public float layerFilterFromZoom = 1f;
    bool layerFilterOn = true;   // the X button's switch (while zoomed)

    [Header("Head-turn parallax (distance set in the adjustments panel)")]
    [Tooltip("Where the point between your eyes sits relative to the neck's pivot, in metres (x right, y up, z ahead). " +
             "Turning the head moves the eyes by this offset turned; the photo is moved to match.")]
    public Vector3 neckToEyes = new Vector3(0f, 0.075f, 0.0805f);

    [Tooltip("Flat export: keep the virtual camera level and shift the picture up or down to where you look, so vertical lines " +
             "stay vertical (like a shift lens). Untick (the default) to aim the camera where you look, so the picture is " +
             "exactly what you see in the frame, in any direction.")]
    public bool flatExportKeepsVerticals = false;

    [Tooltip("Diagnostic: draw the soft surround from the pole-free fill maps (on) or from the photo's own mips, as before (off). Can be changed while running.")]
    public bool useFillMaps = true;

    [Tooltip("Size of the picker, adjustments panel and notices: 1 = the original size, 0.7 = 30% smaller. " +
             "Takes effect the next time a panel opens.")]
    [Range(0.4f, 1.2f)]
    public float uiScale = 0.7f;

    /// The panels' size factor (the Inspector's UI Scale), read when they open
    public static float UIScale = 0.7f;

    [Tooltip("How much the photo behind the picker is softened, from 0 (not at all) to 1 (its average colour).")]
    [Range(0f, 1f)]
    public float pickerBackdropBlur = 0.3f;

    [Tooltip("Tick if the picker's thumbnails come out upside down. Thumbnails already made stay as they " +
             "are: delete the app's thumbs folder (persistentDataPath/thumbs) to remake them.")]
    public bool flipThumbnails = false;

    [Tooltip("Show the picker's thumbnails in stereo (each eye its own picture).")]
    public bool stereoThumbnails = true;

    [Tooltip("How far behind the panel distant scenery appears in the thumbnails: each eye's picture is " +
             "shifted by this fraction of its width (0 = distant things on the panel's surface, near things in front of it). " +
             "Takes effect the next time the picker opens.")]
    [Range(0f, 0.06f)]
    public float thumbnailDepth = 0.015f;

    [Header("Kept photos (quicker switching)")]
    [Tooltip("Keep the photo you've just left ready, so stepping back to it is instant. Quest: its own pair of layers " +
             "(about 0.4 GB); PC: its full-size copy (about 0.5 GB of graphics memory). Read at start-up.")]
    public bool keepPrevious = true;

    [Tooltip("While you look at a photo, prepare the next one in the direction you're going (decoded, and on the Quest " +
             "written into its own layers out of sight), so stepping on is instant once it's ready (about 2.5 s after the " +
             "current one appears). Read at start-up.")]
    public bool preloadNext = true;

    [Tooltip("PC (Link, Air Link, Editor): keep photos as above (each about 0.5 GB of graphics memory at full size). " +
             "Only used with at least 6 GB of graphics memory.")]
    public bool keepPhotosOnPC = true;

    [Tooltip("Seconds for the switch to a kept photo: a crossfade of the sharp layers on the Quest, a quick blur through " +
             "on the sphere otherwise. 0 = at once.")]
    public float quickSwitchSeconds = 0.35f;

    [Tooltip("Quest: decode the photo being prepared with Android's decoder on its own thread and upload it to the GPU " +
             "a strip at a time, a frame apart, instead of Unity's decode, which uploads the whole photo in one frame " +
             "(about 200 ms: a judder while the next photo is prepared). Falls back to Unity's decode if anything fails.")]
    public bool uploadInStrips = true;

    [Tooltip("Rows per strip for Upload In Strips (a 13,888-wide photo: 128 rows is about 7 MB per frame, 55 strips).")]
    [Range(16, 1024)]
    public int stripRows = 128;

    [Tooltip("Tick if photos prepared with Upload In Strips come out upside down (the next photo only: the first one " +
             "shown, or any reached by a long jump, uses Unity's decode).")]
    public bool flipStripUpload = false;

    [Tooltip("Quest (experiment): once the next photo's layers are written, show them for a few frames underneath the " +
             "current photo at a strength too faint to see, so the headset has drawn them once before the switch. " +
             "Aimed at the tear-like black flicker at the push, if it comes from the first time the headset draws them.")]
    public bool warmUpPreparedLayers = false;

    [Header("Transitions")]
    [Tooltip("Blur each photo away to its average colour, hold that colour while the next one loads, " +
             "then sharpen the next one in. Untick to fade through black instead.")]
    public bool blurTransition = true;

    [Tooltip("Seconds to blur the old photo away.")]
    public float blurOutSeconds = 0.5f;

    [Tooltip("Seconds to blend from the old photo's average colour to the new one's.")]
    public float colourBlendSeconds = 0.4f;

    [Tooltip("Seconds to sharpen the new photo in.")]
    public float blurInSeconds = 0.7f;

    [Tooltip("Quest: seconds to fade the sharp layer in over the sphere, starting with the sharpen-in. " +
             "The layer is prepared (held invisible) during the colour blend. 0 = the old way: the layer " +
             "switches on after the sharpen-in has finished.")]
    public float layerFadeSeconds = 0.4f;

    [Tooltip("Quest: fade the layer by scaling its colour as well as its alpha. Leave off unless the fade " +
             "looks too dark or too bright half-way (it depends on how the compositor applies the scale).")]
    public bool layerFadeScalesColour = false;

    [Tooltip("Seconds for each half of the fade through black (when Blur Transition is off).")]
    public float fadeSeconds = 0.25f;

    static readonly int MainTexId      = Shader.PropertyToID("_MainTex");
    static readonly int CoverageId     = Shader.PropertyToID("_Coverage");
    static readonly int StereoId       = Shader.PropertyToID("_StereoSBS");
    static readonly int FisheyeId      = Shader.PropertyToID("_Fisheye");
    static readonly int MaxMipId       = Shader.PropertyToID("_MaxMip");
    static readonly int FadeId         = Shader.PropertyToID("_Fade");
    static readonly int WorldToImageId = Shader.PropertyToID("_WorldToImage");
    static readonly int LayerModeId    = Shader.PropertyToID("_LayerMode");
    static readonly int EdgeFeatherId  = Shader.PropertyToID("_EdgeFeather");
    static readonly int EyeId          = Shader.PropertyToID("_Eye");
    static readonly int FlipYId        = Shader.PropertyToID("_FlipY");
    static readonly int ForceOpaqueId  = Shader.PropertyToID("_ForceOpaque");
    static readonly int BlurId         = Shader.PropertyToID("_Blur");
    static readonly int HoldMixId      = Shader.PropertyToID("_HoldMix");
    static readonly int HoldTexId      = Shader.PropertyToID("_HoldTex");
    static readonly int ExposureId     = Shader.PropertyToID("_Exposure");
    static readonly int ContrastId     = Shader.PropertyToID("_Contrast");
    static readonly int SaturationId   = Shader.PropertyToID("_Saturation");
    static readonly int MipBiasId      = Shader.PropertyToID("_MipBias");
    static readonly int ConvergenceId  = Shader.PropertyToID("_Convergence");
    static readonly int FillMap0Id     = Shader.PropertyToID("_FillMap0");
    static readonly int FillMap1Id     = Shader.PropertyToID("_FillMap1");
    static readonly int FillMapSizeId  = Shader.PropertyToID("_FillMapSize");
    static readonly int FillMirrorId   = Shader.PropertyToID("_FillMirror");

    readonly List<string> files = new List<string>();
    int index;
    int photoGeneration;   // bumped on every photo change, so stale coroutines can tell
    bool busy;
    Transform head;
    readonly List<Transform> controllerRoots = new List<Transform>();            // see UpdateControllers
    readonly HashSet<GameObject> controllerParts = new HashSet<GameObject>();    // parts active at the start
    readonly HashSet<GameObject> controllerPartsLogged = new HashSet<GameObject>();
    bool controllersFound;
    bool layerFading;   // the sharp layer is fading in over the sphere (see FadeLayerIn)
    VR180Notice notice;
    readonly HashSet<string> camerasWarned = new HashSet<string>();   // uncalibrated cameras already mentioned this session
    const float CalibrationNoticeSeconds = 9f;
    RenderTexture current;
    RenderTexture holdTex;   // 1x1: the previous photo's average colour, shown while the next loads
    PhotoInfo currentInfo;
    Quaternion imageToWorld = Quaternion.identity;
    bool centredOnce;              // the first photo sets "straight ahead"; later ones keep it
    float recentreAt = -1f;        // a pending recentre after the headset recentred (its time)
    readonly List<UnityEngine.XR.XRInputSubsystem> inputSubsystems = new List<UnityEngine.XR.XRInputSubsystem>();

    // The soft fill's pole-free copies of the photo (see VR180/StereoEquirect), one per eye
    const int FillMapSize = 1024;
    readonly RenderTexture[] fillMaps = new RenderTexture[2];
    Material fillBakeMaterial;

    TexturesExtension layerTextures, layerTexturesRight;
    Material extractMaterial;
    Texture layerLeft, layerRight;

    InputAction nextAction, prevAction, recentreAction, toggleLayerAction, toggleRadiusAction, menuAction;
    InputAction adjustAction, navAction, stickClickAction, favouriteAction;
    bool navArmed = true;   // the thumbstick has to come back to the centre between steps
    float menuPressTime;
    bool menuHoldHandled;
    VR180PhotoPicker picker;
    VR180FlatExport flatExport;
    bool framing;
    string currentFolder;
    const string LastFolderKey = "VR180.lastFolder";
    float inspectorLayerRadius;

    VR180AdjustPanel adjustPanel;
    VR180LoadingIndicator loading;
    VR180FavouriteBadge badge;
    bool viewingFavourites;   // the Favourites slideshow: no star as each one loads
    bool shufflingFavourites; // ... in shuffled order (see Shuffle Favourites)
    bool sessionLost;         // the XR session ended (headset asleep): rebuild the layer when it's back
    VR180Adjustments.Values adjust = VR180Adjustments.Values.Default;   // the current photo's (set from Get on each load)
    VR180Adjustments.Values adjustBeforePanel;
    float mipBias, mipBiasBeforePanel, materialMipBias;

    // Roll compensation (experimental): turns the photo about the line of sight so its stereo
    // baseline follows the eyes', removing vertical mismatch between the eyes at the cost of the
    // photo no longer staying level. Only when looking down and to the side (see the Inspector's
    // roll settings), where the mismatch is worst and the horizon is out of view anyway; elsewhere
    // the photo stays level. rollStrength (the panel's slider, one setting for all photos) is the
    // strength at the edge of that zone and beyond: 0 = off, 1 = full.
    float rollStrength, rollBeforePanel;
    Quaternion viewRoll = Quaternion.identity;   // this frame's extra rotation about the gaze
    const string RollKey = "VR180.rollCompensation";

    // Head-turn parallax (experimental): turning the head moves the eyes (they're in front of the
    // neck's pivot), but a photo at infinity doesn't move with them, so near things seem to swing
    // round with the head. With a distance set, the photo is put on a sphere of that radius, centred
    // where each eye would be with the head facing the photo's centre, so things at that distance
    // stay put. Only the head's turning counts: leaning or walking still carries the photo along.
    float parallaxDistance, parallaxBeforePanel;   // metres; 0 = off
    Vector3 parallaxOffset;                        // this frame's eye displacement from turning the head (world)
    bool eyeSourceLogged;
    const string ParallaxKey = "VR180.parallaxDistance";
    const string QuickSwitchKey = "VR180.quickSwitchSeconds";   // the B panel's Quick switch slider (Inspector value = first-run default)
    public const float QuickSwitchMin = 0.25f, QuickSwitchMax = 2f;
    float quickSwitchBeforePanel;
    static readonly int ParallaxId = Shader.PropertyToID("_Parallax");

    // Zoom: longitude and latitude scaled by zoom about zoomCentre (photo frame, radians), on the
    // sphere (shader) and on the layer (a cropped part of its image over scaled angles)
    float zoom = 1f;
    Vector2 zoomCentre;
    bool zoomPaused;     // stopped at 1x on the way through, until the stick is let go
    bool zoomChanging;   // for logging the result when the stick is let go
    static readonly int ZoomId = Shader.PropertyToID("_Zoom");
    const string MipBiasKey = "VR180.mipBias";

    /// True on the Quest itself; false on PC (Link, Air Link) and in the Editor
    public static bool OnQuest => Application.platform == RuntimePlatform.Android;

    internal struct PhotoInfo
    {
        public Vector4 coverage;   // lonMin, lonMax, latMin, latMax in radians
        public bool sideBySide;
        public bool fisheye;            // an unprocessed QooCam photo, converted as it's used
        public VR180Fisheye.Raw raw;
        public Vector3 baseline;        // the camera's left-right axis in the photo's frame (x, unless levelling tilted it)
        public Quaternion tilt;         // photo's frame -> level frame: a raw (or tagged Studio) photo levelled by turning the sphere, else identity
    }

    /// The current photo's levelling turn (photo's frame -> level frame); identity for most photos
    Quaternion PhotoTilt => currentInfo.tilt.w == 0f && currentInfo.tilt.x == 0f && currentInfo.tilt.y == 0f && currentInfo.tilt.z == 0f
        ? Quaternion.identity : currentInfo.tilt;

    /// Photo's frame -> world: the yaw that puts it ahead of you, then (raw photos) its levelling turn
    Quaternion Display => imageToWorld * PhotoTilt;

    // ------------------------------------------------------------------ lifecycle

    void OnValidate()
    {
        UIScale = uiScale;
        VR180Thumbnails.FlipY = flipThumbnails;
        VR180PhotoPicker.StereoThumbnails = stereoThumbnails;
        VR180PhotoPicker.ThumbnailDepth = thumbnailDepth;
        VR180Fisheye.Levelling = levelFisheyePhotos;
        VR180Fisheye.LensPatch = lensPatch;
        VR180Fisheye.LevelBySphere = levelByTurningSphere;
    }

    void Awake()
    {
        UIScale = uiScale;
        VR180Thumbnails.FlipY = flipThumbnails;
        VR180PhotoPicker.StereoThumbnails = stereoThumbnails;
        VR180PhotoPicker.ThumbnailDepth = thumbnailDepth;
        VR180Fisheye.Levelling = levelFisheyePhotos;
        VR180Fisheye.LensPatch = lensPatch;
        VR180Fisheye.LevelBySphere = levelByTurningSphere;
        // On PC the layer can't show stereo VR180 (see Layer On PC), so leave it out entirely
        if (!OnQuest && !layerOnPC && photoLayer != null)
        {
            photoLayer.gameObject.SetActive(false);
            if (photoLayerRight != null) photoLayerRight.gameObject.SetActive(false);
            photoLayer = photoLayerRight = null;
            Debug.Log("VR180Viewer: running on PC, so the compositor layer is off; photos show on the sphere at full resolution");
        }
        // On PC the eye images are what limit sharpness, so render them larger (see PC Render Scale)
        if (!OnQuest && pcRenderScale > 1.001f && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
        {
            savedRenderScale = urp.renderScale;
            urp.renderScale = Mathf.Clamp(pcRenderScale, 1f, 2f);
            Debug.Log($"VR180Viewer: PC render scale {urp.renderScale:F2} (the URP asset's own {savedRenderScale:F2} is put back on exit)");
        }

        nextAction = new InputAction("Next", InputActionType.Button);
        nextAction.AddBinding("<Keyboard>/rightArrow");   // on the controller: push either thumbstick right

        prevAction = new InputAction("Previous", InputActionType.Button);
        prevAction.AddBinding("<Keyboard>/leftArrow");

        navAction = new InputAction("Navigate", InputActionType.Value, expectedControlType: "Vector2");
        navAction.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
        navAction.AddBinding("<XRController>{RightHand}/{Primary2DAxis}");

        adjustAction = new InputAction("Adjust", InputActionType.Button);
        adjustAction.AddBinding("<XRController>{RightHand}/{SecondaryButton}");   // B
        adjustAction.AddBinding("<Keyboard>/a");

        favouriteAction = new InputAction("Favourite", InputActionType.Button);
        favouriteAction.AddBinding("<XRController>{RightHand}/{TriggerButton}");
        favouriteAction.AddBinding("<Keyboard>/space");

        recentreAction = new InputAction("Recentre", InputActionType.Button);
        recentreAction.AddBinding("<Keyboard>/r");

        stickClickAction = new InputAction("StickClick", InputActionType.Button);   // hold to recentre
        stickClickAction.AddBinding("<XRController>{RightHand}/{Primary2DAxisClick}");

        menuAction = new InputAction("Menu", InputActionType.Button);
        menuAction.AddBinding("<XRController>{RightHand}/{PrimaryButton}");   // A
        menuAction.AddBinding("<Keyboard>/m");

        toggleLayerAction = new InputAction("ToggleLayer", InputActionType.Button);
        toggleLayerAction.AddBinding("<XRController>{LeftHand}/{PrimaryButton}");
        toggleLayerAction.AddBinding("<Keyboard>/l");

        toggleRadiusAction = new InputAction("ToggleRadius", InputActionType.Button);
        toggleRadiusAction.AddBinding("<XRController>{LeftHand}/{SecondaryButton}");
        toggleRadiusAction.AddBinding("<Keyboard>/k");
        inspectorLayerRadius = layerRadius;

        if (photoLayer != null)
        {
            layerTextures = photoLayer.GetComponent<TexturesExtension>();
            if (layerTextures == null)
                Debug.LogError("VR180Viewer: the photo layer needs a Source Textures extension component.");
            if (eyeExtractShader == null)
                Debug.LogError("VR180Viewer: assign the Hidden/VR180/EyeExtract shader.");
            else
                extractMaterial = new Material(eyeExtractShader);
            photoLayer.gameObject.SetActive(false);

            if (photoLayerRight != null)
            {
                layerTexturesRight = photoLayerRight.GetComponent<TexturesExtension>();
                if (layerTexturesRight == null)
                    Debug.LogError("VR180Viewer: the right photo layer needs a Source Textures extension component.");
                photoLayerRight.gameObject.SetActive(false);
                VR180StereoEquirectHandler.SetEye(photoLayer, VR180StereoEquirectHandler.Eye.Left);
                VR180StereoEquirectHandler.SetEye(photoLayerRight, VR180StereoEquirectHandler.Eye.Right);
            }
            VR180StereoEquirectHandler.StaticSwapchains = singleImageSwapchains;
            VR180StereoEquirectHandler.Install();
        }
        CreateKeptPhotos();
    }

    void OnEnable()
    {
        nextAction.Enable();
        prevAction.Enable();
        recentreAction.Enable();
        menuAction.Enable();
        toggleLayerAction.Enable();
        toggleRadiusAction.Enable();
        navAction.Enable();
        adjustAction.Enable();
        stickClickAction.Enable();
        favouriteAction.Enable();
    }

    void OnDisable()
    {
        nextAction.Disable();
        prevAction.Disable();
        recentreAction.Disable();
        menuAction.Disable();
        toggleLayerAction.Disable();
        toggleRadiusAction.Disable();
        navAction.Disable();
        adjustAction.Disable();
        stickClickAction.Disable();
        favouriteAction.Disable();
    }

    void OnDestroy()
    {
        // Put the URP asset's render scale back (in the Editor, runtime changes to it are saved)
        if (savedRenderScale > 0f && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            urp.renderScale = savedRenderScale;
        nextAction.Dispose();
        prevAction.Dispose();
        recentreAction.Dispose();
        menuAction.Dispose();
        toggleLayerAction.Dispose();
        toggleRadiusAction.Dispose();
        navAction.Dispose();
        adjustAction.Dispose();
        stickClickAction.Dispose();
        favouriteAction.Dispose();
        Application.lowMemory -= OnLowMemory;
        FreeLayerTextures();
        foreach (var k in kept) ReleaseKept(k);
        if (extractMaterial != null) Destroy(extractMaterial);
        if (viewerMaterial != null)
        {
            viewerMaterial.SetTexture(MainTexId, null);
            viewerMaterial.SetTexture(HoldTexId, null);
            viewerMaterial.SetTexture(FillMap0Id, null);
            viewerMaterial.SetTexture(FillMap1Id, null);
            viewerMaterial.SetFloat(FillMapSizeId, 0f);
            viewerMaterial.SetFloat(LayerModeId, 0f);
            viewerMaterial.SetFloat(BlurId, 0f);
            viewerMaterial.SetFloat(HoldMixId, 0f);
            viewerMaterial.SetFloat(FadeId, 1f);
            // Leave the material asset as it was (in the Editor, runtime changes to it are saved)
            ApplyAdjust(viewerMaterial, VR180Adjustments.Values.Default);
            viewerMaterial.SetVector(ParallaxId, Vector4.zero);
            viewerMaterial.SetVector(ZoomId, new Vector4(1f, 0f, 0f, 0f));
            if (head != null) viewerMaterial.SetFloat(MipBiasId, materialMipBias);
        }
        if (holdTex != null) { holdTex.Release(); Destroy(holdTex); }
        if (current != null) { current.Release(); Destroy(current); }
        foreach (var m in fillMaps) if (m != null) { m.Release(); Destroy(m); }
        if (fillBakeMaterial != null) Destroy(fillBakeMaterial);
        foreach (var sub in inputSubsystems) if (sub != null) sub.trackingOriginUpdated -= OnTrackingOriginUpdated;
        VR180StereoEquirectHandler.SessionStopped -= OnXRSessionStopped;
    }

    IEnumerator Start()
    {
        if (viewerMaterial == null) { Debug.LogError("VR180Viewer: no material assigned."); yield break; }

        var cam = Camera.main;
        if (cam == null) { Debug.LogError("VR180Viewer: no camera tagged MainCamera."); yield break; }
        head = cam.transform;
        if (cam.farClipPlane < sphereRadius * 1.5f) cam.farClipPlane = sphereRadius * 1.5f;

        CreateSphere();
        viewerMaterial.SetFloat(FadeId, blurTransition ? 1f : 0f);
        viewerMaterial.SetFloat(BlurId, blurTransition ? 1f : 0f);
        viewerMaterial.SetFloat(HoldMixId, blurTransition ? 1f : 0f);   // start from black
        holdTex = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "VR180 held colour" };
        holdTex.Create();
        Graphics.Blit(Texture2D.blackTexture, holdTex);
        viewerMaterial.SetTexture(HoldTexId, holdTex);
        viewerMaterial.SetFloat(LayerModeId, 0f);
        viewerMaterial.SetFloat(FillMapSizeId, 0f);   // until the first photo's fill maps are made
        Recentre();
        WatchHeadsetRecentre();

        // Sharpness (mip bias) is one setting for all photos, remembered between sessions
        materialMipBias = viewerMaterial.GetFloat(MipBiasId);
        mipBias = PlayerPrefs.GetFloat(MipBiasKey, materialMipBias);
        rollStrength = PlayerPrefs.GetFloat(RollKey, 0.7f);
        parallaxDistance = PlayerPrefs.GetFloat(ParallaxKey, 0f);
        quickSwitchSeconds = Mathf.Clamp(PlayerPrefs.GetFloat(QuickSwitchKey, quickSwitchSeconds), QuickSwitchMin, QuickSwitchMax);
        viewerMaterial.SetFloat(MipBiasId, mipBias);
        ApplyAdjust(viewerMaterial, adjust);

        adjustPanel = gameObject.AddComponent<VR180AdjustPanel>();
        adjustPanel.Init(this);
        loading = gameObject.AddComponent<VR180LoadingIndicator>();
        loading.Init(head);
        badge = gameObject.AddComponent<VR180FavouriteBadge>();
        badge.Init(head);
        notice = gameObject.AddComponent<VR180Notice>();
        notice.Init(head);
        tiltHint = gameObject.AddComponent<VR180TiltHint>();
        tiltHint.Init(head);

        Debug.Log($"VR180Viewer: running on {(OnQuest ? "the Quest" : "PC")}; max texture size on this device is {SystemInfo.maxTextureSize}, " +
                  $"system memory {SystemInfo.systemMemorySize} MB, graphics memory {SystemInfo.graphicsMemorySize} MB");
        Application.lowMemory += OnLowMemory;

        gameObject.AddComponent<VR180Thumbnails>();
        picker = gameObject.AddComponent<VR180PhotoPicker>();
        picker.Init(this, PhotoRoot);
        VR180StereoEquirectHandler.SessionStopped += OnXRSessionStopped;

        string flatFolder = Application.platform == RuntimePlatform.Android
            ? questFlatFolder
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pcFolder).TrimEnd('\\', '/')) ?? pcFolder, "VR180 Flat");
        VR180PhotoPicker.ExcludedFolder = flatFolder;   // flat pictures aren't VR180, so keep them out of the picker

        // Camera calibration files (QooCam Studio's <serial>_refined.xml) for raw fisheye photos.
        // Created empty so it's easy to find over USB; the picker doesn't list it.
        VR180Fisheye.CalibrationFolder = Path.Combine(PhotoRoot, VR180Fisheye.CalibrationFolderName);
        try { Directory.CreateDirectory(VR180Fisheye.CalibrationFolder); }
        catch (Exception e) { Debug.Log($"VR180Viewer: couldn't create {VR180Fisheye.CalibrationFolder} yet: {e.Message}"); }
        flatExport = gameObject.AddComponent<VR180FlatExport>();
        flatExport.Init(this, flatFolder);

        // Start in the last folder used, if it's still there; otherwise the picker's root.
        // On Quest without All files access, fall back to the app's own folder as before.
        string folder;
        bool sharedOk = VR180PhotoPicker.HasAllFilesAccess();
        string last = PlayerPrefs.GetString(LastFolderKey, "");
        if (!sharedOk)
            folder = Path.Combine(Application.persistentDataPath, questSubfolder);
        else if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
            folder = last;
        else
            folder = PhotoRoot;
        Debug.Log($"VR180Viewer [picker]: All files access {(sharedOk ? "granted" : "not granted")}, root {PhotoRoot}");

        if (!FindPhotos(folder)) yield break;
        currentFolder = folder;

        Debug.Log($"VR180Viewer: found {files.Count} photo(s) in {folder}");
        if (files.Count > 0) yield return Show(0);
        else OpenPicker();   // nothing to show yet: go straight to the picker
    }

    string PhotoRoot => Application.platform == RuntimePlatform.Android ? questPhotoRoot : pcFolder;

    // ------------------------------------------------------------------ for the flat export

    public Transform Head => head;
    public string CurrentPhotoPath => files.Count > 0 ? files[index] : null;
    public Vector4 CurrentCoverage => currentInfo.coverage;
    public bool CurrentSideBySide => currentInfo.sideBySide;
    public Quaternion ImageToWorld => Display;   // photo's frame -> world, including a raw photo's levelling turn
    public bool CanStartFraming => !busy && !framing && current != null && files.Count > 0 &&
                                   (picker == null || !picker.IsOpen) && (adjustPanel == null || !adjustPanel.IsOpen);
    public VR180Adjustments.Values CurrentAdjustments => adjust;

    /// While framing, the sharp layer is hidden: it draws over everything, including the viewfinder
    public void SetFraming(bool on)
    {
        framing = on;
        if (on) { ResetZoom(); HideLayer(); }
        else if (useLayer && LayerReady) ShowLayer();
    }

    // ------------------------------------------------------------------ picker

    void OpenPicker()
    {
        if (picker == null || picker.IsOpen || busy || head == null) return;
        if (adjustPanel != null && adjustPanel.IsOpen) return;
        HideLayer();   // the photo layer draws over everything, including the picker
        StartCoroutine(Animate(BlurId, Mathf.Max(pickerBackdropBlur, viewerMaterial.GetFloat(BlurId)), 0.3f));
        picker.Open(currentFolder, head, files.Count > 0 ? files[index] : null);
        Debug.Log("VR180Viewer [picker]: opened");
    }

    public void ClosePicker()
    {
        if (picker == null || !picker.IsOpen) return;
        picker.Close();
        StartCoroutine(ClosePickerRoutine());
        Debug.Log("VR180Viewer [picker]: closed");
    }

    IEnumerator ClosePickerRoutine()
    {
        busy = true;
        yield return Animate(BlurId, 0f, 0.3f);
        busy = false;
        if (useLayer && LayerReady && files.Count > 0) ShowLayer();
    }

    /// Called by the picker: show the photos in a folder, starting from the first, or from startAt
    /// (a photo's full path) if it's one of them.
    public void LoadFolder(string folder, string startAt = null)
    {
        if (busy) return;
        if (picker != null) picker.Close();
        files.Clear();
        if (!FindPhotos(folder) || files.Count == 0)
        {
            Debug.LogWarning($"VR180Viewer [picker]: no photos in {folder}");
            return;
        }
        currentFolder = folder;
        viewingFavourites = false;
        shufflingFavourites = false;
        PlayerPrefs.SetString(LastFolderKey, folder);
        PlayerPrefs.Save();
        int start = startAt == null ? 0 : Mathf.Max(0, files.FindIndex(f => SamePath(f, startAt)));
        Debug.Log($"VR180Viewer [picker]: showing {files.Count} photo(s) from {folder}, starting at {Path.GetFileName(files[start])}");
        StartCoroutine(Show(start));
    }

    // Diagnostic: reports which physical control triggered each action
    static void LogPress(InputAction a)
    {
        if (a.WasPressedThisFrame())
            Debug.Log($"VR180Viewer [input]: {a.name} pressed via {a.activeControl?.path ?? "unknown control"}");
    }

    void Update()
    {
        // A long frame freezes the app (the headset keeps the view steady, but input is only read once a frame)
        if (Time.unscaledDeltaTime > (busy ? 0.025f : 0.06f) && Time.frameCount > 300)
            Debug.Log($"VR180Viewer [timing]: a frame took {Time.unscaledDeltaTime * 1000f:F0} ms" +
                      (prefetchStage != null ? $" (preparing the next photo: {prefetchStage})" : busy ? " (changing photo)" : ""));
        UpdateView();
        CheckWake();
        if (recentreAt >= 0f && Time.unscaledTime >= recentreAt)
        {
            recentreAt = -1f;
            Recentre();
            Debug.Log("VR180Viewer: the headset recentred, so the photo has too");
        }

        LogPress(nextAction);
        LogPress(prevAction);
        LogPress(recentreAction);
        LogPress(toggleLayerAction);
        LogPress(toggleRadiusAction);
        LogPress(menuAction);
        LogPress(adjustAction);
        LogPress(favouriteAction);

        // While framing a flat export, the export owns the controls
        if (framing) return;

        // While the adjustments panel is open, the trigger and ray are for its sliders; B closes it
        if (adjustPanel != null && adjustPanel.IsOpen)
        {
            if (adjustAction.WasPressedThisFrame()) CloseAdjustments();
            return;
        }

        if (recentreAction.WasPressedThisFrame()) Recentre();

        // Holding the right thumbstick click recentres (a hold, so a hard push while
        // stepping through photos doesn't set it off)
        if (stickClickAction.WasPressedThisFrame())
        {
            menuPressTime = Time.unscaledTime;
            menuHoldHandled = false;
        }
        // Editor: T switches the zoom style (to compare the two)
        if ((picker == null || !picker.IsOpen) && Keyboard.current != null && Keyboard.current.tKey.wasPressedThisFrame)
        {
            zoomStyle = zoomStyle == ZoomStyle.Map ? ZoomStyle.Telephoto : ZoomStyle.Map;
            badge.Buzz(zoomStyle == ZoomStyle.Telephoto);
            Debug.Log($"VR180Viewer [zoom]: style {zoomStyle}" + (zoom != 1f ? $" at {zoom:F2}x" : ""));
        }
        if (stickClickAction.IsPressed() && !menuHoldHandled && Time.unscaledTime - menuPressTime > 0.6f)
        {
            menuHoldHandled = true;
            Recentre();
            Debug.Log("VR180Viewer: recentred");
        }

        // A opens or closes the picker
        if (menuAction.WasPressedThisFrame())
        {
            if (picker != null && picker.IsOpen) ClosePicker();
            else OpenPicker();
        }

        // While the picker is open, the trigger and ray are for the picker, and either thumbstick
        // scrolls it; nothing else applies
        if (picker != null && picker.IsOpen)
        {
            picker.Scroll(navAction.ReadValue<Vector2>());
            return;
        }

        if (toggleLayerAction.WasPressedThisFrame() && photoLayer != null && zoom != 1f)
        {
            // While zoomed, X switches the layer filter experiment instead (compare at 2x)
            layerFilterOn = !layerFilterOn;
            badge.Buzz(layerFilterOn);
            Debug.Log($"VR180Viewer [zoom]: layer filter {(layerFilterOn ? layerFilter.ToString() : "off")} at {zoom:F2}x" +
                      (VR180StereoEquirectHandler.SettingsSupported ? "" : " (but the runtime hasn't enabled the extension)"));
        }
        else if (toggleLayerAction.WasPressedThisFrame() && !busy && LayerReady)
        {
            useLayer = !useLayer;
            if (useLayer) ShowLayer(); else HideLayer();
            Debug.Log($"VR180Viewer: compositor layer {(useLayer ? "on" : "off")}");
        }

        if (toggleRadiusAction.WasPressedThisFrame() && photoLayer != null && zoom != 1f)
        {
            // While zoomed, Y flips the layer's crop instead (diagnostic, see Zoom Crop Flip Y)
            zoomCropFlipY = !zoomCropFlipY;
            Debug.Log($"VR180Viewer [zoom]: crop flip {(zoomCropFlipY ? "on" : "off")}");
        }
        else if (toggleRadiusAction.WasPressedThisFrame() && photoLayer != null)
        {
            // Swap between the Inspector's radius and 0 (infinitely far), applied immediately
            float other = inspectorLayerRadius > 0f ? inspectorLayerRadius : 50f;
            layerRadius = layerRadius > 0f ? 0f : other;
            if (photoLayer.LayerData is EquirectMeshLayerData data) data.Radius = layerRadius;
            if (photoLayerRight != null && photoLayerRight.LayerData is EquirectMeshLayerData dataR) dataR.Radius = layerRadius;
            Debug.Log($"VR180Viewer [layer]: radius now {layerRadius} {(layerRadius == 0f ? "(infinite)" : "m")}");
        }

        // Thumbstick left/right steps through the photos, one step per push
        int step = 0;
        Vector2 stick = navAction.ReadValue<Vector2>();
        if (navArmed && Mathf.Abs(stick.x) > 0.7f && Mathf.Abs(stick.x) > Mathf.Abs(stick.y))
        {
            step = stick.x > 0f ? 1 : -1;
            navArmed = false;
            Debug.Log($"VR180Viewer [input]: thumbstick {(step > 0 ? "right (next)" : "left (previous)")} via {navAction.activeControl?.path ?? "unknown control"}");
        }
        else if (stick.magnitude < 0.35f)
        {
            navArmed = true;
        }

        if (busy || files.Count == 0) return;
        if (current != null) UpdateZoom(stick);
        if (adjustAction.WasPressedThisFrame() && current != null) OpenAdjustments();
        else if (favouriteAction.WasPressedThisFrame() && current != null) ToggleFavourite();
        else if (step > 0 || nextAction.WasPressedThisFrame()) StartCoroutine(Show(index + 1, 1));
        else if (step < 0 || prevAction.WasPressedThisFrame()) StartCoroutine(Show(index - 1, -1));
    }

    // ------------------------------------------------------------------ roll compensation

    /// Called by the adjustments panel's slider (0 = off, 1 = full); takes effect at once
    public void SetRollCompensation(float strength) => rollStrength = Mathf.Clamp01(strength);

    // Every frame: work out the roll correction, then aim the sphere and the layers with it
    void UpdateView()
    {
        if (head == null || viewerMaterial == null) return;
        viewRoll = Quaternion.identity;

        if (rollStrength > 0f && currentInfo.sideBySide)
        {
            Vector3 gaze = head.forward;   // no eye tracking on the Quest 3, so the head's direction

            // Only looking down and to the side: how far below the horizon, and how far round from
            // the photo's centre (imageToWorld only turns about the vertical, so this is level)
            Vector3 g = Quaternion.Inverse(imageToWorld) * gaze;
            float down = -Mathf.Asin(Mathf.Clamp(g.y, -1f, 1f)) * Mathf.Rad2Deg;
            float centreLon = (currentInfo.coverage.x + currentInfo.coverage.y) * 0.5f * Mathf.Rad2Deg;
            float side = Mathf.Abs(Mathf.DeltaAngle(centreLon, Mathf.Atan2(g.x, g.z) * Mathf.Rad2Deg));
            float zone = Ramp(down, rollDownStart, rollDownFull) * Ramp(side, rollSideStart, rollSideFull);

            // The camera's left-right direction (its stereo baseline) and the eyes', both as seen
            // looking along the gaze
            Vector3 baseline = currentInfo.baseline.sqrMagnitude > 0.5f ? currentInfo.baseline : Vector3.right;
            Vector3 photoAcross = Vector3.ProjectOnPlane(Display * baseline, gaze);
            Vector3 eyesAcross = Vector3.ProjectOnPlane(head.right, gaze);
            // Looking far to the side, the photo has little stereo left and the angle becomes
            // unreliable, so the correction fades out there
            float weight = zone * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.5f, photoAcross.magnitude));
            if (weight > 0f && eyesAcross.sqrMagnitude > 1e-6f)
            {
                float angle = Mathf.Clamp(Vector3.SignedAngle(photoAcross, eyesAcross, gaze), -45f, 45f);
                viewRoll = Quaternion.AngleAxis(angle * rollStrength * weight, gaze);
            }
        }

        // Head-turn parallax: how far turning the head (from facing the photo's centre) has moved the eyes
        parallaxOffset = parallaxDistance > 0f ? head.rotation * neckToEyes - imageToWorld * neckToEyes : Vector3.zero;
        viewerMaterial.SetVector(ParallaxId, new Vector4(parallaxOffset.x, parallaxOffset.y, parallaxOffset.z, parallaxDistance));

        viewerMaterial.SetVector(ZoomId, new Vector4(zoom, zoomCentre.x, zoomCentre.y, Telephoto ? 1f : 0f));
        viewerMaterial.SetMatrix(WorldToImageId, Matrix4x4.Rotate(Quaternion.Inverse(viewRoll * Display)));
        UpdateTelephotoLayer();
        if (photoLayer != null && photoLayer.gameObject.activeSelf) UpdateLayerPose();
        viewerMaterial.SetFloat(FillMapSizeId, useFillMaps && fillMaps[0] != null ? FillMapSize : 0f);
    }

    // ------------------------------------------------------------------ zoom

    /// Zoomed in with the telephoto style (zooming out always uses the map style)
    bool Telephoto => zoomStyle == ZoomStyle.Telephoto && zoom > 1f;

    /// Whether the layer's image holds the view wanted now: the telephoto zoom baked in, or none
    bool LayerBakeMatches => Telephoto
        ? layerBakeZoom == zoom && layerBakeCentre == zoomCentre
        : layerBakeZoom == 1f;

    // The Quest's equirect layer can only zoom in the map style. For a telephoto zoom, the layer
    // steps aside while the zoom changes (the sphere shows it, softer: its copy is reduced on the
    // Quest); once the zoom has settled, the layer is rebuilt with the zoom baked into its image
    // (RebuildLayer: a re-decode, about 2.5 s). Back at 1x it's rebuilt plain the same way.
    void UpdateTelephotoLayer()
    {
        if (photoLayer == null) return;
        if (StereoLayers)
        {
            if (!LayerBakeMatches && photoLayer.gameObject.activeSelf)
            {
                HideLayer();
                telephotoHidLayer = true;
            }
            if (telephotoHidLayer && !busy && !PanelOpen && !zoomChanging && current != null && files.Count > 0 &&
                Time.unscaledTime - lastZoomTime > zoomSettleSeconds)
            {
                telephotoHidLayer = false;
                if (!useLayer) return;
                if (LayerBakeMatches) { if (LayerReady) ShowLayer(); }
                else
                {
                    Debug.Log($"VR180Viewer [zoom]: rebuilding the layer {(Telephoto ? $"with the {zoom:F2}x telephoto zoom" : "unzoomed")}");
                    StartCoroutine(RebuildLayer());
                }
            }
            return;
        }
        // (One layer for both eyes, Unity's handler: no baking; it just steps aside while zoomed in)
        if (Telephoto && !busy && !PanelOpen)
        {
            if (photoLayer.gameObject.activeSelf)
            {
                HideLayer();
                telephotoHidLayer = true;
                Debug.Log("VR180Viewer [zoom]: telephoto zoom, so the layer steps aside (the sphere is softer on the Quest)");
            }
        }
        else if (telephotoHidLayer && !Telephoto && !busy && !PanelOpen)
        {
            telephotoHidLayer = false;
            if (useLayer && LayerReady) ShowLayer();
        }
    }

    // Thumbstick up = in, down = out, at a rate set by how far it's pushed. Starting from 1x, the
    // zoom centres on where you're looking left/right, and on the horizon up/down (see Zoom Vertical
    // Follow); it stays there (so the photo stays steady in space as
    // you look around) until the zoom is back to 1x.
    void UpdateZoom(Vector2 stick)
    {
        float y = stick.y;
        if (Mathf.Abs(y) < 0.35f || Mathf.Abs(y) < Mathf.Abs(stick.x))
        {
            zoomPaused = false;
            if (zoomChanging)
            {
                zoomChanging = false;
                Debug.Log($"VR180Viewer [zoom]: {zoom:F2}x{(Telephoto ? " telephoto" : zoom != 1f ? " map" : "")}" + (zoom != 1f
                    ? $" about {zoomCentre.x * Mathf.Rad2Deg:F0} deg across, {zoomCentre.y * Mathf.Rad2Deg:F0} deg up" : ""));
            }
            return;
        }
        if (zoomPaused) return;

        if (zoom == 1f)
        {
            // Where you're looking left/right, but vertically only part way from the horizon to the
            // gaze (none by default), so the horizon stays straight; worked out level, then turned
            // into the photo's frame (which differs only for a raw photo levelled by the sphere)
            Vector3 gl = Quaternion.Inverse(imageToWorld) * head.forward;
            float az = Mathf.Atan2(gl.x, gl.z);
            // (A telephoto zoom keeps the horizon straight wherever it's centred, so it centres
            // exactly where you look)
            float follow = zoomStyle == ZoomStyle.Telephoto && y > 0f ? 1f : zoomVerticalFollow;
            float el = follow * Mathf.Asin(Mathf.Clamp(gl.y, -1f, 1f));
            Vector3 g = Quaternion.Inverse(PhotoTilt) * new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el));
            Vector4 c = currentInfo.coverage;
            zoomCentre = new Vector2(Mathf.Clamp(Mathf.Atan2(g.x, g.z), c.x, c.y),
                                     Mathf.Clamp(Mathf.Asin(Mathf.Clamp(g.y, -1f, 1f)), c.z, c.w));
        }
        float rate = Mathf.Sign(y) * (Mathf.Abs(y) - 0.35f) / 0.65f;
        float next = Mathf.Clamp(zoom * Mathf.Pow(2f, rate * zoomSpeed * Time.unscaledDeltaTime), zoomMin, zoomMax);
        if ((zoom > 1f && next <= 1f) || (zoom < 1f && next >= 1f))
        {
            next = 1f;
            zoomPaused = true;
        }
        zoom = next;
        zoomChanging = true;
        lastZoomTime = Time.unscaledTime;
    }

    void ResetZoom()
    {
        zoom = 1f;
        zoomPaused = zoomChanging = false;
        telephotoHidLayer = false;   // whoever resets the zoom also decides about the layer
    }

    static ulong LayerFilterFlags(LayerFilter f)
    {
        const ulong ss = VR180StereoEquirectHandler.NormalSuperSampling, qss = VR180StereoEquirectHandler.QualitySuperSampling;
        const ulong sh = VR180StereoEquirectHandler.NormalSharpening, qsh = VR180StereoEquirectHandler.QualitySharpening;
        switch (f)
        {
            case LayerFilter.Sharpen: return sh;
            case LayerFilter.QualitySharpen: return qsh;
            case LayerFilter.SuperSample: return ss;
            case LayerFilter.QualitySuperSample: return qss;
            case LayerFilter.SharpenAndSuperSample: return sh | ss;
            case LayerFilter.QualityBoth: return qsh | qss;
            default: return 0;
        }
    }

    /// The layer's view of the zoomed photo: which part of its image (x from the left, y from the top,
    /// 0..1), over which angles (radians), and the longitude (degrees) of that part's centre as shown
    void ZoomedLayerView(out Rect crop, out float central, out float upper, out float lower, out float centreDeg)
    {
        const float H = Mathf.PI / 2f;
        Vector4 c = currentInfo.coverage;
        float m = zoom, gl = zoomCentre.x, gp = zoomCentre.y;
        // The part of the photo that fits: at most all the way round, and up to the poles, once scaled
        float l0 = Mathf.Max(c.x, gl - Mathf.PI / m), l1 = Mathf.Min(c.y, gl + Mathf.PI / m);
        float p0 = Mathf.Max(c.z, gp + (-H - gp) / m), p1 = Mathf.Min(c.w, gp + (H - gp) / m);
        float d0 = gl + m * (l0 - gl), d1 = gl + m * (l1 - gl);
        central = Mathf.Min(d1 - d0, 2f * Mathf.PI - 1e-4f);
        centreDeg = (d0 + d1) * 0.5f * Mathf.Rad2Deg;
        upper = Mathf.Min(gp + m * (p1 - gp), H);
        lower = Mathf.Max(gp + m * (p0 - gp), -H);
        float w = c.y - c.x, h = c.w - c.z;
        crop = Rect.MinMaxRect((l0 - c.x) / w, (c.w - p1) / h, (l1 - c.x) / w, (c.w - p0) / h);
    }

    /// Called by the adjustments panel's slider (metres, 0 = off); takes effect at once
    public void SetParallaxDistance(float metres) => parallaxDistance = metres > 0f ? Mathf.Max(metres, 0.5f) : 0f;

    /// The B panel's Quick switch slider: seconds for the crossfade to a photo that was prepared in advance
    public void SetQuickSwitchSeconds(float seconds) => quickSwitchSeconds = Mathf.Clamp(seconds, QuickSwitchMin, QuickSwitchMax);

    /// The layers' radius: the parallax distance when that's on, otherwise the Inspector's (normally 0, infinite)
    float EffectiveLayerRadius => parallaxDistance > 0f ? parallaxDistance : layerRadius;

    static void SetLayerRadius(CompositionLayer layer, float r)
    {
        if (layer != null && layer.LayerData is EquirectMeshLayerData data && !Mathf.Approximately(data.Radius, r)) data.Radius = r;
    }

    /// Where the two eyes are (world space): from the headset if it says, else 63 mm apart either side of the head
    void EyePositions(out Vector3 left, out Vector3 right)
    {
        var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.CenterEye);
        if (device.isValid &&
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.leftEyePosition, out Vector3 l) &&
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.rightEyePosition, out Vector3 r) &&
            (l - r).sqrMagnitude > 1e-6f)
        {
            // Tracking space -> world: the camera's parent is the XR Origin's camera offset
            Transform space = head.parent;
            left = space != null ? space.TransformPoint(l) : l;
            right = space != null ? space.TransformPoint(r) : r;
            // A sanity check: their midpoint should be the head (the camera)
            if (((left + right) * 0.5f - head.position).sqrMagnitude < 0.05f * 0.05f)
            {
                if (!eyeSourceLogged)
                {
                    eyeSourceLogged = true;
                    Debug.Log($"VR180Viewer [parallax]: eye positions from the headset, {Vector3.Distance(left, right) * 1000f:F1} mm apart");
                }
                return;
            }
        }
        if (!eyeSourceLogged)
        {
            eyeSourceLogged = true;
            Debug.Log("VR180Viewer [parallax]: the headset gave no usable eye positions; assuming 63 mm apart");
        }
        left = head.position - head.right * 0.0315f;
        right = head.position + head.right * 0.0315f;
    }

    /// 0 below a, 1 above b, a smooth S-curve between (b may be below a, for a falling ramp)
    static float Ramp(float x, float a, float b) =>
        Mathf.Approximately(a, b) ? (x >= b ? 1f : 0f) : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));

    // ------------------------------------------------------------------ favourites

    void ToggleFavourite()
    {
        bool on = VR180Favourites.Toggle(files[index]);
        Debug.Log($"VR180Viewer [favourites]: {Path.GetFileName(files[index])} {(on ? "added" : "removed")}");
        // Sound and buzz always; the star only where the sharp layer isn't covering the view
        badge.Announce(on, viewerMaterial.GetFloat(LayerModeId) < 0.5f);
    }

    /// Called by the adjustments panel's Favourite tick box
    public void SetFavourite(bool on)
    {
        if (files.Count == 0) return;
        VR180Favourites.Set(files[index], on);
        badge.Announce(on, false);   // the panel's tick box already shows it
        Debug.Log($"VR180Viewer [favourites]: {Path.GetFileName(files[index])} {(on ? "added" : "removed")} (panel)");
    }

    /// Called by the picker: a slideshow of every favourite that can be found, in name order
    public void LoadFavourites()
    {
        if (busy) return;
        var favs = VR180Favourites.Resolve(PhotoRoot);
        if (favs.Count == 0)
        {
            Debug.LogWarning("VR180Viewer [favourites]: none of the favourites could be found");
            return;
        }
        if (picker != null) picker.Close();
        files.Clear();
        int start = 0;
        shufflingFavourites = ShuffleFavourites;
        if (shufflingFavourites) files.AddRange(ShuffledFavourites(favs, out start));
        else files.AddRange(favs);
        currentFolder = null;   // the picker opens at its root next time
        viewingFavourites = true;
        Debug.Log($"VR180Viewer [favourites]: showing {files.Count} favourite(s)" +
                  (shufflingFavourites ? $", shuffled, from number {start + 1}" : ""));
        StartCoroutine(Show(start));
    }

    /// Called by the picker after deleting photos, so the list being viewed doesn't include them
    public void ForgetFiles(ICollection<string> deleted)
    {
        if (deleted.Count == 0 || files.Count == 0) return;
        var gone = new HashSet<string>(deleted.Select(p => Path.GetFullPath(p)), StringComparer.OrdinalIgnoreCase);
        foreach (var k in kept)
            if (k.path != null && gone.Contains(Path.GetFullPath(k.path))) Evict(k, "deleted");
        if (currentPath != null && gone.Contains(Path.GetFullPath(currentPath))) currentPath = null;   // never kept as "previous"
        string cur = files[index];
        int before = files.Take(index).Count(f => !gone.Contains(Path.GetFullPath(f)));   // survivors before the current photo
        bool curGone = gone.Contains(Path.GetFullPath(cur));
        files.RemoveAll(f => gone.Contains(Path.GetFullPath(f)));
        if (files.Count == 0) { index = 0; return; }
        // If the photo on screen was deleted, "next" should go to the one that followed it
        index = curGone ? (before > 0 ? before - 1 : files.Count - 1) : files.IndexOf(cur);
    }

    // ------------------------------------------------------------------ adjustments

    void OpenAdjustments()
    {
        if (adjustPanel == null || adjustPanel.IsOpen || head == null) return;
        HideLayer();   // the photo layer draws over everything, including the panel
        adjustBeforePanel = adjust;
        mipBiasBeforePanel = mipBias;
        rollBeforePanel = rollStrength;
        parallaxBeforePanel = parallaxDistance;
        quickSwitchBeforePanel = quickSwitchSeconds;
        // QooCam photos with a gravity reading (raw ones, and Studio's with the reading copied back in)
        // can have their levelling switched off
        string tilt = null;
        if (currentInfo.raw.hasGravity && VR180Fisheye.Levelling)
        {
            VR180Fisheye.Tilt(currentInfo.raw, out float pitch, out float roll);
            tilt = $"camera {Mathf.Abs(pitch):0.0}\u00b0 {(pitch >= 0f ? "up" : "down")}, {Mathf.Abs(roll):0.0}\u00b0 {(roll >= 0f ? "right" : "left")}";
        }
        adjustPanel.Open(head, Path.GetFileName(files[index]), adjust, mipBias, VR180Favourites.IsFavourite(files[index]), rollStrength, parallaxDistance,
                        quickSwitchSeconds, tilt);
        Debug.Log("VR180Viewer [adjust]: opened");
    }

    /// Called by the panel as the sliders move: shows the change live on the sphere
    public void PreviewAdjustments(VR180Adjustments.Values v, float bias)
    {
        adjust = v;
        mipBias = bias;
        ApplyAdjust(viewerMaterial, adjust);
        viewerMaterial.SetFloat(MipBiasId, mipBias);
    }

    /// Saves the settings and, if the photo's adjustments changed, rebuilds the sharp layer with them
    public void CloseAdjustments()
    {
        if (adjustPanel == null || !adjustPanel.IsOpen) return;
        adjustPanel.Close();

        if (Mathf.Abs(mipBias - mipBiasBeforePanel) > 0.001f)
        {
            PlayerPrefs.SetFloat(MipBiasKey, mipBias);
            PlayerPrefs.Save();
        }
        if (Mathf.Abs(rollStrength - rollBeforePanel) > 0.001f)
        {
            PlayerPrefs.SetFloat(RollKey, rollStrength);
            PlayerPrefs.Save();
            Debug.Log($"VR180Viewer [adjust]: roll compensation now {rollStrength * 100f:0}%");
        }
        if (Mathf.Abs(parallaxDistance - parallaxBeforePanel) > 0.001f)
        {
            PlayerPrefs.SetFloat(ParallaxKey, parallaxDistance);
            PlayerPrefs.Save();
            string what = parallaxDistance > 0f ? "at " + parallaxDistance.ToString("F1", CultureInfo.InvariantCulture) + " m" : "off";
            Debug.Log($"VR180Viewer [adjust]: head-turn parallax {what}");
        }
        if (Mathf.Abs(quickSwitchSeconds - quickSwitchBeforePanel) > 0.001f)
        {
            PlayerPrefs.SetFloat(QuickSwitchKey, quickSwitchSeconds);
            PlayerPrefs.Save();
            Debug.Log($"VR180Viewer [adjust]: quick switch now {quickSwitchSeconds:F2} s");
        }
        // A photo following the default convergence sets the default for all such photos
        if (!adjust.ownConvergence && Mathf.Abs(adjust.convergence - VR180Adjustments.DefaultConvergence) > 0.001f)
        {
            VR180Adjustments.DefaultConvergence = adjust.convergence;
            Debug.Log($"VR180Viewer [adjust]: default convergence now {adjust.convergence:F2} deg");
        }
        bool changed = !VR180Adjustments.Values.Same(adjust, adjustBeforePanel);
        // Convergence only turns the two layers, so only the other settings need the layer rebuilt
        bool imageChanged = !VR180Adjustments.Values.SameImage(adjust, adjustBeforePanel);
        if (changed && files.Count > 0) VR180Adjustments.Set(files[index], adjust);
        Debug.Log($"VR180Viewer [adjust]: closed; exposure {adjust.exposure:F2}, contrast {adjust.contrast:F2}, " +
                  $"saturation {adjust.saturation:F2}, convergence {adjust.convergence:F2} deg, mip bias {mipBias:F2}" +
                  $"{(adjust.keepTilt ? ", horizon not levelled" : "")}{(changed ? " (saved for this photo)" : "")}");

        // Levelling is part of the raw photo's conversion (layer and sphere), so reload the photo
        if (adjust.keepTilt != adjustBeforePanel.keepTilt && files.Count > 0)
        {
            Debug.Log($"VR180Viewer [adjust]: reloading to {(adjust.keepTilt ? "show the camera's tilt" : "level the horizon")}");
            StartCoroutine(Show(index));
            return;
        }

        if (photoLayer == null || !useLayer) return;
        if (imageChanged) StartCoroutine(RebuildLayer());
        else if (LayerReady) ShowLayer();
    }

    static void ApplyAdjust(Material m, VR180Adjustments.Values v)
    {
        if (m == null) return;
        m.SetFloat(ExposureId, v.exposure);
        m.SetFloat(ContrastId, v.contrast);
        m.SetFloat(SaturationId, v.saturation);
        m.SetFloat(ConvergenceId, v.convergence);
    }

    // The layer's image has the adjustments baked in, and its full-size source has usually been
    // freed, so re-decode the photo and write the layer again. The sphere shows the adjusted
    // photo (a little softer) meanwhile.
    IEnumerator RebuildLayer()
    {
        busy = true;
        photoGeneration++;   // stops the previous image's coroutines
        string path = files[index];
        if (prefetching != null) yield return CancelPrefetch();   // it would compete for memory; started again afterwards
        loading.Show();
        DiscardLayerSwapchains();   // the layer is hidden by now; a new pair is made once the photo is ready
        float t0 = Time.realtimeSinceStartup;
        using (var req = DecodeRequest(path))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"VR180Viewer [adjust]: failed to reload {path}: {req.error}");
                loading.Hide();
                busy = false;
                yield break;
            }
            Texture2D src = DownloadHandlerTexture.GetContent(req);
            BuildLayerTextures(src);
            Destroy(src);
        }
        yield return null;
        loading.Hide();
        Debug.Log($"VR180Viewer [layer]: layer rebuilt in {Time.realtimeSinceStartup - t0:F2}s");
        if (useLayer && !PanelOpen) ShowLayer();
        busy = false;
        PlanKept();
    }

    // Diagnostic: the Quest stops sending controller input to an app that has lost focus
    // (for example while a system Settings window is open)
    void OnApplicationFocus(bool focus) => Debug.Log($"VR180Viewer: application focus {(focus ? "gained" : "lost")}");
    void OnApplicationPause(bool paused)
    {
        Debug.Log($"VR180Viewer: application {(paused ? "paused" : "resumed")}");
        if (!paused) StartCoroutine(CheckLayerAfterResume());
    }

    // The controllers are only needed for pointing at the picker and the adjustments panel. The rest of
    // the time they're hidden: over the Quest's sharp layer they vanish anyway, and over the sphere they
    // would pop up wherever the hands happen to be. Hidden by switching off what hangs under each
    // controller (model, Near-Far ray, poke pointer), so the rays can't touch anything either; the
    // controllers themselves keep tracking, and the buttons work as before (their actions are separate).
    // Checked every frame: the template's ControllerInputActionManager switches the Teleport Interactor
    // (with its own line) on whenever a thumbstick is pushed up or down, i.e. when zooming or scrolling.
    // Parts that were off at the start (teleport) are kept off even while a panel is open.
    void UpdateControllers()
    {
        if (head == null) return;
        if (!controllersFound)
        {
            controllersFound = true;
            // The template's XR Origin: Camera Offset holds Main Camera, Left Controller and Right Controller
            Transform offset = head.parent;
            var names = new List<string>();
            if (offset != null)
                foreach (Transform c in offset)
                    if (c != head && c.name.EndsWith("Controller"))
                    {
                        controllerRoots.Add(c);
                        foreach (Transform part in c)
                            if (part.gameObject.activeSelf) { controllerParts.Add(part.gameObject); names.Add($"{c.name}/{part.name}"); }
                    }
            Debug.Log(controllerRoots.Count > 0
                ? $"VR180Viewer: controller parts shown only with a panel open: {string.Join(", ", names)}; everything else under the controllers kept off"
                : "VR180Viewer: no controllers found under the camera's parent, so they can't be hidden");
        }
        if (!hideControllers) return;
        bool panel = (picker != null && picker.IsOpen) || (adjustPanel != null && adjustPanel.IsOpen);
        foreach (Transform root in controllerRoots)
        {
            if (root == null) continue;
            foreach (Transform part in root)
            {
                GameObject go = part.gameObject;
                bool want = panel && controllerParts.Contains(go);
                if (go.activeSelf == want) continue;
                // Something else switched it on (most likely the teleport pointer): say so once
                if (go.activeSelf && !controllerParts.Contains(go) && controllerPartsLogged.Add(go))
                    Debug.Log($"VR180Viewer: {root.name}/{go.name} was switched on by something else; keeping it off");
                go.SetActive(want);
            }
        }
    }

    bool PanelOpen => framing || (picker != null && picker.IsOpen) || (adjustPanel != null && adjustPanel.IsOpen);

    // ------------------------------------------------------------------ waking from sleep

    // When the headset sleeps, the XR session ends and the headset discards the layer's images. The
    // full-size eye textures were freed once the layer held the photo, so nothing would refill it,
    // and the sphere (drawing only the surround while the layer shows) would leave the view black.
    // So: as soon as the session ends, let the sphere show the photo again; once the session is
    // back, decode the photo again and rebuild the layer (about 2.5 s, with the loading dots).
    void OnXRSessionStopped()
    {
        sessionLost = true;
        HideLayer();
        foreach (var k in kept) Evict(k, "the XR session ended, taking the layers' images with it");
        Debug.Log("VR180Viewer [layer]: the XR session ended (headset asleep?); the sphere shows the photo until the layer is rebuilt");
    }

    void CheckWake()
    {
        if (!sessionLost || !VR180StereoEquirectHandler.Active || busy || framing || current == null || files.Count == 0) return;
        sessionLost = false;
        RestoreLayer("the XR session restarted");
    }

    // A fallback in case the session events don't come: a little after resuming, check the layer
    IEnumerator CheckLayerAfterResume()
    {
        yield return new WaitForSecondsRealtime(1.5f);
        if (busy || current == null || files.Count == 0) yield break;
        if (useLayer && StereoLayers && !LayerReady) RestoreLayer("resumed with the layer's image gone");
    }

    void RestoreLayer(string why)
    {
        if (!useLayer || photoLayer == null || layerTextures == null) return;
        if (LayerReady)
        {
            // The eye textures were still held, so the layer refills itself
            if (!PanelOpen) ShowLayer();
            Debug.Log($"VR180Viewer [layer]: {why}; layer shown again");
            return;
        }
        HideLayer();
        Debug.Log($"VR180Viewer [layer]: {why}; rebuilding the layer");
        StartCoroutine(RebuildLayer());
    }

    // ------------------------------------------------------------------ shuffled favourites

    const string ShuffleKey = "VR180.shuffleFavourites";

    /// The picker's Shuffle switch: the All favourites slideshow in a shuffled order that carries on
    /// across sessions, with no repeats until every favourite has been shown
    public static bool ShuffleFavourites
    {
        get => PlayerPrefs.GetInt(ShuffleKey, 0) == 1;
        set { PlayerPrefs.SetInt(ShuffleKey, value ? 1 : 0); PlayerPrefs.Save(); }
    }

    [Serializable]
    class ShuffleState
    {
        public List<string> order = new List<string>();
        public int position = -1;   // the last one shown
    }

    static string ShuffleFile => Path.Combine(Application.persistentDataPath, "shuffle.json");
    static readonly System.Random shuffleRandom = new System.Random();

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// The favourites in the saved shuffled order (favourites added since go somewhere among the
    /// ones still to come; removed ones drop out), and where to start: after the last one shown
    static List<string> ShuffledFavourites(List<string> favs, out int start)
    {
        var state = new ShuffleState();
        try { if (File.Exists(ShuffleFile)) state = JsonUtility.FromJson<ShuffleState>(File.ReadAllText(ShuffleFile)) ?? new ShuffleState(); }
        catch (Exception e) { Debug.LogWarning($"VR180Viewer [favourites]: couldn't read the shuffle: {e.Message}"); }

        var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in favs) present[Path.GetFullPath(f)] = f;

        // Keep the saved order for favourites still there; note how many of them were already shown
        var order = new List<string>();
        int shown = 0;
        for (int i = 0; i < state.order.Count; i++)
        {
            string full;
            try { full = Path.GetFullPath(state.order[i]); } catch { continue; }
            if (!present.TryGetValue(full, out string f) || order.Contains(f)) continue;
            order.Add(f);
            if (i <= state.position) shown = order.Count;
        }
        // New favourites (or a first shuffle): at random places among those still to come
        var fresh = favs.Where(f => !order.Contains(f)).ToList();
        Shuffle(fresh, null);
        foreach (string f in fresh) order.Insert(shuffleRandom.Next(shown, order.Count + 1), f);

        start = shown;
        if (start >= order.Count)
        {
            // Everything had been shown: a new round
            Shuffle(order, shown > 0 ? order[shown - 1] : null);
            start = 0;
        }
        return order;
    }

    /// Shuffles in place; notFirst (the one just seen) won't come first
    static void Shuffle(List<string> list, string notFirst)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = shuffleRandom.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        if (notFirst != null && list.Count > 1 && list[0] == notFirst)
        {
            int j = 1 + shuffleRandom.Next(list.Count - 1);
            (list[0], list[j]) = (list[j], list[0]);
        }
    }

    void SaveShuffle(int position)
    {
        var state = new ShuffleState { order = new List<string>(files), position = position };
        try { File.WriteAllText(ShuffleFile, JsonUtility.ToJson(state)); }
        catch (Exception e) { Debug.LogWarning($"VR180Viewer [favourites]: couldn't save the shuffle: {e.Message}"); }
    }

    void LateUpdate()
    {
        UpdateControllers();   // late, so anything the XR template switched on this frame is caught before rendering
        // Only matters if the layer has a finite radius: keep its centre at the head
        if (photoLayer != null && photoLayer.gameObject.activeSelf) UpdateLayerPose();
    }

    // ------------------------------------------------------------------ photos

    /// The photos the viewer can show in a folder, sorted by name.
    public static List<string> ListPhotos(string folder) =>
        Directory.GetFiles(folder)
            .Where(IsPhotoFile)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// A JPEG that is a photo: not the QooCam's own small previews (..._Thumbstitched.JPG), which the
    /// camera import used to copy too
    public static bool IsPhotoFile(string f)
    {
        string e = Path.GetExtension(f).ToLowerInvariant();
        return (e == ".jpg" || e == ".jpeg") &&
               Path.GetFileName(f).IndexOf("thumbstitched", StringComparison.OrdinalIgnoreCase) < 0;
    }

    bool FindPhotos(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);   // so the folder exists for copying into
            files.AddRange(ListPhotos(folder));
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"VR180Viewer: can't read folder {folder}: {e.Message}");
            return false;
        }
    }

    // step: +1 = next, -1 = previous, 0 = anything else (the direction of travel is then unchanged)
    IEnumerator Show(int i, int step = 0)
    {
        busy = true;
        photoGeneration++;
        float tPush = Time.realtimeSinceStartup;
        if (step != 0) lastStep = step;
        // A shuffled favourites slideshow that has shown every photo: shuffle again and carry on
        if (shufflingFavourites && i >= files.Count && files.Count > 1)
        {
            string last = files[index];
            Shuffle(files, last);
            i = 0;
            Debug.Log($"VR180Viewer [favourites]: all {files.Count} shown; shuffled again");
        }
        index = ((i % files.Count) + files.Count) % files.Count;
        string path = files[index];
        if (shufflingFavourites) SaveShuffle(index);

        // Kept photos: the one wanted may be ready already (no decode), or being prepared (wait for it)
        if (KeepingOn && !Same(path, currentPath))
        {
            Kept k = FindKept(path);
            if (k != null && k == prefetching)
            {
                prefetchUrgent = true;
                float tw = Time.realtimeSinceStartup;
                if (!k.decoded)
                {
                    // Still decoding: let it be seen that something's happening (the sharp layer would hide the dots)
                    HideLayer();
                    if (!blurTransition) { if (viewerMaterial.GetFloat(FadeId) > 0.5f) yield return Fade(0f); }
                    else if (current != null && viewerMaterial.GetFloat(HoldMixId) < 0.5f)
                    {
                        yield return Animate(BlurId, 1f, blurOutSeconds);
                        Graphics.CopyTexture(current, 0, current.mipmapCount - 1, holdTex, 0, 0);
                        viewerMaterial.SetFloat(HoldMixId, 1f);
                    }
                    loading.Show();
                }
                while (prefetching == k) yield return null;
                loading.Hide();
                prefetchUrgent = false;
                Debug.Log($"VR180Viewer [kept]: waited {Time.realtimeSinceStartup - tw:F2}s for {Path.GetFileName(path)} to finish being prepared");
            }
            if (k != null && k.ready && Same(k.path, path) && KeptStillGood(k))
            {
                yield return SwitchToKept(k, tPush);
                yield break;
            }
        }
        // Not kept: decode it. Anything being prepared is stopped first (it would compete for memory)
        if (prefetching != null) yield return CancelPrefetch();

        // Start decoding the new photo at once, so the decode overlaps the old photo's blur-out
        // instead of waiting for it (on the Quest the old photo's sphere copy is only quarter
        // size, so having both at once costs little memory). Disposed by the using block below.
        float t0 = Time.realtimeSinceStartup;
        UnityWebRequest pending = DecodeRequest(path);
        UnityWebRequestAsyncOperation decoding = pending.SendWebRequest();   // JPEG decoding happens off the main thread

        HideLayer();
        if (blurTransition)
        {
            // Blur the old photo away to its average colour, then hold that colour (its 1x1 mip)
            // so the old photo can be freed while the next one loads
            if (current != null && viewerMaterial.GetFloat(HoldMixId) < 0.5f)
            {
                yield return Animate(BlurId, 1f, blurOutSeconds);
                Graphics.CopyTexture(current, 0, current.mipmapCount - 1, holdTex, 0, 0);
            }
            viewerMaterial.SetFloat(HoldMixId, 1f);
        }
        else
        {
            yield return Fade(0f);
        }
        ResetZoom();         // each photo starts unzoomed (the old one has blurred away by now)
        yield return null;   // make sure the held frame is on screen before the heavy work

        // Keep the photo being left (its layers already hold it), unless it isn't wanted or can't be
        RetireCurrent(path);

        loading.Show();
        currentInfo = ReadMetadata(path);
        adjust = VR180Adjustments.Get(path);   // baked into the layer; applied to the sphere below

        // Free the previous photo first, to keep peak memory down
        // (The layer's eye textures are kept and reused: Unity's layer code holds on to them,
        // and destroying them while it does makes it fail on every frame.)
        viewerMaterial.SetTexture(MainTexId, null);
        if (current != null) { current.Release(); Destroy(current); current = null; }
        currentPath = null;
        // Single-image swapchains (Quest): the old photo's are spent anyway, so free them before the decode's peak
        DiscardLayerSwapchains();

        float tWait = Time.realtimeSinceStartup;   // the blur-out is over; anything after this is waiting for the decode
        using (var req = pending)
        {
            if (!decoding.isDone) yield return decoding;
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"VR180Viewer: failed to load {path}: {req.error}");
                loading.Hide();
                busy = false;
                yield break;
            }
            Texture2D src = DownloadHandlerTexture.GetContent(req);
            if (currentInfo.fisheye && !VR180Fisheye.PlausibleSize(src.width, src.height))
            {
                Debug.LogWarning($"VR180Viewer: {Path.GetFileName(path)} looks like a raw fisheye photo but is {src.width}x{src.height}; showing it as it is");
                currentInfo.fisheye = false;
            }

            // Layer textures first, straight from the full-size decoded photo
            bool layerWanted = photoLayer != null && layerTextures != null && extractMaterial != null;
            if (layerWanted) BuildLayerTextures(src);

            // The sphere's copy can be much smaller when the layer is showing the photo
            // (An unprocessed QooCam photo is converted on the way into both: no full-size converted copy)
            current = MakeMipmapped(src, layerWanted ? sphereHalvingsWithLayer : 0, currentInfo.fisheye, currentInfo.raw);
            Destroy(src);
            BakeFillMaps();
            currentPath = path;
        }
        yield return null;   // let the decoded photo's memory be freed before the layer starts up
        loading.Hide();

        Debug.Log($"VR180Viewer: {Path.GetFileName(path)} {current.width}x{current.height}, " +
                  $"{(currentInfo.sideBySide ? "side-by-side" : "mono")}, " +
                  $"loaded in {Time.realtimeSinceStartup - t0:F2}s from the push " +
                  $"({Time.realtimeSinceStartup - tWait:F2}s of it after the blur-out)");

        ApplySphere();
        // A discreet corner star (seen while it sharpens in), but not in the Favourites slideshow
        if (!viewingFavourites && VR180Favourites.IsFavourite(path)) badge.Corner();
        // Only the first photo is centred on where you're looking; after that every photo appears
        // in the same place, and recentring is left to the headset (or the thumbstick hold)
        if (!centredOnce) { Recentre(); centredOnce = true; }

        // A photo whose centre is well above or below the horizon (and your gaze): a brief arrow
        // that way, timed to finish within the sharpen-in (the layer covers it on the Quest)
        ShowTiltHint(blurTransition ? colourBlendSeconds + blurInSeconds : 1f);

        // Quest: switch the sharp layer on now but invisible (alpha 0), so the headset's swapchains are
        // written during the colour blend; it then fades in over the sphere as the sharpen-in starts.
        // Not when the calibration notice is due (the layer would cover it).
        bool calibrationNoticeDue = currentInfo.fisheye && currentInfo.raw.calibrationProblem != null && notice != null &&
                                    !camerasWarned.Contains(currentInfo.raw.serial ?? "");
        bool fadeLayer = useLayer && blurTransition && layerFadeSeconds > 0f && StereoLayers &&
                         VR180StereoEquirectHandler.ColourScaleSupported && !calibrationNoticeDue && !PanelOpen && LayerReady;
        if (fadeLayer) ShowLayer(fadeIn: true);

        // Bring the new photo in on the sphere, then hand over to the sharper layer
        if (blurTransition)
        {
            viewerMaterial.SetFloat(BlurId, 1f);
            yield return Animate(HoldMixId, 0f, colourBlendSeconds);   // old average -> new average
            if (fadeLayer) StartCoroutine(FadeLayerIn(photoGeneration));
            yield return Animate(BlurId, 0f, blurInSeconds);           // then sharpen
        }
        else
        {
            yield return Fade(1f);
        }
        // A raw photo from a camera without its calibration file: say so (once per camera per
        // session) before the layer appears, since the layer would cover the message
        if (currentInfo.fisheye && currentInfo.raw.calibrationProblem != null && notice != null &&
            camerasWarned.Add(currentInfo.raw.serial ?? ""))
        {
            notice.Show("Camera not calibrated", currentInfo.raw.calibrationProblem, CalibrationNoticeSeconds);
            while (notice.Visible) yield return null;
        }

        if (useLayer && !fadeLayer) ShowLayer();
        busy = false;
        // The picker's thumbnail, if it hasn't one yet: almost free from the copy just made
        VR180Thumbnails.FromViewed(path, current, currentInfo.coverage, currentInfo.sideBySide, PhotoTilt);
        PlanKept();   // start preparing the next photo
    }

    // ------------------------------------------------------------------ kept photos (19 November 2026)
    //
    // Besides the photo on view, up to two more are kept ready: the one just left (Keep Previous) and the
    // next one in the direction you're going (Preload Next), prepared while you look at the current one.
    // On the Quest each has its own pair of layers (clones of Photo Layer and Photo Layer Right) whose
    // single-image swapchains already hold it, written out of sight (the handler's SetHeld), plus its
    // quarter-size sphere copy and fill maps; on PC just its full-size sphere copy. Switching to a kept
    // photo is a crossfade between the two pairs (Quick Switch Seconds), with no decode.
    //
    // The current photo lives in the viewer's usual fields (photoLayer, current, currentInfo and so on);
    // a Kept holds the same things for another photo, and SwapWithCurrent exchanges the two, so the rest
    // of the viewer only ever deals with "the current photo" as before. The layer pairs move between them.

    class Kept
    {
        public string path;                 // null = free
        public bool ready;                  // holds the photo, ready to show
        public bool preparing, decoded;     // being prepared (Prefetch); its JPEG has been decoded
        public CompositionLayer left, right;
        public TexturesExtension texL, texR;
        public Texture eyeL, eyeR;          // only while its layers are being written
        public RenderTexture sphere;
        public readonly RenderTexture[] fill = new RenderTexture[2];
        public PhotoInfo info;
        public VR180Adjustments.Values adjust;
        public float bakeZoom = 1f;
        public Vector2 bakeCentre;
    }

    readonly List<Kept> kept = new List<Kept>();
    bool keptWithLayers;     // the Quest: each Kept has its own pair of layers
    string currentPath;      // the photo in the current fields (null = none)
    string previousPath;     // the photo shown before it
    int lastStep = 1;        // the direction of travel: +1 next, -1 previous
    Kept prefetching;        // the one being prepared, if any
    bool prefetchCancel;     // asks Prefetch to stop
    bool prefetchUrgent;     // the photo being prepared has been asked for: no waiting for a quiet moment
    int keepLevel = 2;       // 2 = previous and next, 1 = next only, 0 = none (stepped down on low memory)
    string prefetchStage;    // what Prefetch is doing this frame, for the [timing] log
    bool deferLayerImages;   // BuildLayerTextures: make the eye textures but don't give them to the layers yet
    const int WarmUpFrames = 10;
    const int OrderBase = 20000;   // the scene pair's order at start; incoming pairs go below (PutBelowAll)

    bool CanKeepLayers => StereoLayers && VR180StereoEquirectHandler.StaticSwapchains;
    bool KeepingOn => kept.Count > 0 && keepLevel > 0 && (!keptWithLayers || CanKeepLayers);

    static bool Same(string a, string b) => a != null && b != null && SamePath(a, b);
    Kept FindKept(string path) => path == null ? null : kept.FirstOrDefault(k => Same(k.path, path));

    void CreateKeptPhotos()
    {
        int extra = (keepPrevious ? 1 : 0) + (preloadNext ? 1 : 0);
        if (extra == 0) return;
        if (OnQuest)
        {
            if (photoLayer == null || photoLayerRight == null || layerTextures == null || layerTexturesRight == null) return;
            // Orders start high: each incoming pair is put below all the others (PutBelowAll), so they count down
            photoLayer.Order = OrderBase;
            photoLayerRight.Order = OrderBase + 1;
            var orders = new List<string>();
            for (int n = 1; n <= extra; n++)
            {
                var k = new Kept
                {
                    left = CloneLayer(photoLayer, photoLayer.Order + 10 * n, $" (kept {n})"),
                    right = CloneLayer(photoLayerRight, photoLayerRight.Order + 10 * n, $" (kept {n})")
                };
                k.texL = k.left.GetComponent<TexturesExtension>();
                k.texR = k.right.GetComponent<TexturesExtension>();
                VR180StereoEquirectHandler.SetEye(k.left, VR180StereoEquirectHandler.Eye.Left);
                VR180StereoEquirectHandler.SetEye(k.right, VR180StereoEquirectHandler.Eye.Right);
                kept.Add(k);
                orders.Add($"{k.left.Order}/{k.right.Order}");
            }
            keptWithLayers = true;
            Debug.Log($"VR180Viewer [kept]: {extra} extra pair(s) of photo layers made (orders {string.Join(", ", orders)}; " +
                      $"the scene's pair is {photoLayer.Order}/{photoLayerRight.Order})");
        }
        else if (photoLayer == null && keepPhotosOnPC)
        {
            if (SystemInfo.graphicsMemorySize < 6000)
            {
                Debug.Log($"VR180Viewer [kept]: only {SystemInfo.graphicsMemorySize} MB of graphics memory, so no photos are kept on PC");
                return;
            }
            for (int n = 0; n < extra; n++) kept.Add(new Kept());
            Debug.Log($"VR180Viewer [kept]: up to {extra} photo(s) kept at full size besides the current one (about 0.5 GB of graphics memory each)");
        }
    }

    static CompositionLayer CloneLayer(CompositionLayer template, int order, string suffix)
    {
        var go = Instantiate(template.gameObject, template.transform.parent);
        go.name = template.gameObject.name + suffix;
        go.SetActive(false);
        var layer = go.GetComponent<CompositionLayer>();
        layer.Order = order;
        return layer;
    }

    // Exchanges the current photo (the viewer's fields) with a kept one
    void SwapWithCurrent(Kept k)
    {
        (photoLayer, k.left) = (k.left, photoLayer);
        (photoLayerRight, k.right) = (k.right, photoLayerRight);
        (layerTextures, k.texL) = (k.texL, layerTextures);
        (layerTexturesRight, k.texR) = (k.texR, layerTexturesRight);
        (layerLeft, k.eyeL) = (k.eyeL, layerLeft);
        (layerRight, k.eyeR) = (k.eyeR, layerRight);
        (current, k.sphere) = (k.sphere, current);
        (fillMaps[0], k.fill[0]) = (k.fill[0], fillMaps[0]);
        (fillMaps[1], k.fill[1]) = (k.fill[1], fillMaps[1]);
        (currentInfo, k.info) = (k.info, currentInfo);
        (adjust, k.adjust) = (k.adjust, adjust);
        (layerBakeZoom, k.bakeZoom) = (k.bakeZoom, layerBakeZoom);
        (layerBakeCentre, k.bakeCentre) = (k.bakeCentre, layerBakeCentre);
        (currentPath, k.path) = (k.path, currentPath);
    }

    // Points the sphere's material at the current photo
    void ApplySphere()
    {
        viewerMaterial.SetTexture(MainTexId, current);
        ApplyAdjust(viewerMaterial, adjust);
        viewerMaterial.SetVector(CoverageId, currentInfo.coverage);
        viewerMaterial.SetFloat(StereoId, currentInfo.sideBySide ? 1f : 0f);
        if (current != null)
            viewerMaterial.SetFloat(MaxMipId, Mathf.Floor(Mathf.Log(Mathf.Max(current.width, current.height), 2f)));
        viewerMaterial.SetTexture(FillMap0Id, fillMaps[0]);
        viewerMaterial.SetTexture(FillMap1Id, fillMaps[1]);
        viewerMaterial.SetFloat(FillMapSizeId, useFillMaps && fillMaps[0] != null ? FillMapSize : 0f);
    }

    // Puts a pair of layers above all the other photo layers. Only ever used on a pair that isn't on view.
    void PutAboveAll(CompositionLayer l, CompositionLayer r, string why)
    {
        int high = int.MinValue;
        foreach (var layer in new[] { photoLayer, photoLayerRight })
            if (layer != null && layer != l && layer != r) high = Mathf.Max(high, layer.Order);
        foreach (var k in kept)
            foreach (var layer in new[] { k.left, k.right })
                if (layer != null && layer != l && layer != r) high = Mathf.Max(high, layer.Order);
        if (high == int.MinValue || Mathf.Min(l.Order, r.Order) > high) return;   // already on top
        l.Order = high + 1;
        r.Order = high + 2;
        Debug.Log($"VR180Viewer [kept]: {l.name} and {r.name} put on top at orders {l.Order}/{r.Order} ({why})");
    }

    // Puts a pair of layers below all the other photo layers (still above the eye buffer, order 0). Only ever
    // used on a pair that isn't on view: changing the order of one on view made the headset drop it for a moment.
    void PutBelowAll(CompositionLayer l, CompositionLayer r, string why)
    {
        int low = int.MaxValue;
        foreach (var layer in new[] { photoLayer, photoLayerRight })
            if (layer != null && layer != l && layer != r) low = Mathf.Min(low, layer.Order);
        foreach (var k in kept)
            foreach (var layer in new[] { k.left, k.right })
                if (layer != null && layer != l && layer != r) low = Mathf.Min(low, layer.Order);
        if (low == int.MaxValue || Mathf.Max(l.Order, r.Order) < low) return;   // already underneath
        if (low - 2 < 1) { Debug.LogWarning($"VR180Viewer [kept]: no orders left below {low} for {l.name}; leaving it"); return; }
        l.Order = low - 2;
        r.Order = low - 1;
        Debug.Log($"VR180Viewer [kept]: {l.name} and {r.name} put underneath at orders {l.Order}/{r.Order} ({why})");
    }

    // The JPEG decode used everywhere a photo is shown: no mip chain (the decoded photo is only ever copied at or
    // below full size, by exact halvings, so mips were never used: they cost about 128 MB and time in the frame
    // the decoded photo arrives in, which was the judder and the low-memory warning), and not readable by script
    static UnityWebRequest DecodeRequest(string path)
    {
        var p = DownloadedTextureParams.Default;
        p.readable = false;
        p.mipmapChain = false;
        return UnityWebRequestTexture.GetTexture(new Uri(path), p);
    }

    // One pair's fade; switchSphere: the surround (sphere) changes to the current photo half-way
    IEnumerator FadePair(CompositionLayer l, CompositionLayer r, float from, float to, float seconds, bool switchSphere)
    {
        bool switched = !switchSphere;
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            SetPairAlpha(l, r, Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t / seconds)));
            if (!switched && t >= seconds * 0.5f) { ApplySphere(); switched = true; }
            yield return null;
        }
        SetPairAlpha(l, r, to);
        if (!switched) ApplySphere();
    }

    static void SetPairAlpha(CompositionLayer l, CompositionLayer r, float a)
    {
        VR180StereoEquirectHandler.SetAlpha(l, a);
        VR180StereoEquirectHandler.SetAlpha(r, a);
    }

    // Frees what a Kept holds (its layers stay, switched off); no coroutines, so safe in OnDestroy
    void ReleaseKept(Kept k)
    {
        k.path = null;
        k.ready = k.preparing = k.decoded = false;
        foreach (var layer in new[] { k.left, k.right })
        {
            if (layer == null) continue;   // (also when already destroyed, at shutdown)
            layer.gameObject.SetActive(false);
            VR180StereoEquirectHandler.SetHeld(layer, true);   // never submitted until deliberately shown
            VR180StereoEquirectHandler.SetAlpha(layer, 1f);
            VR180StereoEquirectHandler.ReleaseImage(layer);
        }
        FreeKeptEyeTextures(k);
        if (k.sphere != null) { DestroyTexture(k.sphere); k.sphere = null; }
        for (int e = 0; e < 2; e++)
            if (k.fill[e] != null) { DestroyTexture(k.fill[e]); k.fill[e] = null; }
        k.bakeZoom = 1f;
    }

    void FreeKeptEyeTextures(Kept k)
    {
        if (k.texL != null) { k.texL.LeftTexture = null; k.texL.RightTexture = null; }
        if (k.texR != null) { k.texR.LeftTexture = null; k.texR.RightTexture = null; }
        if (k.eyeR != null && k.eyeR != k.eyeL) DestroyTexture(k.eyeR);
        if (k.eyeL != null) DestroyTexture(k.eyeL);
        k.eyeL = k.eyeR = null;
    }

    // Lets go of a kept photo: textures freed now, its single-image swapchains as soon as the handler allows
    void Evict(Kept k, string why)
    {
        if (k == null) return;
        if (k == prefetching) { prefetchCancel = true; return; }   // Prefetch tidies up after itself
        string name = k.path != null ? Path.GetFileName(k.path) : null;
        ReleaseKept(k);
        if (k.left != null && k.right != null) StartCoroutine(DiscardSoon(k.left, k.right));
        if (name != null) Debug.Log($"VR180Viewer [kept]: {name} let go ({why})");
    }

    IEnumerator DiscardSoon(CompositionLayer l, CompositionLayer r)
    {
        int serialL = VR180StereoEquirectHandler.ImageSerial(l), serialR = VR180StereoEquirectHandler.ImageSerial(r);
        for (int f = 0; f < 30; f++)
        {
            // Given a new photo meanwhile: that replaces the swapchain anyway
            if (VR180StereoEquirectHandler.ImageSerial(l) != serialL || VR180StereoEquirectHandler.ImageSerial(r) != serialR) yield break;
            if (l.gameObject.activeSelf || r.gameObject.activeSelf) yield break;
            bool doneL = VR180StereoEquirectHandler.DiscardSwapchain(l);
            bool doneR = VR180StereoEquirectHandler.DiscardSwapchain(r);
            if (doneL && doneR) yield break;
            yield return null;
        }
    }

    /// The photo ahead in the direction of travel (null if there isn't one to prepare)
    string AheadPath()
    {
        if (files.Count < 2) return null;
        int j = index + lastStep;
        if (shufflingFavourites && j >= files.Count) return null;   // the slideshow reshuffles there
        j = ((j % files.Count) + files.Count) % files.Count;
        return files[j];
    }

    // Cold path, just before a new photo is decoded: keep the photo being left (its layers already hold it)
    // in a free Kept, so the current fields are free for the new one. Everything else kept, except the
    // photo ahead, is let go first, to keep the decode's peak memory down.
    void RetireCurrent(string newPath)
    {
        string leaving = currentPath;
        if (leaving != null && !Same(leaving, newPath)) previousPath = leaving;
        if (!KeepingOn) return;
        string ahead = preloadNext ? AheadPath() : null;
        foreach (var k in kept)
            if (k.path != null && !Same(k.path, ahead)) Evict(k, "making room for the photo being loaded");

        string why = null;
        if (!keepPrevious || keepLevel < 2) why = "";
        else if (leaving == null || current == null || Same(leaving, newPath)) why = "";
        else if (layerBakeZoom != 1f) why = "it has a telephoto zoom baked into its layers";
        else if (photoLayer != null && (layerLeft != null || !VR180StereoEquirectHandler.HasImage(photoLayer) ||
                                        !VR180StereoEquirectHandler.HasImage(photoLayerRight)))
            why = "its layers weren't fully written yet";
        Kept free = why == null ? kept.FirstOrDefault(k => k.path == null) : null;
        if (why == null && free == null) why = "no room";
        if (why != null)
        {
            if (why.Length > 0) Debug.Log($"VR180Viewer [kept]: {Path.GetFileName(leaving)} not kept ({why})");
            return;
        }
        if (photoLayer != null) SetPairAlpha(photoLayer, photoLayerRight, 1f);
        SwapWithCurrent(free);   // the current fields now hold free's empty layers, ready for the new photo
        free.ready = true;
        Debug.Log($"VR180Viewer [kept]: {Path.GetFileName(free.path)} kept, so going back to it is instant");
    }

    // After a photo is shown: let go of anything no longer next to it, and start preparing the one ahead
    void PlanKept()
    {
        if (!KeepingOn)
        {
            // (for example the handler went back to multi-image swapchains, or memory is low)
            foreach (var k in kept) if (k.path != null) Evict(k, "photos aren't being kept just now");
            return;
        }
        if (currentPath == null || files.Count < 2) return;
        string ahead = preloadNext ? AheadPath() : null;
        string behind = keepPrevious && keepLevel >= 2 ? previousPath : null;
        if (Same(ahead, currentPath)) ahead = null;
        foreach (var k in kept)
            if (k.path != null && k != prefetching && !Same(k.path, ahead) && !Same(k.path, behind))
                Evict(k, "no longer next to the photo on view");
        if (prefetching != null && !Same(prefetching.path, ahead)) prefetchCancel = true;
        if (ahead == null || FindKept(ahead) != null) return;
        Kept free = kept.FirstOrDefault(k => k.path == null && k != prefetching);
        if (free == null) return;
        StartCoroutine(Prefetch(free, ahead));
    }

    IEnumerator CancelPrefetch()
    {
        if (prefetching == null) yield break;
        prefetchCancel = true;
        float t0 = Time.realtimeSinceStartup;
        while (prefetching != null && Time.realtimeSinceStartup - t0 < 2f) yield return null;
    }

    void EndPrefetch(Kept k, string why)
    {
        prefetchStage = null;
        if (prefetching == k) prefetching = null;
        prefetchCancel = false;
        k.preparing = false;
        Evict(k, why);
    }

    // Prepares a photo in a free Kept while the current one is on view: decoded, its sphere copy and fill
    // maps made, and (Quest) its own layers switched on but held, so that their single images are written
    // without being shown; then the layers are switched off again and the eye textures freed.
    IEnumerator Prefetch(Kept k, string path)
    {
        prefetching = k;
        prefetchCancel = false;
        k.path = path;
        k.ready = k.decoded = false;
        k.preparing = true;
        string name = Path.GetFileName(path);

        // Wait for a quiet moment: the current photo settled (its eye textures freed), nothing else going on
        float tQuiet = Time.realtimeSinceStartup;
        while (!prefetchUrgent && (busy || PanelOpen || layerFading || zoomChanging ||
                                   (layerLeft != null && Time.realtimeSinceStartup - tQuiet < 5f)))
        {
            if (prefetchCancel) { EndPrefetch(k, "cancelled"); yield break; }
            yield return null;
        }

        PhotoInfo info = ReadMetadata(path);
        if (info.fisheye && info.raw.calibrationProblem != null && notice != null && !camerasWarned.Contains(info.raw.serial ?? ""))
        {
            EndPrefetch(k, "its camera's calibration notice hasn't been shown yet");
            yield break;
        }
        var adj = VR180Adjustments.Get(path);
        float t0 = Time.realtimeSinceStartup;
        Texture src = null;
        string stripNote = "";
        if (uploadInStrips && VR180StripDecoder.Available)
        {
            // Android decodes it on its own thread; the upload is spread over many frames (paused while a
            // fade or zoom is running, hurried if the photo has already been asked for)
            var sr = new VR180StripDecoder.Result();
            prefetchStage = "uploading the photo in strips";
            yield return VR180StripDecoder.Load(path, stripRows, flipStripUpload,
                () => prefetchUrgent ? 8 : (layerFading || zoomChanging || busy) ? 0 : 1,
                () => prefetchCancel, sr);
            if (sr.cancelled || prefetchCancel)
            {
                if (sr.texture != null) Destroy(sr.texture);
                EndPrefetch(k, "cancelled while decoding");
                yield break;
            }
            if (sr.texture != null)
            {
                src = sr.texture;
                stripNote = $" (Android's decode {sr.decodeSeconds:F2}s, then {sr.strips} strips uploaded over {sr.frames} frames " +
                            $"in {sr.uploadSeconds:F2}s, {sr.totalMs / Mathf.Max(1, sr.frames):F1} ms a frame on average, worst {sr.worstMs:F0} ms)";
            }
            else Debug.LogWarning($"VR180Viewer [strips]: {name}: {sr.error}; using Unity's decode instead");
        }
        if (src == null)
        using (var req = DecodeRequest(path))
        {
            UnityWebRequestAsyncOperation op = req.SendWebRequest();   // decoded off the main thread
            while (!op.isDone)
            {
                if (prefetchCancel) { req.Abort(); EndPrefetch(k, "cancelled while decoding"); yield break; }
                yield return null;
            }
            if (req.result != UnityWebRequest.Result.Success)
            {
                EndPrefetch(k, $"couldn't load it: {req.error}");
                yield break;
            }
            prefetchStage = "the decoded photo arriving";
            src = DownloadHandlerTexture.GetContent(req);
        }
        k.decoded = true;
        yield return null;   // (so the [timing] log tells this frame from the next step's)
        float decodeSeconds = Time.realtimeSinceStartup - t0;
        if (info.fisheye && !VR180Fisheye.PlausibleSize(src.width, src.height)) info.fisheye = false;

        // Built in steps, a frame or more apart, so that no one frame has too much to do (a frame that takes
        // too long freezes the app: the view still follows the head, but a quick push of the stick is missed).
        // The swapchains, the slowest part when memory is short, come last, once the decoded photo is freed.
        bool layered = k.left != null;
        float buildMs = 0f;
        for (int step = 0; step < 2; step++)
        {
            while (!prefetchUrgent && (layerFading || zoomChanging || busy) && !prefetchCancel) yield return null;
            if (prefetchCancel) { Destroy(src); EndPrefetch(k, "cancelled"); yield break; }
            float tStep = Time.realtimeSinceStartup;
            if (step == 0)
            {
                prefetchStage = "its layers' eye textures";
                if (layered) BuildKept(k, src, info, adj, layers: true);
            }
            else
            {
                prefetchStage = "its sphere copy";
                BuildKept(k, src, info, adj, layers: false);
                Destroy(src);
                src = null;
            }
            buildMs += (Time.realtimeSinceStartup - tStep) * 1000f;
            yield return null;
        }
        for (int f = 0; f < 3; f++) yield return null;   // the decoded photo's memory back first

        float writeSeconds = 0f;
        if (layered)
        {
            while (!prefetchUrgent && (layerFading || zoomChanging || busy) && !prefetchCancel) yield return null;
            if (prefetchCancel) { EndPrefetch(k, "cancelled"); yield break; }
            prefetchStage = "its layers' swapchains";
            VR180StereoEquirectHandler.SetImage(k.left, k.eyeL);
            VR180StereoEquirectHandler.SetImage(k.right, k.eyeR);
            VR180StereoEquirectHandler.SetHeld(k.left, true);
            VR180StereoEquirectHandler.SetHeld(k.right, true);
            SetPairAlpha(k.left, k.right, 1f);
            k.left.gameObject.SetActive(true);
            k.right.gameObject.SetActive(true);
            float tw = Time.realtimeSinceStartup;
            while (!(VR180StereoEquirectHandler.IsResident(k.left) && VR180StereoEquirectHandler.IsResident(k.right)))
            {
                if (prefetchCancel) { prefetchStage = null; EndPrefetch(k, "cancelled while writing its layers"); yield break; }
                if (Time.realtimeSinceStartup - tw > 10f) { prefetchStage = null; EndPrefetch(k, "its layers were never written"); yield break; }
                yield return null;
            }
            writeSeconds = Time.realtimeSinceStartup - tw;
            if (warmUpPreparedLayers)
            {
                // Drawn once by the headset before the switch: underneath the current photo, at 1% strength
                prefetchStage = "warming its layers";
                PutBelowAll(k.left, k.right, "the pair being warmed, held");
                SetPairAlpha(k.left, k.right, 0.01f);
                VR180StereoEquirectHandler.SetHeld(k.left, false);
                VR180StereoEquirectHandler.SetHeld(k.right, false);
                for (int f = 0; f < WarmUpFrames; f++)
                {
                    if (prefetchCancel) break;
                    yield return null;
                }
                VR180StereoEquirectHandler.SetHeld(k.left, true);
                VR180StereoEquirectHandler.SetHeld(k.right, true);
                SetPairAlpha(k.left, k.right, 1f);
                if (prefetchCancel) { prefetchStage = null; EndPrefetch(k, "cancelled while warming its layers"); yield break; }
                Debug.Log($"VR180Viewer [kept]: {name}'s layers drawn faintly underneath for {WarmUpFrames} frames " +
                          $"(orders {k.left.Order}/{k.right.Order} under {photoLayer.Order}/{photoLayerRight.Order})");
            }
            // Switched off but left held: only showing it (SwitchToKept, ShowLayer) lets it be submitted
            k.left.gameObject.SetActive(false);
            k.right.gameObject.SetActive(false);
            VR180StereoEquirectHandler.ReleaseImage(k.left);
            VR180StereoEquirectHandler.ReleaseImage(k.right);
            FreeKeptEyeTextures(k);
        }
        prefetchStage = null;
        k.preparing = false;
        k.ready = true;
        prefetching = null;
        prefetchCancel = false;
        Debug.Log($"VR180Viewer [kept]: {name} prepared out of sight: decoded in {decodeSeconds:F2}s, built in {buildMs:F0} ms" +
                  (k.left != null ? $", layers written in {writeSeconds:F2}s" : "") +
                  $", {Time.realtimeSinceStartup - tQuiet:F2}s in all{VR180StereoEquirectHandler.MemoryNote()}{stripNote}");
    }

    // Builds a kept photo's textures and layer images with the usual code, by standing it in as the current
    // photo for a moment: swapped in, built, swapped back, all within this frame, so nothing else sees it
    // layers: the layers' eye textures (not yet given to the layers: Prefetch does that with SetImage, later);
    // otherwise the sphere's copy and fill maps
    void BuildKept(Kept k, Texture src, PhotoInfo info, VR180Adjustments.Values adj, bool layers)
    {
        float savedZoom = zoom;
        zoom = 1f;   // a kept photo never has a telephoto zoom baked into its layers
        SwapWithCurrent(k);
        try
        {
            currentInfo = info;
            adjust = adj;
            bool layerWanted = photoLayer != null && layerTextures != null && extractMaterial != null;
            if (layers)
            {
                deferLayerImages = true;
                if (layerWanted) BuildLayerTextures(src);
            }
            else
            {
                current = MakeMipmapped(src, layerWanted ? sphereHalvingsWithLayer : 0, info.fisheye, info.raw);
                BakeFillMaps();
            }
        }
        finally
        {
            deferLayerImages = false;
            SwapWithCurrent(k);
            zoom = savedZoom;
        }
    }

    // Whether a kept photo can still be shown as it is; lets it go if not
    bool KeptStillGood(Kept k)
    {
        var now = VR180Adjustments.Get(k.path);
        string why = null;
        if (!VR180Adjustments.Values.SameImage(now, k.adjust) || now.keepTilt != k.adjust.keepTilt) why = "its adjustments have changed";
        else if (k.sphere == null) why = "it has no picture";
        else if (k.left != null && !CanKeepLayers) why = "layers can't be kept just now";
        else if (k.left != null && !(VR180StereoEquirectHandler.HasImage(k.left) && VR180StereoEquirectHandler.HasImage(k.right)))
            why = "its layers lost the photo";
        if (why == null) { k.adjust = now; return true; }   // (convergence may follow a new default)
        Evict(k, why);
        return false;
    }

    // Shows a kept photo. With the sharp layer on view (the Quest), a crossfade between the two pairs of
    // layers: whichever pair is on top fades (in, or out), the other stays at full strength underneath, so
    // it's a true crossfade either way. Otherwise (PC, layer off, or the old one not on view), a quick blur
    // through on the sphere. The photo being left takes the kept one's place.
    IEnumerator SwitchToKept(Kept k, float tPush)
    {
        if (prefetching != null) yield return CancelPrefetch();   // preparing some other photo, no longer wanted
        string name = Path.GetFileName(k.path);
        // (Diagnostic, 19 November: set VR180StereoEquirectHandler.TraceFrames = 15 here to log, frame by frame,
        // every photo layer the headset is given as a switch starts.)
        bool oldShowing = useLayer && keptWithLayers && CanKeepLayers && !PanelOpen && !layerFading &&
                          photoLayer.gameObject.activeSelf &&
                          VR180StereoEquirectHandler.IsVisible(photoLayer) && VR180StereoEquirectHandler.IsVisible(photoLayerRight);
        if (layerFading) EndLayerFade();
        ResetZoom();
        float third = quickSwitchSeconds / 3f;

        if (!oldShowing)
        {
            HideLayer();
            if (!blurTransition) { if (viewerMaterial.GetFloat(FadeId) > 0.5f) yield return Fade(0f); }
            else if (current != null && viewerMaterial.GetFloat(HoldMixId) < 0.5f)
            {
                if (third > 0f) yield return Animate(BlurId, 1f, third);
                viewerMaterial.SetFloat(BlurId, 1f);
                Graphics.CopyTexture(current, 0, current.mipmapCount - 1, holdTex, 0, 0);
                viewerMaterial.SetFloat(HoldMixId, 1f);
            }
        }

        // The kept photo becomes the current one; the one being left takes its place in k
        CompositionLayer oldL = photoLayer, oldR = photoLayerRight;
        previousPath = currentPath;
        SwapWithCurrent(k);

        bool crossfaded = false;
        if (oldShowing)
        {
            bool fade = VR180StereoEquirectHandler.ColourScaleSupported && quickSwitchSeconds > 0f;
            // The incoming pair should start underneath (one appearing on top showed a black frame at the push),
            // so if it would be on top, the outgoing pair is raised above every photo layer. (Unity sorts layers
            // by their Order; orders only ever go up, by 2 at most per switch.)
            // The incoming pair goes on top, fading in from 0; the outgoing pair is never reordered and keeps its
            // place in the list of layers. (19 November: whenever a pair was put underneath the one on view, or the
            // one on view was raised, the headset dropped the one on view for a frame, apparently because its
            // position in the list changed: black, or the sphere showing through.)
            PutAboveAll(photoLayer, photoLayerRight, "the incoming pair, while still switched off");
            bool newOnTop = photoLayer.Order > oldL.Order;   // true unless the change was refused
            // The sphere follows the current photo's orientation (its levelling turn) from now on, while the old
            // photo's layer still covers it at the old one's; in layer mode the sphere is black where the photo is,
            // so where the two differ black showed through at the push (at the edges of view, or a diagonal band
            // when the two photos' roll differs). So for the crossfade the sphere shows the new photo in full.
            ApplySphere();
            viewerMaterial.SetFloat(LayerModeId, 0f);
            // Both start from how things are now: the new pair invisible (on top or underneath)
            SetPairAlpha(photoLayer, photoLayerRight, fade ? 0f : 1f);
            VR180StereoEquirectHandler.SetHeld(photoLayer, false);
            VR180StereoEquirectHandler.SetHeld(photoLayerRight, false);
            UpdateLayerPose();
            photoLayer.gameObject.SetActive(true);
            photoLayerRight.gameObject.SetActive(true);
            float tv = Time.realtimeSinceStartup;
            while (!(VR180StereoEquirectHandler.IsVisible(photoLayer) && VR180StereoEquirectHandler.IsVisible(photoLayerRight)) &&
                   Time.realtimeSinceStartup - tv < 2f)
                yield return null;
            crossfaded = VR180StereoEquirectHandler.IsVisible(photoLayer) && VR180StereoEquirectHandler.IsVisible(photoLayerRight);
            if (crossfaded && fade)
            {
                // The pair on top makes the crossfade; the one underneath only shows through the top one's
                // feathered edge, so it fades too, briefly, while it's covered: before the main fade if it's the
                // new one, after it if it's the old one. (Switching it at once flickered at the edges of view.)
                float edge = Mathf.Min(0.15f, quickSwitchSeconds * 0.5f);
                if (newOnTop)
                {
                    yield return FadePair(photoLayer, photoLayerRight, 0f, 1f, quickSwitchSeconds, false);
                    yield return FadePair(oldL, oldR, 1f, 0f, edge, false);
                }
                else
                {
                    yield return FadePair(photoLayer, photoLayerRight, 0f, 1f, edge, false);
                    yield return FadePair(oldL, oldR, 1f, 0f, quickSwitchSeconds, false);
                }
            }
            ApplySphere();
            oldL.gameObject.SetActive(false);
            oldR.gameObject.SetActive(false);
            SetPairAlpha(oldL, oldR, 1f);
            SetPairAlpha(photoLayer, photoLayerRight, 1f);
            if (crossfaded) StartCoroutine(EnterLayerModeWhenVisible(photoGeneration));   // the sphere back to the surround only
            if (!crossfaded)
            {
                Debug.LogWarning("VR180Viewer [kept]: the kept photo's layers didn't appear within 2 s; the sphere shows it meanwhile");
                viewerMaterial.SetFloat(LayerModeId, 0f);
                StartCoroutine(EnterLayerModeWhenVisible(photoGeneration));
            }
        }
        else
        {
            ApplySphere();
            if (!viewingFavourites && VR180Favourites.IsFavourite(currentPath)) badge.Corner();
            ShowTiltHint(Mathf.Max(0.5f, 2f * third));
            if (!blurTransition) yield return Fade(1f);
            else
            {
                viewerMaterial.SetFloat(BlurId, 1f);
                if (third > 0f) yield return Animate(HoldMixId, 0f, third);
                viewerMaterial.SetFloat(HoldMixId, 0f);
                if (third > 0f) yield return Animate(BlurId, 0f, third);
                viewerMaterial.SetFloat(BlurId, 0f);
            }
            if (useLayer && LayerReady && !PanelOpen) ShowLayer();
        }

        // Keep the photo just left, if it's complete (k now holds it)
        string keepWhy = null;
        if (!keepPrevious || keepLevel < 2) keepWhy = "";
        else if (k.path == null || k.sphere == null) keepWhy = "";
        else if (k.bakeZoom != 1f) keepWhy = "it had a telephoto zoom baked into its layers";
        else if (k.left != null && (k.eyeL != null || !VR180StereoEquirectHandler.HasImage(k.left) || !VR180StereoEquirectHandler.HasImage(k.right)))
            keepWhy = "its layers weren't fully written yet";
        if (keepWhy == null) k.ready = true;
        else Evict(k, keepWhy.Length > 0 ? keepWhy : "not keeping the previous photo");

        Debug.Log($"VR180Viewer [kept]: {name} shown {Time.realtimeSinceStartup - tPush:F2}s after the push " +
                  $"({(oldShowing ? (crossfaded ? $"{quickSwitchSeconds:F2}s crossfade of the layers" : "layers late") : "quick blur on the sphere")}, no decode)" +
                  VR180StereoEquirectHandler.MemoryNote());
        busy = false;
        VR180Thumbnails.FromViewed(currentPath, current, currentInfo.coverage, currentInfo.sideBySide, PhotoTilt);
        PlanKept();
    }

    // The system is short of memory: keep fewer photos for the rest of the session
    void OnLowMemory()
    {
        Debug.LogWarning("VR180Viewer: the system says memory is running low" + VR180StereoEquirectHandler.MemoryNote());
        if (kept.Count == 0 || keepLevel == 0) return;
        keepLevel--;
        string ahead = keepLevel >= 1 && preloadNext ? AheadPath() : null;
        foreach (var k in kept)
            if (k.path != null && (keepLevel == 0 || !Same(k.path, ahead))) Evict(k, "memory is low");
        Debug.LogWarning("VR180Viewer [kept]: memory is low, so for the rest of this session " +
                         (keepLevel == 1 ? "the previous photo isn't kept (the next is still prepared)" : "no photos are kept or prepared"));
    }

    void ShowTiltHint(float seconds)
    {
        if (tiltHint == null || tiltHintDegrees <= 0f || head == null || PhotoTilt == Quaternion.identity) return;
        Vector4 c = currentInfo.coverage;
        float lon = (c.x + c.y) * 0.5f, lat = (c.z + c.w) * 0.5f;
        Vector3 centre = Display * new Vector3(Mathf.Sin(lon) * Mathf.Cos(lat), Mathf.Sin(lat), Mathf.Cos(lon) * Mathf.Cos(lat));
        float centreEl = Mathf.Asin(Mathf.Clamp(centre.y, -1f, 1f)) * Mathf.Rad2Deg;
        float gazeEl = Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        float delta = centreEl - gazeEl;
        if (Mathf.Abs(centreEl) < tiltHintDegrees || Mathf.Abs(delta) < tiltHintDegrees) return;
        tiltHint.Show(delta > 0f, seconds);
        Debug.Log($"VR180Viewer: the photo's centre is {Mathf.Abs(centreEl):F0} deg {(centreEl > 0f ? "up" : "down")} " +
                  $"({Mathf.Abs(delta):F0} deg from your gaze), so a brief arrow points {(delta > 0f ? "up" : "down")}");
    }

    // Copies the decoded photo into a texture with a complete mip chain, generated on the GPU,
    // optionally halving its size first. The mips drive the sharp sampling and the soft edge fill.
    // Halves a texture step by step (each bilinear sample at half size averages a 2x2 block
    // cleanly). Returns a temporary RenderTexture for the caller to release, or null if n is 0.
    static RenderTexture HalveTemp(Texture src, int n)
    {
        Texture from = src;
        RenderTexture temp = null;
        for (int i = 0; i < n && from.width >= 4 && from.height >= 4; i++)
        {
            var half = RenderTexture.GetTemporary(from.width / 2, from.height / 2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            half.filterMode = FilterMode.Bilinear;
            Graphics.Blit(from, half);
            if (temp != null) RenderTexture.ReleaseTemporary(temp);
            temp = half;
            from = half;
        }
        return temp;
    }

    internal static RenderTexture MakeMipmapped(Texture src, int halvings, bool fisheye = false, VR180Fisheye.Raw raw = default)
    {
        RenderTexture temp = HalveTemp(src, halvings);
        Texture from = temp != null ? temp : src;

        var rt = new RenderTexture(from.width, from.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            name = "VR180 photo",
            useMipMap = true,
            autoGenerateMips = false,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
            anisoLevel = 8
        };
        rt.Create();
        Material convert = fisheye ? VR180Fisheye.ConversionMaterial(raw, from.width, from.height) : null;
        if (convert != null) Graphics.Blit(from, rt, convert);   // halved fisheye -> equirect at this size
        else Graphics.Blit(from, rt);
        rt.GenerateMips();
        if (temp != null) RenderTexture.ReleaseTemporary(temp);
        return rt;
    }

    // Makes the soft fill's pole-free copies of the current photo (one per eye; see
    // Hidden/VR180/FillBake), from the sphere's mipmapped copy. A few milliseconds on the GPU.
    void BakeFillMaps()
    {
        if (current == null) return;
        if (fillBakeMaterial == null)
        {
            var shader = Resources.Load<Shader>("VR180FillBake");
            if (shader == null) { Debug.LogWarning("VR180Viewer: Resources/VR180FillBake.shader is missing; the fill uses the photo's own mips"); return; }
            fillBakeMaterial = new Material(shader);
        }
        fillBakeMaterial.SetVector(CoverageId, currentInfo.coverage);
        fillBakeMaterial.SetFloat(StereoId, currentInfo.sideBySide ? 1f : 0f);
        fillBakeMaterial.SetFloat(FillMirrorId, viewerMaterial.GetFloat(FillMirrorId));
        fillBakeMaterial.SetFloat("_MapSize", FillMapSize);
        for (int eye = 0; eye < 2; eye++)
        {
            if (fillMaps[eye] == null)
            {
                fillMaps[eye] = new RenderTexture(FillMapSize, FillMapSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = eye == 0 ? "VR180 fill (left)" : "VR180 fill (right)",
                    useMipMap = true,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                fillMaps[eye].Create();
            }
            fillBakeMaterial.SetFloat(EyeId, eye);
            Graphics.Blit(current, fillMaps[eye], fillBakeMaterial);
            fillMaps[eye].GenerateMips();
        }
        // (ApplySphere gives them to the sphere's material)
    }

    // ------------------------------------------------------------------ compositor layer

    // The layer can be shown if we still hold the eye textures, or (with our own handler) if the
    // headset's swapchains already hold the photo after the textures were freed
    bool LayerReady => photoLayer != null && layerTextures != null &&
                       (layerLeft != null || (StereoLayers && VR180StereoEquirectHandler.HasImage(photoLayer)));

    // Two layers, one per eye, are only possible with our own layer handler (on the Quest)
    bool StereoLayers => photoLayerRight != null && layerTexturesRight != null && VR180StereoEquirectHandler.Active;

    void BuildLayerTextures(Texture fullSrc)
    {
        if (layerTextures == null || extractMaterial == null) return;

        RenderTexture small = HalveTemp(fullSrc, layerHalvings);
        Texture src = small != null ? small : fullSrc;

        int eyeW = currentInfo.sideBySide ? src.width / 2 : src.width;
        int eyeH = src.height;

        extractMaterial.SetVector(CoverageId, currentInfo.coverage);
        extractMaterial.SetFloat(StereoId, currentInfo.sideBySide ? 1f : 0f);
        extractMaterial.SetFloat(FisheyeId, currentInfo.fisheye ? 1f : 0f);
        if (currentInfo.fisheye) VR180Fisheye.SetParams(extractMaterial, currentInfo.raw, src.width, src.height);
        extractMaterial.SetFloat(EdgeFeatherId, viewerMaterial.GetFloat(EdgeFeatherId));
        extractMaterial.SetFloat(FlipYId, flipLayerVertically ? 1f : 0f);
        extractMaterial.SetFloat(ForceOpaqueId, forceOpaqueLayer ? 1f : 0f);
        ApplyAdjust(extractMaterial, adjust);   // the layer image carries the photo's adjustments
        // A telephoto zoom is baked into the image (the layer's angles stay unzoomed)
        layerBakeZoom = Telephoto && StereoLayers ? zoom : 1f;
        layerBakeCentre = zoomCentre;
        extractMaterial.SetVector(TeleZoomId, new Vector4(layerBakeZoom, zoomCentre.x, zoomCentre.y, 0f));

        // Reuse the existing eye textures if they're the right size, otherwise replace them
        bool reuse = !layerUsesTexture2D &&
                     layerLeft is RenderTexture l && l.width == eyeW && l.height == eyeH &&
                     layerRight is RenderTexture r && r.width == eyeW && r.height == eyeH &&
                     (layerRight != layerLeft) == currentInfo.sideBySide;
        if (reuse)
        {
            extractMaterial.SetFloat(EyeId, 0);
            Graphics.Blit(src, (RenderTexture)layerLeft, extractMaterial);
            if (currentInfo.sideBySide)
            {
                extractMaterial.SetFloat(EyeId, 1);
                Graphics.Blit(src, (RenderTexture)layerRight, extractMaterial);
            }
        }
        else
        {
            FreeLayerTextures();
            layerLeft = MakeEyeTexture(src, 0, eyeW, eyeH);
            layerRight = currentInfo.sideBySide ? MakeEyeTexture(src, 1, eyeW, eyeH) : layerLeft;
            layerTextures.LeftTexture = layerLeft;
            layerTextures.RightTexture = layerRight;
            if (layerTexturesRight != null)
            {
                // The right-eye layer reads its image from its own Left Texture slot
                layerTexturesRight.LeftTexture = layerRight;
                layerTexturesRight.RightTexture = layerRight;
            }
        }

        // With our own handler, the headset's swapchains are written once from these textures,
        // after which they can be freed (see ReleaseWhenResident)
        if (StereoLayers && !deferLayerImages)
        {
            VR180StereoEquirectHandler.SetImage(photoLayer, layerLeft);
            VR180StereoEquirectHandler.SetImage(photoLayerRight, layerRight);
        }

        ApplyLayerData(photoLayer);
        if (photoLayerRight != null) ApplyLayerData(photoLayerRight);

        Debug.Log($"VR180Viewer [layer]: eye textures {eyeW}x{eyeH} as {(layerUsesTexture2D ? "Texture2D" : "RenderTexture")}, " +
                  $"graphics {SystemInfo.graphicsDeviceType}, copy support {SystemInfo.copyTextureSupport}, " +
                  $"opaque {forceOpaqueLayer}, target eye {layerTextures.TargetEye}, reused {reuse}");
        CheckTexture(layerLeft, "left");
        CheckTexture(layerRight, "right");
        if (small != null) RenderTexture.ReleaseTemporary(small);
    }

    void ApplyLayerData(CompositionLayer layer)
    {
        if (!(layer.LayerData is EquirectMeshLayerData data))
        {
            Debug.LogError($"VR180Viewer: {layer.name}'s Layer Type should be Equirect.");
            return;
        }
        Vector4 c = currentInfo.coverage;
        data.Radius = EffectiveLayerRadius;
        data.CentralHorizontalAngle = c.y - c.x;
        data.UpperVerticalAngle = c.w;

        // Our own handler passes the angles through unchanged. Unity's handler (used over Link)
        // still negates Lower, which no longer matches Composition Layers 2.6, where Lower is
        // measured from the equator (negative = below). Without compensating, the headset gets
        // upper +90 and lower +90: a band of zero height, so nothing is visible.
        bool ours = VR180StereoEquirectHandler.Active;
        data.LowerVerticalAngle = (!ours && compensateOpenXRLowerAngle) ? -c.z : c.z;
        float sentLower = ours ? data.LowerVerticalAngle : -data.LowerVerticalAngle;
        Debug.Log($"VR180Viewer [layer]: {layer.name} equirect radius {data.Radius}, horizontal {data.CentralHorizontalAngle * Mathf.Rad2Deg:F1} deg, " +
                  $"upper {data.UpperVerticalAngle * Mathf.Rad2Deg:F1}, lower {data.LowerVerticalAngle * Mathf.Rad2Deg:F1} " +
                  $"(sent to headset as {sentLower * Mathf.Rad2Deg:F1}), blend {data.BlendType}, " +
                  $"handler {(ours ? "VR180 stereo" : "Unity")}");
    }

    // Reads back the centre pixel of a layer texture, to prove it actually contains the photo
    static void CheckTexture(Texture t, string label)
    {
        if (t == null) { Debug.LogWarning($"VR180Viewer [layer]: {label} texture is missing"); return; }
        if (!SystemInfo.supportsAsyncGPUReadback) return;
        AsyncGPUReadback.Request(t, 0, t.width / 2, 1, t.height / 2, 1, 0, 1, TextureFormat.RGBA32, r =>
        {
            if (r.hasError) { Debug.LogWarning($"VR180Viewer [layer]: couldn't read back the {label} texture"); return; }
            Color32 px = r.GetData<Color32>()[0];
            Debug.Log($"VR180Viewer [layer]: {label} centre pixel RGBA = {px.r},{px.g},{px.b},{px.a}");
        });
    }

    // One eye at full resolution, with the feathered alpha edge. Normally a plain GPU-only
    // Texture2D; optionally the RenderTexture itself, for diagnosis.
    Texture MakeEyeTexture(Texture src, int eye, int w, int h)
    {
        extractMaterial.SetFloat(EyeId, eye);

        if (!layerUsesTexture2D)
        {
            var keep = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = eye == 0 ? "VR180 layer left (RT)" : "VR180 layer right (RT)",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            keep.Create();
            Graphics.Blit(src, keep, extractMaterial);
            return keep;
        }

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(src, rt, extractMaterial);

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
        {
            name = eye == 0 ? "VR180 layer left" : "VR180 layer right",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        tex.Apply(false, true);                      // drop the CPU copy; we only need it on the GPU
        Graphics.CopyTexture(rt, 0, 0, tex, 0, 0);   // GPU-to-GPU copy, no readback
        RenderTexture.ReleaseTemporary(rt);
        return tex;
    }

    void FreeLayerTextures()
    {
        if (layerTextures != null)
        {
            layerTextures.LeftTexture = null;
            layerTextures.RightTexture = null;
        }
        if (layerTexturesRight != null)
        {
            layerTexturesRight.LeftTexture = null;
            layerTexturesRight.RightTexture = null;
        }
        if (layerRight != null && layerRight != layerLeft) DestroyTexture(layerRight);
        if (layerLeft != null) DestroyTexture(layerLeft);
        layerLeft = layerRight = null;
    }

    static void DestroyTexture(Texture t)
    {
        if (t is RenderTexture rt) rt.Release();
        Destroy(t);
    }

    // fadeIn: start invisible; FadeLayerIn brings it up (Show only). Otherwise it appears at full strength.
    void ShowLayer(bool fadeIn = false)
    {
        if (!LayerReady) return;
        layerFading = fadeIn;
        SetPairAlpha(photoLayer, photoLayerRight, fadeIn ? 0f : 1f);
        VR180StereoEquirectHandler.SetHeld(photoLayer, false);
        VR180StereoEquirectHandler.SetHeld(photoLayerRight, false);
        UpdateLayerPose();
        photoLayer.gameObject.SetActive(true);
        if (StereoLayers) photoLayerRight.gameObject.SetActive(true);
        // With our own handler the layer is held back until the headset has the whole photo,
        // so the sphere must keep drawing it until then (otherwise there's a flash of black)
        if (StereoLayers) StartCoroutine(EnterLayerModeWhenVisible(photoGeneration));
        else viewerMaterial.SetFloat(LayerModeId, 1f);
        Debug.Log($"VR180Viewer [layer]: shown, order {photoLayer.Order}, {(StereoLayers ? "stereo (two layers)" : "one layer")}, head at {head.position}");
        if (StereoLayers)
        {
            if (layerLeft != null) StartCoroutine(ReleaseWhenResident(photoGeneration));
        }
        else
        {
            StartCoroutine(NudgeLayer());
        }
    }

    // Fades the sharp layer in over the sphere (layerFadeSeconds), once the headset holds the photo.
    // The layers' colour scale does the fading (VR180StereoEquirectHandler.SetAlpha).
    IEnumerator FadeLayerIn(int generation)
    {
        float t0 = Time.realtimeSinceStartup;
        while (!(VR180StereoEquirectHandler.IsVisible(photoLayer) && VR180StereoEquirectHandler.IsVisible(photoLayerRight)))
        {
            if (generation != photoGeneration) yield break;   // a new photo has taken over (it resets the alpha)
            if (!StillFading() || Time.realtimeSinceStartup - t0 > 10f) { EndLayerFade(); yield break; }
            yield return null;
        }
        float waited = Time.realtimeSinceStartup - t0;
        for (float t = 0f; t < layerFadeSeconds; t += Time.deltaTime)
        {
            if (generation != photoGeneration) yield break;
            if (!StillFading()) { EndLayerFade(); yield break; }
            SetPairAlpha(photoLayer, photoLayerRight, Mathf.SmoothStep(0f, 1f, t / layerFadeSeconds));
            yield return null;
        }
        EndLayerFade();
        Debug.Log($"VR180Viewer [layer]: faded in over {layerFadeSeconds:F2}s, after waiting {waited:F2}s for the headset to hold the photo");
    }

    bool StillFading() => layerFading && useLayer && photoLayer != null && photoLayer.gameObject.activeSelf;

    void EndLayerFade()
    {
        layerFading = false;
        SetPairAlpha(photoLayer, photoLayerRight, 1f);
    }

    IEnumerator EnterLayerModeWhenVisible(int generation)
    {
        float t0 = Time.realtimeSinceStartup;
        while (!(VR180StereoEquirectHandler.IsVisible(photoLayer) && VR180StereoEquirectHandler.IsVisible(photoLayerRight)))
        {
            if (generation != photoGeneration || !useLayer || !photoLayer.gameObject.activeSelf) yield break;
            if (Time.realtimeSinceStartup - t0 > 10f)
            {
                Debug.LogWarning("VR180Viewer [layer]: layer never became visible; staying on the sphere");
                yield break;
            }
            yield return null;
        }
        yield return null;   // let the layer's first frame reach the headset before the sphere steps aside
        // Fading in: the sphere keeps drawing the photo underneath until the layer is fully opaque
        while (layerFading)
        {
            if (generation != photoGeneration || !useLayer || !photoLayer.gameObject.activeSelf) yield break;
            yield return null;
        }
        if (generation == photoGeneration && useLayer && photoLayer.gameObject.activeSelf)
        {
            viewerMaterial.SetFloat(LayerModeId, 1f);
            Debug.Log($"VR180Viewer [layer]: layer visible {Time.realtimeSinceStartup - t0:F2}s after showing");
        }
    }

    // Once the headset's swapchain images all hold the photo, the full-size eye textures
    // (about 190 MB each) are no longer needed. Gives up quietly if the photo changes first.
    IEnumerator ReleaseWhenResident(int generation)
    {
        float t0 = Time.realtimeSinceStartup;
        while (generation == photoGeneration && Time.realtimeSinceStartup - t0 < 15f)
        {
            if (VR180StereoEquirectHandler.IsResident(photoLayer) &&
                (photoLayerRight == null || VR180StereoEquirectHandler.IsResident(photoLayerRight)))
            {
                VR180StereoEquirectHandler.ReleaseImage(photoLayer);
                if (photoLayerRight != null) VR180StereoEquirectHandler.ReleaseImage(photoLayerRight);
                FreeLayerTextures();
                Debug.Log($"VR180Viewer [layer]: eye textures freed {Time.realtimeSinceStartup - t0:F2}s after showing");
                yield break;
            }
            yield return null;
        }
        if (generation == photoGeneration)
            Debug.LogWarning("VR180Viewer [layer]: swapchains never reported the photo as resident; keeping the eye textures");
    }

    // The layer's own image buffers are created a frame or two after it's switched on, and
    // the layer code only copies our textures into them when it thinks the textures have
    // changed. Marking them as changed for a few frames makes sure a copy lands after the
    // buffers exist.
    IEnumerator NudgeLayer()
    {
        for (int i = 0; i < 8; i++)
        {
            yield return null;
            if (layerLeft == null) yield break;
            layerLeft.IncrementUpdateCount();
            if (layerRight != null && layerRight != layerLeft) layerRight.IncrementUpdateCount();
        }
        Debug.Log("VR180Viewer [layer]: nudged");
    }

    // Single-image swapchains can't be rewritten, so the next photo needs new ones anyway; freeing the old
    // pair early keeps memory down while it decodes. The handler only frees a layer hidden for a few frames.
    void DiscardLayerSwapchains()
    {
        if (!StereoLayers) return;
        VR180StereoEquirectHandler.DiscardSwapchain(photoLayer);
        VR180StereoEquirectHandler.DiscardSwapchain(photoLayerRight);
    }

    void HideLayer()
    {
        if (photoLayer != null) photoLayer.gameObject.SetActive(false);
        if (photoLayerRight != null) photoLayerRight.gameObject.SetActive(false);
        viewerMaterial.SetFloat(LayerModeId, 0f);
    }

    void UpdateLayerPose()
    {
        if (photoLayer == null || head == null) return;
        // Turn the layer so its centre matches the centre of the photo's coverage
        float centreLon = (currentInfo.coverage.x + currentInfo.coverage.y) * 0.5f * Mathf.Rad2Deg;
        // Zoom (our handler only, i.e. on the Quest): a cropped part of the image over scaled angles,
        // unless a telephoto zoom is baked into the image itself
        bool zoomed = zoom != 1f && VR180StereoEquirectHandler.Active && layerBakeZoom == 1f;
        Rect crop = default;
        float central = 0f, upper = 0f, lower = 0f;
        if (zoomed) ZoomedLayerView(out crop, out central, out upper, out lower, out centreLon);
        VR180StereoEquirectHandler.CropFromBottom = !SystemInfo.graphicsUVStartsAtTop ^ zoomCropFlipY;
        VR180StereoEquirectHandler.SetZoom(photoLayer, zoomed, crop, central, upper, lower);
        VR180StereoEquirectHandler.SetZoom(photoLayerRight, zoomed, crop, central, upper, lower);
        bool filter = layerFilterOn && (layerFilterFromZoom <= 0f || zoom > layerFilterFromZoom + 1e-4f);
        VR180StereoEquirectHandler.SettingsFlags = filter ? LayerFilterFlags(layerFilter) : 0;
        VR180StereoEquirectHandler.FadeScalesColour = layerFadeScalesColour;
        Quaternion rot = Display * Quaternion.Euler(0f, centreLon, 0f);
        // Convergence: turn the left eye's image right and the right eye's left by half each
        // (positive = crossed = nearer), matching the sphere shader. Only with one layer per eye.
        // Zoom magnifies it too, as the shader applies it within the photo.
        float half = StereoLayers ? adjust.convergence * 0.5f * (zoomed || layerBakeZoom != 1f ? zoom : 1f) : 0f;
        // Head-turn parallax: each eye's layer centred where that eye would be had the head not
        // turned (with one layer for both eyes, the head's); otherwise centred on the head
        float radius = EffectiveLayerRadius;
        SetLayerRadius(photoLayer, radius);
        SetLayerRadius(photoLayerRight, radius);
        Vector3 posL = head.position, posR = head.position;
        if (parallaxDistance > 0f)
        {
            if (StereoLayers) EyePositions(out posL, out posR);
            posL -= parallaxOffset;
            posR -= parallaxOffset;
        }
        // Roll compensation turns both about the line of sight (identity when it's off)
        // (Convergence turns about the photo's own vertical, which is the world's unless a raw photo is levelled by the sphere.)
        photoLayer.transform.SetPositionAndRotation(posL, viewRoll * rot * Quaternion.Euler(0f, half, 0f));
        if (photoLayerRight != null) photoLayerRight.transform.SetPositionAndRotation(posR, viewRoll * rot * Quaternion.Euler(0f, -half, 0f));
    }

    // ------------------------------------------------------------------ metadata

    // Reads the Google VR180 / GPano XMP fields near the start of the file.
    // Falls back to 180 x 180 degrees, side-by-side, if they're missing.
    internal static PhotoInfo ReadMetadata(string path)
    {
        const float H = Mathf.PI / 2f;
        var info = new PhotoInfo { coverage = new Vector4(-H, H, -H, H), sideBySide = true, baseline = Vector3.right, tilt = Quaternion.identity };

        string text;
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[(int)Math.Min(fs.Length, 256 * 1024)];
            int read = 0, n;
            while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
            text = Encoding.ASCII.GetString(buf, 0, read);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer: couldn't read metadata from {path}: {e.Message}");
            return info;
        }

        string stereo = Field(text, "StereoMode");
        if (stereo != null)
        {
            info.sideBySide = stereo.Equals("left-right", StringComparison.OrdinalIgnoreCase);
            if (stereo.Equals("top-bottom", StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning("VR180Viewer: top-bottom stereo isn't supported yet; showing as mono.");
        }

        float fullW = Num(text, "FullPanoWidthPixels");
        float fullH = Num(text, "FullPanoHeightPixels");
        float cropW = Num(text, "CroppedAreaImageWidthPixels");
        float cropH = Num(text, "CroppedAreaImageHeightPixels");
        float left  = Num(text, "CroppedAreaLeftPixels");
        float top   = Num(text, "CroppedAreaTopPixels");

        if (fullW > 0 && fullH > 0 && cropW > 0 && cropH > 0)
        {
            float lonMin = left / fullW * 2f * Mathf.PI - Mathf.PI;
            float lonMax = (left + cropW) / fullW * 2f * Mathf.PI - Mathf.PI;
            float latMax = H - top / fullH * Mathf.PI;
            float latMin = H - (top + cropH) / fullH * Mathf.PI;
            info.coverage = new Vector4(lonMin, lonMax, latMin, latMax);
        }

        // An unprocessed QooCam photo (two fisheye circles): always 180 x 180 side-by-side once converted
        if (VR180Fisheye.TryGetRaw(path, out info.raw))
        {
            info.fisheye = true;
            info.sideBySide = true;
            info.coverage = new Vector4(-H, H, -H, H);
            info.baseline = VR180Fisheye.Baseline(info.raw);
            info.tilt = VR180Fisheye.DisplayTilt(info.raw);
        }
        // A QooCam Studio photo with the camera's gravity reading copied back in (qoocam_tag_studio.py):
        // already stitched, in the camera's own frame, so it only needs the levelling turn
        else if (VR180Fisheye.TryGetStitchedTilt(path, out info.raw))
            info.tilt = VR180Fisheye.StitchedTilt(info.raw);
        return info;
    }

    // Handles both attribute form (GPano:X="...") and element form (<GPano:X>...</GPano:X>)
    static string Field(string text, string name)
    {
        var m = Regex.Match(text, $@"GPano:{name}\s*=\s*""([^""]*)""");
        if (!m.Success) m = Regex.Match(text, $@"<GPano:{name}>([^<]*)</GPano:{name}>");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    static float Num(string text, string name) =>
        float.TryParse(Field(text, name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

    // ------------------------------------------------------------------ view

    void CreateSphere()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "PhotoSphere";
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(head, false);   // follows the head; the shader keeps the photo world-fixed
        go.transform.localScale = Vector3.one * (sphereRadius * 2f);

        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = viewerMaterial;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    // Puts the centre of the photo straight ahead of where you're looking (yaw only,
    // so the horizon stays level).
    void Recentre()
    {
        if (head == null || viewerMaterial == null) return;
        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
        imageToWorld = Quaternion.LookRotation(f.normalized, Vector3.up);
        viewerMaterial.SetMatrix(WorldToImageId, Matrix4x4.Rotate(Quaternion.Inverse(viewRoll * Display)));
        if (photoLayer != null && photoLayer.gameObject.activeSelf) UpdateLayerPose();
    }

    // The headset's own recentre (holding the Quest button) resets its "local" tracking space to
    // where you're facing. Ask for that space (Device), rather than one fixed to the room, so it
    // happens; and when it does, recentre the photo too, which also clears any recentre made with
    // the thumbstick. (The new head pose arrives a frame or two after the event, hence the delay.)
    void WatchHeadsetRecentre()
    {
        SubsystemManager.GetSubsystems(inputSubsystems);
        foreach (var sub in inputSubsystems)
        {
            var mode = sub.GetTrackingOriginMode();
            if (mode != UnityEngine.XR.TrackingOriginModeFlags.Device &&
                (sub.GetSupportedTrackingOriginModes() & UnityEngine.XR.TrackingOriginModeFlags.Device) != 0)
            {
                bool ok = sub.TrySetTrackingOriginMode(UnityEngine.XR.TrackingOriginModeFlags.Device);
                Debug.Log($"VR180Viewer: tracking origin {mode} -> Device {(ok ? "done" : "refused")}");
            }
            sub.trackingOriginUpdated += OnTrackingOriginUpdated;
        }
    }

    void OnTrackingOriginUpdated(UnityEngine.XR.XRInputSubsystem sub)
    {
        if (centredOnce) recentreAt = Time.unscaledTime + 0.15f;
    }

    // Eases a material float to a target value
    IEnumerator Animate(int id, float target, float seconds)
    {
        float start = viewerMaterial.GetFloat(id);
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            viewerMaterial.SetFloat(id, Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, t / seconds)));
            yield return null;
        }
        viewerMaterial.SetFloat(id, target);
    }

    IEnumerator Fade(float target)
    {
        float start = viewerMaterial.GetFloat(FadeId);
        for (float t = 0f; t < fadeSeconds; t += Time.deltaTime)
        {
            viewerMaterial.SetFloat(FadeId, Mathf.Lerp(start, target, t / fadeSeconds));
            yield return null;
        }
        viewerMaterial.SetFloat(FadeId, target);
    }
}
