using UnityEngine;

//Shows where a teleport will land: a spinning ring with ripples on the ground, and a ghost headset
//at the exact pose your head will have after the teleport, so you can see where your view will go.
//It has no colliders, so the teleport arc can't hit it.
public class TeleportMarker : MonoBehaviour
{
    public Transform ring;          //ring of dashes on the ground, spins
    public LineRenderer[] ripples;  //circles that grow outward and fade, one after another
    public Transform headset;       //ghost headset placed where your head will be; all of its renderers pulse
    public LineRenderer beam;       //line from the ground up to the headset
    public Transform beamDot;       //climbs the beam

    [Header("Animation")]
    public float spinSpeed = 45;            //degrees per second
    public float rippleTime = 1.2f;         //seconds for one ripple to grow and fade
    public float rippleStartRadius = 0.05f; //m
    public float rippleEndRadius = 0.35f;   //m
    public float pulseRate = 1;             //headset pulses per second
    [Range(0f, 1f)] public float pulseMin = 0.4f; //dimmest the headset gets, as a fraction of its normal opacity
    public float beamDotTime = 0.75f;       //seconds for the dot to climb from the ground to the headset
    public float beamGap = 0.07f;           //m, the beam stops this far below the head so it doesn't poke through the headset

    Material[] rippleMaterials;
    Color[] rippleColors;
    Material[] headsetMaterials;
    Color[] headsetColors;

    void Awake()
    {
        //.material makes a copy for each renderer, so each one can fade on its own (like Grabbable's highlight)
        rippleMaterials = new Material[ripples.Length];
        rippleColors = new Color[ripples.Length];
        for (int i = 0; i < ripples.Length; i++)
        {
            rippleMaterials[i] = ripples[i].material;
            rippleColors[i] = rippleMaterials[i].color;
        }

        Renderer[] headsetParts = headset.GetComponentsInChildren<Renderer>();
        headsetMaterials = new Material[headsetParts.Length];
        headsetColors = new Color[headsetParts.Length];
        for (int i = 0; i < headsetParts.Length; i++)
        {
            headsetMaterials[i] = headsetParts[i].material;
            headsetColors[i] = headsetMaterials[i].color;
        }
    }

    //landingPoint is where our feet will be; headPosition and headRotation are where our head will be
    public void Show(Vector3 landingPoint, Vector3 headPosition, Quaternion headRotation)
    {
        transform.position = landingPoint;
        headset.SetPositionAndRotation(headPosition, headRotation);
        beam.SetPosition(0, landingPoint);
        beam.SetPosition(1, headPosition + Vector3.down * beamGap);
        gameObject.SetActive(true);
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }

    void Update()
    {
        ring.Rotate(0, spinSpeed * Time.deltaTime, 0);

        for (int i = 0; i < ripples.Length; i++)
        {
            float t = (Time.time / rippleTime + (float)i / ripples.Length) % 1; //0 to 1, ripples evenly spaced in time
            SetCircle(ripples[i], Mathf.Lerp(rippleStartRadius, rippleEndRadius, t));
            Color color = rippleColors[i];
            color.a *= 1 - t; //fade out as it grows
            rippleMaterials[i].color = color;
        }

        float pulse = Mathf.Lerp(pulseMin, 1, 0.5f + 0.5f * Mathf.Cos(Time.time * pulseRate * 2 * Mathf.PI));
        for (int i = 0; i < headsetMaterials.Length; i++)
        {
            Color color = headsetColors[i];
            color.a *= pulse;
            headsetMaterials[i].color = color;
        }

        float climb = (Time.time / beamDotTime) % 1; //0 at the ground, 1 at the headset
        beamDot.position = Vector3.Lerp(beam.GetPosition(0), beam.GetPosition(1), climb);
    }

    //puts the line's points evenly around a flat circle (in the line's local space)
    static void SetCircle(LineRenderer line, float radius)
    {
        for (int i = 0; i < line.positionCount; i++)
        {
            float angle = i * 2 * Mathf.PI / line.positionCount;
            line.SetPosition(i, new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius));
        }
    }
}
