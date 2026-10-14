using UnityEngine;

/// <summary>
/// The lowest-fidelity avatar: a head that copies the headset and two hands that copy the tracked hands.
/// Nothing is guessed, so nothing can be guessed wrong. The head is on the MirrorOnly layer so you
/// see it in the mirror but not from inside it.
/// </summary>
public class AbstractAvatar : MonoBehaviour
{
    public Transform hmd, head, leftHand, rightHand;
    public HandSource hands;

    void LateUpdate()
    {
        if (hmd) head.SetPositionAndRotation(hmd.position, hmd.rotation);
        Place(leftHand, hands ? hands.left : null);
        Place(rightHand, hands ? hands.right : null);
    }

    static void Place(Transform t, HandSource.Hand h)
    {
        if (t == null) return;
        t.gameObject.SetActive(h != null && h.tracked);
        if (h == null || !h.tracked) return;
        // a mitten: its +Z along the fingers, its -Y out of the palm, centered a little past the wrist
        t.SetPositionAndRotation(h.wrist + h.fingers * 0.06f, Quaternion.LookRotation(h.fingers, -h.palm));
    }
}
