using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// A small kinematic trigger sphere that presses PokeButtons. One follows each index fingertip when hand
/// tracking is on, and each controller's grab point otherwise.
/// </summary>
[RequireComponent(typeof(SphereCollider), typeof(Rigidbody))]
public class Poker : MonoBehaviour
{
    public HandSource hands;
    public bool isLeft;
    public Transform controllerTip;

    void Awake()
    {
        var rb = GetComponent<Rigidbody>(); rb.isKinematic = true; rb.useGravity = false;
        var c = GetComponent<SphereCollider>(); c.isTrigger = true; c.radius = 0.012f;
    }

    void Update()
    {
        var h = hands ? hands.Get(isLeft) : null;
        if (h != null && h.hasJoints)
            transform.position = h.joints[UnityEngine.XR.Hands.XRHandJointID.IndexTip.ToIndex()];
        else if (controllerTip)
            transform.position = controllerTip.position;
    }
}
