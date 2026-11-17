// VR180TiltHint: a brief arrow saying "this photo's centre is up there" (or down there),
// created at runtime by VR180Viewer (15 November 2026).
//
// A photo levelled by turning the sphere (a raw QooCam photo, or a Studio photo tagged with the
// camera's tilt) can have its centre well above or below the horizon, and nothing else says so.
// As such a photo sharpens in, a soft white chevron appears a little above (or below) the centre
// of view, drifts a few centimetres the way it points, and fades. It is drawn in the scene, so on
// the Quest it is only visible until the sharp layer takes over: the viewer times it to finish
// within the sharpen-in.

using UnityEngine;
using UnityEngine.UI;

public class VR180TiltHint : MonoBehaviour
{
    const float Distance = 1.5f;      // metres
    const float Offset = 0.22f;       // above/below the centre of view (about 8 degrees at 1.5 m)
    const float Drift = 0.04f;        // how far it moves the way it points while showing
    const float Size = 110f;          // canvas units are millimetres
    const float FadeIn = 0.2f, FadeOut = 0.35f;

    Transform head;
    Canvas canvas;
    CanvasGroup group;
    RectTransform root, arrow;
    Texture2D tex;
    Vector3 dir = Vector3.forward;
    float start = -1f, length;
    bool up;

    public void Init(Transform headTransform)
    {
        head = headTransform;
        Build();
        canvas.gameObject.SetActive(false);
    }

    /// Shows the arrow (pointing up or down) for this many seconds, including its fades
    public void Show(bool pointUp, float seconds)
    {
        up = pointUp;
        length = Mathf.Max(seconds, FadeIn + FadeOut + 0.1f);
        start = Time.unscaledTime;
        if (head != null) dir = head.forward;
        arrow.localRotation = Quaternion.Euler(0f, 0f, up ? 0f : 180f);
    }

    public void Hide() => start = -1f;

    void LateUpdate()
    {
        if (head == null || canvas == null) return;
        float t = start < 0f ? -1f : Time.unscaledTime - start;
        bool visible = t >= 0f && t < length;
        if (canvas.gameObject.activeSelf != visible) canvas.gameObject.SetActive(visible);
        if (!visible) { start = -1f; return; }

        float a = Mathf.Min(Mathf.Clamp01(t / FadeIn), Mathf.Clamp01((length - t) / FadeOut));
        group.alpha = a * a * (3f - 2f * a);   // smoothstep

        // Follows the gaze softly, like the other little indicators
        dir = Vector3.Slerp(dir, head.forward, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime)).normalized;
        Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
        float sign = up ? 1f : -1f;
        float y = sign * (Offset + Drift * Mathf.SmoothStep(0f, 1f, t / length));
        canvas.transform.SetPositionAndRotation(head.position + dir * Distance + rot * new Vector3(0f, y, 0f), rot);
    }

    void OnDestroy()
    {
        if (canvas != null) Destroy(canvas.gameObject);
        if (tex != null) Destroy(tex);
    }

    void Build()
    {
        var go = new GameObject("VR180 Tilt Hint", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.sortingOrder = 11;
        group = go.GetComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;
        root = (RectTransform)go.transform;
        root.sizeDelta = new Vector2(Size, Size);
        root.localScale = Vector3.one * 0.001f;

        var arrowGo = new GameObject("Arrow", typeof(RectTransform));
        arrow = (RectTransform)arrowGo.transform;
        arrow.SetParent(root, false);
        arrow.anchorMin = arrow.anchorMax = arrow.pivot = new Vector2(0.5f, 0.5f);
        arrow.sizeDelta = new Vector2(Size, Size);
        var img = arrowGo.AddComponent<Image>();
        img.sprite = MakeChevron();
        img.raycastTarget = false;
    }

    // An upward chevron with round ends: white, over a soft dark shadow so it reads against
    // bright skies as well as dark ground. Drawn as a distance field, so its edges are smooth.
    Sprite MakeChevron()
    {
        const int n = 128;
        Vector2 a = new Vector2(0.22f, 0.40f) * n, b = new Vector2(0.50f, 0.66f) * n, c = new Vector2(0.78f, 0.40f) * n;
        float half = 0.065f * n;      // half the stroke width
        float shadow = 0.12f * n;     // how far the shadow spreads beyond the stroke
        Vector2 shadowShift = new Vector2(0f, -0.02f * n);

        tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "VR180 tilt arrow", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float d = Mathf.Min(Segment(p, a, b), Segment(p, b, c)) - half;
                float ds = Mathf.Min(Segment(p - shadowShift, a, b), Segment(p - shadowShift, b, c)) - half;
                float core = Mathf.Clamp01(0.5f - d);                                    // 1 px antialiasing
                float sh = 0.55f * (1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ds / shadow)));
                float alpha = core + sh * (1f - core);
                float white = alpha > 0f ? core / alpha : 0f;
                byte v = (byte)Mathf.RoundToInt(white * 255f);
                px[y * n + x] = new Color32(v, v, v, (byte)Mathf.RoundToInt(alpha * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
    }

    static float Segment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(p, a + t * ab);
    }
}
