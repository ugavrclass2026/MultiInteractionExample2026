# MultiInteractionExample2026

A from-scratch VR grab system for Meta Quest 3 (Unity 6000.3, URP, OpenXR). No XR Interaction Toolkit:
two small runtime scripts plus Unity's built-in `TrackedPoseDriver`.

## Getting started

1. Open the project in **Unity 6000.3.22f1** (Android build support installed). It is already configured: XR Plug-in
   Management with the OpenXR loader, the Meta Quest Support feature and Touch controller profiles, Android/ARM64/IL2CPP
   player settings, Vulkan, and physics at 90 Hz. Packages: `com.unity.xr.openxr` 1.16.1, `com.unity.xr.management` 4.5.4.
2. Open `Assets/Scenes/InteractionDemo.unity`.
3. With a Quest 3 in developer mode connected over USB: **File ▸ Build Profiles ▸ Android ▸ Build And Run**.

Controls: squeeze the **grip** near an object to grab it, let go to release or throw; squeeze the grip on empty air and
pull to move yourself through the world.

## How the interaction system works

Input uses the Input System with actions bound *inline* on the components (no Input Action asset to maintain):

### Tracking: `TrackedPoseDriver` (Unity, `UnityEngine.InputSystem.XR`)
On the camera and both hands. Position/rotation/tracking-state actions are bound to `<XRHMD>/devicePosition` etc. for
the head and `<XRController>{LeftHand}/devicePosition` etc. for the hands. The driver enables its own inline actions and
updates the transform both in Update and right before rendering.

### `Hand`
```
Left Hand          <- TrackedPoseDriver + Hand
  Graphics         <- the visible part, a separate child so it can be swapped or hidden
  Grab Point       <- held objects lock to this transform (at the controller's grip pose, i.e. the palm)
```
`Hand` reads the grip through one inline action (`gripAction`, bound to `<XRController>{LeftHand}/gripPressed`), which it
enables in `OnEnable`. Every frame, while not holding anything:
1. **Select** — `Physics.OverlapSphere(grabPoint, grabRadius)` finds colliders; each one's `attachedRigidbody` must carry a
   `Grabbable`. The one whose surface (`Collider.ClosestPoint`) is closest to the grab point becomes `Selected` and is highlighted.
2. **Grab** — on the frame the grip is pressed, the selection is grabbed. A `Hold Target` child of the grab point (created in
   `Awake`) records where the object should sit: it is parented to an anchor, given the object's current world pose, then
   re-parented under the grab point keeping that local pose. The anchor depends on the grabbable's `GrabMode`:
   - `SnapObjectToHand`: the object's `attachPoint` (how it wants to be held) — the attach point effectively becomes the
     grab point, so the object jumps into the hand. Attach points must have world scale 1 (the wand keeps its scale on a
     child); objects without an attach point use their pivot and are unaffected by scale.
   - `SnapHandToObject`: the grab point itself — the object doesn't move.
   Independently, a grabbable's `hideHand` flag hides the hand graphics while it is held (the Book and Crate use it).
   If another hand is holding the object, it releases first, so objects can be passed hand to hand.
3. **Hold** (`FixedUpdate`) — set `linearVelocity` and `angularVelocity` to exactly the values that would carry the object to
   the `Hold Target`'s position and rotation in one physics step.
   The object stays a normal rigidbody, so it can be blocked by tables and walls and can knock other things over.
   (Project Settings ▸ Physics ▸ Default Max Angular Speed is raised to 500 rad/s so a fast wrist flick is not clamped;
   Unity's default is 50.)
4. **Release** — when the grip is let go. Every physics step while holding, the object's velocities were recorded, each
   sample blended with the previous one (`smoothing`) and kept for `releaseWindow` seconds (30 ms). The throw uses the
   fastest linear and angular velocity in that window, because a hand slows down just before it lets go.
5. **Air grab** (locomotion) — a grip press with nothing selected instead "grabs the air": the hand's world position is
   stored as `pullAnchor`, and every frame the `rig` (XR Origin) is moved by `pullAnchor - hand.position`, so the grabbed
   point stays fixed under your hand and you pull yourself through the world. Released with the grip. A static `pulling`
   is shared by both hands so only one pulls at a time.

### `Grabbable`
Marks a rigidbody as grabbable: `grabMode`, an optional `attachPoint`, and highlighting (tints the renderer's material;
a counter lets two hands highlight the same object at once). Objects need a Rigidbody and at least one Collider.

## Demo scene
`XR Origin` (camera + hands) at the floor origin — OpenXR uses the floor (stage) tracking origin by default, so the camera
sits at real head height. A table 0.55 m in front holds a **Cube**, **Ball** and **Wand** (snap to hand; the wand's attach
point is on its handle so it snaps in pointing forward) and a **Book** (hand snaps to object). A **Crate** on the floor also
uses hand-snaps-to-object. Physics runs at 90 Hz to match the headset.

## Avatar demo (`Assets/AvatarDemo/AvatarDemo.unity`)

Half-body avatars, a stereo mirror, and hand tracking on Quest 3. The scene is generated from `InteractionDemo`:
**Avatar Demo ▸ Build Scene** (`Assets/AvatarDemo/Editor/AvatarDemoBuilder.cs`). Batch build:
`Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod AvatarDemoBuilder.BuildApk` →
`Builds/AvatarDemo.apk` (app id `com.ugavr.avatardemo`).

- **Avatars:** head + hands; a robot with IK arms; a Rocketbox human (MIT) with IK arms and tracked fingers
- **Input:** hand tracking (XR Hands, 26 joints) and controllers at the same time (`XR_META_simultaneous_hands_and_controllers`)
- **Pinch:** grab and throw near an object; pinch in the air to pull yourself
- **Torso:** turns with the head and hands; chest twists toward the head
- **Body tracking (optional):** Meta inside-out body tracking (`XR_FB_body_tracking`, high fidelity); tracked shoulders turn the torso, tracked elbows are the IK poles. Pauses simultaneous hands/controllers while on.
- **Height and arm length:** buttons scale the avatar and lengthen or shorten the arms
- **Mirror:** one camera per eye at the eye's reflection, off-axis frustum through the mirror (`StereoMirror`)
- **First person:** full avatar on layer `MirrorOnly`, headless copy on `FirstPersonOnly` (`FirstPersonHead`)
- **Buttons** (poke with a fingertip or controller): next avatar, hands/controllers, body tracking, mirror, height −/+, arms −/+
- **Hand source:** hand tracking unless that hand holds a moving controller; a controller left still is ignored once the hand is tracked
- **Panel status:** each hand's source, simultaneous mode, body-tracking diagnostics (OpenXR result, active, confidence, valid joints)

Scripts: `HalfBodyAvatar` (per-frame loop), `TwoBoneIK`, `HandSource`, `BodySource`, `MetaBodyFeature` (OpenXR feature calling
Meta's extensions directly), `PinchGrabber`, `StereoMirror`, `FirstPersonHead`, `LimbVisual`, `AbstractAvatar`, `JointDots`,
`PokeButton`, `AvatarSwitcher`. Project changes: `com.unity.xr.hands`, OpenXR Hand Tracking + Meta Hand Tracking Aim +
the AvatarDemo feature (Android), layers `MirrorSurface`, `MirrorOnly`, `FirstPersonOnly`.
