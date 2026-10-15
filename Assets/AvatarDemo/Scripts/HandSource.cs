using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

/// <summary>
/// Where each avatar hand should be this frame: from hand tracking (the 26 OpenXR joints) when the
/// headset sees your hands, otherwise from the controller. Avatars read Wrist/Palm/Fingers from here,
/// so every avatar works with either input.
/// </summary>
[DefaultExecutionOrder(-50)]
public class HandSource : MonoBehaviour
{
    public enum Mode { Auto, ControllersOnly }
    public Mode mode = Mode.Auto;

    public Transform trackingSpace;                 // XR Origin: hand-tracking poses are relative to it
    public Transform leftController, rightController;
    [Tooltip("Optional: with body tracking on, a hand with neither hand tracking nor a controller uses the body tracker's hand joints")]
    public BodySource body;
    [Tooltip("Simultaneous mode off: a controller that hasn't moved 2 mm or turned 2 degrees for this long is lying somewhere, not in a hand (s)")]
    public float stillTime = 1.5f;
    [Tooltip("When a hand changes source (controller, hand tracking, body tracking) or jumps more than 15 cm, blend over this long (s)")]
    public float switchBlend = 0.2f;
    readonly Vector3[] anchorPos = new Vector3[2];
    readonly Quaternion[] anchorRot = new Quaternion[2];
    readonly float[] lastMoved = new float[2];
    // per hand: last output, its source, blend start, and counters for the log
    readonly Vector3[] outWrist = new Vector3[2], outFingers = new Vector3[2], outPalm = new Vector3[2];
    readonly int[] outSource = new int[2], switches = new int[2], jumps = new int[2];
    readonly float[] blendStart = new float[2], lastGood = new float[2];
    [Tooltip("A better source that drops out for less than this keeps its last pose (s)")]
    public float dropGrace = 0.25f;
    // controller -> wrist, in the controller's aim frame (the wrist sits behind the grip)
    public Vector3 wristOffset = new Vector3(0f, -0.02f, -0.08f);
    [Tooltip("Extra rotation of the avatar hand relative to the controller's aim pose (degrees), per hand")]
    public Vector3 leftHandEuler = new Vector3(0f, 0f, 0f), rightHandEuler = new Vector3(0f, 0f, 0f);

    // the controller's aim (pointer) pose: its forward is where the controller points, which is close to
    // the direction of the knuckles; the grip pose's axes run along the handle instead
    readonly InputAction[] aimRot = { new InputAction(binding: "<XRController>{LeftHand}/pointerRotation"), new InputAction(binding: "<XRController>{RightHand}/pointerRotation") };
    readonly InputAction[] tracked = { new InputAction(binding: "<XRController>{LeftHand}/isTracked"), new InputAction(binding: "<XRController>{RightHand}/isTracked") };
    void OnEnable() { foreach (var a in aimRot) a.Enable(); foreach (var a in tracked) a.Enable(); }
    void OnDisable() { foreach (var a in aimRot) a.Disable(); foreach (var a in tracked) a.Disable(); }

    public class Hand
    {
        public bool isLeft, tracked, fromHandTracking, fromController, fromBody;
        public int Source => fromController ? 1 : fromHandTracking ? 2 : fromBody ? 3 : 0;
        public Vector3 wrist;          // world position of the wrist
        public Vector3 fingers;        // unit direction the fingers point
        public Vector3 palm;           // unit direction the palm faces
        public readonly Vector3[] joints = new Vector3[XRHandJointID.EndMarker.ToIndex()];
        public bool hasJoints;
    }
    public readonly Hand left = new Hand { isLeft = true }, right = new Hand { isLeft = false };

    // optional override for demos and screenshots: (hand, time) -> wrist, fingers, palm
    public System.Func<Hand, float, (Vector3, Vector3, Vector3)> demo;

    XRHandSubsystem subsystem;
    public XRHandSubsystem Subsystem => subsystem;
    static readonly List<XRHandSubsystem> found = new List<XRHandSubsystem>();

    void Update()
    {
        if (subsystem == null || !subsystem.running)
        {
            SubsystemManager.GetSubsystems(found);
            subsystem = null;
            foreach (var s in found) if (s.running) { subsystem = s; break; }
        }
        Fill(left, leftController, subsystem != null ? subsystem.leftHand : default);
        Fill(right, rightController, subsystem != null ? subsystem.rightHand : default);
        Smooth(left); Smooth(right);
        if (Time.frameCount % 144 == 0)
        {
            var mf = MetaBodyFeature.Instance;
            Debug.Log($"[AvatarDemo] hands: sim {(mf != null && mf.SimultaneousOn ? 1 : 0)} body {(body != null && body.Tracked ? 1 : 0)} mode {mode} | " +
                      $"L hand {B(subsystem != null && subsystem.leftHand.isTracked)} ctrl {B(Ctrl(0))} still {Time.time - lastMoved[0]:F1}s -> {Src(left)} sw {switches[0]} jump {jumps[0]} | " +
                      $"R hand {B(subsystem != null && subsystem.rightHand.isTracked)} ctrl {B(Ctrl(1))} still {Time.time - lastMoved[1]:F1}s -> {Src(right)} sw {switches[1]} jump {jumps[1]} | " +
                      $"detached L {Det(true)} R {Det(false)}");
            switches[0] = switches[1] = jumps[0] = jumps[1] = 0;
        }
        if (Time.frameCount % 144 == 72) Profiles();
    }

    void Fill(Hand h, Transform controller, XRHand xr)
    {
        h.hasJoints = false;
        h.fromHandTracking = false;
        h.fromController = false;
        h.fromBody = false;
        if (demo != null)
        {
            (h.wrist, h.fingers, h.palm) = demo(h, Time.time);
            h.tracked = true;
            return;
        }
        int i = h.isLeft ? 0 : 1;
        bool ctrl = controller != null && Ctrl(i);
        if (ctrl)
        {
            // local pose: moving the rig (locomotion) must not count as the controller moving
            if ((controller.localPosition - anchorPos[i]).sqrMagnitude > 0.002f * 0.002f || Quaternion.Angle(controller.localRotation, anchorRot[i]) > 2f)
            { lastMoved[i] = Time.time; anchorPos[i] = controller.localPosition; anchorRot[i] = controller.localRotation; }
        }
        // With simultaneous mode on, Quest reports a controller at /user/hand/* only while it is in that hand (a
        // controller put down moves to the detached path). With it off (body tracking, "hands or controllers"), a
        // controller lying on the table is still reported there, so it counts as held only while it moves.
        var f = MetaBodyFeature.Instance;
        bool simultaneous = f != null && f.SimultaneousOn;
        if (ctrl && !simultaneous && Time.time - lastMoved[i] > stillTime) ctrl = false;
        bool hand = mode == Mode.Auto && xr.isTracked;
        if (hand)
        {
            for (int j = 0; j < h.joints.Length; j++)
                if (xr.GetJoint(XRHandJointIDUtility.FromIndex(j)).TryGetPose(out var jp)) h.joints[j] = ToWorld(jp).position;
            h.hasJoints = true;
        }
        // controller first: it is in this hand
        if (ctrl)
        {
            h.hasJoints = false;
            Quaternion aim = AimRotation(controller, i) * Quaternion.Euler(h.isLeft ? leftHandEuler : rightHandEuler);
            h.wrist = ControllerWrist(controller, i);
            h.fingers = aim * Vector3.forward;
            // palm faces in toward the body's midline, a little down
            Vector3 r = aim * Vector3.right, d = aim * Vector3.down;
            h.palm = ((h.isLeft ? r : -r) + d * 0.35f).normalized;
            h.fromController = true;
            h.tracked = true;
            return;
        }
        if (hand)
        {
            FromJoints(h);
            h.fromHandTracking = true;
            h.tracked = true;
            return;
        }
        h.hasJoints = false;
        // Body tracking on: the headset tracks hands OR controllers, so a free hand next to a held controller
        // has no hand tracking. The body tracker still estimates that hand.
        if (FromBody(h)) return;
        h.tracked = false;
    }

    // Directions from the joints themselves, so no axis convention is assumed:
    // fingers = wrist -> middle knuckle; palm = fingers x (little knuckle -> index knuckle), flipped for the left hand.
    static void FromJoints(Hand h)
    {
        Vector3 J(XRHandJointID id) => h.joints[id.ToIndex()];
        h.wrist = J(XRHandJointID.Wrist);
        h.fingers = (J(XRHandJointID.MiddleProximal) - h.wrist).normalized;
        Vector3 across = J(XRHandJointID.IndexProximal) - J(XRHandJointID.LittleProximal);
        h.palm = Vector3.Cross(h.fingers, across).normalized * (h.isLeft ? -1f : 1f);
    }

    // XrBodyJointFB has each hand as palm, wrist, then the other 24 joints in OpenXR hand-joint order.
    // Unity's XRHandJointID index order is wrist (0), palm (1), then the same 24.
    bool FromBody(Hand h)
    {
        if (body == null || !body.Tracked) return false;
        int b = h.isLeft ? MetaBodyFeature.LeftWrist - 1 : MetaBodyFeature.RightWrist - 1;   // the palm joint
        if (!body.Valid(b + 1) || !body.Valid(b + 7) || !body.Valid(b + 12) || !body.Valid(b + 22)) return false;
        h.joints[0] = body.pos[b + 1];
        h.joints[1] = body.pos[b];
        for (int j = 2; j < h.joints.Length; j++) h.joints[j] = body.pos[b + j];
        h.hasJoints = true;
        FromJoints(h);
        h.fromBody = true;
        h.tracked = true;
        return true;
    }

    Quaternion AimRotation(Transform controller, int i)
    {
        Quaternion q = aimRot[i].ReadValue<Quaternion>();
        if (Mathf.Abs(q.x) + Mathf.Abs(q.y) + Mathf.Abs(q.z) + Mathf.Abs(q.w) < 0.5f) return controller.rotation;   // no aim pose
        return trackingSpace ? trackingSpace.rotation * q : q;
    }

    Vector3 ControllerWrist(Transform controller, int i) => controller.position + AimRotation(controller, i) * wristOffset;

    Pose ToWorld(Pose p) => trackingSpace
        ? new Pose(trackingSpace.TransformPoint(p.position), trackingSpace.rotation * p.rotation)
        : p;

    public Hand Get(bool isLeft) => isLeft ? left : right;
    /// <summary>The controller in that hand is tracked (held; a put-down controller is a DetachedTouchController).</summary>
    public bool ControllerTracked(bool isLeft) => tracked[isLeft ? 0 : 1].ReadValue<float>() > 0.5f;
    string lastDevices;
    void Profiles()
    {
        var f = MetaBodyFeature.Instance;
        if (f != null)
            Debug.Log("[AvatarDemo] profiles: " + string.Join(", ", System.Array.ConvertAll(
                new[] { "/user/hand/left", "/user/hand/right", "/user/detached_controller_meta/left", "/user/detached_controller_meta/right" },
                p => p.Replace("/user/", "") + " " + f.CurrentProfile(p))));
        var names = new List<string>();
        foreach (var d in UnityEngine.InputSystem.InputSystem.devices) names.Add($"{d.layout}:{d.description.product}");
        string all = string.Join(" | ", names);
        if (all != lastDevices) { lastDevices = all; Debug.Log("[AvatarDemo] devices: " + all); }
    }
    bool Ctrl(int i) => tracked[i].ReadValue<float>() > 0.5f;
    static string B(bool b) => b ? "1" : "0";

    // A hand's output, smoothed:
    //  - a better source (controller > hand tracking > body tracking) that drops out for less than dropGrace keeps
    //    its last pose, so one-frame tracking losses don't flip the source back and forth;
    //  - when the source changes, or the same source jumps more than 15 cm, blend over switchBlend.
    void Smooth(Hand h)
    {
        int i = h.isLeft ? 0 : 1;
        int src = h.tracked ? h.Source : 0;
        if (outSource[i] != 0 && Rank(src) > Rank(outSource[i]) && Time.time - lastGood[i] < dropGrace)
        {
            h.tracked = true; h.hasJoints = false;
            h.fromController = outSource[i] == 1; h.fromHandTracking = outSource[i] == 2; h.fromBody = outSource[i] == 3;
            h.wrist = outWrist[i]; h.fingers = outFingers[i]; h.palm = outPalm[i];
            return;
        }
        if (src == 0) { outSource[i] = 0; outWrist[i] = Vector3.zero; return; }   // zero: don't count the return as a jump
        lastGood[i] = Time.time;
        if (src != outSource[i])
        {
            if (outSource[i] != 0) { switches[i]++; blendStart[i] = Time.time; }
            else blendStart[i] = -1f;                    // was untracked: HalfBodyAvatar eases the arm in
            outSource[i] = src;
        }
        // the same source can jump too (hand tracking re-finding a hand): blend those as well
        bool jump = outWrist[i] != Vector3.zero && (h.wrist - outWrist[i]).sqrMagnitude > 0.15f * 0.15f;
        if (jump) { jumps[i]++; if (Time.time - blendStart[i] > switchBlend) blendStart[i] = Time.time; }
        float k = blendStart[i] < 0f || switchBlend <= 0f ? 1f : Mathf.Clamp01((Time.time - blendStart[i]) / switchBlend);
        if (k < 1f)
        {
            h.wrist = Vector3.Lerp(outWrist[i], h.wrist, k);
            h.fingers = Vector3.Slerp(outFingers[i], h.fingers, k).normalized;
            h.palm = Vector3.Slerp(outPalm[i], h.palm, k).normalized;
        }
        outWrist[i] = h.wrist; outFingers[i] = h.fingers; outPalm[i] = h.palm;
    }

    static int Rank(int source) => source == 0 ? 9 : source;    // 1 controller, 2 hand tracking, 3 body tracking

    static string Det(bool left) { var d = DetachedControllersFeature.Find(left); return d == null ? "none" : d.isTracked.isPressed ? "tracked" : "untracked"; }
    static string Src(Hand h) => h.fromHandTracking ? "hand" : h.fromController ? "controller" : h.fromBody ? "body" : "-";
}
