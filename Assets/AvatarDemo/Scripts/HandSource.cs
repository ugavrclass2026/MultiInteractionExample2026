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
        if (Time.frameCount % 144 == 0)
            Debug.Log($"[AvatarDemo] hands: subsystems {found.Count}, running {(subsystem != null)}, " +
                      $"L hand {(subsystem != null && subsystem.leftHand.isTracked)} ctrl {tracked[0].ReadValue<float>():F0} -> {Src(left)}, " +
                      $"R hand {(subsystem != null && subsystem.rightHand.isTracked)} ctrl {tracked[1].ReadValue<float>():F0} -> {Src(right)}, mode {mode}, " +
                      $"detached L {Det(true)} R {Det(false)}");
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
        bool ctrl = controller != null && tracked[i].ReadValue<float>() > 0.5f;
        bool hand = mode == Mode.Auto && xr.isTracked;
        if (hand)
        {
            for (int j = 0; j < h.joints.Length; j++)
                if (xr.GetJoint(XRHandJointIDUtility.FromIndex(j)).TryGetPose(out var jp)) h.joints[j] = ToWorld(jp).position;
            h.hasJoints = true;
        }
        // Controller first. With simultaneous hands and controllers on, Quest reports a controller at
        // /user/hand/left only while it is in that hand; a controller put down moves to the detached path.
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
    static string Det(bool left) { var d = DetachedControllersFeature.Find(left); return d == null ? "none" : d.isTracked.isPressed ? "tracked" : "untracked"; }
    static string Src(Hand h) => h.fromHandTracking ? "hand" : h.fromController ? "controller" : h.fromBody ? "body" : "-";
}
