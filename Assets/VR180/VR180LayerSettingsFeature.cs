// VR180LayerSettingsFeature: an OpenXR feature whose only job is to ask the runtime for the
// XR_FB_composition_layer_settings extension, so VR180StereoEquirectHandler can ask the Quest's
// compositor to sharpen or supersample the photo layers (experiment, 12 November 2026).
//
// To use it: Project Settings > XR Plug-in Management > OpenXR > Android tab, tick
// "VR180 Layer Sharpening (experiment)" in the features list. The log then says
// "XR_FB_composition_layer_settings enabled" when a session starts on the Quest.

using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

#if UNITY_EDITOR
[OpenXRFeature(UiName = "VR180 Layer Sharpening (experiment)",
    BuildTargetGroups = new[] { BuildTargetGroup.Android },
    Company = "VR180 Viewer",
    Desc = "Asks for XR_FB_composition_layer_settings, so the viewer can ask the Quest to sharpen or supersample its photo layers.",
    OpenxrExtensionStrings = VR180StereoEquirectHandler.LayerSettingsExtension,
    Version = "0.1.0",
    FeatureId = FeatureId)]
#endif
public class VR180LayerSettingsFeature : OpenXRFeature
{
    public const string FeatureId = "com.grahamwell.vr180viewer.layersettings";
}
