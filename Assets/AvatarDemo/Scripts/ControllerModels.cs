using UnityEngine;

/// <summary>
/// Shows a Quest Touch Plus model wherever each controller is: in your hand (the normal controller
/// pose) or put down (the detached-controller pose, see DetachedControllersFeature). Hidden when the
/// headset doesn't know where that controller is.
/// Models: WebXR input profiles "meta-quest-touch-plus" (MIT), authored in the grip frame.
/// </summary>
[DefaultExecutionOrder(-40)]
public class ControllerModels : MonoBehaviour
{
    public HandSource hands;                      // has the held controllers' transforms and tracked state
    public Transform trackingSpace;
    public Transform leftModel, rightModel;       // each model's root; posed at the grip every frame
    [Tooltip("Model rotation in the grip frame (degrees). The glTF is authored in the grip frame; Blender's FBX export\n" +
             "tilts it 90 degrees and Unity mirrors x, so (-90, 180, 0) maps it to (-x, z, y). Checked on a Quest 3 with\n" +
             "logFit. For testing, a file controller.txt in Application.persistentDataPath holding \"x y z\" overrides it.")]
    public Vector3 modelEuler = new Vector3(-90f, 180f, 0f);
    [Tooltip("Log, while a controller is held, the axis-aligned rotation that best puts the model's thumbstick and trigger under the tracked thumb and index tips")]
    public bool logFit;
    float nextCheck;

    public string Status { get; private set; } = "";

    void LateUpdate()
    {
        if (Time.time > nextCheck) { nextCheck = Time.time + 1f; ReadOverride(); if (logFit) { Measure(true); Measure(false); } }
        string l = Place(leftModel, true), r = Place(rightModel, false);
        Status = $"L {l}, R {r}";
    }

    string Place(Transform model, bool left)
    {
        if (model == null) return "-";
        Transform held = left ? hands.leftController : hands.rightController;
        Vector3 pos; Quaternion rot; string where;
        if (hands.ControllerTracked(left)) { pos = held.position; rot = held.rotation; where = "in hand"; }
        else
        {
            var d = DetachedControllersFeature.Find(left);
            if (d == null || !d.isTracked.isPressed) { model.gameObject.SetActive(false); return d == null ? "no device" : "not tracked"; }
            pos = d.devicePosition.ReadValue(); rot = d.deviceRotation.ReadValue();
            if (trackingSpace) { pos = trackingSpace.TransformPoint(pos); rot = trackingSpace.rotation * rot; }
            where = "put down";
        }
        model.gameObject.SetActive(true);
        model.SetPositionAndRotation(pos, rot * Quaternion.Euler(modelEuler));
        return where;
    }

    string lastOverride;
    void ReadOverride()
    {
        var path = System.IO.Path.Combine(Application.persistentDataPath, "controller.txt");
        if (!System.IO.File.Exists(path)) return;
        string t = System.IO.File.ReadAllText(path).Trim();
        if (t == lastOverride) return;
        lastOverride = t;
        var f = t.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
        if (f.Length == 3 && float.TryParse(f[0], out var x) && float.TryParse(f[1], out var y) && float.TryParse(f[2], out var z))
        {
            modelEuler = new Vector3(x, y, z);
            Debug.Log($"[AvatarDemo] controller model rotation {modelEuler}");
        }
    }

    // Calibration check: while a controller is held, Quest still reports that hand's joints. The thumb rests
    // on the thumbstick and the index finger on the trigger, so find the axis-aligned model rotation that puts
    // the model's thumbstick and trigger nearest those fingertips (all in the grip frame), and log it.
    void Measure(bool left)
    {
        var model = left ? leftModel : rightModel;
        var sub = hands.Subsystem;
        if (model == null || sub == null || !hands.ControllerTracked(left)) return;
        var xh = left ? sub.leftHand : sub.rightHand;
        if (!xh.isTracked) return;
        if (!xh.GetJoint(UnityEngine.XR.Hands.XRHandJointID.ThumbTip).TryGetPose(out var thumb) ||
            !xh.GetJoint(UnityEngine.XR.Hands.XRHandJointID.IndexTip).TryGetPose(out var index)) return;
        Transform grip = left ? hands.leftController : hands.rightController;
        Vector3 G(Vector3 trackingPos)
        {
            Vector3 w = trackingSpace ? trackingSpace.TransformPoint(trackingPos) : trackingPos;
            return Quaternion.Inverse(grip.rotation) * (w - grip.position);
        }
        Vector3 t = G(thumb.position), ix = G(index.position);
        var stick = Find(model, "thumbstick"); var trig = Find(model, "trigger");
        if (stick == null || trig == null) return;
        // the parts in the model root's own frame (no modelEuler)
        Vector3 M(Transform part) => Quaternion.Inverse(model.rotation) * (part.GetComponent<Renderer>() ? part.GetComponent<Renderer>().bounds.center - model.position : part.position - model.position);
        Vector3 ms = M(stick), mt = M(trig);
        float best = float.MaxValue; Vector3 bestE = Vector3.zero;
        for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++) for (int z = 0; z < 4; z++)
        {
            var e = new Vector3(x * 90, y * 90, z * 90); var q = Quaternion.Euler(e);
            float err = (q * ms - t).sqrMagnitude + (q * mt - ix).sqrMagnitude;
            if (err < best) { best = err; bestE = e; }
        }
        var cur = Quaternion.Euler(modelEuler);
        Debug.Log($"[AvatarDemo] grip-frame {(left ? "L" : "R")}: thumb {t:F3} index {ix:F3} | model now stick {cur * ms:F3} trigger {cur * mt:F3} | best euler {bestE} (rms {Mathf.Sqrt(best / 2):F3} m)");
    }

    static Transform Find(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t) { var f = Find(c, name); if (f) return f; }
        return null;
    }
}
