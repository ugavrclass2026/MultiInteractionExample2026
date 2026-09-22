using UnityEngine;

public enum GrabMode
{
    SnapObjectToHand,
    SnapHandToObject
}

[RequireComponent(typeof(Rigidbody))]
public class Grabbable : MonoBehaviour
{
    public GrabMode grabMode = GrabMode.SnapObjectToHand;
    public Transform attachPoint;
    public bool hideHand;
    public Color highlightColor = Color.yellow;

    public Rigidbody Body { get; private set; }
    public Hand HeldBy { get; set; }
    public Transform AttachPoint => attachPoint != null ? attachPoint : transform;

    Renderer visual;
    Color normalColor;
    int highlightCount;

    void Awake()
    {
        Body = GetComponent<Rigidbody>();

        visual = GetComponentInChildren<Renderer>();
        if (visual != null)
            normalColor = visual.material.color;
    }

    public void Highlight()
    {
        highlightCount++;
        UpdateColor();
    }

    public void Unhighlight()
    {
        highlightCount--;
        UpdateColor();
    }

    void UpdateColor()
    {
        if (visual != null)
            visual.material.color = highlightCount > 0 ? highlightColor : normalColor;
    }
}

