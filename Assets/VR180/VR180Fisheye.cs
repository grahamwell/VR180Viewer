// VR180Fisheye: shows unprocessed QooCam 3 Ultra photos (two fisheye circles side by side, as
// imported from the camera) by converting them on the GPU into the 180 x 180 degree side-by-side
// equirectangular geometry QooCam Studio would make. The viewer converts straight into the
// layer's eye images (EyeExtract's fisheye mode) and into the sphere's copy (FisheyeToEquirect),
// so no full-size converted image is ever held; flat export uses ConvertIfRaw.
// The lens model itself is in VR180Fisheye.hlsl.
//
// Calibration: each camera's own, from the file QooCam Studio writes when you calibrate it,
// %APPDATA%\QooCamStudio\calibration\<serial>\<serial>_refined.xml. The app looks for
// <serial>_refined.xml (or <serial>.xml) for the serial number in each photo's EXIF (SN=):
//   1. in the photo's own folder,
//   2. in the calibration folder, Pictures/QooCam Calibration on the Quest (or a <serial>
//      subfolder of it, if the whole folder was copied),
//   3. on Windows (Editor and PC), in QooCam Studio's own calibration folder.
// The file describes two OpenCV fisheye cameras ("theta" distortion) for a w x h image, with a
// rotation per lens in OpenCV axes (y down); converted here to x right, y up, z ahead.
// Without a file, the built-in calibration (Graham's camera, QU9302327745) is used, with a warning.
//
// Levelling: the minimal rotation that makes the camera's gravity reading (IMUHEX in the EXIF
// comment, accelerometer at 2048 per g) point straight down. Can be switched off for all photos
// (Levelling) or for one photo (its "Level the horizon" box in the adjustments panel: keepTilt).
// Since 12 November it's done by turning the viewer's sphere and layers (DisplayTilt): the picture
// is converted in the camera's own frame, so nothing the lenses saw is lost, and a shot taken
// pointing well down (or up) can be looked at all the way to where the camera pointed, with a
// level horizon. LevelBySphere = false puts back the old way (levelling inside the conversion,
// which cuts off whatever was beyond 90 degrees above or below the horizon).
//
// Lens patch: each lens sees part of the other lens at the edge of its view (on Graham's camera the
// left lens sees the right lens's barrel from about 83 degrees off its axis, so it shows inside the
// 180-degree picture; the right lens only sees the left one from about 94 degrees, just outside it).
// Where a lens is obstructed, the shader takes that direction from the other lens instead. The
// obstruction is measured from Graham's camera (10 November 2026), in each lens's own frame.
//
// Photos already stitched by QooCam Studio (14 November): Studio drops the gravity reading from its
// output, so qoocam_tag_studio.py (run on the PC) copies IMUHEX back in as a JPEG comment. A stitched
// photo that has one (TryGetStitchedTilt) is levelled by turning the sphere, exactly like a raw photo:
// with Studio's Horizon Correction off, its picture is in the same camera frame as our own conversion
// (measured: within 0.02 degrees). A photo Studio levelled itself must not be tagged (or untick its
// "Level the horizon" box), or it is turned twice.
//
// Not done (yet): lens shading (GAINMAP0/1 in the EXIF comment), Studio's colour processing.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;

public static class VR180Fisheye
{
    /// Level the horizon from the camera's gravity reading, as QooCam Studio does
    public static bool Levelling = true;

    /// Level by turning the sphere and layers (true), or inside the conversion as before (false)
    public static bool LevelBySphere = true;

    /// Fill in where each lens sees the other one, from the other lens (see the notes at the top)
    public static bool LensPatch = true;

    // Where each lens sees the other one, in degrees in its own frame (x right, y up, z ahead):
    // (inner edge, measured from the lens's axis towards the other lens; centre latitude; half
    // height; corner radius). A is the other lens's barrel, B the housing round it, which starts
    // further out and is taller. Measured on Graham's camera; the patch extends OccMargin beyond
    // these, then fades out over OccFeather, so other cameras built the same way should be covered.
    static readonly Vector4[] OccA = { new Vector4(83.1f, 1.6f, 15.3f, 4f), new Vector4(93.75f, 0.3f, 14.7f, 4f) };
    static readonly Vector4[] OccB = { new Vector4(89.6f, 0f, 35f, 10f),    new Vector4(100.3f, 0f, 35f, 10f) };
    const float OccMargin = 2f, OccFeather = 3f;

    /// Name of the folder (under the photo root) where calibration files go
    public const string CalibrationFolderName = "QooCam Calibration";

    /// Full path of the calibration folder; set by the viewer
    public static string CalibrationFolder;

    internal struct Lens
    {
        public float f, cx, cy;        // pixels, for an image Calibration.width wide, from its top-left corner
        public Vector4 k;              // distortion k1..k4
        public Matrix4x4 bodyToLens;   // camera body -> lens, x right, y up, z ahead
    }

    internal class Calibration
    {
        public float width;
        public Lens[] lenses;
        public string source;
    }

    // Graham's camera, QU9302327745; fitted to a QooCam Studio conversion (9 November 2026) and
    // matching Studio's own file for it to within a pixel and 0.02 degrees
    const string BuiltInSerial = "QU9302327745";
    static readonly Vector4 BuiltInDistortion = new Vector4(-0.011315f, 0.001868f, -0.003552f, 0.000244f);
    static readonly Calibration BuiltIn = new Calibration
    {
        width = 13888f,
        source = "built in (camera " + BuiltInSerial + ")",
        lenses = new[]
        {
            new Lens
            {
                f = 2150.697f, cx = 3573.397f, cy = 3473.242f, k = BuiltInDistortion,
                bodyToLens = Rows(0.9990710f, -0.0430923f, -0.0003395f,
                                  0.0430922f,  0.9990711f, -0.0001607f,
                                  0.0003461f,  0.0001459f,  0.9999999f)
            },
            new Lens
            {
                f = 2150.718f, cx = 10537.972f, cy = 3469.720f, k = BuiltInDistortion,
                bodyToLens = Rows(0.9992503f, -0.0385179f, -0.0039083f,
                                  0.0385409f,  0.9992391f,  0.0059817f,
                                  0.0036749f, -0.0061279f,  0.9999745f)
            },
        }
    };

    const float CircleRadiusFraction = 3520f / 13888f;   // beyond this (of the image width): outside the lens's image circle, black

    static Matrix4x4 Rows(float a, float b, float c, float d, float e, float f, float g, float h, float i)
    {
        var m = Matrix4x4.identity;
        m.SetRow(0, new Vector4(a, b, c, 0f));
        m.SetRow(1, new Vector4(d, e, f, 0f));
        m.SetRow(2, new Vector4(g, h, i, 0f));
        return m;
    }

    static Material material;

    /// What a raw photo needs for conversion: the camera's gravity reading as an "up" direction
    /// in the camera's frame (x right, y up, z ahead), if the photo has one, and the calibration
    public struct Raw
    {
        public Vector3 up;
        public bool hasGravity;
        public string serial;
        /// This photo is to be shown as the camera was held (its "Level the horizon" box unticked)
        public bool keepTilt;
        /// Why the camera's own calibration isn't being used (no file, or an unreadable one); null if all's well
        public string calibrationProblem;
        internal Calibration calibration;
    }

    /// True for an unprocessed QooCam photo
    public static bool TryGetRaw(string path, out Raw raw)
    {
        raw = default;
        if (!IsRaw(path, out raw.up, out raw.hasGravity, out raw.serial)) return false;
        raw.calibration = CalibrationFor(raw.serial, Path.GetDirectoryName(path), out raw.calibrationProblem);
        raw.keepTilt = VR180Adjustments.Get(path).keepTilt;   // per photo, from the adjustments panel

        Tilt(raw, out float pitch, out float roll);
        Debug.Log($"VR180Fisheye: {Path.GetFileName(path)} is an unprocessed dual-fisheye photo from camera {raw.serial ?? "(no serial)"}; " +
                  $"calibration {raw.calibration.source}; " +
                  (raw.hasGravity ? $"camera tilt {pitch:F1} deg pitch, {roll:F1} deg roll, " +
                                    $"{(IsLevelled(raw) ? (LevelBySphere ? "levelled by turning the sphere" : "levelled in the conversion") : raw.keepTilt && Levelling ? "not levelled (this photo's choice)" : "not levelled")}"
                                  : "no gravity reading"));
        return true;
    }

    /// The camera's tilt from its gravity reading, in degrees (pitch + = nose up, roll + = turned clockwise)
    public static void Tilt(Raw raw, out float pitch, out float roll)
    {
        pitch = Mathf.Atan2(raw.up.z, raw.up.y) * Mathf.Rad2Deg;
        roll = Mathf.Atan2(-raw.up.x, raw.up.y) * Mathf.Rad2Deg;
    }

    /// Whether this photo's horizon is levelled when converted
    public static bool IsLevelled(Raw raw) => Levelling && raw.hasGravity && !raw.keepTilt;

    /// The rotation that levels the photo: view direction (level) -> camera body direction
    static Quaternion LevelRotation(Raw raw) => IsLevelled(raw) ? Quaternion.FromToRotation(Vector3.up, raw.up) : Quaternion.identity;

    /// The rotation applied inside the conversion (only with LevelBySphere off): converted picture -> camera body
    static Quaternion ConversionRotation(Raw raw) => LevelBySphere ? Quaternion.identity : LevelRotation(raw);

    /// The rotation the viewer applies to the converted picture to level it (only with LevelBySphere on):
    /// converted picture's frame -> level frame. Identity if the photo isn't levelled.
    public static Quaternion DisplayTilt(Raw raw) => LevelBySphere ? Quaternion.Inverse(LevelRotation(raw)) : Quaternion.identity;

    /// True for a photo stitched by QooCam Studio that has had the camera's gravity reading copied back
    /// into it (qoocam_tag_studio.py). Only up, hasGravity, serial and keepTilt are filled in: the photo
    /// needs no conversion, just the levelling turn (StitchedTilt).
    public static bool TryGetStitchedTilt(string path, out Raw raw)
    {
        raw = default;
        raw.up = Vector3.up;
        string text = ReadHead(path);
        if (text == null || !text.Contains("PRODUCT=QooCam") || !text.Contains("STITCHED=1") || !text.Contains("IMUHEX=")) return false;
        if (!TryGravity(text, path, out raw.up)) return false;
        raw.hasGravity = true;
        var sn = Regex.Match(text, @"\bSN=([A-Za-z0-9]+)");
        if (sn.Success) raw.serial = sn.Groups[1].Value;
        raw.keepTilt = VR180Adjustments.Get(path).keepTilt;   // per photo, from the adjustments panel

        Tilt(raw, out float pitch, out float roll);
        Debug.Log($"VR180Fisheye: {Path.GetFileName(path)} is a QooCam Studio photo carrying the camera's tilt " +
                  $"({pitch:F1} deg pitch, {roll:F1} deg roll); " +
                  (IsLevelled(raw) ? "levelled by turning the sphere" : raw.keepTilt && Levelling ? "not levelled (this photo's choice)" : "not levelled"));
        return true;
    }

    /// The rotation the viewer applies to such a photo to level it: photo's frame -> level frame.
    /// Identity if the photo isn't levelled. (Always by turning the sphere, whatever LevelBySphere says.)
    public static Quaternion StitchedTilt(Raw raw) => Quaternion.Inverse(LevelRotation(raw));

    /// The camera's stereo baseline (its left-to-right axis) in the converted picture's frame
    /// (x right, y up, z ahead). Just x unless levelling turned the picture inside the conversion;
    /// then it's tilted by the camera's roll. (Levelled by the sphere, the picture is the camera's own frame.)
    public static Vector3 Baseline(Raw raw) => Quaternion.Inverse(ConversionRotation(raw)) * Vector3.right;

    // The camera's own image size (the camera JPEG and the DNG are both this)
    const float CameraWidth = 13888f, CameraHeight = 6944f;

    /// True if an image of this size can be a raw photo: 2:1, allowing for a few pixels trimmed from each edge
    public static bool PlausibleSize(int width, int height) => height > 0 && Mathf.Abs(width - 2f * height) <= 0.01f * width;

    /// Where the camera's full frame sits in a source image of this size: its scale (1 = full size, 0.5 = halved)
    /// and the border trimmed from each side, in source pixels. A raw developer may drop a few pixels from every
    /// edge (RawTherapee drops 4 unless its demosaicing Border is 0); that's a trim, not a smaller picture.
    static void SourceFraming(int sourceWidth, int sourceHeight, out float scale, out float trimX, out float trimY)
    {
        scale = sourceWidth / CameraWidth;
        trimX = trimY = 0f;
        if (sourceHeight <= 0) return;
        float p = Mathf.Pow(2f, Mathf.Round(Mathf.Log(scale, 2f)));   // the nearest of 1, 1/2, 1/4...
        float tx = (CameraWidth * p - sourceWidth) * 0.5f, ty = (CameraHeight * p - sourceHeight) * 0.5f;
        if (tx >= 0f && ty >= 0f && tx <= 32f * p && ty <= 32f * p)
        {
            scale = p;
            trimX = tx;
            trimY = ty;
        }
    }

    /// Sets a material's lens parameters (VR180Fisheye.hlsl) for a source texture of this size
    /// (height 0 = unknown: then the image is assumed to be the whole frame, scaled)
    public static void SetParams(Material m, Raw raw, int sourceWidth, int sourceHeight = 0)
    {
        Calibration cal = raw.calibration ?? BuiltIn;
        SourceFraming(sourceWidth, sourceHeight, out float scale, out float trimX, out float trimY);
        float s = scale * CameraWidth / cal.width;   // calibration pixels -> source pixels
        Matrix4x4 levelling = Matrix4x4.Rotate(ConversionRotation(raw));
        for (int eye = 0; eye < 2; eye++)
        {
            Lens lens = cal.lenses[eye];
            m.SetMatrix(eye == 0 ? "_M0" : "_M1", lens.bodyToLens * levelling);
            m.SetVector(eye == 0 ? "_Lens0" : "_Lens1", new Vector4(lens.f * s, lens.cx * s - trimX, lens.cy * s - trimY,
                                                                    CircleRadiusFraction * CameraWidth * scale));
            m.SetVector(eye == 0 ? "_K0" : "_K1", lens.k);
            m.SetVector(eye == 0 ? "_OccA0" : "_OccA1", OccA[eye]);
            m.SetVector(eye == 0 ? "_OccB0" : "_OccB1", OccB[eye]);
        }
        m.SetFloat("_LensPatch", LensPatch ? 1f : 0f);
        m.SetVector("_OccFeather", new Vector4(OccMargin, OccFeather, 0f, 0f));
    }

    /// The FisheyeToEquirect material, set up for a source texture of this size (null if the shader is missing)
    public static Material ConversionMaterial(Raw raw, int sourceWidth, int sourceHeight = 0)
    {
        if (material == null)
        {
            var shader = Resources.Load<Shader>("VR180FisheyeToEquirect");
            if (shader == null)
            {
                Debug.LogError("VR180Fisheye: Resources/VR180FisheyeToEquirect.shader is missing");
                return null;
            }
            material = new Material(shader);
        }
        SetParams(material, raw, sourceWidth, sourceHeight);
        return material;
    }

    static bool IsRaw(string path, out Vector3 up, out bool hasGravity, out string serial)
    {
        up = Vector3.up;
        hasGravity = false;
        serial = null;
        string text = ReadHead(path);
        if (text == null) return false;

        if (!text.Contains("PRODUCT=QooCam") || !text.Contains("STITCHED=0") || !text.Contains("LENS=")) return false;

        var sn = Regex.Match(text, @"\bSN=([A-Za-z0-9]+)");
        if (sn.Success) serial = sn.Groups[1].Value;

        hasGravity = TryGravity(text, path, out up);
        return true;
    }

    /// The first 64 KB of a file as text (the EXIF block is at the start), or null if it can't be read
    static string ReadHead(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[(int)Math.Min(fs.Length, 64 * 1024)];
            int read = 0, n;
            while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
            return Encoding.ASCII.GetString(buf, 0, read);
        }
        catch (Exception) { return null; }
    }

    /// The camera's gravity reading (IMUHEX) as an "up" direction in the camera's frame; false (and up
    /// straight up) if there is none or it doesn't look like 1 g
    static bool TryGravity(string text, string path, out Vector3 up)
    {
        up = Vector3.up;
        // Nine little-endian 16-bit values; the accelerometer is the middle three
        var m = Regex.Match(text, "IMUHEX=([0-9A-Fa-f]{36})");
        if (!m.Success) return false;
        string hex = m.Groups[1].Value;
        short Value(int k) => (short)(int.Parse(hex.Substring(k * 4, 2), NumberStyles.HexNumber) |
                                      (int.Parse(hex.Substring(k * 4 + 2, 2), NumberStyles.HexNumber) << 8));
        var a = new Vector3(Value(3), Value(4), Value(5));
        float g = a.magnitude;
        if (g > 1500f && g < 2600f)   // about 1 g: the camera was held still
        {
            up = -a / g;
            return true;
        }
        Debug.LogWarning($"VR180Fisheye: {Path.GetFileName(path)}: gravity reading {a} looks wrong; not levelling");
        return false;
    }

    // ------------------------------------------------------------------ calibration files

    struct Cached { public Calibration cal; public string path; public DateTime stamp; }
    static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static Calibration CalibrationFor(string serial, string photoFolder, out string problem)
    {
        problem = null;
        if (string.IsNullOrEmpty(serial)) return BuiltIn;
        string unreadable = null;

        string path = FindCalibrationFile(serial, photoFolder);
        if (path != null)
        {
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (cache.TryGetValue(serial, out var c) && c.path == path && c.stamp == stamp) return c.cal;
            try
            {
                Calibration cal = ParseCalibration(path);
                cache[serial] = new Cached { cal = cal, path = path, stamp = stamp };
                Debug.Log($"VR180Fisheye: read the calibration for camera {serial} from {path}");
                return cal;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"VR180Fisheye: couldn't read the calibration file {path}: {e.Message}");
                unreadable = e.Message;
            }
        }

        if (string.Equals(serial, BuiltInSerial, StringComparison.OrdinalIgnoreCase)) return BuiltIn;   // the built-in one is this camera's

        problem = (unreadable != null
                      ? $"The calibration file for camera {serial} couldn't be read ({unreadable}),"
                      : $"There's no calibration file for camera {serial},") +
                  " so another camera's calibration is being used and the two eyes may not line up.\n\n" +
                  $"On the PC, copy {serial}_refined.xml from %APPDATA%\\QooCamStudio\\calibration\\{serial} " +
                  $"into the \"{CalibrationFolderName}\" folder in Pictures on the Quest.";

        if (warned.Add(serial))
            Debug.LogWarning($"VR180Fisheye: no calibration file for camera {serial}, so using the built-in one (another camera's). " +
                             $"Copy {serial}_refined.xml from %APPDATA%\\QooCamStudio\\calibration\\{serial} on the PC into " +
                             $"{CalibrationFolder ?? CalibrationFolderName}.");
        return BuiltIn;
    }

    static string FindCalibrationFile(string serial, string photoFolder)
    {
        var folders = new List<string>();
        if (!string.IsNullOrEmpty(photoFolder)) folders.Add(photoFolder);
        if (!string.IsNullOrEmpty(CalibrationFolder))
        {
            folders.Add(CalibrationFolder);
            folders.Add(Path.Combine(CalibrationFolder, serial));
        }
        if (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer)
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QooCamStudio", "calibration", serial));

        foreach (string folder in folders)
            foreach (string name in new[] { serial + "_refined.xml", serial + ".xml" })
            {
                string p = Path.Combine(folder, name);
                try { if (File.Exists(p)) return p; } catch (Exception) { }
            }
        return null;
    }

    // QooCam Studio's rig file: two <camera type="opencv-fisheye" distortion="theta"> elements,
    // each with fx, cx, cy, w, h attributes, <D> (k1..k4) and <R> (3x3, row by row, OpenCV axes)
    static Calibration ParseCalibration(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        XmlNodeList cams = doc.SelectNodes("/rig/cameras/camera");
        if (cams == null || cams.Count < 2) throw new Exception("expected two <camera> entries under <rig><cameras>");

        var cal = new Calibration { lenses = new Lens[2], source = path };
        for (int i = 0; i < 2; i++)
        {
            var c = (XmlElement)cams[i];
            string type = c.GetAttribute("type");
            if (type != "opencv-fisheye") throw new Exception($"camera {i} is type \"{type}\", not opencv-fisheye");
            string distortion = c.GetAttribute("distortion");
            if (distortion != "" && distortion != "theta") throw new Exception($"camera {i} has \"{distortion}\" distortion, not theta");
            if (c.GetAttribute("flip") == "yes") Debug.LogWarning($"VR180Fisheye: {path}: camera {i} is marked flip=\"yes\"; ignored");

            float w = Number(c, "w");
            if (i == 0) cal.width = w;
            else if (Mathf.Abs(w - cal.width) > 0.5f) throw new Exception("the two cameras have different image widths");

            float[] d = Numbers(c, "D", 4);
            float[] r = Numbers(c, "R", 9);
            // OpenCV axes (x right, y down, z ahead) to ours (y up): S R S with S = diag(1, -1, 1)
            cal.lenses[i] = new Lens
            {
                f = (Number(c, "fx") + Number(c, "fy")) * 0.5f,
                cx = Number(c, "cx"),
                cy = Number(c, "cy"),
                k = new Vector4(d[0], d[1], d[2], d[3]),
                bodyToLens = Rows( r[0], -r[1],  r[2],
                                  -r[3],  r[4], -r[5],
                                   r[6], -r[7],  r[8])
            };
        }
        if (cal.width <= 0f) throw new Exception("no image width");
        return cal;
    }

    static float Number(XmlElement e, string attribute)
    {
        if (!float.TryParse(e.GetAttribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            throw new Exception($"missing or bad {attribute}=\"...\"");
        return v;
    }

    static float[] Numbers(XmlElement e, string child, int count)
    {
        XmlElement node = e[child];
        if (node == null) throw new Exception($"missing <{child}>");
        string[] parts = node.InnerText.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < count) throw new Exception($"<{child}> should have {count} numbers");
        var v = new float[count];
        for (int i = 0; i < count; i++)
            v[i] = float.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
        return v;
    }

    // ------------------------------------------------------------------ flat export

    /// If path is an unprocessed QooCam photo, converts the decoded src into a new 180 x 180 side-by-side
    /// RenderTexture (and destroys src). Otherwise returns src unchanged.
    public static Texture ConvertIfRaw(Texture2D src, string path)
    {
        if (src == null || !TryGetRaw(path, out Raw raw)) return src;
        if (!PlausibleSize(src.width, src.height))
        {
            Debug.LogWarning($"VR180Fisheye: {Path.GetFileName(path)} is {src.width}x{src.height}, not 2:1; left as it is");
            return src;
        }
        Material mat = ConversionMaterial(raw, src.width, src.height);
        if (mat == null) return src;

        var rt = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            name = "VR180 fisheye converted",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        rt.Create();
        Graphics.Blit(src, rt, mat);
        UnityEngine.Object.Destroy(src);
        return rt;
    }
}
