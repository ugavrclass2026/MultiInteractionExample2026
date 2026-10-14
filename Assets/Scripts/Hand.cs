using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Composites;

public class Hand : MonoBehaviour
{
    public enum TeleporterState {INACTIVE,ACTIVE};
    TeleporterState teleporterState = TeleporterState.INACTIVE;
    bool teleportPossible = false;
    Vector3 teleportPoint; //where we would teleport to
    [SerializeField] bool stickTeleports = true;
    GameObject[] teleportSegments = new GameObject[50];
    [SerializeField] GameObject teleporterLineSegmentPrefab;
    [SerializeField] TeleportMarker teleportMarkerPrefab; //shows where we would land, and where our head would end up
    TeleportMarker teleportMarker;
    bool snapRotatePossible = true;
    public InputAction gripAction; //make sure you enable these and disable them (see below)
    [SerializeField] InputAction driveAction = new InputAction(expectedControlType:"Vector2"); //this action is going to cause us to drive forward/backward and left/right
    [SerializeField] InputAction teleportAction; // causes the teleporter to become active 

    public float maxDrivingSpeed = 3; //m//s
    public GameObject graphics; //makes it easy to disable or scale without affecting the logic or attached objects
    public Transform grabPoint; 
    public Transform rig; //purely used for air grab movement
    public Transform head; //used for driving movement
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
        for(int i = 0; i < 50; i++)
        {
            teleportSegments[i] = Instantiate<GameObject>(teleporterLineSegmentPrefab);
            teleportSegments[i].SetActive(false);
        }
        teleportMarker = Instantiate(teleportMarkerPrefab);
        teleportMarker.Hide();
    }

    void OnEnable()
    {
        gripAction.Enable();
        driveAction.Enable();
        teleportAction.Enable();
    }

    void OnDisable()
    {
        gripAction.Disable();
        driveAction.Disable();
        teleportAction.Disable();
        if (pulling == this)
            pulling = null;
    }

    void Update()
    {

        Vector2 teleportControl = teleportAction.ReadValue<Vector2>();

        Vector2 driveControl = driveAction.ReadValue<Vector2>();
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

        if(!stickTeleports){ //driving 
            Vector3 driveDirection = head.forward;
            driveDirection.y = 0;
            driveDirection.Normalize();

            Vector3 strafeDirection = head.right; 
            strafeDirection.y = 0;
            strafeDirection.Normalize();

            rig.position += (driveDirection * driveControl.y * maxDrivingSpeed + 
                            strafeDirection * driveControl.x * maxDrivingSpeed)
                            * Time.deltaTime;
        }

        if(teleporterState == TeleporterState.INACTIVE && teleportControl.y > .9f)
        {
            //activate the teleporter line
            teleporterState = TeleporterState.ACTIVE;
        }else if(teleporterState == TeleporterState.ACTIVE && teleportControl.y < .85f)
        {
            //deactivate the telporter line AND do a teleport if the location is valid
            if(teleportPossible) //the arc found somewhere flat to land; otherwise teleportPoint is stale
            {
                rig.Translate(TeleportOffset(),Space.World);
            }

            for(int i = 0; i < 50; i++)
            {
                teleportSegments[i].SetActive(false);
            }
            teleportMarker.Hide();
            teleporterState = TeleporterState.INACTIVE;
        }

        if(teleporterState == TeleporterState.ACTIVE)
        {
            //test and render the teleporter line
            //start a simulation of a teleporter arc
            float dt = .1f; //"seconds"
            Vector3 cp = transform.position; //start the current point at the hand position
            Vector3 cv = transform.forward*5; //max of 5 meters per second
            teleportPossible = false; //assume it's not possible to teleport
            for(int i = 0; i < 50; i++) //max of 50 segments
            {
                //move the current point according to gravity and velocity
                Vector3 np = cp + cv*dt;
                cv += Physics.gravity*dt;

                Vector3 between = np-cp;
                //shoot a ray from cp to np
                RaycastHit hit;
                float distance = between.magnitude;
                teleportSegments[i].transform.position = cp;
                teleportSegments[i].transform.LookAt(np);
                teleportSegments[i].transform.localScale = new Vector3(1,1,distance);
                teleportSegments[i].SetActive(true);
                
                if(Physics.Raycast(cp,between.normalized, out hit, distance))
                {

                    if(hit.normal.y > .9f) //this is roughly horizontal
                    {
                        teleportPossible = true;
                        teleportPoint = hit.point;
                        
                    }
                    teleportSegments[i].transform.localScale = new Vector3(1,1,hit.distance);
                    //turn off subsequent points
                    for(int j = i + 1; j < 50; j++)
                    {
                        //if (!teleportSegments[j].activeSelf)
                        //{
                        //    break; //this and subsequent are inactive
                        //}
                        teleportSegments[j].SetActive(false);
                    }
                    break; 
                }

                cp = np;
                
            }

            if(teleportPossible) //show where we would land, and where our head (and view) would end up
            {
                teleportMarker.Show(teleportPoint, head.position + TeleportOffset(), head.rotation);
            }
            else
            {
                teleportMarker.Hide();
            }
        }

        if(snapRotatePossible && Mathf.Abs(teleportControl.x) > .9f)
        {
            //make the rig rotate, but keep the player in the same world position

            Vector3 playerPos = head.transform.position;
            rig.RotateAround(playerPos,Vector3.up,Mathf.Sign(teleportControl.x)*15);

            snapRotatePossible = false;
        }else if(!snapRotatePossible && Mathf.Abs(teleportControl.x) < .85f)
        {
            snapRotatePossible = true;
        }


        gripWasPressed = gripPressed; //makes the event not fire again next frame
    }

    //how far the rig has to move so the point on the floor below our head lands on the teleport point
    Vector3 TeleportOffset()
    {
        Vector3 headInRig = rig.transform.InverseTransformPoint(head.transform.position);
        Vector3 footInRig = new Vector3(headInRig.x,0,headInRig.z);
        Vector3 footInWorld = rig.transform.TransformPoint(footInRig);

        return teleportPoint - footInWorld;
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
            angle -= 360f; //shortest path is the other way
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
