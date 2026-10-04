using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Deliberate, single-leg pose controller used while the final body mesh
    /// is still being iterated. The mouse supplies only a ground target; a
    /// two-link IK chain creates a visible thigh lift and knee bend.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonLegStepController : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public RaccoonPhysicsRig PhysicsRig;
        public Camera InputCamera;

        [Header("Input")]
        public bool UseLeftLeg = true;
        public bool EnableMouseControl = true;
        public float GroundHeight;

        [Header("Step shape")]
        public float StepDuration = 0.9f;
        public float LiftHeight = 0.24f;
        public bool AutoLiftOnPlay = true;
        public float AutoLiftDelay = 0.35f;
        public float LandingDuration = 0.35f;
        public float BodyAdvanceFactor = 0.45f;
        public float MaxReachScale = 0.88f;
        public float KneeForwardBias = 0.18f;
        public bool KeepFootLevel = true;

        public bool IsReady { get; private set; }
        public bool IsStepping { get; private set; }
        public bool IsHovering { get; private set; }
        public float StepProgress { get; private set; }

        enum StepState { Idle, Lifting, Hovering, Landing, Planted }
        StepState _state;

        Transform _upperProxy;
        Transform _lowerProxy;
        Transform _footProxy;
        Transform _supportUpperProxy;
        Transform _supportLowerProxy;
        Transform _supportFootProxy;
        Vector3 _hipPivot;
        Vector3 _hipLocal;
        Vector3 _startAnkle;
        Vector3 _startToe;
        Vector3 _targetGround;
        Vector3 _footAxis;
        Quaternion _upperTwist;
        Quaternion _lowerTwist;
        Quaternion _footVisualInitialRotation;
        Quaternion _footVisualToProxyRotation;
        Transform _footVisualBone;
        float _groundAnkleHeight;
        float _playTimer;
        float _upperLength;
        float _lowerLength;
        float _footLength;
        Vector3 _supportHipLocal;
        Vector3 _supportFootWorld;
        Vector3 _supportToeWorld;
        Vector3 _supportFootAxis;
        float _supportUpperLength;
        float _supportLowerLength;
        float _supportFootLength;
        Quaternion _supportUpperTwist;
        Quaternion _supportLowerTwist;
        Quaternion _supportFootVisualInitialRotation;
        Quaternion _supportFootVisualToProxyRotation;
        Vector3 _rootStartPosition;
        Vector3 _rootTargetPosition;

        void Awake()
        {
            if (BoneMap == null) BoneMap = GetComponent<RaccoonBoneMap>();
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();
            if (InputCamera == null) InputCamera = Camera.main;

            // This controller owns the isolated-leg test. Disable the older
            // foot pull/balance behaviours so no second controller fights IK.
            RaccoonLegMechanism mechanism = GetComponent<RaccoonLegMechanism>();
            if (mechanism != null) mechanism.enabled = false;
            RaccoonFootController footController = GetComponent<RaccoonFootController>();
            if (footController != null) footController.enabled = false;
            RaccoonBalance balance = GetComponent<RaccoonBalance>();
            if (balance != null) balance.enabled = false;
            RaccoonMouseInput mouseInput = GetComponent<RaccoonMouseInput>();
            if (mouseInput != null) mouseInput.enabled = false;
        }

        void Start()
        {
            if (BoneMap == null || PhysicsRig == null)
                return;

            BoneMap.BuildFrom(transform);
            PhysicsRig.SetPhysicsActive(true);
            Transform upperBone = UseLeftLeg ? BoneMap.leftUpperLeg : BoneMap.rightUpperLeg;
            Transform lowerBone = UseLeftLeg ? BoneMap.leftLowerLeg : BoneMap.rightLowerLeg;
            Transform footBone = UseLeftLeg ? BoneMap.leftFoot : BoneMap.rightFoot;
            Transform toeBone = UseLeftLeg ? BoneMap.leftToe : BoneMap.rightToe;
            Transform supportUpperBone = UseLeftLeg ? BoneMap.rightUpperLeg : BoneMap.leftUpperLeg;
            Transform supportLowerBone = UseLeftLeg ? BoneMap.rightLowerLeg : BoneMap.leftLowerLeg;
            Transform supportFootBone = UseLeftLeg ? BoneMap.rightFoot : BoneMap.leftFoot;
            Transform supportToeBone = UseLeftLeg ? BoneMap.rightToe : BoneMap.leftToe;
            _upperProxy = PhysicsRig.GetProxyFor(upperBone);
            _lowerProxy = PhysicsRig.GetProxyFor(lowerBone);
            _footProxy = PhysicsRig.GetProxyFor(footBone);
            _footVisualBone = footBone;
            _supportUpperProxy = PhysicsRig.GetProxyFor(supportUpperBone);
            _supportLowerProxy = PhysicsRig.GetProxyFor(supportLowerBone);
            _supportFootProxy = PhysicsRig.GetProxyFor(supportFootBone);

            if (_upperProxy == null || _lowerProxy == null || _footProxy == null ||
                _supportUpperProxy == null || _supportLowerProxy == null || _supportFootProxy == null ||
                toeBone == null || supportToeBone == null)
                return;

            _hipPivot = upperBone.position;
            _hipLocal = transform.InverseTransformPoint(_hipPivot);
            _startAnkle = footBone.position;
            _startToe = toeBone.position;
            _upperLength = Vector3.Distance(upperBone.position, lowerBone.position);
            _lowerLength = Vector3.Distance(lowerBone.position, footBone.position);
            _footLength = Mathf.Max(0.05f, Vector3.Distance(footBone.position, toeBone.position));
            _footAxis = (_startToe - _startAnkle).normalized;
            if (_footAxis.sqrMagnitude < 0.001f)
                _footAxis = transform.forward;
            _groundAnkleHeight = _startAnkle.y;
            _footVisualInitialRotation = footBone.rotation;
            _footVisualToProxyRotation = Quaternion.Inverse(_footProxy.rotation) * footBone.rotation;
            _upperTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (lowerBone.position - upperBone.position).normalized)) * _upperProxy.rotation;
            _lowerTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (footBone.position - lowerBone.position).normalized)) * _lowerProxy.rotation;

            _supportHipLocal = transform.InverseTransformPoint(supportUpperBone.position);
            _supportFootWorld = supportFootBone.position;
            _supportToeWorld = supportToeBone.position;
            _supportUpperLength = Vector3.Distance(supportUpperBone.position, supportLowerBone.position);
            _supportLowerLength = Vector3.Distance(supportLowerBone.position, supportFootBone.position);
            _supportFootLength = Mathf.Max(0.05f, Vector3.Distance(supportFootBone.position, supportToeBone.position));
            _supportFootAxis = (_supportToeWorld - _supportFootWorld).normalized;
            if (_supportFootAxis.sqrMagnitude < 0.001f)
                _supportFootAxis = transform.forward;
            _supportFootVisualInitialRotation = supportFootBone.rotation;
            _supportFootVisualToProxyRotation = Quaternion.Inverse(_supportFootProxy.rotation) * supportFootBone.rotation;
            _supportUpperTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (supportLowerBone.position - supportUpperBone.position).normalized)) * _supportUpperProxy.rotation;
            _supportLowerTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (supportFootBone.position - supportLowerBone.position).normalized)) * _supportLowerProxy.rotation;

            // Keep the whole test deterministic: the hip and all non-test
            // bodies are kinematic; the three test proxies are posed directly.
            Rigidbody[] bodies = PhysicsRig.PhysicsRoot.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i].linearVelocity = Vector3.zero;
                bodies[i].angularVelocity = Vector3.zero;
                bodies[i].isKinematic = true;
                bodies[i].useGravity = false;
            }

            _targetGround = _startAnkle;
            IsReady = true;
            _state = StepState.Idle;
            ApplyPose(_targetGround);
            Debug.Log("[RaccoonStep] Controlled single-leg IK ready: thigh lift and knee bend are mouse-driven.", this);
        }

        void Update()
        {
            if (!IsReady || !EnableMouseControl)
                return;

            _playTimer += Time.deltaTime;
            if (AutoLiftOnPlay && _state == StepState.Idle && _playTimer >= AutoLiftDelay)
            {
                BeginLift();
            }

            if (_state == StepState.Hovering)
            {
                Vector3 hoverTarget;
                if (TryGetGroundTarget(out hoverTarget))
                    _targetGround = ClampTarget(hoverTarget);

                if (Input.GetMouseButtonDown(0))
                {
                    StepProgress = 0f;
                    _rootStartPosition = transform.position;
                    Vector3 advance = Vector3.ProjectOnPlane(_targetGround - _startAnkle, Vector3.up);
                    _rootTargetPosition = _rootStartPosition + advance * BodyAdvanceFactor;
                    _state = StepState.Landing;
                }
            }
            else if ((_state == StepState.Idle || _state == StepState.Planted) && Input.GetMouseButtonDown(0))
            {
                Vector3 target;
                if (TryGetGroundTarget(out target))
                {
                    _targetGround = ClampTarget(target);
                    BeginLift();
                }
            }
        }

        void FixedUpdate()
        {
            if (!IsReady)
                return;

            if (_state == StepState.Lifting)
            {
                StepProgress = Mathf.Clamp01(StepProgress + Time.fixedDeltaTime / Mathf.Max(0.2f, StepDuration));
                float liftBlend = StepProgress * StepProgress * (3f - 2f * StepProgress);
                Vector3 liftTarget = Vector3.Lerp(_startAnkle, _targetGround, liftBlend * 0.15f)
                    + Vector3.up * (LiftHeight * liftBlend);
                ApplyPose(liftTarget);
                if (StepProgress >= 1f)
                {
                    StepProgress = 1f;
                    IsStepping = false;
                    IsHovering = true;
                    _state = StepState.Hovering;
                }
            }
            else if (_state == StepState.Hovering)
            {
                ApplyPose(_targetGround + Vector3.up * LiftHeight);
            }
            else if (_state == StepState.Landing)
            {
                StepProgress = Mathf.Clamp01(StepProgress + Time.fixedDeltaTime / Mathf.Max(0.15f, LandingDuration));
                float landBlend = StepProgress * StepProgress * (3f - 2f * StepProgress);
                transform.position = Vector3.Lerp(_rootStartPosition, _rootTargetPosition, landBlend);
                Vector3 landingTarget = Vector3.Lerp(
                    _targetGround + Vector3.up * LiftHeight,
                    _targetGround,
                    landBlend);
                ApplyPose(landingTarget);
                ApplySupportPose();
                if (StepProgress >= 1f)
                {
                    IsStepping = false;
                    IsHovering = false;
                    _state = StepState.Planted;
                    ApplyPose(_targetGround);
                    ApplySupportPose();
                }
            }
        }

        void BeginLift()
        {
            StepProgress = 0f;
            IsStepping = true;
            IsHovering = false;
            _state = StepState.Lifting;
        }

        bool TryGetGroundTarget(out Vector3 target)
        {
            target = _startAnkle;
            if (InputCamera == null)
                InputCamera = Camera.main;
            if (InputCamera == null)
                return false;

            Ray ray = InputCamera.ScreenPointToRay(Input.mousePosition);
            Plane ground = new Plane(Vector3.up, new Vector3(0f, GroundHeight, 0f));
            float distance;
            if (!ground.Raycast(ray, out distance))
                return false;

            target = ray.GetPoint(distance);
            target.y = _groundAnkleHeight;
            return true;
        }

        Vector3 ClampTarget(Vector3 target)
        {
            Vector3 offset = target - _hipPivot;
            offset.y = 0f;
            float maximum = (_upperLength + _lowerLength) * Mathf.Clamp(MaxReachScale, 0.5f, 0.98f);
            if (offset.magnitude > maximum)
                offset = offset.normalized * maximum;
            target = _hipPivot + offset;
            target.y = _groundAnkleHeight;
            return target;
        }

        void ApplyPose(Vector3 ankleTarget)
        {
            _hipPivot = transform.TransformPoint(_hipLocal);
            Vector3 toTarget = ankleTarget - _hipPivot;
            float distance = Mathf.Clamp(toTarget.magnitude,
                Mathf.Abs(_upperLength - _lowerLength) + 0.01f,
                Mathf.Max(0.02f, _upperLength + _lowerLength - 0.01f));
            Vector3 direction = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.down;

            Vector3 pole = transform.forward + Vector3.up * KneeForwardBias;
            Vector3 bendDirection = Vector3.ProjectOnPlane(pole, direction).normalized;
            if (bendDirection.sqrMagnitude < 0.0001f)
                bendDirection = Vector3.up;

            float cosKnee = (_upperLength * _upperLength + distance * distance - _lowerLength * _lowerLength)
                / (2f * _upperLength * distance);
            cosKnee = Mathf.Clamp(cosKnee, -1f, 1f);
            float sinKnee = Mathf.Sqrt(Mathf.Max(0f, 1f - cosKnee * cosKnee));
            Vector3 knee = _hipPivot + direction * (cosKnee * _upperLength)
                + bendDirection * (sinKnee * _upperLength);

            SetSegment(_upperProxy, _hipPivot, knee, _upperTwist);
            SetSegment(_lowerProxy, knee, ankleTarget, _lowerTwist);

            Vector3 levelAxis = Vector3.ProjectOnPlane(_footAxis, Vector3.up).normalized;
            if (levelAxis.sqrMagnitude < 0.0001f)
                levelAxis = transform.forward;
            Vector3 toeTarget = ankleTarget + levelAxis * _footLength;
            _footProxy.position = Vector3.Lerp(ankleTarget, toeTarget, 0.5f);
            if (KeepFootLevel)
            {
                // Preserve the visual foot's initial world orientation. This
                // removes the axial twist introduced by underconstrained IK.
                _footProxy.rotation = _footVisualInitialRotation * Quaternion.Inverse(_footVisualToProxyRotation);
            }
            else
            {
                SetSegment(_footProxy, ankleTarget, toeTarget, Quaternion.identity);
            }
        }

        void ApplySupportPose()
        {
            Vector3 supportHip = transform.TransformPoint(_supportHipLocal);
            Vector3 toTarget = _supportFootWorld - supportHip;
            float distance = Mathf.Clamp(toTarget.magnitude,
                Mathf.Abs(_supportUpperLength - _supportLowerLength) + 0.01f,
                Mathf.Max(0.02f, _supportUpperLength + _supportLowerLength - 0.01f));
            Vector3 direction = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.down;
            Vector3 pole = transform.forward + Vector3.up * KneeForwardBias;
            Vector3 bendDirection = Vector3.ProjectOnPlane(pole, direction).normalized;
            if (bendDirection.sqrMagnitude < 0.0001f)
                bendDirection = Vector3.up;

            float cosKnee = (_supportUpperLength * _supportUpperLength + distance * distance
                - _supportLowerLength * _supportLowerLength) / (2f * _supportUpperLength * distance);
            cosKnee = Mathf.Clamp(cosKnee, -1f, 1f);
            float sinKnee = Mathf.Sqrt(Mathf.Max(0f, 1f - cosKnee * cosKnee));
            Vector3 knee = supportHip + direction * (cosKnee * _supportUpperLength)
                + bendDirection * (sinKnee * _supportUpperLength);

            SetSegment(_supportUpperProxy, supportHip, knee, _supportUpperTwist);
            SetSegment(_supportLowerProxy, knee, _supportFootWorld, _supportLowerTwist);

            Vector3 levelAxis = Vector3.ProjectOnPlane(_supportFootAxis, Vector3.up).normalized;
            if (levelAxis.sqrMagnitude < 0.0001f)
                levelAxis = transform.forward;
            Vector3 toeTarget = _supportFootWorld + levelAxis * _supportFootLength;
            _supportFootProxy.position = Vector3.Lerp(_supportFootWorld, toeTarget, 0.5f);
            _supportFootProxy.rotation = _supportFootVisualInitialRotation
                * Quaternion.Inverse(_supportFootVisualToProxyRotation);
        }

        void SetSegment(Transform proxy, Vector3 start, Vector3 end, Quaternion twist)
        {
            if (proxy == null)
                return;

            Vector3 axis = end - start;
            if (axis.sqrMagnitude < 0.000001f)
                axis = Vector3.up * 0.01f;
            proxy.position = Vector3.Lerp(start, end, 0.5f);
            proxy.rotation = Quaternion.FromToRotation(Vector3.up, axis.normalized) * twist;
        }
    }
}
