using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Calls Meta OpenXR extensions directly, without the Meta SDK:
///   - XR_FB_body_tracking (+ XR_META_body_tracking_fidelity): 70 upper-body and hand joints from the headset.
///     Fidelity High is inside-out body tracking on Quest 3 (the cameras see elbows and torso).
///   - XR_META_simultaneous_hands_and_controllers: hand tracking and controllers at the same time.
///   - XR_META_hand_tracking_frequency_hint: Fast Motion Mode (60 Hz hand tracking), switchable at runtime.
///   - XR_META_hand_tracking_wide_motion_mode2 (+ XR_EXT_hand_tracking_data_source): Wide Motion Mode, hands estimated
///     from body tracking when out of the cameras' view. Requested when a hand tracker is created, so this feature wraps
///     xrCreateHandTrackerEXT (HookGetInstanceProcAddr) and restarts XR Hands to apply it.
///   - XR_FB_face_tracking2 (+ XR_META_face_tracking_visemes): 70 face expression weights and 15 visemes, from the
///     face cameras (Quest Pro) or, on headsets without them (Quest 2, 3, 3S), from the microphone.
/// The two don't run together (Quest 3, 2026-10-14): with simultaneous mode on, xrCreateBodyTrackerFB fails
/// with -2. So body tracking pauses simultaneous mode (the headset then tracks hands OR controllers).
/// AvatarSwitcher's tracking modes decide when it is on.
/// Poses are converted from OpenXR (right-handed) to Unity (left-handed) and are relative to the app's
/// tracking space (the XR Origin).
/// </summary>
#if UNITY_EDITOR
[UnityEditor.XR.OpenXR.Features.OpenXRFeature(UiName = "AvatarDemo: Meta body + face tracking + simultaneous hands/controllers",
    BuildTargetGroups = new[] { BuildTargetGroup.Android },
    Company = "UGA CSCI 4830",
    Desc = "XR_FB_body_tracking, XR_FB_face_tracking2 and XR_META_simultaneous_hands_and_controllers, called directly.",
    OpenxrExtensionStrings = Extensions,
    Version = "0.1.0",
    Category = UnityEditor.XR.OpenXR.Features.FeatureCategory.Feature,
    FeatureId = "edu.uga.csci4830.avatardemo.metabody")]
#endif
public class MetaBodyFeature : OpenXRFeature
{
    const string Extensions = "XR_FB_body_tracking XR_META_body_tracking_fidelity XR_META_simultaneous_hands_and_controllers XR_KHR_convert_timespec_time XR_FB_face_tracking2 XR_META_face_tracking_visemes XR_META_hand_tracking_frequency_hint XR_EXT_hand_tracking_data_source XR_META_hand_tracking_wide_motion_mode2";
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateFaceTracker(ulong session, ref FaceTrackerCreateInfo info, out ulong tracker);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DestroyFaceTracker(ulong tracker);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetFaceWeights(ulong tracker, ref FaceExpressionInfo info, ref FaceExpressionWeights weights);
    [StructLayout(LayoutKind.Sequential)] struct FaceTrackerCreateInfo { public int type; public IntPtr next; public int expressionSet; public uint dataSourceCount; public IntPtr dataSources; }
    [StructLayout(LayoutKind.Sequential)] struct FaceExpressionInfo { public int type; public IntPtr next; public long time; }
    [StructLayout(LayoutKind.Sequential)] struct FaceExpressionWeights
    { public int type; public IntPtr next; public uint weightCount; public IntPtr weights; public uint confidenceCount; public IntPtr confidences;
      public int isValid; public int isEyeFollowingValid; public int dataSource; public long time; }
    CreateFaceTracker createFace; DestroyFaceTracker destroyFace; GetFaceWeights getFace;
    ulong faceTracker;
    IntPtr faceWeights, faceConfidences, faceVisemes;     // unmanaged buffers the runtime writes into
    const int XR_TYPE_FACE_TRACKER_CREATE_INFO2_FB = 1000287014, XR_TYPE_FACE_EXPRESSION_INFO2_FB = 1000287015,
              XR_TYPE_FACE_EXPRESSION_WEIGHTS2_FB = 1000287016, XR_TYPE_FACE_TRACKING_VISEMES_META = 1000541000;
    public const int ExpressionCount = 70, VisemeCount = 15;
    public bool FaceTrackingOn => faceTracker != 0;
    public string FaceStatus { get; private set; } = "off";
    public int FaceDataSource { get; private set; } = -1;      // 0 = cameras, 1 = audio
    int faceCalls;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetFrequencyHint(ulong session, int hint);
    SetFrequencyHint frequencyHint;
    public bool FastMotionOn { get; private set; }
    public static int HandTrackersCreated { get; private set; }
    public static int LastHandTrackerResult { get; private set; } = -999;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StringToPath(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string s, out ulong path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PathToString(ulong instance, ulong path, uint capacity, out uint count, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetCurrentProfile(ulong session, ulong topLevelPath, ref ProfileState state);
    [StructLayout(LayoutKind.Sequential)] struct ProfileState { public int type; public IntPtr next; public ulong profile; }
    StringToPath toPath; PathToString toString; GetCurrentProfile currentProfile;

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
        createFace = Get<CreateFaceTracker>("xrCreateFaceTracker2FB");
        destroyFace = Get<DestroyFaceTracker>("xrDestroyFaceTracker2FB");
        getFace = Get<GetFaceWeights>("xrGetFaceExpressionWeights2FB");
        frequencyHint = Get<SetFrequencyHint>("xrSetHandTrackingFrequencyHintMETA");
        toPath = Get<StringToPath>("xrStringToPath");
        toString = Get<PathToString>("xrPathToString");
        currentProfile = Get<GetCurrentProfile>("xrGetCurrentInteractionProfile");
        Status = $"body {(createTracker != null ? "available" : "unsupported")}, simultaneous {(resumeSim != null ? "available" : "unsupported")}, face {(createFace != null ? "available" : "unsupported")}";
        Debug.Log("[AvatarDemo] MetaBodyFeature: " + Status);
    }

    protected override void OnSessionBegin(ulong xrSession) => SetSimultaneous(true);

    protected override void OnSessionDestroy(ulong xrSession)
    {
        SetBodyTracking(false);
        SetFaceTracking(false);
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

    /// <summary>Fast Motion Mode: 60 Hz hand tracking. Meta: not with simultaneous hands and controllers.</summary>
    public bool SetFastMotion(bool on)
    {
        if (frequencyHint == null || session == 0) return false;
        int r = frequencyHint(session, on ? 2 : 1);    // XR_HAND_TRACKING_FREQUENCY_HINT_HIGH_META / DEFAULT_META
        Debug.Log($"[AvatarDemo] fast motion mode {(on ? "on" : "off")}: result {r}");
        if (r == 0) FastMotionOn = on;
        return r == 0;
    }
    public bool FastMotionSupported => frequencyHint != null;

    /// <summary>Wide Motion Mode. Unity's XR Hands creates its hand trackers once, so this feature creates its own
    /// pair asking for the wide-motion data source and, while the mode is on, answers XR Hands' joint queries
    /// (xrLocateHandJointsEXT) from them. Meta: not with simultaneous hands and controllers.</summary>
    public void SetWideMotion(bool on)
    {
        if (on)
        {
            for (int hand = 1; hand <= 2; hand++)          // XR_HAND_LEFT_EXT, XR_HAND_RIGHT_EXT
            {
                if (wideTracker[hand] != 0 || nextCreateHandTracker == null || handSession == 0) continue;
                var info = new HandTrackerCreateInfo { type = 1000051001, hand = hand, jointSet = 0 };   // XR_TYPE_HAND_TRACKER_CREATE_INFO_EXT
                IntPtr sources = Marshal.AllocHGlobal(8), ds = Marshal.AllocHGlobal(Marshal.SizeOf<HandTrackingDataSourceInfo>()),
                       ci = Marshal.AllocHGlobal(Marshal.SizeOf<HandTrackerCreateInfo>());
                Marshal.WriteInt32(sources, 0, 1);              // XR_HAND_TRACKING_DATA_SOURCE_UNOBSTRUCTED_EXT
                Marshal.WriteInt32(sources, 4, 1000695000);     // XR_HAND_TRACKING_DATA_SOURCE_UNOBSTRUCTED_WIDE_MOTION_META
                Marshal.StructureToPtr(new HandTrackingDataSourceInfo { type = 1000428000, next = IntPtr.Zero, count = 2, sources = sources }, ds, false);
                info.next = ds;
                Marshal.StructureToPtr(info, ci, false);
                int r = nextCreateHandTracker(handSession, ci, out wideTracker[hand]);
                Marshal.FreeHGlobal(ci); Marshal.FreeHGlobal(ds); Marshal.FreeHGlobal(sources);
                if (r != 0) wideTracker[hand] = 0;
                Debug.Log($"[AvatarDemo] wide motion tracker {(hand == 1 ? "L" : "R")}: result {r}");
            }
            WideMotionOn = wideTracker[1] != 0 && wideTracker[2] != 0;
        }
        else
        {
            WideMotionOn = false;
            for (int hand = 1; hand <= 2; hand++)
            {
                if (wideTracker[hand] != 0 && nextDestroyHandTracker != null) nextDestroyHandTracker(wideTracker[hand]);
                wideTracker[hand] = 0;
            }
            Debug.Log("[AvatarDemo] wide motion mode off");
        }
    }
    public static bool WideMotionOn { get; private set; }
    /// <summary>The data source of the last joint query per hand (1 = cameras, 1000695000 = wide motion), and whether it was active.</summary>
    public static readonly int[] LastDataSource = new int[3];
    public static readonly bool[] LastSourceActive = new bool[3];

    // ---- wrapping XR Hands' hand-tracker calls ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetProcAddrRaw(ulong instance, IntPtr name, out IntPtr fn);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateHandTracker(ulong session, IntPtr createInfo, out ulong tracker);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DestroyHandTracker(ulong tracker);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LocateHandJoints(ulong tracker, IntPtr locateInfo, IntPtr locations);
    [StructLayout(LayoutKind.Sequential)] struct HandTrackerCreateInfo { public int type; public IntPtr next; public int hand; public int jointSet; }
    [StructLayout(LayoutKind.Sequential)] struct HandTrackingDataSourceInfo { public int type; public IntPtr next; public uint count; public IntPtr sources; }
    static GetProcAddrRaw nextGetProcAddr;
    static CreateHandTracker nextCreateHandTracker;
    static DestroyHandTracker nextDestroyHandTracker;
    static LocateHandJoints nextLocateHandJoints;
    static readonly GetProcAddrRaw hookedGetProcAddr = HookedGetProcAddr;
    static readonly CreateHandTracker hookedCreateHandTracker = HookedCreateHandTracker;
    static readonly DestroyHandTracker hookedDestroyHandTracker = HookedDestroyHandTracker;
    static readonly LocateHandJoints hookedLocateHandJoints = HookedLocateHandJoints;
    static readonly System.Collections.Generic.Dictionary<ulong, int> handOf = new System.Collections.Generic.Dictionary<ulong, int>();
    static readonly ulong[] wideTracker = new ulong[3];
    static ulong handSession;
    static IntPtr sourceState;          // XrHandTrackingDataSourceStateEXT, chained into joint queries to see which source answered
    static int locateCalls;

    protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
    {
        nextGetProcAddr = Marshal.GetDelegateForFunctionPointer<GetProcAddrRaw>(func);
        return Marshal.GetFunctionPointerForDelegate(hookedGetProcAddr);
    }

    [AOT.MonoPInvokeCallback(typeof(GetProcAddrRaw))]
    static int HookedGetProcAddr(ulong instance, IntPtr name, out IntPtr fn)
    {
        int r = nextGetProcAddr(instance, name, out fn);
        if (r != 0 || fn == IntPtr.Zero) return r;
        switch (Marshal.PtrToStringAnsi(name))
        {
            case "xrCreateHandTrackerEXT":
                nextCreateHandTracker = Marshal.GetDelegateForFunctionPointer<CreateHandTracker>(fn);
                fn = Marshal.GetFunctionPointerForDelegate(hookedCreateHandTracker); break;
            case "xrDestroyHandTrackerEXT":
                nextDestroyHandTracker = Marshal.GetDelegateForFunctionPointer<DestroyHandTracker>(fn);
                fn = Marshal.GetFunctionPointerForDelegate(hookedDestroyHandTracker); break;
            case "xrLocateHandJointsEXT":
                nextLocateHandJoints = Marshal.GetDelegateForFunctionPointer<LocateHandJoints>(fn);
                fn = Marshal.GetFunctionPointerForDelegate(hookedLocateHandJoints); break;
        }
        return r;
    }

    [AOT.MonoPInvokeCallback(typeof(CreateHandTracker))]
    static int HookedCreateHandTracker(ulong session, IntPtr createInfo, out ulong tracker)
    {
        LastHandTrackerResult = nextCreateHandTracker(session, createInfo, out tracker);
        int hand = Marshal.ReadInt32(createInfo, 16);       // XrHandTrackerCreateInfoEXT.hand
        if (LastHandTrackerResult == 0) { handOf[tracker] = hand; handSession = session; }
        HandTrackersCreated++;
        Debug.Log($"[AvatarDemo] xrCreateHandTrackerEXT ({(hand == 1 ? "L" : "R")}): result {LastHandTrackerResult}");
        return LastHandTrackerResult;
    }

    [AOT.MonoPInvokeCallback(typeof(DestroyHandTracker))]
    static int HookedDestroyHandTracker(ulong tracker) { handOf.Remove(tracker); return nextDestroyHandTracker(tracker); }

    [AOT.MonoPInvokeCallback(typeof(LocateHandJoints))]
    static int HookedLocateHandJoints(ulong tracker, IntPtr locateInfo, IntPtr locations)
    {
        int hand = 0;
        if (!WideMotionOn || !handOf.TryGetValue(tracker, out hand) || wideTracker[hand] == 0)
            return nextLocateHandJoints(tracker, locateInfo, locations);
        tracker = wideTracker[hand];
        // our trackers were created with a data-source request, so they can report which source answered:
        // put a data-source state struct at the front of the output's next chain, then restore it
        if (sourceState == IntPtr.Zero) sourceState = Marshal.AllocHGlobal(24);
        IntPtr oldNext = Marshal.ReadIntPtr(locations, 8);
        Marshal.WriteInt32(sourceState, 0, 1000428001);     // XR_TYPE_HAND_TRACKING_DATA_SOURCE_STATE_EXT
        Marshal.WriteIntPtr(sourceState, 8, oldNext);
        Marshal.WriteInt32(sourceState, 16, 0); Marshal.WriteInt32(sourceState, 20, 0);
        Marshal.WriteIntPtr(locations, 8, sourceState);
        int r = nextLocateHandJoints(tracker, locateInfo, locations);
        Marshal.WriteIntPtr(locations, 8, oldNext);
        LastSourceActive[hand] = Marshal.ReadInt32(sourceState, 16) != 0;
        LastDataSource[hand] = Marshal.ReadInt32(sourceState, 20);
        if (locateCalls++ % 288 == 0)
            Debug.Log($"[AvatarDemo] hand joints (wide motion trackers): L source {SourceName(1)}, R source {SourceName(2)}, result {r}");
        return r;
    }

    public static string SourceName(int hand) => !LastSourceActive[hand] ? "-" : LastDataSource[hand] == 1000695000 ? "wide motion" : LastDataSource[hand] == 1 ? "cameras" : LastDataSource[hand] == 2 ? "controller" : LastDataSource[hand].ToString();

    /// <summary>Start or stop face tracking. Asks for the face cameras first, then the microphone.</summary>
    public bool SetFaceTracking(bool on)
    {
        if (!on)
        {
            if (faceTracker != 0 && destroyFace != null) destroyFace(faceTracker);
            faceTracker = 0; FaceStatus = "off";
            return true;
        }
        if (faceTracker != 0) return true;
        if (createFace == null || session == 0) { FaceStatus = "face tracking unsupported"; return false; }
        int r = -1;
        foreach (var sources in new[] { new[] { 0, 1 }, new[] { 1 } })    // visual + audio, else audio only
        {
            var src = Marshal.AllocHGlobal(4 * sources.Length);
            Marshal.Copy(sources, 0, src, sources.Length);
            var info = new FaceTrackerCreateInfo { type = XR_TYPE_FACE_TRACKER_CREATE_INFO2_FB, expressionSet = 0, dataSourceCount = (uint)sources.Length, dataSources = src };
            r = createFace(session, ref info, out faceTracker);
            Marshal.FreeHGlobal(src);
            Debug.Log($"[AvatarDemo] face tracker ({(sources.Length == 2 ? "cameras or audio" : "audio")}): result {r}");
            if (r == 0) break;
            faceTracker = 0;
        }
        if (r != 0) { FaceStatus = "face tracker failed: " + r; return false; }
        if (faceWeights == IntPtr.Zero)
        {
            faceWeights = Marshal.AllocHGlobal(4 * ExpressionCount);
            faceConfidences = Marshal.AllocHGlobal(4 * 2);
            faceVisemes = Marshal.AllocHGlobal(80);            // XrFaceTrackingVisemesMETA: type, next, isValid, 15 floats
        }
        FaceStatus = "on";
        return true;
    }

    /// <summary>This frame's 70 expression weights (XrFaceExpression2FB order) and 15 visemes, 0..1.</summary>
    public bool TryGetFace(float[] expressions, float[] visemes, out bool visemesValid)
    {
        visemesValid = false;
        if (faceTracker == 0 || getFace == null || convertTime == null) return false;
        var ts = new Timespec();
        clock_gettime(1 /* CLOCK_MONOTONIC */, ref ts);
        if (convertTime(instance, ref ts, out long now) != 0) return false;
        Marshal.WriteInt32(faceVisemes, 0, XR_TYPE_FACE_TRACKING_VISEMES_META);
        Marshal.WriteInt64(faceVisemes, 8, 0);
        Marshal.WriteInt32(faceVisemes, 16, 0);
        var info = new FaceExpressionInfo { type = XR_TYPE_FACE_EXPRESSION_INFO2_FB, time = now };
        var w = new FaceExpressionWeights { type = XR_TYPE_FACE_EXPRESSION_WEIGHTS2_FB, next = faceVisemes, weightCount = ExpressionCount, weights = faceWeights, confidenceCount = 2, confidences = faceConfidences };
        int r = getFace(faceTracker, ref info, ref w);
        if (faceCalls++ % 288 == 0)
            Debug.Log($"[AvatarDemo] face: result {r}, valid {w.isValid}, source {(w.dataSource == 0 ? "cameras" : "audio")}, visemes valid {Marshal.ReadInt32(faceVisemes, 16)}");
        if (r != 0 || w.isValid == 0) { FaceStatus = r != 0 ? "face error " + r : "no face data"; return false; }
        FaceDataSource = w.dataSource;
        Marshal.Copy(faceWeights, expressions, 0, ExpressionCount);
        visemesValid = Marshal.ReadInt32(faceVisemes, 16) != 0;
        if (visemesValid) Marshal.Copy(faceVisemes + 20, visemes, 0, VisemeCount);
        FaceStatus = "tracking (" + (w.dataSource == 0 ? "cameras" : "audio") + ")";
        return true;
    }

    /// <summary>Diagnostics: the interaction profile the runtime reports for a top-level path, e.g. /user/hand/left.</summary>
    public string CurrentProfile(string userPath)
    {
        if (toPath == null || currentProfile == null || toString == null || session == 0) return "n/a";
        if (toPath(instance, userPath, out var top) != 0) return "bad path";
        var st = new ProfileState { type = 53 /* XR_TYPE_INTERACTION_PROFILE_STATE */ };
        int r = currentProfile(session, top, ref st);
        if (r != 0) return "error " + r;
        if (st.profile == 0) return "none";
        var buf = Marshal.AllocHGlobal(256);
        try { return toString(instance, st.profile, 256, out var n, buf) == 0 ? Marshal.PtrToStringAnsi(buf).Replace("/interaction_profiles/", "") : "?"; }
        finally { Marshal.FreeHGlobal(buf); }
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
            return true;
        }
        if (tracker != 0) return true;
        if (createTracker == null || session == 0) { Status = "body tracking unsupported"; return false; }
        if (SimultaneousOn) SetSimultaneous(false);
        var info = new BodyTrackerCreateInfo { type = XR_TYPE_BODY_TRACKER_CREATE_INFO_FB, jointSet = 0 };
        int r = createTracker(session, ref info, out tracker);
        if (r != 0) { tracker = 0; Status = "body tracker failed: " + r; Debug.Log("[AvatarDemo] " + Status); return false; }
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
