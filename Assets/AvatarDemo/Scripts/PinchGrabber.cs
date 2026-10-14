using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Grabbing and moving with tracked hands, the same way the controller Hand script does it with the grip:
///   - pinch (thumb tip to index tip) near a Grabbable: hold it; it follows the pinch with velocity, and is
///     thrown with the fastest recent velocity on release;
///   - pinch in empty air: pull yourself through the world (the pinch point stays fixed in the world).
/// Only active while that hand comes from hand tracking.
/// </summary>
public class PinchGrabber : MonoBehaviour
{
    public HandSource hands;
    public bool isLeft;
    public Transform rig;                     // XR Origin, moved by air pulls
    public Transform cursor;                  // a small sphere at the pinch point
    public float pinchOn = 0.02f, pinchOff = 0.04f, grabRadius = 0.06f;

    static PinchGrabber pulling;
    bool pinched;
    Vector3 pullAnchor;
    Grabbable selected, held;
    Vector3 holdPos; Quaternion holdRot;      // the object's pose in the pinch frame
    readonly List<(float t, Vector3 v, Vector3 w)> samples = new List<(float, Vector3, Vector3)>();
    Renderer cursorRenderer;

    void Start() { if (cursor) cursorRenderer = cursor.GetComponent<Renderer>(); }

    void Update()
    {
        var h = hands.Get(isLeft);
        bool active = h.fromHandTracking && h.hasJoints;
        if (cursor) cursor.gameObject.SetActive(active);
        if (!active) { if (pinched) EndPinch(); Select(null); return; }

        Vector3 thumb = h.joints[XRHandJointID.ThumbTip.ToIndex()], index = h.joints[XRHandJointID.IndexTip.ToIndex()];
        Vector3 point = (thumb + index) * 0.5f;
        float gap = Vector3.Distance(thumb, index);
        if (cursor) cursor.position = point;
        if (cursorRenderer) cursorRenderer.material.color = pinched ? new Color(0.71f, 0.40f, 0.11f) : new Color(0.01f, 0.54f, 0.65f);

        if (!pinched && held == null) Select(Closest(point));
        if (!pinched && gap < pinchOn)
        {
            pinched = true;
            if (selected != null) Grab(selected, point, Frame(h));
            else if (pulling == null) { pulling = this; pullAnchor = point; }
        }
        else if (pinched && gap > pinchOff) EndPinch();

        if (pulling == this) rig.position += pullAnchor - point;
        if (held != null) { lastPoint = point; lastFrame = Frame(h); }
    }

    Vector3 lastPoint; Quaternion lastFrame;
    static Quaternion Frame(HandSource.Hand h) => Quaternion.LookRotation(h.fingers, -h.palm);

    void FixedUpdate()
    {
        if (held == null) return;
        var body = held.Body;
        Vector3 targetPos = lastPoint + lastFrame * holdPos;
        Quaternion targetRot = lastFrame * holdRot;
        body.linearVelocity = (targetPos - body.position) / Time.fixedDeltaTime;
        (targetRot * Quaternion.Inverse(body.rotation)).normalized.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;
        body.angularVelocity = float.IsFinite(axis.x) ? axis * (angle * Mathf.Deg2Rad) / Time.fixedDeltaTime : Vector3.zero;
        samples.Add((Time.time, body.linearVelocity, body.angularVelocity));
        samples.RemoveAll(s => Time.time - s.t > 0.03f);
    }

    Grabbable Closest(Vector3 p)
    {
        Grabbable best = null; float bestD = float.MaxValue;
        foreach (var c in Physics.OverlapSphere(p, grabRadius))
        {
            var g = c.attachedRigidbody ? c.attachedRigidbody.GetComponent<Grabbable>() : null;
            if (g == null) continue;
            float d = Vector3.Distance(p, c.ClosestPoint(p));
            if (d < bestD) { bestD = d; best = g; }
        }
        return best;
    }

    void Select(Grabbable g)
    {
        if (g == selected) return;
        if (selected) selected.Unhighlight();
        selected = g;
        if (selected) selected.Highlight();
    }

    void Grab(Grabbable g, Vector3 point, Quaternion frame)
    {
        if (g.HeldBy != null) g.HeldBy.Release();     // take it from a controller hand
        Select(null);
        held = g;
        // remember the object's pose relative to the pinch, so it doesn't jump
        holdPos = Quaternion.Inverse(frame) * (g.transform.position - point);
        holdRot = Quaternion.Inverse(frame) * g.transform.rotation;
        lastPoint = point; lastFrame = frame;
        samples.Clear();
    }

    void EndPinch()
    {
        pinched = false;
        if (pulling == this) pulling = null;
        if (held != null)
        {
            Vector3 v = held.Body.linearVelocity, w = held.Body.angularVelocity;
            foreach (var s in samples) { if (s.v.magnitude > v.magnitude) v = s.v; if (s.w.magnitude > w.magnitude) w = s.w; }
            held.Body.linearVelocity = v; held.Body.angularVelocity = w;
            held = null;
        }
    }
}
