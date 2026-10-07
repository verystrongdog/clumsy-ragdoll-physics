using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// 独立的主动脚步控制器。它只驱动物理代理脚，不依赖旧的 ClumsyRagdoll 系统。
    /// 默认关闭，便于先验证代理骨架，再逐步开启步态。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonFootController : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public RaccoonPhysicsRig PhysicsRig;

        [Header("Activation")]
        public bool EnableStepping;
        public bool ActivatePhysicsWhenEnabled;
        public bool UseMouseTarget;
        public bool RequireStepRequest = true;
        [Tooltip("Keep the calibrated neutral pose kinematic until the first deliberate step.")]
        public bool HoldNeutralPoseUntilStep = true;
        [Tooltip("Automatically run a one-foot balance test after entering Play Mode.")]
        public bool AutoSingleSupportTest = true;
        public float AutoSingleSupportDelay = 1.0f;
        public bool HoldSingleSupportPose = true;
        public Vector2 MouseTarget { get; private set; }

        [Header("Step timing")]
        [Min(0.2f)] public float StepDuration = 0.72f;
        [Range(0.05f, 0.5f)] public float StepLift = 0.14f;
        [Range(0.05f, 0.8f)] public float StepLength = 0.28f;
        [Range(0.05f, 0.35f)] public float StepWidth = 0.12f;
        [Range(0.1f, 0.5f)] public float MaxStepForward = 0.38f;
        [Range(0.05f, 0.35f)] public float MaxStepLateral = 0.22f;

        [Header("Foot servo")]
        public float PositionSpring = 95f;
        public float VelocityDamper = 16f;
        public float MaxAcceleration = 35f;
        public float BodyAdvanceAcceleration = 9f;

        public bool IsInitialized { get; private set; }
        public float StepPhase { get; private set; }
        public string ActiveFoot { get; private set; }
        public bool IsSingleSupport { get { return _stepInProgress; } }
        public Vector3 SupportFootWorldPosition
        {
            get
            {
                Transform support = _leftSwinging ?
                    PhysicsRig != null ? PhysicsRig.GetProxyFor(BoneMap != null ? BoneMap.rightFoot : null) : null :
                    PhysicsRig != null ? PhysicsRig.GetProxyFor(BoneMap != null ? BoneMap.leftFoot : null) : null;
                return support != null ? support.position : transform.position;
            }
        }
        public Vector3 SupportFootNeutralWorldPosition
        {
            get
            {
                return transform.TransformPoint(_leftSwinging ? _rightNeutralLocal : _leftNeutralLocal);
            }
        }

        Rigidbody _leftBody;
        Rigidbody _rightBody;
        Rigidbody _hipsBody;
        Vector3 _leftNeutralLocal;
        Vector3 _rightNeutralLocal;
        Vector3 _requestedStepLocal;
        bool _leftSwinging = true;
        bool _stepInProgress;
        bool _simulationReleased;
        bool _autoDemoStarted;

        void Awake()
        {
            if (BoneMap == null) BoneMap = GetComponent<RaccoonBoneMap>();
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();
            ActiveFoot = "Left";
        }

        void Start()
        {
            TryInitialize();
            if (HoldNeutralPoseUntilStep && PhysicsRig != null && PhysicsRig.PhysicsActive)
            {
                PhysicsRig.SetPhysicsActive(false);
                Debug.Log("[RaccoonStep] Holding calibrated neutral stance until the first step.", this);
            }
        }

        void FixedUpdate()
        {
            if (!EnableStepping || !TryInitialize())
                return;

            if (AutoSingleSupportTest && !_autoDemoStarted && !_simulationReleased &&
                Time.timeSinceLevelLoad >= Mathf.Max(0.1f, AutoSingleSupportDelay))
            {
                BeginSingleSupportDemo();
            }

            if (!PhysicsRig.PhysicsActive)
                return;

            if (RequireStepRequest && !_stepInProgress)
            {
                // The foot joints are intentionally free in translation so a
                // stepping foot can be planted at a new location. While no
                // step is requested, actively hold both feet at their stance
                // targets; otherwise gravity makes the whole proxy body fall.
                DriveFoot(_leftBody, _leftNeutralLocal, false);
                DriveFoot(_rightBody, _rightNeutralLocal, false);
                return;
            }

            float duration = Mathf.Max(0.2f, StepDuration);
            if (!HoldSingleSupportPose || !_autoDemoStarted)
                StepPhase += Time.fixedDeltaTime / duration;
            float phase = Mathf.Clamp01(StepPhase);
            ActiveFoot = _leftSwinging ? "Left" : "Right";
            ApplyBodyAdvance(phase);
            DriveFoot(_leftBody, _leftNeutralLocal, _leftSwinging);
            DriveFoot(_rightBody, _rightNeutralLocal, !_leftSwinging);

            if (UseMouseTarget && _hipsBody != null)
            {
                Vector3 desiredPlanar = transform.right * MouseTarget.x + transform.forward * MouseTarget.y;
                _hipsBody.AddForce(desiredPlanar * 4f, ForceMode.Acceleration);
            }

            if (StepPhase >= 1f && !HoldSingleSupportPose)
            {
                StepPhase = 0f;
                _leftSwinging = !_leftSwinging;
                _stepInProgress = false;
            }
        }

        bool TryInitialize()
        {
            if (IsInitialized)
                return true;

            if (BoneMap == null || PhysicsRig == null || !BoneMap.IsValid)
                return false;

            Transform leftProxy = PhysicsRig.GetProxyFor(BoneMap.leftFoot);
            Transform rightProxy = PhysicsRig.GetProxyFor(BoneMap.rightFoot);
            if (leftProxy == null || rightProxy == null)
                return false;

            _leftBody = leftProxy.GetComponent<Rigidbody>();
            _rightBody = rightProxy.GetComponent<Rigidbody>();
            Transform hipsProxy = PhysicsRig.GetProxyFor(BoneMap.hips);
            _hipsBody = hipsProxy != null ? hipsProxy.GetComponent<Rigidbody>() : null;
            if (_leftBody == null || _rightBody == null || _hipsBody == null)
                return false;

            _leftNeutralLocal = transform.InverseTransformPoint(leftProxy.position);
            _rightNeutralLocal = transform.InverseTransformPoint(rightProxy.position);
            IsInitialized = true;
            Debug.Log($"[RaccoonStep] Foot controller initialized: left={_leftBody.name}, right={_rightBody.name}", this);
            return true;
        }

        public void SetMouseTarget(Vector2 target)
        {
            MouseTarget = Vector2.ClampMagnitude(target, 1f);
        }

        public void RequestStep(Vector3 worldTarget)
        {
            if (_stepInProgress)
                return;

            if (HoldNeutralPoseUntilStep && !_simulationReleased && PhysicsRig != null)
            {
                PhysicsRig.SetPhysicsActive(true);
                _simulationReleased = true;
                Debug.Log("[RaccoonStep] Released physics proxy for active stepping.", this);
            }

            Vector3 localTarget = transform.InverseTransformPoint(worldTarget);
            Vector3 neutralLocal = _leftSwinging ? _leftNeutralLocal : _rightNeutralLocal;
            Vector3 delta = localTarget - neutralLocal;
            delta.x = Mathf.Clamp(delta.x, -MaxStepLateral, MaxStepLateral);
            delta.z = Mathf.Clamp(delta.z, -0.18f, MaxStepForward);
            if (delta.sqrMagnitude < 0.01f)
                delta.z = StepLength;
            _requestedStepLocal = neutralLocal + delta;
            StepPhase = 0f;
            _stepInProgress = true;
            Debug.Log($"[RaccoonStep] Step requested: foot={(_leftSwinging ? "Left" : "Right")}, localTarget={_requestedStepLocal}", this);
        }

        void BeginSingleSupportDemo()
        {
            _autoDemoStarted = true;
            _leftSwinging = true;
            _stepInProgress = true;
            StepPhase = 0.5f;
            _requestedStepLocal = _leftNeutralLocal + Vector3.forward * 0.04f;

            if (PhysicsRig != null)
            {
                PhysicsRig.SetPhysicsActive(true);
                _simulationReleased = true;
            }

            Debug.Log("[RaccoonStep] Single-support demo started: left foot lifted, right foot supporting.", this);
        }

        void ApplyBodyAdvance(float phase)
        {
            if (_hipsBody == null || !_stepInProgress)
                return;

            Vector3 neutralLocal = _leftSwinging ? _leftNeutralLocal : _rightNeutralLocal;
            Vector3 stepDelta = _requestedStepLocal - neutralLocal;
            stepDelta.y = 0f;
            Vector3 worldAdvance = transform.TransformDirection(stepDelta);
            if (worldAdvance.sqrMagnitude > 0.0001f)
                _hipsBody.AddForce(worldAdvance.normalized * BodyAdvanceAcceleration * Mathf.Clamp01(phase), ForceMode.Acceleration);
        }

        void DriveFoot(Rigidbody body, Vector3 neutralLocal, bool swinging)
        {
            if (body == null)
                return;

            float phase = Mathf.Clamp01(StepPhase);
            Vector3 targetLocal = neutralLocal;
            if (swinging)
            {
                Vector3 stepTarget = RequireStepRequest
                    ? _requestedStepLocal
                    : neutralLocal + Vector3.forward * StepLength;
                targetLocal = Vector3.Lerp(neutralLocal, stepTarget, phase);
                targetLocal += Vector3.up * (Mathf.Sin(phase * Mathf.PI) * StepLift);
                targetLocal += transform.right * MouseTarget.x * 0.20f;
                targetLocal += transform.forward * MouseTarget.y * 0.35f;
            }

            Vector3 targetWorld = transform.TransformPoint(targetLocal);
            Vector3 acceleration = RaccoonFootMotor.CalculateAcceleration(
                targetWorld, body.position, body.linearVelocity,
                PositionSpring, VelocityDamper, MaxAcceleration);
            body.AddForce(acceleration, ForceMode.Acceleration);
        }

        public void SetStepping(bool enabled)
        {
            EnableStepping = enabled;
            if (enabled && ActivatePhysicsWhenEnabled && PhysicsRig != null)
                PhysicsRig.SetPhysicsActive(true);
        }

        [ContextMenu("Start Stepping")]
        void StartSteppingFromMenu()
        {
            SetStepping(true);
        }

        [ContextMenu("Stop Stepping")]
        void StopSteppingFromMenu()
        {
            SetStepping(false);
        }
    }
}
