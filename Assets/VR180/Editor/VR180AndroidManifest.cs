// Adds the "All files access" permission (MANAGE_EXTERNAL_STORAGE) to the Android build's
// manifest, so the photo picker can read shared folders such as Pictures/VR180 on the Quest.
// Without it in the manifest, the Quest's settings page shows the permission greyed out.

#if UNITY_ANDROID
using System.IO;
using UnityEditor.Android;
using UnityEngine;

public class VR180AndroidManifest : IPostGenerateGradleAndroidProject
{
    const string Permission = "android.permission.MANAGE_EXTERNAL_STORAGE";

    public int callbackOrder => 100;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifest))
        {
            Debug.LogWarning($"VR180AndroidManifest: no manifest at {manifest}; All files access not added.");
            return;
        }

        string text = File.ReadAllText(manifest);
        if (text.Contains(Permission)) return;

        int at = text.IndexOf("<application", System.StringComparison.Ordinal);
        if (at < 0)
        {
            Debug.LogWarning("VR180AndroidManifest: couldn't find <application> in the manifest; All files access not added.");
            return;
        }
        text = text.Insert(at, $"<uses-permission android:name=\"{Permission}\" />\n  ");
        File.WriteAllText(manifest, text);
        Debug.Log("VR180AndroidManifest: added MANAGE_EXTERNAL_STORAGE to the Android manifest.");
    }
}
#endif
