using UnityEngine;

/// <summary>Draws the 26 tracked joints of each hand as small spheres while hand tracking is on.</summary>
public class JointDots : MonoBehaviour
{
    public HandSource hands;
    public Material material;
    public float radius = 0.007f;
    public bool show = true;
    Transform[] l, r;

    void Start()
    {
        l = Make("Left joints"); r = Make("Right joints");
    }

    Transform[] Make(string name)
    {
        var root = new GameObject(name).transform; root.SetParent(transform, false);
        var a = new Transform[hands.left.joints.Length];
        for (int i = 0; i < a.Length; i++)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(s.GetComponent<Collider>());
            s.transform.SetParent(root, false);
            s.transform.localScale = Vector3.one * radius * 2f;
            if (material) s.GetComponent<MeshRenderer>().sharedMaterial = material;
            a[i] = s.transform;
        }
        return a;
    }

    void LateUpdate()
    {
        Show(l, hands.left); Show(r, hands.right);
    }

    void Show(Transform[] dots, HandSource.Hand h)
    {
        bool on = show && h.hasJoints;
        for (int i = 0; i < dots.Length; i++)
        {
            if (dots[i].gameObject.activeSelf != on) dots[i].gameObject.SetActive(on);
            if (on) dots[i].position = h.joints[i];
        }
    }
}
