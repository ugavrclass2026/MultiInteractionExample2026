using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The demo's control panel: which avatar is on, where the hands come from, whether the mirror is on,
/// and height calibration. The poke buttons call these; N/H/M/C do the same on a keyboard in the editor.
/// If Application.persistentDataPath/demo.txt exists, the hands move by themselves and the avatar
/// changes every few seconds (for recording a demo without wearing the headset).
/// </summary>
public class AvatarSwitcher : MonoBehaviour
{
    [System.Serializable]
    public class Entry { public string label; public GameObject root; public bool showControllerHands; public bool showJoints = true; }
    public Entry[] avatars;
    public int current;
    public HandSource hands;
    public BodySource body;
    public JointDots joints;
    public GameObject mirror;
    public Hand[] controllerHands;     // the grab system's hands: hidden when an avatar has its own hands
    public Transform hmd, floor;
    public TextMesh status;

    bool demo;
    float demoNext;

    void Start()
    {
        demo = File.Exists(Path.Combine(Application.persistentDataPath, "demo.txt"));
        if (demo) { hands.demo = DemoHands; demoNext = Time.time + 6f; Debug.Log("[AvatarDemo] demo mode"); }
        if (File.Exists(Path.Combine(Application.persistentDataPath, "body.txt")) && body) body.Set(true);   // test hook: start with body tracking on
        Apply();
    }

    public void NextAvatar() { current = (current + 1) % avatars.Length; Apply(); }

    public void ToggleHands()
    {
        hands.mode = hands.mode == HandSource.Mode.Auto ? HandSource.Mode.ControllersOnly : HandSource.Mode.Auto;
        Apply();
    }

    public void ToggleMirror() { mirror.SetActive(!mirror.activeSelf); Apply(); }

    public void ToggleBody() { if (body) body.Toggle(); UpdateStatus(); }

    HalfBodyAvatar Current => avatars[current].root.GetComponent<HalfBodyAvatar>();

    public void HeightUp() { foreach (var hb in All()) hb.ScaleBy(1.04f); UpdateStatus(); }
    public void HeightDown() { foreach (var hb in All()) hb.ScaleBy(1f / 1.04f); UpdateStatus(); }
    public void ArmsUp() { if (Current) Current.ArmsBy(0.05f); UpdateStatus(); }
    public void ArmsDown() { if (Current) Current.ArmsBy(-0.05f); UpdateStatus(); }

    System.Collections.Generic.IEnumerable<HalfBodyAvatar> All()
    {
        foreach (var a in avatars) foreach (var hb in a.root.GetComponentsInChildren<HalfBodyAvatar>(true)) yield return hb;
    }

    void Apply()
    {
        for (int i = 0; i < avatars.Length; i++) avatars[i].root.SetActive(i == current);
        var e = avatars[current];
        if (joints) joints.show = e.showJoints;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (!status) return;
        string Src(HandSource.Hand h) => h.fromHandTracking ? "hand" : h.fromController ? "controller" : "-";
        var mb = MetaBodyFeature.Instance;
        var cur = Current;
        status.text = $"Avatar: {avatars[current].label}\n" +
                      $"Hands: {HandSource.ModeLabel(hands.mode)} - L {Src(hands.left)}, R {Src(hands.right)}" +
                      (mb != null && mb.SimultaneousOn ? " (simultaneous)" : "") + "\n" +
                      $"Body tracking: {(body ? body.Status : "-")}\n" +
                      (cur ? $"Height x{cur.transform.localScale.x:F2}   Arms x{cur.ArmScale:F2}   " : "") + $"Mirror: {(mirror.activeSelf ? "on" : "off")}";
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.nKey.wasPressedThisFrame) NextAvatar();
            if (kb.hKey.wasPressedThisFrame) ToggleHands();
            if (kb.mKey.wasPressedThisFrame) ToggleMirror();
            if (kb.bKey.wasPressedThisFrame) ToggleBody();
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
