using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// 为独立物理代理提供基础重心和姿态稳定，不依赖 ClumsyRagdoll。
    /// 它只在 PhysicsRig 已经开启物理时工作。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    public sealed class RaccoonBalance : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public RaccoonPhysicsRig PhysicsRig;
        public RaccoonStepCharacter Character;
        public RaccoonFootController FootController;
        public RaccoonAlternatingLegController StepController;
        public RaccoonLegStepController SingleLegController;
        public RaccoonCenterOfMassBalance CenterOfMassBalance;

        [Header("Arm swing")]
        public bool EnableArmSwing = true;
        public float ArmSwingTorque = 3.5f;
        public float ArmSwingFrequency = 1.8f;
        public float ArmBalanceTorque = 2.0f;
        public float MaxArmSwingAngularVelocity = 3.0f;
        public float ArmSwingDamping = 0.6f;
        [Tooltip("Sign that maps positive arm rotation to the character's forward direction.")]
        public float ArmSwingForwardSign = 1f;
        [Tooltip("Maximum visual arm-angle change per second, preventing step-boundary snaps.")]
        public float ArmSwingTransitionSpeed = 180f;
        [Tooltip("Visible elbow bend added while the arm swings forward or backward.")]
        public float ArmElbowBendAngle = 16f;
        [Tooltip("Visible forward/back arm swing angle while a step is in progress.")]
        public float ArmSwingAngle = 14f;
        [Tooltip("How much the forearm follows the upper-arm swing.")]
        public float ArmSwingForearmFollow = 0.55f;


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
        public bool ArmProxyReady { get; private set; }
        public string ArmProxyStatus { get; private set; }
        public bool ArmSwingActive { get; private set; }
        public int ArmSwingApplyCount { get; private set; }
        public int FixedTickCount { get; private set; }
        public int UpdateTickCount { get; private set; }

        Rigidbody _hipsBody;
        Transform _leftFoot;
        Transform _rightFoot;
        Transform _leftUpperArm;
        Transform _rightUpperArm;
        Transform _leftLowerArm;
        Transform _rightLowerArm;
        Quaternion _leftUpperArmNeutral;
        Quaternion _rightUpperArmNeutral;
        Quaternion _leftLowerArmNeutral;
        Quaternion _rightLowerArmNeutral;
        Quaternion _leftUpperVisualNeutral;
        Quaternion _rightUpperVisualNeutral;
        Quaternion _leftLowerVisualNeutral;
        Quaternion _rightLowerVisualNeutral;
        bool _visualArmNeutralCached;
        float _currentSwingAngle;
        float _currentForearmAngle;
        float _forearmSwingVelocity;
        bool _hasSwingPose;
        Vector3 _neutralHips;
        Vector3 _neutralFeetCenter;

void Awake()
        {
            if (BoneMap == null) BoneMap = GetComponent<RaccoonBoneMap>();
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();
            if (Character == null) Character = GetComponent<RaccoonStepCharacter>();
            if (FootController == null) FootController = GetComponent<RaccoonFootController>();
            if (StepController == null) StepController = GetComponent<RaccoonAlternatingLegController>();
            if (SingleLegController == null) SingleLegController = GetComponent<RaccoonLegStepController>();
            if (CenterOfMassBalance == null) CenterOfMassBalance = GetComponent<RaccoonCenterOfMassBalance>();
        }

        void Start()
        {
            TryInitialize();
        }

        void Update()
        {
            UpdateTickCount++;
        }

        void LateUpdate()
        {
            // RaccoonPhysicsRig synchronizes the mesh in its LateUpdate;
            // this component runs after it and applies the visual gait pose.
            UpdateArmSwing();
        }

        void FixedUpdate()
        {
            FixedTickCount++;

            if (!EnableBalance || !TryInitialize())
                return;

            if (!PhysicsRig.PhysicsActive)
                return;

            TiltAngle = Vector3.Angle(_hipsBody.transform.up, Vector3.up);
            UpdateState();
            if ((Character != null && Character.State == RaccoonStepState.Falling)
                || (CenterOfMassBalance != null && CenterOfMassBalance.IsFalling))
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
            _leftUpperArm = PhysicsRig.GetProxyFor(BoneMap.leftUpperArm);
            _rightUpperArm = PhysicsRig.GetProxyFor(BoneMap.rightUpperArm);
            _leftLowerArm = PhysicsRig.GetProxyFor(BoneMap.leftLowerArm);
            _rightLowerArm = PhysicsRig.GetProxyFor(BoneMap.rightLowerArm);
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
    

        void UpdateArmSwing()
        {
            ArmSwingActive = false;
            if (!EnableArmSwing || PhysicsRig == null || BoneMap == null
                || !TryInitializeArmProxies())
                return;
            ArmProxyReady = true;
            if ((Character != null && Character.State == RaccoonStepState.Falling)
                || (CenterOfMassBalance != null && CenterOfMassBalance.IsFalling))
                return;

            bool footControllerStepping = FootController != null && FootController.IsSingleSupport;
            bool alternatingStepping = StepController != null && StepController.IsStepping;
            bool singleLegStepping = SingleLegController != null
                && (SingleLegController.IsStepping || SingleLegController.IsHovering);
            bool stepActive = footControllerStepping || alternatingStepping || singleLegStepping;

            float stepProgress = 0f;
            bool leftLegActive = FootController != null && FootController.ActiveFoot == "Left";
            if (alternatingStepping && StepController != null)
            {
                leftLegActive = StepController.ActiveLegIsLeft;
                stepProgress = StepController.StepProgress;
            }
            else if (footControllerStepping && FootController != null)
            {
                stepProgress = FootController.StepPhase;
            }
            else
            {
                stepProgress = SingleLegController != null
                    ? SingleLegController.StepProgress
                    : 0f;
            }

            // Pure piecewise-linear front/back motion. While the left leg
            // advances, the left arm moves rear-to-front; after the leg
            // changes, the left arm moves front-to-rear. The right arm is
            // always the opposite angle. No waveform is used.
            float rearAngle = -ArmSwingAngle * Mathf.Sign(ArmSwingForwardSign);
            float frontAngle = -rearAngle;
            float targetSwingAngle = _currentSwingAngle;
            if (stepActive)
            {
                float t = Mathf.Clamp01(stepProgress);
                targetSwingAngle = leftLegActive
                    ? Mathf.Lerp(rearAngle, frontAngle, t)
                    : Mathf.Lerp(frontAngle, rearAngle, t);
            }
            if (!_hasSwingPose)
            {
                _currentSwingAngle = targetSwingAngle;
                _currentForearmAngle = targetSwingAngle
                    * Mathf.Clamp01(ArmSwingForearmFollow);
                _hasSwingPose = true;
            }
            else
            {
                _currentSwingAngle = Mathf.MoveTowards(
                    _currentSwingAngle,
                    targetSwingAngle,
                    Mathf.Max(1f, ArmSwingTransitionSpeed) * Time.deltaTime);
                float targetForearmAngle = targetSwingAngle
                    * Mathf.Clamp01(ArmSwingForearmFollow);
                _currentForearmAngle = Mathf.SmoothDamp(
                    _currentForearmAngle,
                    targetForearmAngle,
                    ref _forearmSwingVelocity,
                    0.14f,
                    Mathf.Max(1f, ArmSwingTransitionSpeed),
                    Time.deltaTime);
            }

            float swingAngle = _currentSwingAngle;
            float forearmAngle = _currentForearmAngle;
            float elbowBend = Mathf.Abs(swingAngle)
                / Mathf.Max(0.01f, ArmSwingAngle) * ArmElbowBendAngle;
            Vector3 axis = transform.right;

            ApplyVisualArmPose(swingAngle, forearmAngle, elbowBend, axis);
            ArmSwingActive = stepActive;
            if (stepActive)
                ArmSwingApplyCount++;
        }

        bool TryInitializeArmProxies()
        {
            if (_leftUpperArm == null) _leftUpperArm = PhysicsRig.GetProxyFor(BoneMap.leftUpperArm);
            if (_rightUpperArm == null) _rightUpperArm = PhysicsRig.GetProxyFor(BoneMap.rightUpperArm);
            if (_leftLowerArm == null) _leftLowerArm = PhysicsRig.GetProxyFor(BoneMap.leftLowerArm);
            if (_rightLowerArm == null) _rightLowerArm = PhysicsRig.GetProxyFor(BoneMap.rightLowerArm);

            if (_leftUpperArm == null || _rightUpperArm == null
                || _leftLowerArm == null || _rightLowerArm == null)
            {
                ArmProxyReady = false;
                ArmProxyStatus = string.Format("LU={0}, RU={1}, LL={2}, RL={3}",
                    _leftUpperArm != null, _rightUpperArm != null,
                    _leftLowerArm != null, _rightLowerArm != null);
                return false;
            }

            if (_leftUpperArmNeutral == Quaternion.identity
                && _rightUpperArmNeutral == Quaternion.identity
                && _leftLowerArmNeutral == Quaternion.identity
                && _rightLowerArmNeutral == Quaternion.identity)
            {
                _leftUpperArmNeutral = _leftUpperArm.rotation;
                _rightUpperArmNeutral = _rightUpperArm.rotation;
                _leftLowerArmNeutral = _leftLowerArm.rotation;
                _rightLowerArmNeutral = _rightLowerArm.rotation;
            }
            if (!_visualArmNeutralCached)
            {
                _leftUpperVisualNeutral = BoneMap.leftUpperArm.rotation;
                _rightUpperVisualNeutral = BoneMap.rightUpperArm.rotation;
                _leftLowerVisualNeutral = BoneMap.leftLowerArm.rotation;
                _rightLowerVisualNeutral = BoneMap.rightLowerArm.rotation;
                _visualArmNeutralCached = true;
            }
            ArmProxyStatus = "ready";
            return true;
        }

        void ApplyVisualArmPose(float upperAngle, float forearmAngle,
            float elbowBend, Vector3 axis)
        {
            if (!_visualArmNeutralCached || BoneMap == null
                || BoneMap.leftUpperArm == null || BoneMap.rightUpperArm == null
                || BoneMap.leftLowerArm == null || BoneMap.rightLowerArm == null)
                return;

            // Rotate the visual bones around their own pivots. The upper arm
            // carries the elbow/hand through the hierarchy, so the limb keeps
            // its length instead of collapsing like a proxy capsule pose.
            Quaternion leftUpperDelta = Quaternion.AngleAxis(upperAngle, axis);
            Quaternion rightUpperDelta = Quaternion.AngleAxis(-upperAngle, axis);
            BoneMap.leftUpperArm.rotation =
                (leftUpperDelta * _leftUpperVisualNeutral).normalized;
            BoneMap.rightUpperArm.rotation =
                (rightUpperDelta * _rightUpperVisualNeutral).normalized;
            BoneMap.leftLowerArm.rotation =
                (leftUpperDelta * Quaternion.AngleAxis(forearmAngle + elbowBend, axis)
                    * _leftLowerVisualNeutral).normalized;
            BoneMap.rightLowerArm.rotation =
                (rightUpperDelta * Quaternion.AngleAxis(-forearmAngle - elbowBend, axis)
                    * _rightLowerVisualNeutral).normalized;
        }
}
}
