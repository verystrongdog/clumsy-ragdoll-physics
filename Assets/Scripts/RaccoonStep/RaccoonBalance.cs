using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// 为独立物理代理提供基础重心和姿态稳定，不依赖 ClumsyRagdoll。
    /// 它只在 PhysicsRig 已经开启物理时工作。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonBalance : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public RaccoonPhysicsRig PhysicsRig;
        public RaccoonStepCharacter Character;
        public RaccoonFootController FootController;

        [Header("Activation")]
        public bool EnableBalance = true;

        [Header("Center of mass")]
        public float PositionSpring = 42f;
        public float PositionDamper = 10f;
        public float MaxBalanceAcceleration = 24f;

        [Header("Upright torque")]
        public float UprightSpring = 28f;
        public float UprightDamper = 5f;
        public float MaxBalanceTorque = 32f;
        [Tooltip("Keeps the first prototype upright while the foot servos are holding stance.")]
        public bool UseStrongUprightAssist = true;
        public float MinimumUprightSpring = 80f;
        public float MinimumUprightDamper = 12f;
        public float MinimumBalanceTorque = 120f;

        [Header("State thresholds")]
        public float UnstableAngle = 18f;
        public float FallingAngle = 42f;

        public bool IsInitialized { get; private set; }
        public float TiltAngle { get; private set; }

        Rigidbody _hipsBody;
        Transform _leftFoot;
        Transform _rightFoot;
        Vector3 _neutralHips;
        Vector3 _neutralFeetCenter;

        void Awake()
        {
            if (BoneMap == null) BoneMap = GetComponent<RaccoonBoneMap>();
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();
            if (Character == null) Character = GetComponent<RaccoonStepCharacter>();
            if (FootController == null) FootController = GetComponent<RaccoonFootController>();
        }

        void Start()
        {
            TryInitialize();
        }

        void FixedUpdate()
        {
            if (!EnableBalance || !TryInitialize() || !PhysicsRig.PhysicsActive)
                return;

            Vector3 targetHips;
            if (FootController != null && FootController.IsSingleSupport)
            {
                // During single support, the pelvis target follows the planted
                // foot rather than the midpoint of both feet.
                targetHips = transform.TransformPoint(_neutralHips)
                    + FootController.SupportFootWorldPosition
                    - FootController.SupportFootNeutralWorldPosition;
            }
            else
            {
                Vector3 feetCenter = (_leftFoot.position + _rightFoot.position) * 0.5f;
                Vector3 initialFeetCenter = transform.TransformPoint(_neutralFeetCenter);
                targetHips = transform.TransformPoint(_neutralHips) + feetCenter - initialFeetCenter;
            }

            Vector3 positionError = targetHips - _hipsBody.position;
            Vector3 acceleration = positionError * PositionSpring - _hipsBody.linearVelocity * PositionDamper;
            acceleration = Vector3.ClampMagnitude(acceleration, MaxBalanceAcceleration);
            _hipsBody.AddForce(acceleration, ForceMode.Acceleration);

            Vector3 tiltAxis = Vector3.Cross(_hipsBody.transform.up, Vector3.up);
            TiltAngle = Vector3.Angle(_hipsBody.transform.up, Vector3.up);
            float uprightSpring = UseStrongUprightAssist
                ? Mathf.Max(UprightSpring, MinimumUprightSpring)
                : UprightSpring;
            float uprightDamper = UseStrongUprightAssist
                ? Mathf.Max(UprightDamper, MinimumUprightDamper)
                : UprightDamper;
            float maxTorque = UseStrongUprightAssist
                ? Mathf.Max(MaxBalanceTorque, MinimumBalanceTorque)
                : MaxBalanceTorque;
            Vector3 torque = tiltAxis * uprightSpring - _hipsBody.angularVelocity * uprightDamper;
            torque = Vector3.ClampMagnitude(torque, maxTorque);
            _hipsBody.AddTorque(torque, ForceMode.Acceleration);

            UpdateState();
        }

        bool TryInitialize()
        {
            if (IsInitialized)
                return true;

            if (BoneMap == null || PhysicsRig == null || !BoneMap.IsValid)
                return false;

            Transform hipsProxy = PhysicsRig.GetProxyFor(BoneMap.hips);
            _leftFoot = PhysicsRig.GetProxyFor(BoneMap.leftFoot);
            _rightFoot = PhysicsRig.GetProxyFor(BoneMap.rightFoot);
            if (hipsProxy == null || _leftFoot == null || _rightFoot == null)
                return false;

            _hipsBody = hipsProxy.GetComponent<Rigidbody>();
            if (_hipsBody == null)
                return false;

            _neutralHips = transform.InverseTransformPoint(hipsProxy.position);
            _neutralFeetCenter = transform.InverseTransformPoint((_leftFoot.position + _rightFoot.position) * 0.5f);
            IsInitialized = true;
            Debug.Log($"[RaccoonStep] Balance initialized: hips={_hipsBody.name}", this);
            return true;
        }

        void UpdateState()
        {
            if (Character == null)
                return;

            if (TiltAngle >= FallingAngle)
                Character.State = RaccoonStepState.Falling;
            else if (TiltAngle >= UnstableAngle)
                Character.State = RaccoonStepState.Unstable;
            else
                Character.State = RaccoonStepState.Stable;
        }
    }
}
