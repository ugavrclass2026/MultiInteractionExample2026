using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Draws the laser for one hand while it points at a UI panel: from the controller's aim pose when that hand holds
/// a controller, else from Meta's hand aim pose. (The clicks themselves come from XRUIInput.) While a hand points at
/// a panel, PinchGrabber leaves its pinches to the UI.
/// </summary>
public class UIRay : MonoBehaviour
{
    public HandSource hands;
    public bool isLeft;
    public Transform trackingSpace;
    public RectTransform[] panels;
    public LineRenderer line;
    public Transform dot;
    public float maxDistance = 4f;

    static readonly bool[] over = new bool[2];
    public static bool IsOverUI(bool left) => over[left ? 0 : 1];

    InputAction ctrlPos, ctrlRot, handPos, handRot;

    void OnEnable()
    {
        string side = isLeft ? "{LeftHand}" : "{RightHand}";
        ctrlPos = new InputAction(binding: $"<XRController>{side}/pointerPosition");
        ctrlRot = new InputAction(binding: $"<XRController>{side}/pointerRotation");
        handPos = new InputAction(binding: $"<MetaAimHand>{side}/devicePosition");
        handRot = new InputAction(binding: $"<MetaAimHand>{side}/deviceRotation");
        foreach (var a in new[] { ctrlPos, ctrlRot, handPos, handRot }) a.Enable();
    }

    void OnDisable() { foreach (var a in new[] { ctrlPos, ctrlRot, handPos, handRot }) a?.Disable(); }

    void LateUpdate()
    {
        int i = isLeft ? 0 : 1;
        over[i] = false;
        var h = hands.Get(isLeft);
        Vector3 p; Quaternion q;
        if (h.fromController) { p = ctrlPos.ReadValue<Vector3>(); q = ctrlRot.ReadValue<Quaternion>(); }
        else if (h.fromHandTracking) { p = handPos.ReadValue<Vector3>(); q = handRot.ReadValue<Quaternion>(); }
        else { Show(false); return; }
        if (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f) { Show(false); return; }
        if (trackingSpace) { p = trackingSpace.TransformPoint(p); q = trackingSpace.rotation * q; }
        var ray = new Ray(p, q * Vector3.forward);

        float best = maxDistance; bool hit = false;
        foreach (var panel in panels)
        {
            if (panel == null || !panel.gameObject.activeInHierarchy) continue;
            var plane = new Plane(panel.forward, panel.position);
            if (!plane.Raycast(ray, out float d) || d > best) continue;
            Vector3 local = panel.InverseTransformPoint(ray.GetPoint(d));
            if (!panel.rect.Contains(new Vector2(local.x, local.y))) continue;
            best = d; hit = true;
        }
        over[i] = hit;
        Show(hit);
        if (!hit) return;
        line.SetPosition(0, ray.origin);
        line.SetPosition(1, ray.GetPoint(best));
        dot.position = ray.GetPoint(best - 0.003f);
    }

    void Show(bool on)
    {
        if (line.enabled != on) line.enabled = on;
        if (dot.gameObject.activeSelf != on) dot.gameObject.SetActive(on);
    }
}
