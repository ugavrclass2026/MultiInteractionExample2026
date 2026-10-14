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
    // controller -> wrist, in the controller's aim frame (the wrist sits behind the grip)
    public Vector3 wristOffset = new Vector3(0f, -0.02f, -0.08f);
    [Tooltip("Extra rotation of the avatar hand relative to the controller's aim pose (degrees), per hand")]
    public Vector3 leftHandEuler = new Vector3(0f, 0f, 0f), rightHandEuler = new Vector3(0f, 0f, 0f);
    [Tooltip("With hands and controllers both tracked, a hand this close to its controller is holding it")]
    public float holdingDistance = 0.15f;

    // the controller's aim (pointer) pose: its forward is where the controller points, which is close to
    // the direction of the knuckles; the grip pose's axes run along the handle instead
    readonly InputAction[] aimRot = { new InputAction(binding: "<XRController>{LeftHand}/pointerRotation"), new InputAction(binding: "<XRController>{RightHand}/pointerRotation") };
    readonly InputAction[] tracked = { new InputAction(binding: "<XRController>{LeftHand}/isTracked"), new InputAction(binding: "<XRController>{RightHand}/isTracked") };
    void OnEnable() { foreach (var a in aimRot) a.Enable(); foreach (var a in tracked) a.Enable(); }
    void OnDisable() { foreach (var a in aimRot) a.Disable(); foreach (var a in tracked) a.Disable(); }

    public class Hand
    {
        public bool isLeft, tracked, fromHandTracking, fromController;
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
    static readonly List<XRHandSubsystem> found = new List<XRHandSubsystem>();

    public static string ModeLabel(Mode m) => m == Mode.Auto ? "hands or controllers" : "controllers only";

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
    }

    void Fill(Hand h, Transform controller, XRHand xr)
    {
        h.hasJoints = false;
        h.fromHandTracking = false;
        h.fromController = false;
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
        // Both tracked (simultaneous hands and controllers): a hand right at its controller is holding it.
        if (hand && ctrl && Vector3.Distance(h.joints[XRHandJointID.Wrist.ToIndex()], ControllerWrist(controller, i)) < holdingDistance) hand = false;
        if (hand)
        {
            // Directions from the joints themselves, so no axis convention is assumed:
            // fingers = wrist -> middle knuckle; palm = fingers x (little knuckle -> index knuckle), flipped for the left hand.
            Vector3 J(XRHandJointID id) => h.joints[id.ToIndex()];
            h.wrist = J(XRHandJointID.Wrist);
            h.fingers = (J(XRHandJointID.MiddleProximal) - h.wrist).normalized;
            Vector3 across = J(XRHandJointID.IndexProximal) - J(XRHandJointID.LittleProximal);
            h.palm = Vector3.Cross(h.fingers, across).normalized * (h.isLeft ? -1f : 1f);
            h.fromHandTracking = true;
            h.tracked = true;
            return;
        }
        h.hasJoints = false;
        if (ctrl)
        {
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
        h.tracked = false;
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
}
