using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.Scripting;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
#endif
#if USE_INPUT_SYSTEM_POSE_CONTROL
using PoseControl = UnityEngine.InputSystem.XR.PoseControl;
#else
using PoseControl = UnityEngine.XR.OpenXR.Input.PoseControl;
#endif

/// <summary>
/// XR_META_detached_controllers: with simultaneous hands and controllers on, a controller you put down
/// leaves /user/hand/left (which becomes your tracked hand) and is reported at
/// /user/detached_controller_meta/left instead. This feature binds the grip pose at those two paths,
/// so a controller lying on a table still has a pose. Two Input System devices appear:
/// "Detached Touch Controller Left" and "... Right" (layout DetachedTouchController).
/// </summary>
#if UNITY_EDITOR
[UnityEditor.XR.OpenXR.Features.OpenXRFeature(UiName = "AvatarDemo: Meta detached controllers",
    BuildTargetGroups = new[] { BuildTargetGroup.Android },
    Company = "UGA CSCI 4830",
    Desc = "Grip pose of controllers that are not in a hand (XR_META_detached_controllers).",
    OpenxrExtensionStrings = "XR_META_detached_controllers XR_META_touch_controller_plus",
    Version = "0.1.0",
    Category = UnityEditor.XR.OpenXR.Features.FeatureCategory.Interaction,
    FeatureId = "edu.uga.csci4830.avatardemo.detached")]
#endif
public class DetachedControllersFeature : OpenXRInteractionFeature
{
    // Quest 3 reports put-down controllers as Touch Plus (seen with xrGetCurrentInteractionProfile), and Unity
    // only creates the device when the reported profile matches this one
    const string Profile = "/interaction_profiles/meta/touch_controller_plus";
    const string Product = "Detached Touch Controller";

    /// <summary>One pose (the grip). The state is a single pose: isTracked, trackingState, position, rotation, ...</summary>
    [Preserve, InputControlLayout(displayName = "Detached Touch Controller (OpenXR)")]
    public class DetachedTouchController : UnityEngine.InputSystem.TrackedDevice
    {
        [Preserve, InputControl(offset = 0, aliases = new[] { "device", "gripPose" }, usage = "Device")]
        public PoseControl devicePose { get; private set; }
        [Preserve, InputControl(offset = 0, usage = "IsTracked")]
        public new ButtonControl isTracked { get; private set; }
        [Preserve, InputControl(offset = 4, usage = "TrackingState")]
        public new IntegerControl trackingState { get; private set; }
        [Preserve, InputControl(offset = 8, noisy = true)]
        public new Vector3Control devicePosition { get; private set; }
        [Preserve, InputControl(offset = 20, noisy = true)]
        public new QuaternionControl deviceRotation { get; private set; }

        public bool IsLeft => description.product != null && description.product.EndsWith("Left");

        protected override void FinishSetup()
        {
            base.FinishSetup();
            devicePose = GetChildControl<PoseControl>("devicePose");
            isTracked = GetChildControl<ButtonControl>("isTracked");
            trackingState = GetChildControl<IntegerControl>("trackingState");
            devicePosition = GetChildControl<Vector3Control>("devicePosition");
            deviceRotation = GetChildControl<QuaternionControl>("deviceRotation");
        }
    }

    protected override void RegisterDeviceLayout()
    {
        UnityEngine.InputSystem.InputSystem.RegisterLayout(typeof(DetachedTouchController),
            matches: new InputDeviceMatcher()
                .WithInterface(UnityEngine.InputSystem.XR.XRUtilities.InterfaceMatchAnyVersion)
                .WithProduct(Product + ".*"));
    }

    protected override void UnregisterDeviceLayout() => UnityEngine.InputSystem.InputSystem.RemoveLayout(nameof(DetachedTouchController));

    protected override string GetDeviceLayoutName() => nameof(DetachedTouchController);

    // One action map per side, so each side is its own device with its own product name.
    protected override void RegisterActionMapsWithRuntime()
    {
        foreach (var left in new[] { true, false })
        {
            string side = left ? "Left" : "Right";
            AddActionMap(new ActionMapConfig
            {
                name = "detachedcontroller" + side.ToLower(),
                localizedName = Product + " " + side,
                desiredInteractionProfile = Profile,
                manufacturer = "Oculus",
                serialNumber = "",
                deviceInfos = new List<DeviceConfig>
                {
                    new DeviceConfig
                    {
                        characteristics = InputDeviceCharacteristics.TrackedDevice | InputDeviceCharacteristics.Controller |
                                          (left ? InputDeviceCharacteristics.Left : InputDeviceCharacteristics.Right),
                        userPath = "/user/detached_controller_meta/" + side.ToLower(),
                    }
                },
                actions = new List<ActionConfig>
                {
                    new ActionConfig
                    {
                        name = "devicePose",
                        localizedName = "Device Pose",
                        type = ActionType.Pose,
                        usages = new List<string> { "Device" },
                        bindings = new List<ActionBinding> { new ActionBinding { interactionPath = "/input/grip/pose", interactionProfileName = Profile } }
                    }
                }
            });
        }
    }

    /// <summary>The detached controller on one side, if the runtime has created it.</summary>
    public static DetachedTouchController Find(bool left)
    {
        foreach (var d in UnityEngine.InputSystem.InputSystem.devices)
            if (d is DetachedTouchController c && c.IsLeft == left) return c;
        return null;
    }
}
