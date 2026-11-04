// VR180AdjustPanel: the in-headset picture adjustments panel (A button), built in code at
// runtime by VR180Viewer, in the same style as the folder picker.
//
// Sliders for brightness (exposure, in stops), contrast, saturation and convergence (the two
// eyes' images turned towards or away from each other), which are saved per
// photo, and sharpness (mip bias), which applies to all photos. Drag a slider with the
// controller ray and trigger. Changes show live on the photo behind the panel; on the Quest
// the sharp compositor layer is rebuilt with them when the panel closes.

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

public class VR180AdjustPanel : MonoBehaviour
{
    const float PanelWidth = 820f, PanelHeight = 1250f;   // canvas units (1 unit = 1 mm)

    VR180Viewer viewer;
    Canvas canvas;
    TextMeshProUGUI titleText;
    Slider exposure, contrast, saturation, convergence, mipBias;
    TextMeshProUGUI exposureText, contrastText, saturationText, convergenceText, mipBiasText;
    Toggle convergenceForAll;   // ticked: this photo follows (and sets) the default convergence
    Toggle favourite;
    Slider roll;
    TextMeshProUGUI rollText;
    bool updating;   // true while the sliders are being set from code

    public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;

    public void Init(VR180Viewer owner)
    {
        viewer = owner;
        Build();
        canvas.gameObject.SetActive(false);
    }

    public void Open(Transform head, string photoName, VR180Adjustments.Values v, float bias, bool isFavourite, float rollStrength)
    {
        titleText.text = photoName;
        roll.SetValueWithoutNotify(rollStrength);
        SetSliders(v, bias);
        favourite.SetIsOnWithoutNotify(isFavourite);
        PlaceInFrontOf(head);
        canvas.gameObject.SetActive(true);
    }

    public void Close()
    {
        if (canvas != null) canvas.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (canvas != null) Destroy(canvas.gameObject);
    }

    // Below eye level and tilted up, so the photo stays visible above it while adjusting
    void PlaceInFrontOf(Transform head)
    {
        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
        f.Normalize();
        Vector3 pos = head.position + f * 1.1f + Vector3.down * 0.42f;
        canvas.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));
    }

    void SetSliders(VR180Adjustments.Values v, float bias)
    {
        updating = true;
        exposure.value = v.exposure;
        contrast.value = v.contrast;
        saturation.value = v.saturation;
        convergence.value = v.convergence;
        convergenceForAll.isOn = !v.ownConvergence;
        mipBias.value = bias;
        updating = false;
        UpdateLabels();
    }

    VR180Adjustments.Values Current => new VR180Adjustments.Values
    {
        exposure = exposure.value,
        contrast = contrast.value,
        saturation = saturation.value,
        convergence = convergence.value,
        ownConvergence = !convergenceForAll.isOn
    };

    void Changed()
    {
        UpdateLabels();
        if (!updating) viewer.PreviewAdjustments(Current, mipBias.value);
    }

    void UpdateLabels()
    {
        exposureText.text = $"Brightness   {exposure.value:+0.00;-0.00;0.00} stops";
        contrastText.text = $"Contrast   {contrast.value:0.00}";
        saturationText.text = $"Saturation   {saturation.value:0.00}";
        convergenceText.text = $"Convergence   {convergence.value:+0.00;-0.00;0.00}°   (+ = nearer; " +
                               $"{(convergenceForAll.isOn ? "all photos" : "this photo only")})";
        mipBiasText.text = $"Sharpness (mip bias)   {mipBias.value:+0.00;-0.00;0.00}";
        rollText.text = roll.value > 0.004f
            ? $"Roll compensation   {roll.value * 100f:0}%   (experimental)"
            : "Roll compensation   off   (experimental)";
    }

    void ResetPhoto()
    {
        // Back to neutral, following the default convergence; sharpness is global, so leave it
        SetSliders(VR180Adjustments.Values.ForNewPhoto, mipBias.value);
        viewer.PreviewAdjustments(Current, mipBias.value);
    }

    // ------------------------------------------------------------------ building the UI

    void Build()
    {
        var go = new GameObject("VR180 Adjust Panel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(TrackedDeviceGraphicRaycaster));
        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        rt.localScale = Vector3.one * 0.001f;
        go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 2f;

        var bg = NewUI("Background", rt);
        Stretch(bg);
        bg.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.08f, 0.10f, 0.94f);
        var layout = bg.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(40, 40, 30, 30);
        layout.spacing = 8;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        titleText = NewText("Title", bg, 40, FontStyles.Bold, new Color(0.95f, 0.95f, 0.95f));
        titleText.textWrappingMode = TextWrappingModes.NoWrap;
        titleText.overflowMode = TextOverflowModes.Ellipsis;
        titleText.gameObject.AddComponent<LayoutElement>().preferredHeight = 60;

        favourite = AddToggle(bg, "Favourite", on => viewer.SetFavourite(on));
        Spacer(bg, 6);

        exposure   = AddSetting(bg, -2f, 2f, out exposureText);
        contrast   = AddSetting(bg, 0.5f, 1.5f, out contrastText);
        saturation = AddSetting(bg, 0f, 2f, out saturationText);
        convergence = AddSetting(bg, -2f, 2f, out convergenceText);
        convergenceForAll = AddToggle(bg, "Use this convergence for all photos (the default)", _ => Changed());
        Spacer(bg, 10);
        mipBias    = AddSetting(bg, -2f, 1f, out mipBiasText);
        roll       = AddSetting(bg, 0f, 1f, out rollText);
        roll.onValueChanged.AddListener(v => { if (!updating) viewer.SetRollCompensation(v); });

        var note = NewText("Note", bg, 26, FontStyles.Italic, new Color(0.65f, 0.67f, 0.72f));
        note.textWrappingMode = TextWrappingModes.Normal;
        note.text = "Brightness, contrast and saturation are saved for this photo; convergence too, unless it's for all photos. " +
                    "Sharpness (PC view and this preview only) and roll compensation apply to all photos.";
        note.gameObject.AddComponent<LayoutElement>().preferredHeight = 80;

        Spacer(bg, 6);
        var buttons = NewUI("Buttons", bg);
        var h = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 20;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = true;
        h.childForceExpandHeight = true;
        buttons.gameObject.AddComponent<LayoutElement>().preferredHeight = 84;
        AddButton(buttons, "Reset photo", ResetPhoto, false);
        AddButton(buttons, "Done  (A)", () => viewer.CloseAdjustments(), true);
    }

    Slider AddSetting(RectTransform parent, float min, float max, out TextMeshProUGUI label)
    {
        label = NewText("Label", parent, 34, FontStyles.Normal, Color.white);
        label.gameObject.AddComponent<LayoutElement>().preferredHeight = 48;

        var holder = NewUI("Slider", parent);
        holder.gameObject.AddComponent<LayoutElement>().preferredHeight = 70;

        // Track (also the part the ray hits, so you can click anywhere along it)
        var track = NewUI("Background", holder);
        Stretch(track);
        track.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);   // invisible, full height
        var bar = NewUI("Bar", track);
        bar.anchorMin = new Vector2(0f, 0.42f);
        bar.anchorMax = new Vector2(1f, 0.58f);
        bar.offsetMin = bar.offsetMax = Vector2.zero;
        var barImage = bar.gameObject.AddComponent<Image>();
        barImage.color = new Color(0.22f, 0.24f, 0.30f);
        barImage.raycastTarget = false;

        var fillArea = NewUI("Fill Area", holder);
        fillArea.anchorMin = new Vector2(0f, 0.42f);
        fillArea.anchorMax = new Vector2(1f, 0.58f);
        fillArea.offsetMin = new Vector2(0f, 0f);
        fillArea.offsetMax = new Vector2(0f, 0f);
        var fill = NewUI("Fill", fillArea);
        fill.sizeDelta = Vector2.zero;
        var fillImage = fill.gameObject.AddComponent<Image>();
        fillImage.color = new Color(0.25f, 0.45f, 0.70f);
        fillImage.raycastTarget = false;

        var handleArea = NewUI("Handle Slide Area", holder);
        Stretch(handleArea);
        handleArea.offsetMin = new Vector2(20f, 0f);
        handleArea.offsetMax = new Vector2(-20f, 0f);
        var handle = NewUI("Handle", handleArea);
        handle.sizeDelta = new Vector2(40f, 0f);
        var handleImage = handle.gameObject.AddComponent<Image>();
        handleImage.color = Color.white;

        var s = holder.gameObject.AddComponent<Slider>();
        s.fillRect = fill;
        s.handleRect = handle;
        s.targetGraphic = handleImage;
        s.direction = Slider.Direction.LeftToRight;
        s.minValue = min;
        s.maxValue = max;
        var colours = s.colors;
        colours.highlightedColor = new Color(0.8f, 0.9f, 1f);
        colours.pressedColor = new Color(0.6f, 0.8f, 1f);
        s.colors = colours;
        s.onValueChanged.AddListener(_ => Changed());
        return s;
    }

    Toggle AddToggle(RectTransform parent, string label, Action<bool> onChange)
    {
        var row = NewUI("Toggle", parent);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 62;
        row.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);   // so the whole row can be clicked

        var box = NewUI("Box", row);
        box.anchorMin = box.anchorMax = new Vector2(0f, 0.5f);
        box.pivot = new Vector2(0f, 0.5f);
        box.sizeDelta = new Vector2(46f, 46f);
        box.anchoredPosition = new Vector2(4f, 0f);
        var boxImage = box.gameObject.AddComponent<Image>();
        boxImage.color = new Color(0.22f, 0.24f, 0.30f);

        var check = NewUI("Check", box);
        Stretch(check);
        check.offsetMin = new Vector2(9f, 9f);
        check.offsetMax = new Vector2(-9f, -9f);
        var checkImage = check.gameObject.AddComponent<Image>();
        checkImage.color = new Color(0.45f, 0.70f, 1f);
        checkImage.raycastTarget = false;

        var text = NewText("Label", row, 30, FontStyles.Normal, Color.white);
        Stretch(text.rectTransform);
        text.rectTransform.offsetMin = new Vector2(70f, 0f);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.text = label;

        var t = row.gameObject.AddComponent<Toggle>();
        t.targetGraphic = boxImage;
        t.graphic = checkImage;
        t.isOn = true;
        var colours = t.colors;
        colours.highlightedColor = new Color(0.8f, 0.9f, 1f);
        colours.pressedColor = new Color(0.6f, 0.8f, 1f);
        t.colors = colours;
        t.onValueChanged.AddListener(on => onChange(on));
        return t;
    }

    void AddButton(RectTransform parent, string label, Action onClick, bool highlight)
    {
        var row = NewUI("Button", parent);
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

        var text = NewText("Label", row, 36, FontStyles.Normal, Color.white);
        Stretch(text.rectTransform);
        text.alignment = TextAlignmentOptions.Center;
        text.text = label;
    }

    static void Spacer(RectTransform parent, float height) =>
        NewUI("Spacer", parent).gameObject.AddComponent<LayoutElement>().preferredHeight = height;

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
