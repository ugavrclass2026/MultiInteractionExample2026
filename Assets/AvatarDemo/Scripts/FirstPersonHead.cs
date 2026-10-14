using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Seeing your own avatar from inside its head shows teeth, eyeballs and hair from behind. Scaling the
/// head bone to zero fixes that, but then the mirror shows a headless body. Instead, every skinned mesh
/// is drawn twice from the same bones:
///   - the original, on a layer only the mirror cameras draw (MirrorOnly), with the head;
///   - a copy whose head triangles are removed, on a layer only the headset camera draws (FirstPersonOnly).
/// For avatars made of separate pieces, put the head piece on MirrorOnly instead.
/// </summary>
public class FirstPersonHead : MonoBehaviour
{
    public Transform headBone;
    public string mirrorOnlyLayer = "MirrorOnly", firstPersonLayer = "FirstPersonOnly";

    void Start()
    {
        int mirrorOnly = LayerMask.NameToLayer(mirrorOnlyLayer), firstPerson = LayerMask.NameToLayer(firstPersonLayer);
        foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh == null) continue;
            var headSet = new HashSet<int>();
            for (int i = 0; i < smr.bones.Length; i++)
                if (smr.bones[i] && (smr.bones[i] == headBone || smr.bones[i].IsChildOf(headBone))) headSet.Add(i);

            var copy = Instantiate(smr.gameObject, smr.transform.parent);
            foreach (Transform c in copy.transform) Destroy(c.gameObject);
            copy.name = smr.name + " (first person, no head)";
            var csmr = copy.GetComponent<SkinnedMeshRenderer>();
            csmr.bones = smr.bones;
            csmr.rootBone = smr.rootBone;
            csmr.sharedMesh = Headless(smr.sharedMesh, headSet);
            csmr.updateWhenOffscreen = true;
            smr.updateWhenOffscreen = true;
            copy.layer = firstPerson;
            smr.gameObject.layer = mirrorOnly;
        }
    }

    static Mesh Headless(Mesh src, HashSet<int> headBones)
    {
        var m = Instantiate(src);
        var w = src.boneWeights;
        bool Head(int v) => w.Length > v && headBones.Contains(w[v].boneIndex0) && w[v].weight0 > 0.5f;
        for (int s = 0; s < m.subMeshCount; s++)
        {
            var tri = m.GetTriangles(s);
            var keep = new List<int>(tri.Length);
            for (int t = 0; t < tri.Length; t += 3)
                if (!(Head(tri[t]) || Head(tri[t + 1]) || Head(tri[t + 2]))) { keep.Add(tri[t]); keep.Add(tri[t + 1]); keep.Add(tri[t + 2]); }
            m.SetTriangles(keep, s);
        }
        return m;
    }
}
