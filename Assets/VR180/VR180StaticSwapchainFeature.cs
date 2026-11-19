// VR180StaticSwapchainFeature: makes single-image (static) swapchains usable for the photo layers.
//
// Two problems, both found 18 November 2026 by disassembling OpenXR 1.19's libUnityOpenXR.so:
//
// 1. When Unity's native CompositionLayerManager creates a layer's swapchain (CreateSwapchainsInGfxThread),
//    it immediately calls UnitySwapchains::AcquireAndWait(0) and ReleaseAll(): an empty acquire / wait /
//    release round trip. A swapchain created with XR_SWAPCHAIN_CREATE_STATIC_IMAGE_BIT may be acquired once
//    only, so that round trip used up its one acquire (every later one: XR_ERROR_CALL_ORDER_INVALID).
//    Fix: the round trip on a new static swapchain is answered here (success, image 0) without reaching
//    the runtime, leaving the real acquire for the photo.
//
// 2. Requests (RequestRenderTextureId) are served on the render thread inside ExtendView, during frame
//    setup, and the layer's image is released again at the end of that render frame (ReleaseSwapchains,
//    run alongside the eye buffers' release). The render thread is a frame behind the main thread, so the
//    image id reaches C# one frame late, after the image has been released: too late to write a single
//    image legally. Fix: Unity's release of a static swapchain's real image is held back here; the handler
//    blits the photo and calls ReleaseAfterWrite, which puts a marker into the render thread's command
//    stream (GL.IssuePluginEvent) just after the blit; the real release follows at the end of that render
//    frame, piggybacked on Unity's next release of a non-static image (the eye buffers, released after the
//    frame's GPU work has been submitted), so the runtime sees the photo's write before the release.
//
// How: hooks xrGetInstanceProcAddr (the documented OpenXRFeature hook; Unity loads all its OpenXR functions
// through it) and wraps xrCreateSwapchain, xrAcquireSwapchainImage, xrWaitSwapchainImage,
// xrReleaseSwapchainImage and xrDestroySwapchain. Swapchains without the static flag pass straight through.
// Written in C# (reverse P/Invoke callbacks) so the project needs no native plugin.
//
// To use it: Project Settings > XR Plug-in Management > OpenXR > Android tab, tick
// "VR180 Single-Image Swapchains". Without it the handler uses multi-image swapchains (logged).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

#if UNITY_EDITOR
[OpenXRFeature(UiName = "VR180 Single-Image Swapchains",
    BuildTargetGroups = new[] { BuildTargetGroup.Android },
    Company = "VR180 Viewer",
    Desc = "Lets the photo layers use single-image swapchains (about 0.75 GB less memory): absorbs the empty " +
           "acquire Unity's layer code makes on every new swapchain, and holds the image until the photo is written.",
    Version = "0.2.0",
    FeatureId = FeatureId)]
#endif
public class VR180StaticSwapchainFeature : OpenXRFeature
{
    public const string FeatureId = "com.grahamwell.vr180viewer.staticswapchains";

    const ulong StaticImageBit = 0x2;   // XR_SWAPCHAIN_CREATE_STATIC_IMAGE_BIT
    const int XR_SUCCESS = 0;
    const int XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO = 57;

    /// True when the hook is in place and every function it needs was found
    public static bool Hooked =>
        s_NextCreate != null && s_NextAcquire != null && s_NextWait != null && s_NextRelease != null && s_NextDestroy != null;

    /// Counters for the log
    public static int Absorbed { get; private set; }        // Unity's creation-time round trips answered here
    public static int HeldReleases { get; private set; }    // Unity releases held back until the photo was written

    // ------------------------------------------------------------------ OpenXR signatures

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetInstanceProcAddrFn(ulong instance, IntPtr name, IntPtr function);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int CreateSwapchainFn(ulong session, IntPtr createInfo, IntPtr swapchain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int AcquireFn(ulong swapchain, IntPtr acquireInfo, IntPtr index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int WaitFn(ulong swapchain, IntPtr waitInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int ReleaseFn(ulong swapchain, IntPtr releaseInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int DestroyFn(ulong swapchain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void RenderEventFn(int eventId);

    // Our versions (kept in static fields so they are never collected while native code holds them)
    static readonly GetInstanceProcAddrFn s_GetProcAddr = InterceptGetInstanceProcAddr;
    static readonly CreateSwapchainFn s_Create = InterceptCreateSwapchain;
    static readonly AcquireFn s_Acquire = InterceptAcquire;
    static readonly WaitFn s_Wait = InterceptWait;
    static readonly ReleaseFn s_Release = InterceptRelease;
    static readonly DestroyFn s_Destroy = InterceptDestroy;
    static readonly RenderEventFn s_Written = OnWrittenOnRenderThread;
    static IntPtr s_WrittenPtr;

    // The next ones down the chain
    static GetInstanceProcAddrFn s_NextGetProcAddr;
    static CreateSwapchainFn s_NextCreate;
    static AcquireFn s_NextAcquire;
    static WaitFn s_NextWait;
    static ReleaseFn s_NextRelease;
    static DestroyFn s_NextDestroy;

    // Life of a static swapchain:
    // Fresh -> FakeAcquired (Unity's creation acquire, answered here) -> Live (its release, answered here)
    // -> Acquired (the real acquire, for our request) -> Held (Unity released it; held back here)
    // -> Written (the photo's blit is in the render thread's stream) -> Released (real release done)
    // WrittenEarly: the write marker came before Unity's release, so that release goes straight through.
    enum Phase { Fresh, FakeAcquired, Live, Acquired, WrittenEarly, Held, Written, Released }
    static readonly Dictionary<ulong, Phase> s_Static = new Dictionary<ulong, Phase>();
    static readonly Dictionary<int, ulong> s_Events = new Dictionary<int, ulong>();   // marker id -> swapchain
    static readonly List<ulong> s_ToRelease = new List<ulong>();
    static readonly object s_Lock = new object();
    static int s_NextEventId = 0x56180000;
    static IntPtr s_ReleaseInfo;

    // ------------------------------------------------------------------ API for the handler

    /// Call on the main thread straight after blitting the photo into a static swapchain's image (the image
    /// id that the request delivered). The image is released at the end of the render frame that runs the blit.
    public static void ReleaseAfterWrite(ulong swapchain)
    {
        if (!Hooked || swapchain == 0) return;
        int id;
        lock (s_Lock)
        {
            if (!s_Static.ContainsKey(swapchain)) return;
            id = s_NextEventId++;
            s_Events[id] = swapchain;
        }
        if (s_WrittenPtr == IntPtr.Zero) s_WrittenPtr = Marshal.GetFunctionPointerForDelegate(s_Written);
        GL.IssuePluginEvent(s_WrittenPtr, id);
    }

    /// True once the static swapchain's image has really been released to the runtime (so the layer may be
    /// submitted), or if this swapchain isn't one we manage
    public static bool IsReleased(ulong swapchain)
    {
        lock (s_Lock)
            return !s_Static.TryGetValue(swapchain, out var phase) || phase == Phase.Released;
    }

    // ------------------------------------------------------------------ the hook

    protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
    {
        s_NextGetProcAddr = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrFn>(func);
        Debug.Log("VR180Viewer [swapchains]: single-image swapchain feature hooked xrGetInstanceProcAddr");
        return Marshal.GetFunctionPointerForDelegate(s_GetProcAddr);
    }

    // XrResult xrGetInstanceProcAddr(XrInstance, const char* name, PFN_xrVoidFunction* function)
    [AOT.MonoPInvokeCallback(typeof(GetInstanceProcAddrFn))]
    static int InterceptGetInstanceProcAddr(ulong instance, IntPtr name, IntPtr function)
    {
        int result = s_NextGetProcAddr(instance, name, function);
        if (result != XR_SUCCESS || function == IntPtr.Zero) return result;
        IntPtr next = Marshal.ReadIntPtr(function);
        if (next == IntPtr.Zero) return result;

        switch (Marshal.PtrToStringAnsi(name))
        {
            case "xrCreateSwapchain":
                s_NextCreate = Marshal.GetDelegateForFunctionPointer<CreateSwapchainFn>(next);
                Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(s_Create));
                break;
            case "xrAcquireSwapchainImage":
                s_NextAcquire = Marshal.GetDelegateForFunctionPointer<AcquireFn>(next);
                Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(s_Acquire));
                break;
            case "xrWaitSwapchainImage":
                s_NextWait = Marshal.GetDelegateForFunctionPointer<WaitFn>(next);
                Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(s_Wait));
                break;
            case "xrReleaseSwapchainImage":
                s_NextRelease = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(next);
                Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(s_Release));
                break;
            case "xrDestroySwapchain":
                s_NextDestroy = Marshal.GetDelegateForFunctionPointer<DestroyFn>(next);
                Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(s_Destroy));
                break;
        }
        return result;
    }

    // ------------------------------------------------------------------ the wrapped functions

    // XrSwapchainCreateInfo: type (4), pad (4), next (8), createFlags at 16, ...
    [AOT.MonoPInvokeCallback(typeof(CreateSwapchainFn))]
    static int InterceptCreateSwapchain(ulong session, IntPtr createInfo, IntPtr swapchain)
    {
        int result = s_NextCreate(session, createInfo, swapchain);
        if (result == XR_SUCCESS && createInfo != IntPtr.Zero && swapchain != IntPtr.Zero &&
            ((ulong)Marshal.ReadInt64(createInfo, 16) & StaticImageBit) != 0)
        {
            ulong handle = (ulong)Marshal.ReadInt64(swapchain);
            lock (s_Lock) s_Static[handle] = Phase.Fresh;
        }
        return result;
    }

    [AOT.MonoPInvokeCallback(typeof(AcquireFn))]
    static int InterceptAcquire(ulong swapchain, IntPtr acquireInfo, IntPtr index)
    {
        bool real = false;
        lock (s_Lock)
        {
            if (s_Static.TryGetValue(swapchain, out var phase))
            {
                if (phase == Phase.Fresh)
                {
                    // Unity's creation-time acquire: pretend, and keep the real one for the photo
                    s_Static[swapchain] = Phase.FakeAcquired;
                    if (index != IntPtr.Zero) Marshal.WriteInt32(index, 0);
                    return XR_SUCCESS;
                }
                real = phase == Phase.Live;
            }
        }
        int result = s_NextAcquire(swapchain, acquireInfo, index);
        if (real && result == XR_SUCCESS)
            lock (s_Lock) if (s_Static.ContainsKey(swapchain)) s_Static[swapchain] = Phase.Acquired;
        return result;
    }

    [AOT.MonoPInvokeCallback(typeof(WaitFn))]
    static int InterceptWait(ulong swapchain, IntPtr waitInfo)
    {
        lock (s_Lock)
            if (s_Static.TryGetValue(swapchain, out var phase) && phase == Phase.FakeAcquired)
                return XR_SUCCESS;
        return s_NextWait(swapchain, waitInfo);
    }

    [AOT.MonoPInvokeCallback(typeof(ReleaseFn))]
    static int InterceptRelease(ulong swapchain, IntPtr releaseInfo)
    {
        bool ours = false;
        lock (s_Lock)
        {
            if (s_Static.TryGetValue(swapchain, out var phase))
            {
                ours = true;
                switch (phase)
                {
                    case Phase.FakeAcquired:   // end of Unity's creation round trip
                        s_Static[swapchain] = Phase.Live;
                        Absorbed++;
                        return XR_SUCCESS;
                    case Phase.Acquired:       // Unity is done with it, but the photo isn't in it yet: hold it
                        s_Static[swapchain] = Phase.Held;
                        HeldReleases++;
                        return XR_SUCCESS;
                    case Phase.WrittenEarly:   // already written: let this release through
                        s_Static[swapchain] = Phase.Released;
                        break;
                }
            }
        }
        int result = s_NextRelease(swapchain, releaseInfo);

        // The end of a render frame (Unity releases the eye buffers after submitting the frame's GPU work):
        // release any static image whose photo was blitted in this frame's command stream
        if (!ours)
        {
            lock (s_Lock)
            {
                s_ToRelease.Clear();
                foreach (var kv in s_Static)
                    if (kv.Value == Phase.Written) s_ToRelease.Add(kv.Key);
                foreach (var h in s_ToRelease) s_Static[h] = Phase.Released;
            }
            if (s_ToRelease.Count > 0)
            {
                if (s_ReleaseInfo == IntPtr.Zero)
                {
                    s_ReleaseInfo = Marshal.AllocHGlobal(16);
                    Marshal.WriteInt32(s_ReleaseInfo, 0, XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO);
                    Marshal.WriteInt32(s_ReleaseInfo, 4, 0);
                    Marshal.WriteIntPtr(s_ReleaseInfo, 8, IntPtr.Zero);
                }
                foreach (var h in s_ToRelease) s_NextRelease(h, s_ReleaseInfo);
            }
        }
        return result;
    }

    [AOT.MonoPInvokeCallback(typeof(DestroyFn))]
    static int InterceptDestroy(ulong swapchain)
    {
        lock (s_Lock) s_Static.Remove(swapchain);
        return s_NextDestroy(swapchain);   // destroying also ends a held acquire
    }

    // Render thread, in command order just after the photo's blit
    [AOT.MonoPInvokeCallback(typeof(RenderEventFn))]
    static void OnWrittenOnRenderThread(int eventId)
    {
        lock (s_Lock)
        {
            if (!s_Events.TryGetValue(eventId, out var swapchain)) return;
            s_Events.Remove(eventId);
            if (!s_Static.TryGetValue(swapchain, out var phase)) return;
            if (phase == Phase.Held) s_Static[swapchain] = Phase.Written;
            else if (phase == Phase.Acquired) s_Static[swapchain] = Phase.WrittenEarly;
        }
    }
}
