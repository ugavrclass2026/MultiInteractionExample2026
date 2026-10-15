using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The demo's control panel (a world-space Unity UI canvas; point with a controller and pull the trigger, or point
/// with a hand and pinch): which avatar is on, the tracking options, the mirror, and the avatar's height and arms.
///
/// Every tracking option Meta offers is a toggle, and a toggle that would conflict with what is on is greyed out
/// with the reason, so an invalid combination can't be started. Meta's rules (Quest 3):
///   - hands + controllers at once (multimodal) can't run with body tracking, Wide Motion Mode or Fast Motion Mode;
///   - multimodal, Wide Motion Mode and Fast Motion Mode need hand tracking on.
/// N / M and the arrow keys do the same on a keyboard in the editor. If Application.persistentDataPath/demo.txt
/// exists, the hands move by themselves and the avatar changes every few seconds (for recording without a headset).
/// </summary>
public class AvatarSwitcher : MonoBehaviour
{
    [System.Serializable]
    public class Entry { public string label; public GameObject root; public bool showControllerHands; public bool showJoints = true; }
    public Entry[] avatars;
    public int current;
    public HandSource hands;
    public BodySource body;
    public FaceSource face;
    public JointDots joints;
    public GameObject mirror;
    public Hand[] controllerHands;     // the grab system's hands: hidden when an avatar has its own hands
    public Transform hmd, floor;

    public enum Opt { HandTracking, Multimodal, Body, WideMotion, FastMotion, Face, Mirror }
    public static readonly string[] OptLabels =
    {
        "Hand tracking", "Hands + controllers at once", "Body tracking", "Wide motion mode", "Fast motion mode", "Face tracking", "Mirror",
    };
    public static readonly string[] OptNotes =
    {
        "off: controllers only", "multimodal; put-down controllers stay tracked", "upper body from the headset cameras",
        "hands out of view estimated from the body", "60 Hz hand tracking, for fast motion", "audio-driven on Quest 3", "",
    };
    readonly bool[] on = { true, true, false, false, false, true, true };

    [Header("Panel (filled in by AvatarDemoBuilder)")]
    public Toggle[] toggles = new Toggle[7];
    public Text[] notes = new Text[7];
    public Text status, avatarName, heightText, armsText;

    bool demo;
    float demoNext;

    void Start()
    {
        demo = File.Exists(Path.Combine(Application.persistentDataPath, "demo.txt"));
        if (demo) { hands.demo = DemoHands; demoNext = Time.time + 6f; Debug.Log("[AvatarDemo] demo mode"); }
        if (File.Exists(Path.Combine(Application.persistentDataPath, "body.txt"))) { Set(Opt.Multimodal, false); Set(Opt.Body, true); }   // test hook
        ApplyAvatar();
        RefreshPanel();
    }

    // ---- tracking options ----

    public bool IsOn(Opt o) => on[(int)o];

    /// <summary>Why this option can't be turned on now (null if it can).</summary>
    public string BlockedOn(Opt o)
    {
        bool ht = on[(int)Opt.HandTracking], mm = on[(int)Opt.Multimodal];
        switch (o)
        {
            case Opt.Multimodal:
                if (!ht) return "needs hand tracking";
                if (on[(int)Opt.Body]) return "turn off body tracking first";
                if (on[(int)Opt.WideMotion]) return "turn off wide motion mode first";
                if (on[(int)Opt.FastMotion]) return "turn off fast motion mode first";
                return null;
            case Opt.Body: return mm ? "turn off hands + controllers at once first" : null;
            case Opt.WideMotion:
            case Opt.FastMotion:
                if (!ht) return "needs hand tracking";
                return mm ? "turn off hands + controllers at once first" : null;
            default: return null;
        }
    }

    /// <summary>Why this option can't be turned off now (null if it can).</summary>
    public string BlockedOff(Opt o)
    {
        if (o != Opt.HandTracking) return null;
        if (on[(int)Opt.Multimodal]) return "turn off hands + controllers at once first";
        if (on[(int)Opt.WideMotion]) return "turn off wide motion mode first";
        if (on[(int)Opt.FastMotion]) return "turn off fast motion mode first";
        return null;
    }

    public void Set(Opt o, bool value)
    {
        if (value == on[(int)o]) { RefreshPanel(); return; }
        string why = value ? BlockedOn(o) : BlockedOff(o);
        if (why != null) { Debug.Log($"[AvatarDemo] {OptLabels[(int)o]} not changed: {why}"); RefreshPanel(); return; }
        on[(int)o] = value;
        var f = MetaBodyFeature.Instance;
        switch (o)
        {
            case Opt.HandTracking: hands.mode = value ? HandSource.Mode.Auto : HandSource.Mode.ControllersOnly; break;
            case Opt.Multimodal: if (f != null) f.SetSimultaneous(value); break;
            case Opt.Body: if (body) body.Set(value); break;
            case Opt.WideMotion:
                if (f != null) f.SetWideMotion(value);
                if (value && !MetaBodyFeature.WideMotionOn) { on[(int)o] = false; Debug.Log("[AvatarDemo] wide motion mode: the runtime refused the trackers"); }
                break;
            case Opt.FastMotion: if (f != null) f.SetFastMotion(value); break;
            case Opt.Face: if (face) face.on = value; break;
            case Opt.Mirror: mirror.SetActive(value); break;
        }
        Debug.Log($"[AvatarDemo] {OptLabels[(int)o]}: {(value ? "on" : "off")}");
        RefreshPanel();
    }

    // UI events (Toggle.onValueChanged)
    public void SetHandTracking(bool v) => Set(Opt.HandTracking, v);
    public void SetMultimodal(bool v) => Set(Opt.Multimodal, v);
    public void SetBody(bool v) => Set(Opt.Body, v);
    public void SetWideMotion(bool v) => Set(Opt.WideMotion, v);
    public void SetFastMotion(bool v) => Set(Opt.FastMotion, v);
    public void SetFace(bool v) => Set(Opt.Face, v);
    public void SetMirror(bool v) => Set(Opt.Mirror, v);

    // ---- avatar ----

    public void NextAvatar() { current = (current + 1) % avatars.Length; ApplyAvatar(); }
    public void PreviousAvatar() { current = (current + avatars.Length - 1) % avatars.Length; ApplyAvatar(); }

    HalfBodyAvatar Current => avatars[current].root.GetComponent<HalfBodyAvatar>();

    public void HeightUp() { foreach (var hb in All()) hb.ScaleBy(1.04f); RefreshPanel(); }
    public void HeightDown() { foreach (var hb in All()) hb.ScaleBy(1f / 1.04f); RefreshPanel(); }
    public void ArmsUp() { if (Current) Current.ArmsBy(0.05f); RefreshPanel(); }
    public void ArmsDown() { if (Current) Current.ArmsBy(-0.05f); RefreshPanel(); }

    System.Collections.Generic.IEnumerable<HalfBodyAvatar> All()
    {
        foreach (var a in avatars) foreach (var hb in a.root.GetComponentsInChildren<HalfBodyAvatar>(true)) yield return hb;
    }

    void ApplyAvatar()
    {
        for (int i = 0; i < avatars.Length; i++) avatars[i].root.SetActive(i == current);
        if (joints) joints.show = avatars[current].showJoints;
        RefreshPanel();
    }

    // ---- panel ----

    void RefreshPanel()
    {
        for (int i = 0; i < toggles.Length; i++)
        {
            var o = (Opt)i;
            string why = on[i] ? BlockedOff(o) : BlockedOn(o);
            if (toggles[i])
            {
                toggles[i].SetIsOnWithoutNotify(on[i]);
                toggles[i].interactable = why == null;
            }
            if (notes[i]) { notes[i].text = why ?? OptNotes[i]; notes[i].color = why != null ? new Color(0.95f, 0.65f, 0.3f) : new Color(0.7f, 0.74f, 0.78f); }
        }
        if (avatarName) avatarName.text = avatars[current].label;
        var cur = Current;
        if (heightText) heightText.text = cur ? $"Height x{cur.transform.localScale.x:F2}" : "Height";
        if (armsText) armsText.text = cur ? $"Arms x{cur.ArmScale:F2}" : "Arms";
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (!status) return;
        string Src(HandSource.Hand h) => h.fromHandTracking ? "hand" : h.fromController ? "controller" : h.fromBody ? "body" : "-";
        var f = MetaBodyFeature.Instance;
        status.text = $"Left: {Src(hands.left)}   Right: {Src(hands.right)}" +
                      (f != null && f.SimultaneousOn ? "   (multimodal)" : "") +
                      (on[(int)Opt.WideMotion] ? $"\nWide motion: {(MetaBodyFeature.WideMotionOn ? $"L {MetaBodyFeature.SourceName(1)}, R {MetaBodyFeature.SourceName(2)}" : "trackers not created")}" : "") + "\n" +
                      (on[(int)Opt.Body] && body ? $"Body: {body.Status}\n" : "") +
                      (face ? $"Face: {face.Status}" : "");
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.nKey.wasPressedThisFrame) NextAvatar();
            if (kb.mKey.wasPressedThisFrame) Set(Opt.Mirror, !on[(int)Opt.Mirror]);
            if (kb.upArrowKey.wasPressedThisFrame) HeightUp();
            if (kb.downArrowKey.wasPressedThisFrame) HeightDown();
            if (kb.rightArrowKey.wasPressedThisFrame) ArmsUp();
            if (kb.leftArrowKey.wasPressedThisFrame) ArmsDown();
        }
        if (demo && Time.time > demoNext) { demoNext = Time.time + 6f; NextAvatar(); }
        if (Time.frameCount % 15 == 0) UpdateStatus();
    }

    void LateUpdate()
    {
        bool show = avatars[current].showControllerHands;
        foreach (var h in controllerHands) if (h && h.graphics && h.graphics.activeSelf != show) h.graphics.SetActive(show);
    }

    // Demo: both hands trace slow loops in front of the chest.
    (Vector3, Vector3, Vector3) DemoHands(HandSource.Hand h, float t)
    {
        float s = h.isLeft ? -1f : 1f;
        Vector3 fwd = Vector3.ProjectOnPlane(hmd.forward, Vector3.up).normalized, right = Vector3.Cross(Vector3.up, fwd);
        Vector3 c = hmd.position + fwd * 0.38f + Vector3.down * 0.35f + right * (0.22f * s);
        Vector3 p = c + right * (0.12f * Mathf.Sin(t * 1.3f + s)) + Vector3.up * (0.18f * Mathf.Sin(t * 0.9f + (h.isLeft ? 0f : 2f)));
        Vector3 fingers = (fwd + Vector3.up * 0.3f * Mathf.Sin(t)).normalized;
        return (p, fingers, h.isLeft ? right : -right);
    }
}
