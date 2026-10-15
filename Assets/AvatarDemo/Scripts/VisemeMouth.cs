using UnityEngine;

/// <summary>
/// Puts LipSync's visemes on an avatar, using whatever face it has (fill in one group):
///   - Shape: a mouth object that opens (height) and spreads or rounds (width). Head + hands and the robot.
///   - Jaw bones: the jaw bone rotates open; the mouth-corner bones move out (spread) or in (round).
///     Rocketbox avatars have facial bones and no blendshapes.
///   - Blendshapes: each viseme drives the blendshape of the same name ("viseme_aa", "aa", ...), as on
///     Ready Player Me and Avaturn avatars.
/// </summary>
[DefaultExecutionOrder(50)]
public class VisemeMouth : MonoBehaviour
{
    public LipSync lipSync;

    [Header("Shape")]
    public Transform mouth;
    public Vector3 closedScale = new Vector3(0.05f, 0.006f, 0.02f);
    public float openHeight = 0.04f, widthChange = 0.4f;

    [Header("Jaw bones")]
    public Transform jaw;
    public float jawOpenDegrees = 16f;
    public Transform leftCorner, rightCorner;
    public float cornerMove = 0.006f;                 // meters at full spread or round

    [Header("Blendshapes")]
    public SkinnedMeshRenderer face;
    public float blendScale = 100f;                   // FBX blendshapes go 0..100; glTF (glTFast) 0..1

    Quaternion jawBind; Vector3 jawAxis;              // jaw axis in the jaw's local frame: the avatar's right
    Vector3 lBind, rBind, lStep, rStep;
    int[] map;

    // blendshape names per viseme; exporters disagree on the last three vowels
    static readonly string[][] Aliases =
    {
        new[] { "sil" }, new[] { "PP" }, new[] { "FF" }, new[] { "TH" }, new[] { "DD" }, new[] { "kk" }, new[] { "CH" },
        new[] { "SS" }, new[] { "nn" }, new[] { "RR" }, new[] { "aa" }, new[] { "E", "e" }, new[] { "ih", "I", "i" },
        new[] { "oh", "O", "o" }, new[] { "ou", "U", "u" },
    };

    void Start()
    {
        if (jaw)
        {
            jawBind = jaw.localRotation;
            // open = the chin goes down: rotate about the avatar's right, sign picked by testing a point ahead of the jaw
            Vector3 chin = transform.forward * 0.06f - transform.up * 0.03f;
            float sign = (Quaternion.AngleAxis(10f, transform.right) * chin).y < chin.y ? 1f : -1f;
            jawAxis = Quaternion.Inverse(jaw.rotation) * transform.right * sign;
        }
        if (leftCorner) { lBind = leftCorner.localPosition; lStep = leftCorner.parent.InverseTransformVector(-transform.right * cornerMove); }
        if (rightCorner) { rBind = rightCorner.localPosition; rStep = rightCorner.parent.InverseTransformVector(transform.right * cornerMove); }
        if (face && face.sharedMesh)
        {
            map = new int[LipSync.Count];
            for (int i = 0; i < LipSync.Count; i++)
            {
                map[i] = -1;
                foreach (var a in Aliases[i])
                {
                    map[i] = face.sharedMesh.GetBlendShapeIndex("viseme_" + a);
                    if (map[i] < 0) map[i] = face.sharedMesh.GetBlendShapeIndex(a);
                    if (map[i] >= 0) break;
                }
            }
        }
    }

    void LateUpdate()
    {
        if (lipSync == null) return;
        float open = lipSync.Open, spread = lipSync.Wide - lipSync.Round;
        if (mouth)
            mouth.localScale = new Vector3(closedScale.x * (1f + widthChange * spread), closedScale.y + openHeight * open, closedScale.z);
        if (jaw) jaw.localRotation = jawBind * Quaternion.AngleAxis(jawOpenDegrees * open, jawAxis);
        if (leftCorner) leftCorner.localPosition = lBind + lStep * spread;
        if (rightCorner) rightCorner.localPosition = rBind + rStep * spread;
        if (map != null)
            for (int i = 0; i < LipSync.Count; i++)
                if (map[i] >= 0) face.SetBlendShapeWeight(map[i], lipSync.visemes[i] * blendScale);
    }
}
