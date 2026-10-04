using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Isolated mechanical test for one articulated leg.
    /// The pelvis is the fixed base; the selected upper leg, lower leg and
    /// foot are the only dynamic bodies. Every joint is a limited universal
    /// joint, so a force applied at the foot must travel through the chain.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonLegMechanism : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public RaccoonPhysicsRig PhysicsRig;

        [Header("Test chain")]
        public bool UseLeftLeg = true;
        public bool RunOnPlay = true;
        public bool UseGravityOnTestLeg = false;
        public float JointAngleLimit = 35f;
        public float KneeBendLimit = 70f;
        public float KneeSideLimit = 8f;
        public float FootPullSpring = 38f;
        public float FootPullDamper = 18f;
        public float MaxPullAcceleration = 12f;
        public bool KeepFootSoleLevel = true;
        public float FootLevelSpring = 90f;
        public float FootLevelDamper = 16f;
        public float MaxFootLevelTorque = 120f;

        [Header("Automatic pull")]
        public bool AutoPullTest = false;
        public float AutoPullDelay = 1.0f;
        public Vector3 AutoPullOffset = new Vector3(0f, 0.02f, 0.22f);

        [Header("Mouse drag")]
        public bool EnableMouseDrag = true;
        public float DragPlaneHeight;

        public bool IsReady { get; private set; }
        public bool IsDragging { get; private set; }

        Rigidbody _hip;
        Rigidbody _upperLeg;
        Rigidbody _lowerLeg;
        Rigidbody _foot;
        Vector3 _neutralFootPosition;
        Quaternion _neutralFootRotation;
        Vector3 _pullTarget;
        bool _autoPullStarted;

        void Awake()
        {
            if (BoneMap == null) BoneMap = GetComponent<RaccoonBoneMap>();
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();

            // This scene is now a dedicated mechanism test. Prevent the
            // balance/step demo from applying a second set of forces.
            RaccoonFootController footController = GetComponent<RaccoonFootController>();
            if (footController != null) footController.enabled = false;
            RaccoonBalance balance = GetComponent<RaccoonBalance>();
            if (balance != null) balance.enabled = false;
            RaccoonMouseInput mouseInput = GetComponent<RaccoonMouseInput>();
            if (mouseInput != null) mouseInput.enabled = false;
        }

        void Start()
        {
            if (!RunOnPlay || PhysicsRig == null || BoneMap == null)
                return;

            BoneMap.BuildFrom(transform);
            PhysicsRig.SetPhysicsActive(true);
            IsReady = BuildTestChain();
        }

        bool BuildTestChain()
        {
            Transform hipProxy = PhysicsRig.GetProxyFor(BoneMap.hips);
            Transform upperProxy = PhysicsRig.GetProxyFor(UseLeftLeg ? BoneMap.leftUpperLeg : BoneMap.rightUpperLeg);
            Transform lowerProxy = PhysicsRig.GetProxyFor(UseLeftLeg ? BoneMap.leftLowerLeg : BoneMap.rightLowerLeg);
            Transform footProxy = PhysicsRig.GetProxyFor(UseLeftLeg ? BoneMap.leftFoot : BoneMap.rightFoot);
            if (hipProxy == null || upperProxy == null || lowerProxy == null || footProxy == null)
            {
                Debug.LogError("[RaccoonStep] Leg mechanism could not resolve the complete leg chain.", this);
                return false;
            }

            _hip = hipProxy.GetComponent<Rigidbody>();
            _upperLeg = upperProxy.GetComponent<Rigidbody>();
            _lowerLeg = lowerProxy.GetComponent<Rigidbody>();
            _foot = footProxy.GetComponent<Rigidbody>();
            if (_hip == null || _upperLeg == null || _lowerLeg == null || _foot == null)
                return false;

            Rigidbody[] bodies = PhysicsRig.PhysicsRoot.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody body = bodies[i];
                bool testBody = body == _upperLeg || body == _lowerLeg || body == _foot;
                body.isKinematic = !testBody;
                body.useGravity = testBody && UseGravityOnTestLeg;
                if (!body.isKinematic)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }

            _hip.isKinematic = true;
            _hip.useGravity = false;
            ConfigureJoints(PhysicsRig.PhysicsRoot.GetComponentsInChildren<ConfigurableJoint>(true));

            _neutralFootPosition = _foot.position;
            _neutralFootRotation = _foot.rotation;
            _pullTarget = _neutralFootPosition;
            DragPlaneHeight = _neutralFootPosition.y;
            Debug.Log("[RaccoonStep] One-leg mechanism ready: pelvis locked, one leg dynamic, all joints limited.", this);
            return true;
        }

        void ConfigureJoints(ConfigurableJoint[] joints)
        {
            for (int i = 0; i < joints.Length; i++)
            {
                ConfigurableJoint joint = joints[i];
                bool belongsToTestChain = joint.GetComponent<Rigidbody>() == _upperLeg ||
                    joint.GetComponent<Rigidbody>() == _lowerLeg ||
                    joint.GetComponent<Rigidbody>() == _foot;
                if (!belongsToTestChain)
                {
                    joint.xMotion = ConfigurableJointMotion.Locked;
                    joint.yMotion = ConfigurableJointMotion.Locked;
                    joint.zMotion = ConfigurableJointMotion.Locked;
                    continue;
                }

                joint.xMotion = ConfigurableJointMotion.Locked;
                joint.yMotion = ConfigurableJointMotion.Locked;
                joint.zMotion = ConfigurableJointMotion.Locked;
                joint.angularXMotion = ConfigurableJointMotion.Limited;
                joint.angularYMotion = ConfigurableJointMotion.Limited;
                joint.angularZMotion = ConfigurableJointMotion.Limited;
                joint.axis = Vector3.right;
                joint.secondaryAxis = Vector3.up;

                bool isKnee = joint.GetComponent<Rigidbody>() == _lowerLeg;
                bool isFoot = joint.GetComponent<Rigidbody>() == _foot;
                float mainLimit = isKnee ? KneeBendLimit : JointAngleLimit;
                float sideLimit = isKnee ? KneeSideLimit : (isFoot ? 10f : JointAngleLimit);
                joint.lowAngularXLimit = new SoftJointLimit { limit = -mainLimit };
                joint.highAngularXLimit = new SoftJointLimit { limit = mainLimit };
                joint.angularYLimit = new SoftJointLimit { limit = sideLimit };
                joint.angularZLimit = new SoftJointLimit { limit = sideLimit };

                // Do not servo the joints back to their bind pose. The limits
                // constrain the mechanism while the foot pull can bend it.
                joint.rotationDriveMode = RotationDriveMode.Slerp;
                joint.slerpDrive = new JointDrive
                {
                    positionSpring = 0f,
                    positionDamper = 2f,
                    maximumForce = Mathf.Infinity
                };
            }
        }

        void Update()
        {
            if (!IsReady || !EnableMouseDrag)
                return;

            if (Input.GetMouseButtonDown(0))
                IsDragging = true;
            if (Input.GetMouseButtonUp(0))
                IsDragging = false;

            if (IsDragging)
            {
                Camera camera = Camera.main;
                if (camera != null)
                {
                    Ray ray = camera.ScreenPointToRay(Input.mousePosition);
                    Plane plane = new Plane(Vector3.up, new Vector3(0f, DragPlaneHeight, 0f));
                    float distance;
                    if (plane.Raycast(ray, out distance))
                        _pullTarget = ray.GetPoint(distance);
                }
            }
        }

        public void SetPullTarget(Vector3 worldTarget)
        {
            if (!IsReady)
                return;

            _pullTarget = worldTarget;
            _autoPullStarted = true;
        }

        void FixedUpdate()
        {
            if (!IsReady || _foot == null)
                return;

            if (AutoPullTest && !_autoPullStarted && Time.timeSinceLevelLoad >= AutoPullDelay)
            {
                _autoPullStarted = true;
                _pullTarget = _neutralFootPosition + transform.TransformDirection(AutoPullOffset);
                Debug.Log("[RaccoonStep] Automatic foot pull started.", this);
            }

            if (!IsDragging && !_autoPullStarted)
                return;

            if (KeepFootSoleLevel)
                HoldFootLevel();

            Vector3 error = _pullTarget - _foot.position;
            Vector3 acceleration = error * FootPullSpring - _foot.linearVelocity * FootPullDamper;
            acceleration = Vector3.ClampMagnitude(acceleration, MaxPullAcceleration);
            Vector3 forcePoint = _foot.position + _foot.transform.forward * 0.05f + _foot.transform.up * 0.02f;
            _foot.AddForceAtPosition(acceleration * _foot.mass, forcePoint, ForceMode.Force);
        }

        void HoldFootLevel()
        {
            Quaternion rotationError = _neutralFootRotation * Quaternion.Inverse(_foot.rotation);
            float angle;
            Vector3 axis;
            rotationError.ToAngleAxis(out angle, out axis);
            if (angle > 180f)
            {
                angle -= 360f;
                axis = -axis;
            }

            Vector3 torque = axis * (angle * Mathf.Deg2Rad * FootLevelSpring)
                - _foot.angularVelocity * FootLevelDamper;
            torque = Vector3.ClampMagnitude(torque, MaxFootLevelTorque);
            _foot.AddTorque(torque, ForceMode.Acceleration);
        }
    }
}
