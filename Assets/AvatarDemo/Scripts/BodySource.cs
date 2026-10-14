using UnityEngine;

/// <summary>
/// Optional upper-body tracking (Meta's inside-out body tracking, through MetaBodyFeature). When on, the
/// avatars use the tracked chest to turn the torso and the tracked elbows as IK poles. Joint positions are
/// given in world space.
/// </summary>
[DefaultExecutionOrder(-60)]
public class BodySource : MonoBehaviour
{
    public Transform trackingSpace;
    public bool Requested { get; private set; }
    public bool Tracked { get; private set; }
    public readonly Vector3[] pos = new Vector3[MetaBodyFeature.JointCount];
    public readonly Quaternion[] rot = new Quaternion[MetaBodyFeature.JointCount];
    readonly bool[] valid = new bool[MetaBodyFeature.JointCount];
    const string Permission = "com.oculus.permission.BODY_TRACKING";

    public string Status
    {
        get
        {
            var f = MetaBodyFeature.Instance;
            if (!Requested) return "off";
            if (f == null) return "unavailable";
            return Tracked ? "tracking" : f.Status;
        }
    }

    public void Toggle() => Set(!Requested);

    public void Set(bool on)
    {
        Requested = on;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (on && !UnityEngine.Android.Permission.HasUserAuthorizedPermission(Permission))
            UnityEngine.Android.Permission.RequestUserPermission(Permission);
#endif
        var f = MetaBodyFeature.Instance;
        if (f != null && !on) f.SetBodyTracking(false);
    }

    void Update()
    {
        Tracked = false;
        var f = MetaBodyFeature.Instance;
        if (!Requested || f == null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(Permission)) return;
#endif
        if (!f.BodyTrackingOn && !f.SetBodyTracking(true)) return;
        if (!f.TryLocate(pos, rot, valid)) return;
        if (!valid[MetaBodyFeature.Chest] || !valid[MetaBodyFeature.LeftArmUpper] || !valid[MetaBodyFeature.RightArmUpper]) return;
        for (int i = 0; i < pos.Length; i++)
        {
            pos[i] = trackingSpace ? trackingSpace.TransformPoint(pos[i]) : pos[i];
            rot[i] = trackingSpace ? trackingSpace.rotation * rot[i] : rot[i];
        }
        Tracked = true;
    }

    public bool Valid(int joint) => Tracked && valid[joint];
}
