using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem;

public class Hand : MonoBehaviour
{
    public InputAction gripAction; //make sure you enable these and disable them (see below)
    public GameObject graphics; //makes it easy to disable or scale without affecting the logic or attached objects
    public Transform grabPoint; 
    public Transform rig; //purely used for air grab movement
    public float grabRadius = 0.1f; //grabbable in this range (defined in the inspector) can be grabbed

    [Header("Release")]
    public float releaseWindow = 0.03f; //seconds
    [Range(0f, 1f)] public float smoothing = 0.5f;

    public Grabbable Selected { get; private set; } //the one that will be grabbed (null otherwise)
    public Grabbable Held { get; private set; } //reference to the held object (null otherwise)

    static Hand pulling; //for locomotion to ensure that we have only one pulling hand
    Vector3 pullAnchor; //for locomotion, a remembered location

    bool gripWasPressed;

    Transform holdTarget;

    struct VelocitySample //for throwing
    {
        public float time;
        public Vector3 velocity;
        public Vector3 angularVelocity;
    }
    readonly List<VelocitySample> samples = new List<VelocitySample>(); //for throwing

    void Awake()
    {
        holdTarget = new GameObject("Hold Target").transform;
        holdTarget.SetParent(grabPoint, false);
    }

    void OnEnable()
    {
        gripAction.Enable();
    }

    void OnDisable()
    {
        gripAction.Disable();
        if (pulling == this)
            pulling = null;
    }

    void Update()
    {
        bool gripPressed = gripAction.IsPressed(); //save for later

        if (Held == null)
            UpdateSelection(); //I could be holding something, so try to highlight

        if (gripPressed && !gripWasPressed) //the event of pressing the grip
        {
            if (Selected != null) //something can be selected
                Grab(Selected);
            else if (pulling == null) //nothing can be selected, I want to air grab, and nothing else is pulling
            {
                pulling = this; //this hand is pulling
                pullAnchor = transform.position; //save the anchor for calculating the offset
            }
        }
        else if (!gripPressed) //no event, and the grip isn't pressed, so unwind things that would be invalid
        {
            if (Held != null)
                Release(); //throw the object
            if (pulling == this)
                pulling = null;
        }

        //deal with locomotion
        if (pulling == this)
            rig.position += pullAnchor - transform.position; //realigning my new hand position 

        gripWasPressed = gripPressed; //makes the event not fire again next frame
    }

    void FixedUpdate()
    {
        if (Held == null)
            return;

        Rigidbody body = Held.Body;

        body.linearVelocity = (holdTarget.position - body.position) / Time.fixedDeltaTime;

        Quaternion remaining = (holdTarget.rotation * Quaternion.Inverse(body.rotation)).normalized;
        remaining.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f)
            angle -= 360f; 
        body.angularVelocity = axis * (angle * Mathf.Deg2Rad) / Time.fixedDeltaTime;

        RecordVelocity(body);
    }

    void UpdateSelection()
    {
        Grabbable closest = null;
        float closestDistance = float.MaxValue;

        foreach (Collider collider in Physics.OverlapSphere(grabPoint.position, grabRadius))
        {
            Rigidbody body = collider.attachedRigidbody;
            Grabbable grabbable = body != null ? body.GetComponent<Grabbable>() : null;
            if (grabbable == null)
                continue;

            float distance = Vector3.Distance(grabPoint.position, collider.ClosestPoint(grabPoint.position));
            if (distance < closestDistance)
            {
                closest = grabbable;
                closestDistance = distance;
            }
        }

        Select(closest);
    }

    void Select(Grabbable grabbable)
    {
        if (grabbable == Selected)
            return;

        if (Selected != null)
            Selected.Unhighlight();

        Selected = grabbable;

        if (Selected != null)
            Selected.Highlight();
    }

    void Grab(Grabbable grabbable)
    {
        if (grabbable.HeldBy != null) //the object is held by something already
            grabbable.HeldBy.Release(); //tell the holder to release it

        Held = grabbable;
        Held.HeldBy = this;
        Select(null); //we don't want this hand to highlight anything anymore

        if (Held.grabMode == GrabMode.SnapObjectToHand)
        {
            holdTarget.SetParent(Held.AttachPoint, false); //child of the attach point
            //set the world position and rotation to the actual held object position
            //effectively finds the inverse of the attachpoint
            holdTarget.SetPositionAndRotation(Held.transform.position, Held.transform.rotation);
            holdTarget.SetParent(grabPoint, false); //composes the grabPoint with the inverse 
        }
        else if(Held.grabMode == GrabMode.SnapHandToObject)
        {
            holdTarget.SetParent(grabPoint, false);  //this should always be the case
            holdTarget.SetPositionAndRotation(Held.transform.position, Held.transform.rotation);
        }

        if (Held.hideHand)
            graphics.SetActive(false);

        samples.Clear();
    }

    public void Release()
    {
        if (Held == null)
            return;

        Rigidbody body = Held.Body;

        Vector3 velocity = body.linearVelocity;
        Vector3 angularVelocity = body.angularVelocity;
        foreach (VelocitySample sample in samples)
        {
            if (sample.velocity.magnitude > velocity.magnitude)
                velocity = sample.velocity;
            if (sample.angularVelocity.magnitude > angularVelocity.magnitude)
                angularVelocity = sample.angularVelocity;
        }
        body.linearVelocity = velocity;
        body.angularVelocity = angularVelocity;

        Held.HeldBy = null;
        Held = null;
        graphics.SetActive(true);
    }

    void RecordVelocity(Rigidbody body)
    {
        VelocitySample sample = new VelocitySample
        {
            time = Time.time,
            velocity = body.linearVelocity,
            angularVelocity = body.angularVelocity
        };

        if (samples.Count > 0)
        {
            VelocitySample previous = samples[samples.Count - 1];
            sample.velocity = Vector3.Lerp(sample.velocity, previous.velocity, smoothing);
            sample.angularVelocity = Vector3.Lerp(sample.angularVelocity, previous.angularVelocity, smoothing);
        }

        samples.Add(sample);
        samples.RemoveAll(s => s.time < Time.time - releaseWindow);
    }
}
