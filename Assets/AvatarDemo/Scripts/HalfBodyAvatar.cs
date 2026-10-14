using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Drives a half-body avatar from three tracked poses (head and two hands), the finger joints when hand
/// tracking is on, and the upper body when body tracking is on. Works on any rig. Every frame, in LateUpdate:
///   1. reset the driven bones to their bind pose (so IK twist never accumulates),
///   2. hips: turn toward where the body faces (body tracking: the tracked shoulders; otherwise the head
///      and hands), with a dead zone and lag,
///   3. chest: twist part of the way toward the head (or the tracked chest), so the torso turns,
///   4. move the whole avatar so its head bone sits where the headset is,
///   5. head bone copies the headset's rotation,
///   6. two-bone IK for each arm (elbows bend toward the tracked elbows if any, else a pole), then the hands,
///   7. fingers follow the tracked finger joints, if any.
/// Optionally (the robot) the arm bones stretch to fit the user's reach.
/// </summary>
public class HalfBodyAvatar : MonoBehaviour
{
    [Header("Inputs")]
    public Transform hmd;
    public HandSource hands;
    public BodySource body;

    [Header("Bones")]
    public Transform head, chest;
    [Tooltip("Spine bones between the hips and the head; the torso twist is shared among them")]
    public Transform[] spine = new Transform[0];
    public Transform leftUpper, leftLower, leftHand, rightUpper, rightLower, rightHand;
    public Transform[] hideAtBind;                 // thighs: scaled to zero
    [Tooltip("Per finger (thumb, index, middle, ring, little), three bones each. Optional.")]
    public Transform[] leftFingers = new Transform[15], rightFingers = new Transform[15];

    [Header("Fit")]
    public Vector3 hmdToHead = new Vector3(0f, -0.08f, -0.09f);
    public Vector3 elbowPole = new Vector3(0.25f, -0.5f, -0.35f);
    public bool explicitHandAxes;
    public Vector3 handFingersAxis = Vector3.forward, handPalmAxis = Vector3.down;

    [Header("Torso turning")]
    public float deadZone = 10f, slowDegPerSec = 60f, fastDegPerSec = 360f, fastAfter = 60f;
    [Range(0f, 1f)] public float chestTwist = 0.6f;          // share of head-vs-hips yaw the chest takes
    public float maxTwist = 50f;
    [Range(0f, 1f)] public float handsWeight = 0.6f;         // how much the hands pull the body's facing

    public float ArmScale { get; private set; } = 1f;

    Transform[] driven;
    Quaternion[] bindLocal;
    Quaternion headRestRelRoot, lHandBind, rHandBind;
    Vector3 lLowerBind, lHandPosBind, rLowerBind, rHandPosBind;
    float bindArmLength;
    float hipsYaw; bool yawInit;
    float avatarEyeHeight;

    void Start()
    {
        foreach (var t in hideAtBind) if (t) t.localScale = Vector3.zero;
        driven = System.Array.FindAll(Concat(new[] { head, chest, leftUpper, leftLower, leftHand, rightUpper, rightLower, rightHand },
                                             spine, leftFingers, rightFingers), t => t != null);
        driven = new System.Collections.Generic.HashSet<Transform>(driven).ToArrayFast();
        bindLocal = new Quaternion[driven.Length];
        for (int i = 0; i < driven.Length; i++) bindLocal[i] = driven[i].localRotation;
        headRestRelRoot = Quaternion.Inverse(transform.rotation) * head.rotation;
        lHandBind = HandFrame(leftHand, leftFingers, true);
        rHandBind = HandFrame(rightHand, rightFingers, false);
        Vector3 eye = head.position - transform.rotation * hmdToHead;
        avatarEyeHeight = transform.InverseTransformPoint(eye).y;
        if (leftLower) { lLowerBind = leftLower.localPosition; lHandPosBind = leftHand.localPosition; }
        if (rightLower) { rLowerBind = rightLower.localPosition; rHandPosBind = rightHand.localPosition; }
        if (rightUpper) bindArmLength = (rightLower.position - rightUpper.position).magnitude + (rightHand.position - rightLower.position).magnitude;
    }

    static Transform[] Concat(params Transform[][] a)
    {
        var l = new System.Collections.Generic.List<Transform>();
        foreach (var x in a) if (x != null) l.AddRange(x);
        return l.ToArray();
    }

    Quaternion HandFrame(Transform hand, Transform[] fingers, bool isLeft)
    {
        if (hand == null) return Quaternion.identity;
        Vector3 f = handFingersAxis, p = handPalmAxis;
        if (!explicitHandAxes && fingers[6] && fingers[3] && fingers[12])
        {
            Vector3 fw = fingers[6].position - hand.position;
            Vector3 across = fingers[3].position - fingers[12].position;
            Vector3 pw = Vector3.Cross(fw, across) * (isLeft ? -1f : 1f);
            f = hand.InverseTransformDirection(fw);
            p = hand.InverseTransformDirection(pw);
        }
        return Quaternion.LookRotation(f, p);
    }

    /// <summary>Make the whole avatar taller or shorter (the head stays pinned to the headset).</summary>
    public void ScaleBy(float factor) => transform.localScale = Vector3.one * Mathf.Clamp(transform.localScale.x * factor, 0.6f, 1.6f);

    /// <summary>Lengthen or shorten both arms (upper arm and forearm together).</summary>
    public void ArmsBy(float delta) => SetArmScale(Mathf.Clamp(ArmScale + delta, 0.6f, 1.6f));

    void SetArmScale(float s)
    {
        ArmScale = s;
        if (leftLower) { leftLower.localPosition = lLowerBind * s; leftHand.localPosition = lHandPosBind * s; }
        if (rightLower) { rightLower.localPosition = rLowerBind * s; rightHand.localPosition = rHandPosBind * s; }
    }

    static float Yaw(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
    static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero; }

    void LateUpdate()
    {
        if (hmd == null || head == null) return;
        for (int i = 0; i < driven.Length; i++) driven[i].localRotation = bindLocal[i];
        bool tracked = body != null && body.Tracked;

        // 2. which way the body faces
        float headYaw = Yaw(Flat(hmd.forward) == Vector3.zero ? transform.forward : Flat(hmd.forward));
        float facing;
        float chestYaw = headYaw;
        if (tracked)
        {
            // shoulders give the chest's facing: forward = (right shoulder - left shoulder) x up
            Vector3 across = body.pos[MetaBodyFeature.RightArmUpper] - body.pos[MetaBodyFeature.LeftArmUpper];
            chestYaw = Yaw(Flat(Vector3.Cross(across, Vector3.up)));
            facing = chestYaw;
        }
        else
        {
            Vector3 f = Flat(hmd.forward);
            var l = hands ? hands.left : null; var r = hands ? hands.right : null;
            if (l != null && r != null && l.tracked && r.tracked)
            {
                Vector3 between = Flat(r.wrist - l.wrist);
                Vector3 handsFwd = Flat(Vector3.Cross(between, Vector3.up));
                // only trust the hands when they are spread and in front of the head
                if (handsFwd != Vector3.zero && (r.wrist - l.wrist).magnitude > 0.25f && Vector3.Dot(handsFwd, f) > 0f)
                    f = Flat(Vector3.Lerp(f, handsFwd, handsWeight));
            }
            facing = Yaw(f);
        }
        if (!yawInit) { hipsYaw = facing; yawInit = true; }
        float err = Mathf.Abs(Mathf.DeltaAngle(hipsYaw, facing));
        float rate = tracked ? fastDegPerSec : err <= deadZone ? 0f : err >= fastAfter ? fastDegPerSec : slowDegPerSec;
        hipsYaw = Mathf.MoveTowardsAngle(hipsYaw, facing, rate * Time.deltaTime);
        transform.rotation = Quaternion.Euler(0f, hipsYaw, 0f);

        // 3. torso twist: the chest (and the spine above the hips) turns part of the way toward the head
        float twist = tracked ? Mathf.DeltaAngle(hipsYaw, chestYaw) : Mathf.Clamp(Mathf.DeltaAngle(hipsYaw, headYaw) * chestTwist, -maxTwist, maxTwist);
        int n = spine.Length + (chest ? 1 : 0);
        if (n > 0)
        {
            var per = Quaternion.AngleAxis(twist / n, Vector3.up);
            foreach (var s in spine) if (s) s.rotation = per * s.rotation;
            if (chest && System.Array.IndexOf(spine, chest) < 0) chest.rotation = per * chest.rotation;
        }

        // 4. pin the head bone to the headset
        Vector3 headTarget = hmd.position + hmd.rotation * (hmdToHead * transform.localScale.x);
        transform.position += headTarget - head.position;

        // 5. head orientation
        head.rotation = hmd.rotation * headRestRelRoot;

        // 6-7. arms
        if (hands != null)
        {
            Arm(hands.left, leftUpper, leftLower, leftHand, lHandBind, leftFingers, -1f, MetaBodyFeature.LeftArmLower);
            Arm(hands.right, rightUpper, rightLower, rightHand, rHandBind, rightFingers, 1f, MetaBodyFeature.RightArmLower);
        }
    }

    void Arm(HandSource.Hand h, Transform upper, Transform lower, Transform hand, Quaternion handBind, Transform[] fingers, float side, int trackedElbow)
    {
        if (!h.tracked || upper == null || lower == null || hand == null) return;
        Vector3 pole = body != null && body.Valid(trackedElbow)
            ? body.pos[trackedElbow]
            : upper.position + transform.rotation * Vector3.Scale(elbowPole, new Vector3(side, 1f, 1f)) * transform.localScale.x;
        TwoBoneIK.Solve(upper, lower, hand, h.wrist, pole);
        hand.rotation = Quaternion.LookRotation(h.fingers, h.palm) * Quaternion.Inverse(handBind);
        if (h.hasJoints) Fingers(h, fingers);
    }

    static readonly XRHandJointID[][] Chains =
    {
        new[] { XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip },
        new[] { XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip },
        new[] { XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip },
        new[] { XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip },
        new[] { XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip },
    };

    void Fingers(HandSource.Hand h, Transform[] f)
    {
        for (int k = 0; k < 5; k++)
        {
            var ids = Chains[k];
            for (int s = 0; s < 3; s++)
            {
                Transform b = f[k * 3 + s];
                if (b == null) break;
                Vector3 from = s < 2 && f[k * 3 + s + 1] ? f[k * 3 + s + 1].position - b.position
                             : (s > 0 && f[k * 3 + s - 1] ? b.position - f[k * 3 + s - 1].position : Vector3.zero);
                Vector3 to = h.joints[ids[s + 1].ToIndex()] - h.joints[ids[s].ToIndex()];
                if (from.sqrMagnitude < 1e-10f || to.sqrMagnitude < 1e-10f) continue;
                b.rotation = Quaternion.FromToRotation(from, to) * b.rotation;
            }
        }
    }
}

static class HashSetExt
{
    public static T[] ToArrayFast<T>(this System.Collections.Generic.HashSet<T> s) { var a = new T[s.Count]; s.CopyTo(a); return a; }
}
