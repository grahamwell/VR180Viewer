// VR180FavouriteBadge: favourite feedback, created at runtime by VR180Viewer.
//  - Marking a favourite: a bright rising chime, one buzz, and a large gold star in the middle
//    of the view. Unmarking: a falling, glum two-note cue, two short buzzes, a grey star.
//    (The stars only show where the sharp compositor layer isn't covering the view: it draws
//    over everything, so on the Quest the sound and buzz are the confirmation.)
//  - A favourite loading (outside the Favourites slideshow): a small, faint star low in the
//    right-hand corner of the view for a couple of seconds.
// The sounds are synthesised in code, so there are no audio assets.

using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class VR180FavouriteBadge : MonoBehaviour
{
    const float Distance = 1.5f;      // metres
    const float Drop = 0.14f;         // below the centre of view, clear of the loading dots
    const float StarSize = 90f;       // canvas units are millimetres
    const float FadeSpeed = 5f;

    static readonly Color Gold = new Color(1f, 0.80f, 0.25f);
    static readonly Color Grey = new Color(0.75f, 0.75f, 0.78f, 0.8f);

    const float CornerScale = 0.4f;    // the corner star is 40% of the full size
    const float CornerAlpha = 0.7f;
    static readonly Vector3 CornerOffset = new Vector3(0.55f, -0.40f, 0f);   // about 20 degrees right, 15 down at 1.5 m

    Transform head;
    RectTransform root;
    bool corner;
    AudioSource audioSource;
    AudioClip addedClip, removedClip;
    Canvas canvas;
    CanvasGroup group;
    Image star;
    TextMeshProUGUI label;
    Texture2D starTex;
    float showUntil, alpha;
    Vector3 dir = Vector3.forward;

    public void Init(Transform headTransform)
    {
        head = headTransform;
        Build();
        canvas.gameObject.SetActive(false);

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;   // plain stereo, not placed in the room
        int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
        addedClip = MakeAddedClip(rate);
        removedClip = MakeRemovedClip(rate);
    }

    /// The full feedback for marking (true) or unmarking (false) a favourite
    public void Announce(bool added, bool showStar)
    {
        audioSource.PlayOneShot(added ? addedClip : removedClip, added ? 0.45f : 0.5f);
        Buzz(added);
        if (showStar) Flash(added, added ? "Favourite" : "Removed from favourites");
    }

    /// A small, faint gold star in the corner of the view, as a favourite loads
    public void Corner(float seconds = 2.5f)
    {
        star.color = Gold;
        label.text = "";
        corner = true;
        root.localScale = Vector3.one * 0.001f * CornerScale;
        showUntil = Time.unscaledTime + seconds;
        if (!canvas.gameObject.activeSelf && head != null) dir = head.forward;
    }

    /// Shows the star for a moment: gold if the photo is a favourite, grey if not
    public void Flash(bool favourite, string text = null, float seconds = 1.3f)
    {
        star.color = favourite ? Gold : Grey;
        label.text = text ?? "";
        corner = false;
        root.localScale = Vector3.one * 0.001f;
        showUntil = Time.unscaledTime + seconds;
        if (!canvas.gameObject.activeSelf && head != null) dir = head.forward;
    }

    /// One firm buzz for "added", two short ones for "removed", on the right controller
    public void Buzz(bool added)
    {
        if (added) Pulse(0.6f, 0.12f);
        else StartCoroutine(DoublePulse());
    }

    IEnumerator DoublePulse()
    {
        Pulse(0.4f, 0.05f);
        yield return new WaitForSecondsRealtime(0.12f);
        Pulse(0.4f, 0.05f);
    }

    // OpenXR haptics go through an action bound to the controller's haptic output
    static InputAction s_Haptic;

    static void Pulse(float amplitude, float seconds)
    {
        try
        {
            if (s_Haptic == null)
            {
                s_Haptic = new InputAction("Haptic", InputActionType.PassThrough, "<XRController>{RightHand}/{Haptic}");
                s_Haptic.Enable();
            }
            UnityEngine.XR.OpenXR.Input.OpenXRInput.SendHapticImpulse(s_Haptic, amplitude, seconds);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"VR180Viewer [favourites]: haptics failed: {e.Message}");
        }
    }

    void OnDestroy()
    {
        if (canvas != null) Destroy(canvas.gameObject);
        if (starTex != null) Destroy(starTex);
        if (addedClip != null) Destroy(addedClip);
        if (removedClip != null) Destroy(removedClip);
        if (s_Haptic != null) { s_Haptic.Dispose(); s_Haptic = null; }
    }

    void LateUpdate()
    {
        if (head == null || canvas == null) return;
        float target = Time.unscaledTime < showUntil ? 1f : 0f;
        alpha = Mathf.MoveTowards(alpha, target, Time.unscaledDeltaTime * FadeSpeed);
        bool visible = alpha > 0.001f;
        if (canvas.gameObject.activeSelf != visible) canvas.gameObject.SetActive(visible);
        if (!visible) return;

        dir = Vector3.Slerp(dir, head.forward, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime)).normalized;
        Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
        Vector3 offset = corner ? CornerOffset : Vector3.down * Drop;
        canvas.transform.SetPositionAndRotation(head.position + dir * Distance + rot * offset, rot);
        group.alpha = alpha * (corner ? CornerAlpha : 1f);
    }

    void Build()
    {
        var go = new GameObject("VR180 Favourite Badge", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.sortingOrder = 11;
        group = go.GetComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;
        root = (RectTransform)go.transform;
        root.sizeDelta = new Vector2(400f, 160f);
        root.localScale = Vector3.one * 0.001f;

        var starGo = new GameObject("Star", typeof(RectTransform));
        var srt = (RectTransform)starGo.transform;
        srt.SetParent(root, false);
        srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 1f);
        srt.pivot = new Vector2(0.5f, 1f);
        srt.sizeDelta = new Vector2(StarSize, StarSize);
        star = starGo.AddComponent<Image>();
        star.sprite = MakeStarSprite();
        star.raycastTarget = false;

        var textGo = new GameObject("Label", typeof(RectTransform));
        var trt = (RectTransform)textGo.transform;
        trt.SetParent(root, false);
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(1f, 0f);
        trt.pivot = new Vector2(0.5f, 0f);
        trt.sizeDelta = new Vector2(0f, 60f);
        label = textGo.AddComponent<TextMeshProUGUI>();
        label.fontSize = 34;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.outlineWidth = 0.25f;
        label.outlineColor = new Color32(0, 0, 0, 200);
        label.raycastTarget = false;
    }

    // ------------------------------------------------------------------ sounds

    // Bright and positive: two quick bell-like notes rising a fourth (E6 then A6)
    static AudioClip MakeAddedClip(int rate)
    {
        float length = 0.6f;
        var data = new float[(int)(rate * length)];
        Bell(data, rate, 0.00f, 1318.5f, 0.55f, 14f);
        Bell(data, rate, 0.09f, 1760.0f, 0.65f, 8f);
        Normalise(data, 0.8f);
        var clip = AudioClip.Create("VR180 favourite added", data.Length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // Disappointed: a soft, low note, then a lower one that sags further as it fades ("aww")
    static AudioClip MakeRemovedClip(int rate)
    {
        float length = 0.75f;
        var data = new float[(int)(rate * length)];
        Glum(data, rate, 0.00f, 0.20f, 392.0f, 392.0f);   // G4
        Glum(data, rate, 0.22f, 0.50f, 311.1f, 262.0f);   // E flat 4, sliding down towards C4
        Normalise(data, 0.7f);
        var clip = AudioClip.Create("VR180 favourite removed", data.Length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    static void Bell(float[] data, int rate, float start, float freq, float level, float decay)
    {
        int s0 = (int)(start * rate);
        for (int i = s0; i < data.Length; i++)
        {
            float t = (i - s0) / (float)rate;
            float env = Mathf.Min(1f, t * 400f) * Mathf.Exp(-t * decay) * level;   // 2.5 ms attack, no click
            float w = 2f * Mathf.PI * freq * t;
            data[i] += env * (Mathf.Sin(w) + 0.25f * Mathf.Sin(2f * w) * Mathf.Exp(-t * 20f));
        }
    }

    static void Glum(float[] data, int rate, float start, float length, float freqFrom, float freqTo)
    {
        int s0 = (int)(start * rate), n = (int)(length * rate);
        double phase = 0;
        for (int k = 0; k < n && s0 + k < data.Length; k++)
        {
            float t = k / (float)rate, u = k / (float)n;
            float freq = Mathf.Lerp(freqFrom, freqTo, u * u);
            phase += 2.0 * System.Math.PI * freq / rate;
            float env = Mathf.Min(1f, t * 60f) * Mathf.Min(1f, (1f - u) * 6f) * 0.5f;   // soft attack and release
            float p = (float)phase;
            data[s0 + k] += env * (Mathf.Sin(p) + 0.3f * Mathf.Sin(2f * p) + 0.1f * Mathf.Sin(3f * p));
        }
    }

    static void Normalise(float[] data, float peak)
    {
        float max = 0f;
        foreach (float v in data) max = Mathf.Max(max, Mathf.Abs(v));
        if (max <= 0f) return;
        float k = peak / max;
        for (int i = 0; i < data.Length; i++) data[i] *= k;
    }

    // ------------------------------------------------------------------ star sprite

    // A five-pointed star, drawn in code (4x4 supersampled for smooth edges)
    Sprite MakeStarSprite()
    {
        const int n = 128, ss = 4;
        var poly = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float a = Mathf.PI / 2f + i * Mathf.PI / 5f;
            float r = (i % 2 == 0 ? 0.48f : 0.20f) * n;
            poly[i] = new Vector2(n * 0.5f + r * Mathf.Cos(a), n * 0.5f + r * Mathf.Sin(a));
        }

        starTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "VR180 star", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                        if (Inside(new Vector2(x + (sx + 0.5f) / ss, y + (sy + 0.5f) / ss), poly)) hits++;
                px[y * n + x] = new Color32(255, 255, 255, (byte)(hits * 255 / (ss * ss)));
            }
        starTex.SetPixels32(px);
        starTex.Apply(false, true);
        return Sprite.Create(starTex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
    }

    static bool Inside(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }
}
