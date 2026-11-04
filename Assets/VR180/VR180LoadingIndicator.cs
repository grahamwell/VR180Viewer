// VR180LoadingIndicator: a small ring of dots in the middle of the view while a photo loads.
// Created at runtime by VR180Viewer. It appears only if loading takes more than a moment,
// fades in and out, and drifts gently after the head rather than being rigidly head-locked.
// (While a photo loads the compositor layer is hidden, so this eye-buffer canvas is visible.)

using UnityEngine;
using UnityEngine.UI;

public class VR180LoadingIndicator : MonoBehaviour
{
    const int DotCount = 8;
    const float Distance = 1.5f;        // metres in front of the eyes
    const float RingRadius = 32f;       // canvas units are millimetres
    const float DotSize = 11f;
    const float BackdropSize = 100f;
    const float ShowDelay = 0.25f;      // seconds before it appears, so quick loads don't flicker
    const float FadeSpeed = 4f;         // alpha per second
    const float TurnsPerSecond = 1.1f;

    Transform head;
    Canvas canvas;
    CanvasGroup group;
    Image[] dots;
    Vector3 dir = Vector3.forward;
    bool wanted;
    float wantedSince, alpha;
    Texture2D circleTex;

    public void Init(Transform headTransform)
    {
        head = headTransform;
        Build();
        canvas.gameObject.SetActive(false);
    }

    public void Show()
    {
        if (wanted) return;
        wanted = true;
        wantedSince = Time.unscaledTime;
    }

    public void Hide() => wanted = false;

    void OnDestroy()
    {
        if (canvas != null) Destroy(canvas.gameObject);
        if (circleTex != null) Destroy(circleTex);
    }

    void LateUpdate()
    {
        if (head == null || canvas == null) return;

        float target = wanted && Time.unscaledTime - wantedSince > ShowDelay ? 1f : 0f;
        alpha = Mathf.MoveTowards(alpha, target, Time.unscaledDeltaTime * FadeSpeed);
        bool visible = alpha > 0.001f;
        if (canvas.gameObject.activeSelf != visible)
        {
            canvas.gameObject.SetActive(visible);
            if (visible) dir = head.forward;   // start straight ahead
        }
        if (!visible) return;

        // Follow the gaze smoothly
        dir = Vector3.Slerp(dir, head.forward, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime)).normalized;
        canvas.transform.SetPositionAndRotation(head.position + dir * Distance, Quaternion.LookRotation(dir, Vector3.up));
        group.alpha = alpha;

        // The bright spot runs round the ring, leaving a fading tail
        float phase = Time.unscaledTime * TurnsPerSecond;
        for (int i = 0; i < DotCount; i++)
        {
            float behind = Mathf.Repeat(phase - i / (float)DotCount, 1f);   // 0 = the lead dot
            float a = Mathf.Lerp(1f, 0.18f, Mathf.Sqrt(behind));
            dots[i].color = new Color(1f, 1f, 1f, a);
        }
    }

    void Build()
    {
        var go = new GameObject("VR180 Loading Indicator", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.sortingOrder = 10;
        group = go.GetComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;
        var root = (RectTransform)go.transform;
        root.sizeDelta = new Vector2(BackdropSize, BackdropSize);
        root.localScale = Vector3.one * 0.001f;

        var sprite = MakeCircleSprite();

        // A soft dark disc, so the white dots show up even against a pale held colour
        var backdrop = NewImage("Backdrop", root, sprite, BackdropSize);
        backdrop.color = new Color(0f, 0f, 0f, 0.35f);

        dots = new Image[DotCount];
        for (int i = 0; i < DotCount; i++)
        {
            float angle = -i * Mathf.PI * 2f / DotCount + Mathf.PI / 2f;   // clockwise from the top
            dots[i] = NewImage("Dot", root, sprite, DotSize);
            dots[i].rectTransform.anchoredPosition = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * RingRadius;
        }
    }

    static Image NewImage(string name, Transform parent, Sprite sprite, float size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(size, size);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.raycastTarget = false;
        return img;
    }

    // A white disc with a softened edge, made in code so no asset is needed
    Sprite MakeCircleSprite()
    {
        const int n = 64;
        circleTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "VR180 loading dot", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        float r = n * 0.5f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                byte a = (byte)(Mathf.Clamp01((r - d) / 1.5f) * 255f);
                px[y * n + x] = new Color32(255, 255, 255, a);
            }
        circleTex.SetPixels32(px);
        circleTex.Apply(false, true);
        return Sprite.Create(circleTex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
    }
}
