using System.Collections.Generic;
using UnityEngine;
using F = FaceSource;

/// <summary>
/// Puts Meta face tracking (FaceSource) on an avatar, using whatever face it has. Fill in one group:
///   - Shapes (head + hands, robot): a mouth bar that opens and spreads, a visor that closes on blinks, two brows.
///   - Facial bones (Rocketbox, "Bip01 ..." names): jaw, lips, mouth corners, eyelids, brows, cheeks, nose.
///   - Blendshapes (Avaturn, Ready Player Me): ARKit names (jawOpen, eyeBlinkLeft, mouthSmileLeft, ...);
///     viseme_* shapes when the mesh has no jawOpen.
/// L/R in the expressions is the person's left/right, which is the avatar's left/right.
/// </summary>
[DefaultExecutionOrder(50)]
public class AvatarFace : MonoBehaviour
{
    public FaceSource face;

    [Header("Shapes")]
    public Transform mouth;
    public Vector3 mouthClosed = new Vector3(0.05f, 0.006f, 0.02f);
    public float mouthOpen = 0.04f, mouthSpread = 0.4f;
    public Transform visor;                        // squashes when the eyes close
    public Transform browL, browR;
    public float browMove = 0.012f, browTilt = 20f;

    [Header("Facial bones")]
    public Transform boneRoot;                     // search under here for Rocketbox's facial bones
    public float jawDegrees = 18f;

    [Header("Blendshapes")]
    public float blendScale = 100f;                // FBX blendshapes go 0..100; glTF (glTFast) 0..1

    // ---- per-frame helpers ----
    float E(int i) => face[i];                     // with FaceSource's gain
    float P(int l, int r) => face.Pair(l, r);

    Vector3 visorBind; Vector3 browLBind, browRBind; Quaternion browLRot, browRRot; Vector3 browUpL, browUpR;

    // A facial bone moved in the avatar's own axes (meters at scale 1): x = avatar right, y = up, z = forward.
    class Bone { public Transform t; public Vector3 bind, x, y, z; }
    readonly Dictionary<string, Bone> bones = new Dictionary<string, Bone>();
    Transform jaw; Quaternion jawBind; Vector3 jawAxis;

    struct Shape { public SkinnedMeshRenderer smr; public int index; public System.Func<float> value; }
    readonly List<Shape> shapes = new List<Shape>();

    void Start()
    {
        if (visor) visorBind = visor.localScale;
        if (browL) { browLBind = browL.localPosition; browLRot = browL.localRotation; browUpL = browL.parent.InverseTransformVector(transform.up); }
        if (browR) { browRBind = browR.localPosition; browRRot = browR.localRotation; browUpR = browR.parent.InverseTransformVector(transform.up); }
        if (boneRoot) FindBones();
        FindBlendshapes();
    }

    void FindBones()
    {
        foreach (var t in boneRoot.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("Bip01 ")) continue;
            string n = t.name.Substring(6);
            if (n == "MJaw")
            {
                jaw = t; jawBind = t.localRotation;
                // open = the chin goes down: rotate about the avatar's right, sign found by testing a point ahead of the jaw
                Vector3 chin = transform.forward * 0.06f - transform.up * 0.03f;
                float sign = (Quaternion.AngleAxis(10f, transform.right) * chin).y < chin.y ? 1f : -1f;
                jawAxis = Quaternion.Inverse(t.rotation) * transform.right * sign;
                continue;
            }
            var p = t.parent;
            bones[n] = new Bone { t = t, bind = t.localPosition, x = p.InverseTransformVector(transform.right),
                                  y = p.InverseTransformVector(transform.up), z = p.InverseTransformVector(transform.forward) };
        }
    }

    void Move(string name, float right, float up, float forward)
    {
        if (bones.TryGetValue(name, out var b)) b.t.localPosition = b.bind + b.x * right + b.y * up + b.z * forward;
    }

    void FindBlendshapes()
    {
        var arkit = new (string name, System.Func<float> v)[]
        {
            ("jawOpen", () => face.Open),
            ("eyeBlinkLeft", () => E(F.EyesClosedL)), ("eyeBlinkRight", () => E(F.EyesClosedR)),
            ("eyeWideLeft", () => E(F.UpperLidRaiserL)), ("eyeWideRight", () => E(F.UpperLidRaiserR)),
            ("browDownLeft", () => E(F.BrowLowererL)), ("browDownRight", () => E(F.BrowLowererR)),
            ("browInnerUp", () => P(F.InnerBrowRaiserL, F.InnerBrowRaiserR)),
            ("browOuterUpLeft", () => E(F.OuterBrowRaiserL)), ("browOuterUpRight", () => E(F.OuterBrowRaiserR)),
            ("cheekPuff", () => P(F.CheekPuffL, F.CheekPuffR)),
            ("cheekSquintLeft", () => E(F.CheekRaiserL)), ("cheekSquintRight", () => E(F.CheekRaiserR)),
            ("noseSneerLeft", () => E(F.NoseWrinklerL)), ("noseSneerRight", () => E(F.NoseWrinklerR)),
            ("mouthSmileLeft", () => E(F.LipCornerPullerL)), ("mouthSmileRight", () => E(F.LipCornerPullerR)),
            ("mouthFrownLeft", () => E(F.LipCornerDepressorL)), ("mouthFrownRight", () => E(F.LipCornerDepressorR)),
            ("mouthStretchLeft", () => E(F.LipStretcherL)), ("mouthStretchRight", () => E(F.LipStretcherR)),
            ("mouthPucker", () => P(F.LipPuckerL, F.LipPuckerR)),
            ("mouthFunnel", () => 0.5f * (P(F.LipFunnelerLB, F.LipFunnelerRB) + P(F.LipFunnelerLT, F.LipFunnelerRT))),
            ("mouthUpperUpLeft", () => E(F.UpperLipRaiserL)), ("mouthUpperUpRight", () => E(F.UpperLipRaiserR)),
            ("mouthLowerDownLeft", () => E(F.LowerLipDepressorL)), ("mouthLowerDownRight", () => E(F.LowerLipDepressorR)),
        };
        foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = smr.sharedMesh;
            if (mesh == null || mesh.blendShapeCount == 0) continue;
            foreach (var (name, v) in arkit)
            {
                int i = mesh.GetBlendShapeIndex(name);
                if (i >= 0) shapes.Add(new Shape { smr = smr, index = i, value = v });
            }
            if (mesh.GetBlendShapeIndex("jawOpen") >= 0) continue;            // ARKit mouth already covers speech
            for (int k = 0; k < F.VisemeNames.Length; k++)
            {
                int i = mesh.GetBlendShapeIndex("viseme_" + F.VisemeNames[k]);
                int kk = k;
                if (i >= 0) shapes.Add(new Shape { smr = smr, index = i, value = () => face.visemes[kk] });
            }
        }
    }

    void LateUpdate()
    {
        if (face == null) return;
        float open = face.Open, spread = face.Spread, blink = P(F.EyesClosedL, F.EyesClosedR);

        // shapes
        if (mouth) mouth.localScale = new Vector3(mouthClosed.x * (1f + mouthSpread * spread), mouthClosed.y + mouthOpen * open, mouthClosed.z);
        if (visor) visor.localScale = new Vector3(visorBind.x, visorBind.y * (1f - 0.85f * blink), visorBind.z);
        if (browL) Brow(browL, browLBind, browLRot, browUpL, F.InnerBrowRaiserL, F.OuterBrowRaiserL, F.BrowLowererL, 1f);
        if (browR) Brow(browR, browRBind, browRRot, browUpR, F.InnerBrowRaiserR, F.OuterBrowRaiserR, F.BrowLowererR, -1f);

        // facial bones (L = the avatar's left = -x)
        if (jaw) jaw.localRotation = jawBind * Quaternion.AngleAxis(jawDegrees * open, jawAxis);
        if (bones.Count > 0)
        {
            for (int s = 0; s < 2; s++)
            {
                bool l = s == 0; string side = l ? "L" : "R"; float sx = l ? -1f : 1f;
                float smile = E(l ? F.LipCornerPullerL : F.LipCornerPullerR), frown = E(l ? F.LipCornerDepressorL : F.LipCornerDepressorR);
                float stretch = E(l ? F.LipStretcherL : F.LipStretcherR), pucker = E(l ? F.LipPuckerL : F.LipPuckerR);
                float funnel = 0.5f * (E(l ? F.LipFunnelerLB : F.LipFunnelerRB) + E(l ? F.LipFunnelerLT : F.LipFunnelerRT));
                Move(side + "MouthCorner", sx * 0.006f * (stretch + 0.5f * smile - 0.8f * pucker - 0.5f * funnel), 0.005f * smile - 0.004f * frown, 0.002f * pucker);
                Move(side + "Upperlip", 0f, 0.004f * E(l ? F.UpperLipRaiserL : F.UpperLipRaiserR), 0.004f * (pucker + funnel));
                Move(side + "MouthBottom", 0f, -0.004f * E(l ? F.LowerLipDepressorL : F.LowerLipDepressorR), 0.004f * (pucker + funnel));
                float closed = E(l ? F.EyesClosedL : F.EyesClosedR), wide = E(l ? F.UpperLidRaiserL : F.UpperLidRaiserR);
                Move(side + "EyeBlinkTop", 0f, -0.009f * closed + 0.002f * wide, 0f);
                Move(side + "EyeBlinkBottom", 0f, 0.003f * closed, 0f);
                float lower = E(l ? F.BrowLowererL : F.BrowLowererR);
                Move(side + "InnerEyebrow", -sx * 0.002f * lower, 0.006f * E(l ? F.InnerBrowRaiserL : F.InnerBrowRaiserR) - 0.004f * lower, 0f);
                Move(side + "OuterEyebrow", 0f, 0.006f * E(l ? F.OuterBrowRaiserL : F.OuterBrowRaiserR) - 0.002f * lower, 0f);
                Move(side + "Cheek", sx * 0.005f * E(l ? F.CheekPuffL : F.CheekPuffR), 0.003f * E(l ? F.CheekRaiserL : F.CheekRaiserR) + 0.002f * smile, 0.003f * E(l ? F.CheekPuffL : F.CheekPuffR));
            }
            float pk = P(F.LipPuckerL, F.LipPuckerR);
            Move("MUpperLip", 0f, 0.004f * P(F.UpperLipRaiserL, F.UpperLipRaiserR), 0.004f * pk);
            Move("MBottomLip", 0f, -0.004f * P(F.LowerLipDepressorL, F.LowerLipDepressorR), 0.004f * pk);
            Move("MMiddleEyebrow", 0f, 0.004f * P(F.InnerBrowRaiserL, F.InnerBrowRaiserR) - 0.004f * P(F.BrowLowererL, F.BrowLowererR), 0f);
            Move("MNose", 0f, 0.002f * P(F.NoseWrinklerL, F.NoseWrinklerR), 0f);
        }

        // blendshapes
        foreach (var s in shapes) s.smr.SetBlendShapeWeight(s.index, s.value() * blendScale);
    }

    // inner brow up tilts the brow; the left brow's inner end is toward +x, the right brow's toward -x
    void Brow(Transform b, Vector3 bind, Quaternion rot, Vector3 up, int inner, int outer, int lowerer, float sign)
    {
        float i = E(inner), o = E(outer), d = E(lowerer);
        b.localPosition = bind + up * (browMove * (0.5f * (i + o) - 0.7f * d));
        b.localRotation = rot * Quaternion.AngleAxis(sign * browTilt * (i - o - d), Vector3.forward);
    }
}
