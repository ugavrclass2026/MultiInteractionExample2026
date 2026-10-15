using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

/// <summary>
/// Normal Unity UI in VR: the Input System's UI module treats each tracked device as a pointer (a ray from its
/// pose) and a button as its click. Canvases need a TrackedDeviceRaycaster. Pointers:
///   - controllers: the aim (pointer) pose, click = trigger;
///   - tracked hands: Meta's hand aim pose (XR Hands, MetaAimHand), click = index pinch.
/// Poses are in tracking space, so the module is told where the XR Origin is.
/// Put this next to an EventSystem whose InputSystemUIInputModule starts disabled; this enables it once the
/// actions are assigned.
/// </summary>
[DefaultExecutionOrder(-100)]
public class XRUIInput : MonoBehaviour
{
    public Transform trackingOrigin;

    void Awake()
    {
        var asset = ScriptableObject.CreateInstance<InputActionAsset>();
        var map = asset.AddActionMap("XR UI");
        var pos = map.AddAction("Position", InputActionType.PassThrough, expectedControlLayout: "Vector3");
        pos.AddBinding("<XRController>/pointerPosition"); pos.AddBinding("<MetaAimHand>/devicePosition");
        var rot = map.AddAction("Orientation", InputActionType.PassThrough, expectedControlLayout: "Quaternion");
        rot.AddBinding("<XRController>/pointerRotation"); rot.AddBinding("<MetaAimHand>/deviceRotation");
        var click = map.AddAction("Click", InputActionType.PassThrough, expectedControlLayout: "Button");
        click.AddBinding("<XRController>/triggerPressed"); click.AddBinding("<MetaAimHand>/indexPressed");

        var module = GetComponent<InputSystemUIInputModule>();
        module.actionsAsset = asset;
        module.trackedDevicePosition = InputActionReference.Create(pos);
        module.trackedDeviceOrientation = InputActionReference.Create(rot);
        module.leftClick = InputActionReference.Create(click);
        module.xrTrackingOrigin = trackingOrigin;
        asset.Enable();
        module.enabled = true;
    }
}
