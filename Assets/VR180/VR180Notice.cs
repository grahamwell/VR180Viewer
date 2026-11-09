// VR180Notice: a short message panel in front of the viewer, built in code, that fades in, stays
// for a few seconds and fades out. Used for "this camera has no calibration file". On the Quest
// the sharp compositor layer draws over anything in the scene, so the viewer holds the layer back
// while a notice is showing (the softer sphere shows the photo meanwhile).

using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class VR180Notice : MonoBehaviour
{
    const float Width = 900f, Height = 420f;   // canvas units (1 unit = 1 mm)
    const float FadeSeconds = 0.4f;

    Transform head;
    Canvas canvas;
    CanvasGroup group;
    TextMeshProUGUI titleText, bodyText;
    float shownAt, until;

    public bool Visible => canvas != null && canvas.gameObject.activeSelf;

    public void Init(Transform headTransform)
    {
        head = headTransform;
        Build();
        canvas.gameObject.SetActive(false);
    }

    public void Show(string title, string body, float seconds)
    {
        titleText.text = title;
        bodyText.text = body;
        shownAt = Time.unscaledTime;
        until = shownAt + seconds;
        group.alpha = 0f;
        Place();
        canvas.gameObject.SetActive(true);
        Debug.Log($"VR180Viewer [notice]: {title}: {body}");
    }

    public void Hide()
    {
        if (canvas != null) canvas.gameObject.SetActive(false);
    }

    void Update()
    {
        if (!Visible) return;
        float t = Time.unscaledTime;
        if (t >= until) { Hide(); return; }
        group.alpha = Mathf.Clamp01(Mathf.Min(t - shownAt, until - t) / FadeSeconds);
    }

    void OnDestroy()
    {
        if (canvas != null) Destroy(canvas.gameObject);
    }

    void Place()
    {
        if (head == null) return;
        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
        f.Normalize();
        Vector3 pos = head.position + f * 1.4f + Vector3.down * 0.05f;
        canvas.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(f, Vector3.up));
    }

    void Build()
    {
        var go = new GameObject("VR180 Notice", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        group = go.GetComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(Width, Height);
        rt.localScale = Vector3.one * 0.001f;
        go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 2f;

        var bg = NewUI("Background", rt);
        bg.anchorMin = Vector2.zero;
        bg.anchorMax = Vector2.one;
        bg.offsetMin = bg.offsetMax = Vector2.zero;
        bg.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.08f, 0.10f, 0.94f);
        var layout = bg.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(40, 40, 32, 32);
        layout.spacing = 18;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        titleText = NewText("Title", bg, 46, FontStyles.Bold, new Color(1.0f, 0.78f, 0.35f));
        bodyText = NewText("Body", bg, 30, FontStyles.Normal, new Color(0.88f, 0.89f, 0.91f));
    }

    static RectTransform NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    static TextMeshProUGUI NewText(string name, Transform parent, float size, FontStyles style, Color colour)
    {
        var t = NewUI(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        t.fontSize = size;
        t.fontStyle = style;
        t.color = colour;
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.Normal;
        return t;
    }
}
