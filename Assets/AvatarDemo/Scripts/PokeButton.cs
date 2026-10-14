using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// A button you push with a fingertip or a controller. Anything carrying a Poker collider that enters the
/// trigger presses it; it re-arms once the poker has left. Works with hand tracking, where there are no
/// buttons to press.
/// </summary>
[RequireComponent(typeof(Collider))]
public class PokeButton : MonoBehaviour
{
    public UnityEvent onPress = new UnityEvent();
    public Transform cap;           // the visible part, pushed in while pressed
    int inside;
    float lastPress = -10f;

    public void Press()
    {
        if (Time.time - lastPress < 0.4f) return;
        lastPress = Time.time;
        onPress.Invoke();
    }

    void OnTriggerEnter(Collider other)
    {
        if (!other.GetComponentInParent<Poker>()) return;
        if (inside++ == 0) Press();
        if (cap) cap.localPosition = new Vector3(0f, 0f, 0.01f);
    }

    void OnTriggerExit(Collider other)
    {
        if (!other.GetComponentInParent<Poker>()) return;
        inside = Mathf.Max(0, inside - 1);
        if (inside == 0 && cap) cap.localPosition = Vector3.zero;
    }
}
