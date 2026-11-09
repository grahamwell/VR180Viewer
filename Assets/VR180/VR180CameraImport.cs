// VR180CameraImport: the C# side of importing photos from a camera plugged into the Quest's USB-C
// port. The work is done by Assets/Plugins/Android/CameraImport.java (Android's MTP API) on
// background threads; this class just forwards calls and reads its state. Quest only: everything
// returns "nothing there" on PC and in the Editor.

using System;
using UnityEngine;

public static class VR180CameraImport
{
    public const int Idle = 0, Scanning = 1, Scanned = 2, Importing = 3, Finished = 4, Failed = 5;

    static AndroidJavaClass cls;
    static AndroidJavaClass J => cls ??= new AndroidJavaClass("com.vr180viewer.CameraImport");
    static AndroidJavaObject Activity => UnityEngine.Android.AndroidApplication.currentActivity;

    static T Call<T>(string method, T fallback, params object[] args)
    {
        if (!VR180Viewer.OnQuest) return fallback;
        try { return J.CallStatic<T>(method, args); }
        catch (Exception e)
        {
            Debug.LogWarning($"VR180Viewer [import]: {method} failed: {e.Message}");
            return fallback;
        }
    }

    static void Run(string method, params object[] args)
    {
        if (!VR180Viewer.OnQuest) return;
        try { J.CallStatic(method, args); }
        catch (Exception e) { Debug.LogWarning($"VR180Viewer [import]: {method} failed: {e.Message}"); }
    }

    /// A description of the connected camera ("KANDAO KONA-IOT-MTP ..."), or null
    public static string FindCamera() => Call<string>("findCamera", null, Activity);
    public static bool HasPermission() => Call("hasPermission", false, Activity);
    public static void RequestPermission() => Run("requestPermission", Activity);
    public static void StartScan(string destFolder) => Run("startScan", Activity, destFolder);
    public static void StartImport(string destFolder, int max) => Run("startImport", Activity, destFolder, max);
    public static void Reset() => Run("reset");

    public static int State => Call("getState", Idle);
    public static string Message => Call("getMessage", "");
    public static int PhotoCount => Call("getPhotoCount", 0);
    public static long PhotoBytes => Call("getPhotoBytes", 0L);
    public static int NewCount => Call("getNewCount", 0);
    public static long NewBytes => Call("getNewBytes", 0L);
    public static int Done => Call("getDone", 0);
    public static int Total => Call("getTotal", 0);
}
