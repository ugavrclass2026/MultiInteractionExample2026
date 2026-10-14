using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Calls two Meta OpenXR extensions directly, without the Meta SDK:
///   - XR_FB_body_tracking (+ XR_META_body_tracking_fidelity): 70 upper-body and hand joints from the headset.
///     Fidelity High is inside-out body tracking on Quest 3 (the cameras see elbows and torso).
///   - XR_META_simultaneous_hands_and_controllers: hand tracking and controllers at the same time.
/// Meta's forum reports that simultaneous mode can't run while body tracking is on, so turning body
/// tracking on pauses it and turning it off resumes it.
/// Poses are converted from OpenXR (right-handed) to Unity (left-handed) and are relative to the app's
/// tracking space (the XR Origin).
/// </summary>
#if UNITY_EDITOR
[UnityEditor.XR.OpenXR.Features.OpenXRFeature(UiName = "AvatarDemo: Meta body tracking + simultaneous hands/controllers",
    BuildTargetGroups = new[] { BuildTargetGroup.Android },
    Company = "UGA CSCI 4830",
    Desc = "XR_FB_body_tracking and XR_META_simultaneous_hands_and_controllers, called directly.",
    OpenxrExtensionStrings = Extensions,
    Version = "0.1.0",
    Category = UnityEditor.XR.OpenXR.Features.FeatureCategory.Feature,
    FeatureId = "edu.uga.csci4830.avatardemo.metabody")]
#endif
public class MetaBodyFeature : OpenXRFeature
{
    const string Extensions = "XR_FB_body_tracking XR_META_body_tracking_fidelity XR_META_simultaneous_hands_and_controllers XR_KHR_convert_timespec_time";
    public const int JointCount = 70;

    // ---- joint indices we use (XrBodyJointFB) ----
    public const int Hips = 1, SpineLower = 2, Chest = 5, Neck = 6, Head = 7,
        LeftArmUpper = 10, LeftArmLower = 11, RightArmUpper = 15, RightArmLower = 16,
        LeftWrist = 19, RightWrist = 45;

    public static MetaBodyFeature Instance { get; private set; }
    public bool BodyTrackingOn => tracker != 0;
    public bool SimultaneousOn { get; private set; }
    public string Status { get; private set; } = "not started";
    // diagnostics, shown on the demo's panel
    public int LastResult { get; private set; } = -999;
    public bool LastActive { get; private set; }
    public float LastConfidence { get; private set; }
    public int LastValid { get; private set; }
    int calls;

    ulong instance, session, tracker;
    IntPtr locations, jointBuf;

    // ---- function pointers ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetProcAddr(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr fn);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateBodyTracker(ulong session, ref BodyTrackerCreateInfo info, out ulong tracker);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DestroyBodyTracker(ulong tracker);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LocateBodyJoints(ulong tracker, ref BodyJointsLocateInfo info, IntPtr locations);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RequestFidelity(ulong tracker, int fidelity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SimultaneousCall(ulong session, ref TypedInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ConvertTimespec(ulong instance, ref Timespec ts, out long time);

    CreateBodyTracker createTracker; DestroyBodyTracker destroyTracker; LocateBodyJoints locate;
    RequestFidelity requestFidelity; SimultaneousCall resumeSim, pauseSim; ConvertTimespec convertTime;

    [StructLayout(LayoutKind.Sequential)] struct BodyTrackerCreateInfo { public int type; public IntPtr next; public int jointSet; }
    [StructLayout(LayoutKind.Sequential)] struct BodyJointsLocateInfo { public int type; public IntPtr next; public ulong baseSpace; public long time; }
    [StructLayout(LayoutKind.Sequential)] struct BodyJointLocations
    { public int type; public IntPtr next; public int isActive; public float confidence; public uint jointCount; public IntPtr jointLocations; public uint skeletonChangedCount; public long time; }
    [StructLayout(LayoutKind.Sequential)] struct TypedInfo { public int type; public IntPtr next; }
    [StructLayout(LayoutKind.Sequential)] struct Timespec { public long sec, nsec; }
    [DllImport("libc")] static extern int clock_gettime(int clock, ref Timespec ts);

    const int XR_TYPE_BODY_TRACKER_CREATE_INFO_FB = 1000076001, XR_TYPE_BODY_JOINTS_LOCATE_INFO_FB = 1000076002,
              XR_TYPE_BODY_JOINT_LOCATIONS_FB = 1000076005,
              XR_TYPE_SIMULTANEOUS_RESUME = 1000532002, XR_TYPE_SIMULTANEOUS_PAUSE = 1000532003;
    const int JointLocationSize = 40;   // XrSpaceLocationFlags (8) + XrPosef (28) + padding to 8

    protected override bool OnInstanceCreate(ulong xrInstance)
    {
        instance = xrInstance;
        Instance = this;
        return true;
    }

    protected override void OnSessionCreate(ulong xrSession)
    {
        session = xrSession;
        var gpa = Marshal.GetDelegateForFunctionPointer<GetProcAddr>(xrGetInstanceProcAddr);
        T Get<T>(string name) where T : class
        {
            if (gpa(instance, name, out var p) != 0 || p == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }
        createTracker = Get<CreateBodyTracker>("xrCreateBodyTrackerFB");
        destroyTracker = Get<DestroyBodyTracker>("xrDestroyBodyTrackerFB");
        locate = Get<LocateBodyJoints>("xrLocateBodyJointsFB");
        requestFidelity = Get<RequestFidelity>("xrRequestBodyTrackingFidelityMETA");
        resumeSim = Get<SimultaneousCall>("xrResumeSimultaneousHandsAndControllersTrackingMETA");
        pauseSim = Get<SimultaneousCall>("xrPauseSimultaneousHandsAndControllersTrackingMETA");
        convertTime = Get<ConvertTimespec>("xrConvertTimespecTimeToTimeKHR");
        Status = $"body {(createTracker != null ? "available" : "unsupported")}, simultaneous {(resumeSim != null ? "available" : "unsupported")}";
        Debug.Log("[AvatarDemo] MetaBodyFeature: " + Status);
    }

    protected override void OnSessionBegin(ulong xrSession) => SetSimultaneous(true);

    protected override void OnSessionDestroy(ulong xrSession)
    {
        SetBodyTracking(false);
        session = 0;
    }

    /// <summary>Hand tracking and controllers at the same time. The runtime resets this every session.</summary>
    public bool SetSimultaneous(bool on)
    {
        var fn = on ? resumeSim : pauseSim;
        if (fn == null || session == 0) return false;
        var info = new TypedInfo { type = on ? XR_TYPE_SIMULTANEOUS_RESUME : XR_TYPE_SIMULTANEOUS_PAUSE };
        int r = fn(session, ref info);
        if (r == 0) SimultaneousOn = on;
        Debug.Log($"[AvatarDemo] simultaneous hands and controllers {(on ? "resume" : "pause")}: result {r}");
        return r == 0;
    }

    /// <summary>Start or stop body tracking. High fidelity = the headset cameras track elbows and torso (Quest 3).</summary>
    public bool SetBodyTracking(bool on, bool highFidelity = true)
    {
        if (!on)
        {
            if (tracker != 0 && destroyTracker != null) destroyTracker(tracker);
            tracker = 0;
            if (locations != IntPtr.Zero) { Marshal.FreeHGlobal(locations); locations = IntPtr.Zero; }
            if (jointBuf != IntPtr.Zero) { Marshal.FreeHGlobal(jointBuf); jointBuf = IntPtr.Zero; }
            SetSimultaneous(true);
            return true;
        }
        if (tracker != 0) return true;
        if (createTracker == null || session == 0) { Status = "body tracking unsupported"; return false; }
        SetSimultaneous(false);                         // reported not to work alongside body tracking
        var info = new BodyTrackerCreateInfo { type = XR_TYPE_BODY_TRACKER_CREATE_INFO_FB, jointSet = 0 };
        int r = createTracker(session, ref info, out tracker);
        if (r != 0) { tracker = 0; Status = "body tracker failed: " + r; Debug.Log("[AvatarDemo] " + Status); SetSimultaneous(true); return false; }
        if (requestFidelity != null) Debug.Log("[AvatarDemo] body fidelity request: " + requestFidelity(tracker, highFidelity ? 2 : 1));
        jointBuf = Marshal.AllocHGlobal(JointLocationSize * JointCount);
        locations = Marshal.AllocHGlobal(Marshal.SizeOf<BodyJointLocations>());
        Status = "body tracking on";
        Debug.Log("[AvatarDemo] " + Status);
        return true;
    }

    /// <summary>Joint poses in tracking space (Unity coordinates). False if not tracking this frame.</summary>
    public bool TryLocate(Vector3[] pos, Quaternion[] rot, bool[] valid)
    {
        if (tracker == 0 || locate == null || convertTime == null) return false;
        var ts = new Timespec();
        clock_gettime(1 /* CLOCK_MONOTONIC */, ref ts);
        if (convertTime(instance, ref ts, out long now) != 0) return false;
        var info = new BodyJointsLocateInfo { type = XR_TYPE_BODY_JOINTS_LOCATE_INFO_FB, baseSpace = GetCurrentAppSpace(), time = now };
        var loc = new BodyJointLocations { type = XR_TYPE_BODY_JOINT_LOCATIONS_FB, jointCount = JointCount, jointLocations = jointBuf };
        Marshal.StructureToPtr(loc, locations, false);
        LastResult = locate(tracker, ref info, locations);
        if (LastResult != 0) { Diag(); return false; }
        loc = Marshal.PtrToStructure<BodyJointLocations>(locations);
        LastActive = loc.isActive != 0; LastConfidence = loc.confidence;
        int nValid = 0;
        for (int i = 0; i < JointCount; i++)
        {
            IntPtr p = jointBuf + i * JointLocationSize;
            ulong flags = (ulong)Marshal.ReadInt64(p);
            float F(int k) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p + 8 + 4 * k));
            // XrPosef: orientation (x, y, z, w) then position (x, y, z); flip Z to go right- -> left-handed
            rot[i] = new Quaternion(-F(0), -F(1), F(2), F(3));
            pos[i] = new Vector3(F(4), F(5), -F(6));
            valid[i] = (flags & 0x3) == 0x3;             // orientation and position valid
            if (valid[i]) nValid++;
        }
        LastValid = nValid;
        Diag();
        if (!LastActive) return false;
        return true;
    }

    void Diag()
    {
        if (calls++ % 144 == 0)
            Debug.Log($"[AvatarDemo] body locate: result {LastResult}, active {LastActive}, confidence {LastConfidence:F2}, valid joints {LastValid}/{JointCount}");
    }
}
