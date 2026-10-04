using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Alternating left/right leg controller for the isolated stepping test.
    /// Left mouse starts/lands the left leg; right mouse starts/lands the
    /// right leg. The opposite leg is the planted support chain.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonAlternatingLegController : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public RaccoonPhysicsRig PhysicsRig;
        public Camera InputCamera;
        public bool EnableMouseControl = true;
        public float GroundHeight;
        public float StepDuration = 0.8f;
        public float LandingDuration = 0.35f;
        public float LiftHeight = 0.22f;
        public float BodyAdvanceFactor = 0.45f;
        public float MaxReachScale = 0.88f;
        public float KneeForwardBias = 0.18f;
        public float NormalStepDistance = 0.30f;
        [Tooltip("Shortest step produced by a very quick click.")]
        public float MinStepDistance = 0.18f;
        [Tooltip("Longest step produced when the mouse is held for MaxPressDuration.")]
        public float MaxStepDistance = 0.65f;
        [Tooltip("Hold duration that reaches MaxStepDistance.")]
        public float MaxPressDuration = 0.45f;


        public bool IsReady { get; private set; }
        public bool IsHovering { get; private set; }
        public bool IsSingleSupport { get { return _state == StepState.Lifting || _state == StepState.Hovering || _state == StepState.Landing; } }
        public bool ActiveLegIsLeft { get; private set; }
        public Vector3 SupportFootWorld { get { return _supportFootWorld; } }

        public void ApplyBalanceCorrection(Vector3 worldDelta)
        {
            if (!IsReady || !IsSingleSupport)
                return;
            worldDelta.y = 0f;
            transform.position += worldDelta;
            MaintainSupportFoot();
        }

        public void MaintainSupportFoot()
        {
            if (!IsReady || !IsSingleSupport || _support == null)
                return;
            ApplySupportPose();
        }

        enum StepState { Idle, Lifting, Hovering, Landing, Planted }
        StepState _state;
        LegData _left;
        LegData _right;
        LegData _active;
        LegData _support;
        Vector3 _targetGround;
        Vector3 _lockedStepDirection;

        Vector3 _supportFootWorld;
        // Fixed contact plane prevents vertical error from accumulating across steps.
        float _groundContactY;

        Vector3 _rootStart;
        Vector3 _rootTarget;
        Vector3 _hipLocal;
        Vector3 _initialRootPosition;
        Quaternion _initialRootRotation;
        Vector3 _initialLeftFootWorld;
        Vector3 _initialRightFootWorld;
        float _progress;
        float _playTimer;
        int _pressedButton = -1;
        float _pressStartTime;
        float _heldPressDuration;
        bool _releaseRequested;

        float GetActionSpeedScale()
        {
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 0.35f : 1f;
        }



        sealed class LegData
        {
            public Transform UpperProxy;
            public Transform LowerProxy;
            public Transform FootProxy;
            public Transform UpperBone;
            public Transform LowerBone;
            public Transform FootBone;
            public Transform ToeBone;
            public Vector3 HipLocal;
            public Vector3 HipToFootLocal;
            public Vector3 FootWorld;
            public float UpperLength;
            public float LowerLength;
            public float FootLength;
            public Vector3 FootAxis;
            public Quaternion UpperTwist;
            public Quaternion LowerTwist;
            public Quaternion FootVisualRotation;
            public Quaternion FootVisualToProxyRotation;
        }

        void Awake()
        {
            if (BoneMap == null) BoneMap = GetComponent<RaccoonBoneMap>();
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();
            if (InputCamera == null) InputCamera = Camera.main;

            RaccoonLegStepController single = GetComponent<RaccoonLegStepController>();
            if (single != null) single.enabled = false;
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
            _left = BuildLeg(true);
            _right = BuildLeg(false);
            if (_left == null || _right == null || PhysicsRig.PhysicsRoot == null)
                return;

            _hipLocal = _left.HipLocal;
            _active = _left;
            _support = _right;
            ActiveLegIsLeft = true;
            _targetGround = _active.FootWorld;
            _supportFootWorld = _support.FootWorld;
            _initialRootPosition = transform.position;
            _initialRootRotation = transform.rotation;
            _initialLeftFootWorld = _left.FootWorld;
            _initialRightFootWorld = _right.FootWorld;
            _groundContactY = (_initialLeftFootWorld.y + _initialRightFootWorld.y) * 0.5f;

            _state = StepState.Idle;

            Rigidbody[] bodies = PhysicsRig.PhysicsRoot.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i].linearVelocity = Vector3.zero;
                bodies[i].angularVelocity = Vector3.zero;
                bodies[i].isKinematic = true;
                bodies[i].useGravity = false;
            }

            IsReady = true;
            ApplyLegPose(_left, transform.TransformPoint(_left.HipLocal), _left.FootWorld);
            ApplyLegPose(_right, transform.TransformPoint(_right.HipLocal), _right.FootWorld);
            Debug.Log("[RaccoonStep] Alternating leg controller ready: left/right mouse step commands enabled.", this);
        }

        public Vector3 GetBalanceSupportPoint(Vector3 point)
        {
            if (IsSingleSupport)
                return _supportFootWorld;

            if (_left == null || _right == null)
                return point;

            Vector3 a = _left.FootWorld;
            Vector3 b = _right.FootWorld;
            Vector3 segment = b - a;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < 0.000001f)
                return a;

            float t = Mathf.Clamp01(Vector3.Dot(point - a, segment) / lengthSquared);
            return Vector3.Lerp(a, b, t);
        }

        public void ResetToStandingPose()
        {
            if (!IsReady)
                return;

            enabled = true;
            transform.SetPositionAndRotation(_initialRootPosition, _initialRootRotation);
            _left.FootWorld = _initialLeftFootWorld;
            _right.FootWorld = _initialRightFootWorld;
            _active = _left;
            _support = _right;
            ActiveLegIsLeft = true;
            _targetGround = _initialLeftFootWorld;
            _supportFootWorld = _initialRightFootWorld;
            _progress = 0f;
            IsHovering = false;
            _state = StepState.Idle;
            ApplyLegPose(_left, transform.TransformPoint(_left.HipLocal), _left.FootWorld);
            ApplyLegPose(_right, transform.TransformPoint(_right.HipLocal), _right.FootWorld);
        }

        LegData BuildLeg(bool left)
        {
            Transform upper = left ? BoneMap.leftUpperLeg : BoneMap.rightUpperLeg;
            Transform lower = left ? BoneMap.leftLowerLeg : BoneMap.rightLowerLeg;
            Transform foot = left ? BoneMap.leftFoot : BoneMap.rightFoot;
            Transform toe = left ? BoneMap.leftToe : BoneMap.rightToe;
            if (upper == null || lower == null || foot == null || toe == null)
                return null;

            Transform upperProxy = PhysicsRig.GetProxyFor(upper);
            Transform lowerProxy = PhysicsRig.GetProxyFor(lower);
            Transform footProxy = PhysicsRig.GetProxyFor(foot);
            if (upperProxy == null || lowerProxy == null || footProxy == null)
                return null;

            LegData leg = new LegData
            {
                UpperBone = upper,
                LowerBone = lower,
                FootBone = foot,
                ToeBone = toe,
                UpperProxy = upperProxy,
                LowerProxy = lowerProxy,
                FootProxy = footProxy,
                HipLocal = transform.InverseTransformPoint(upper.position),
                HipToFootLocal = transform.InverseTransformPoint(foot.position)
                    - transform.InverseTransformPoint(upper.position),
                FootWorld = foot.position,
                UpperLength = Vector3.Distance(upper.position, lower.position),
                LowerLength = Vector3.Distance(lower.position, foot.position),
                FootLength = Mathf.Max(0.05f, Vector3.Distance(foot.position, toe.position)),
                FootAxis = (toe.position - foot.position).normalized,
                FootVisualRotation = foot.rotation,
                FootVisualToProxyRotation = Quaternion.Inverse(footProxy.rotation) * foot.rotation
            };
            if (leg.FootAxis.sqrMagnitude < 0.001f)
                leg.FootAxis = transform.forward;
            leg.UpperTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (lower.position - upper.position).normalized)) * upperProxy.rotation;
            leg.LowerTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (foot.position - lower.position).normalized)) * lowerProxy.rotation;
            return leg;
        }

void Update()
        {
            if (!IsReady || !EnableMouseControl)
                return;

            _playTimer += Time.deltaTime;
            UpdatePressDuration();

            int activeButton = ActiveLegIsLeft ? 0 : 1;
            bool released = Input.GetMouseButtonUp(activeButton);
            if (released && (_state == StepState.Lifting || _state == StepState.Hovering))
            {
                _releaseRequested = true;
                if (_state == StepState.Hovering)
                    BeginLanding();
            }

            Vector3 target;
            if ((_state == StepState.Hovering || _state == StepState.Lifting)
                && TryGetGroundTarget(out target))
                _targetGround = ClampTarget(target, _active);

            if (_state == StepState.Idle || _state == StepState.Planted)
            {
                // Hold the matching mouse button to lift that foot; release lands it.
                if (Input.GetMouseButtonDown(activeButton))
                {
                    BeginPress(activeButton);
                    BeginLift(ActiveLegIsLeft);
                }
            }
        }

        bool AnyStepButtonDown()
        {
            return Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
        }

        Vector3 GetMouseGroundDirection()
        {
            if (InputCamera == null)
                InputCamera = Camera.main;
            if (InputCamera == null)
                return transform.forward;

            Ray ray = InputCamera.ScreenPointToRay(Input.mousePosition);
            Plane plane = new Plane(Vector3.up, new Vector3(0f, GroundHeight, 0f));
            float distance;
            if (!plane.Raycast(ray, out distance))
                return transform.forward;

            Vector3 point = ray.GetPoint(distance);
            Vector3 direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f)
                return transform.forward;
            return direction.normalized;
        }

void BeginPress(int button)
        {
            _pressedButton = button;
            _pressStartTime = Time.time;
            _heldPressDuration = 0f;
            _releaseRequested = false;
            _lockedStepDirection = GetMouseGroundDirection();
        }

        void UpdatePressDuration()
        {
            if (_pressedButton < 0)
                return;

            if (Input.GetMouseButton(_pressedButton))
                _heldPressDuration = Mathf.Max(0f, Time.time - _pressStartTime);
            else if (Input.GetMouseButtonUp(_pressedButton))
                _heldPressDuration = Mathf.Max(0f, Time.time - _pressStartTime);
        }

        float GetCurrentStepDistance()
        {
            float minDistance = Mathf.Max(0.01f, MinStepDistance);
            float maxDistance = Mathf.Max(minDistance, MaxStepDistance);
            float duration = Mathf.Min(_heldPressDuration, Mathf.Max(0.01f, MaxPressDuration));
            float t = Mathf.Clamp01(duration / Mathf.Max(0.01f, MaxPressDuration));
            return Mathf.Lerp(minDistance, maxDistance, t);
        }

bool IsPrecisePlacementMode()
        {
            if (InputCamera == null)
                InputCamera = Camera.main;
            RaccoonCameraFollow cameraFollow = InputCamera != null
                ? InputCamera.GetComponent<RaccoonCameraFollow>()
                : null;
            return cameraFollow != null && cameraFollow.IsFirstPerson;
        }

void FixedUpdate()
        {
            if (!IsReady)
                return;

            if (_state == StepState.Lifting)
            {
                _progress = Mathf.Clamp01(_progress + Time.fixedDeltaTime * GetActionSpeedScale() / Mathf.Max(0.2f, StepDuration));
                float blend = _progress * _progress * (3f - 2f * _progress);
                Vector3 hip = transform.TransformPoint(_active.HipLocal);
                Vector3 foot = Vector3.Lerp(_active.FootWorld, _targetGround, blend * 0.12f)
                    + Vector3.up * (LiftHeight * blend);
                ApplyLegPose(_active, hip, foot);
                ApplySupportPose();

                if (_progress >= 1f)
                {
                    // A held button keeps the foot suspended. Release is the
                    // only event that starts the landing phase.
                    if (_releaseRequested)
                    {
                        BeginLanding();
                    }
                    else
                    {
                        IsHovering = true;
                        _state = StepState.Hovering;
                    }
                }
            }
            else if (_state == StepState.Hovering)
            {
                ApplyLegPose(_active, transform.TransformPoint(_active.HipLocal),
                    _targetGround + Vector3.up * LiftHeight);
                ApplySupportPose();
            }
            else if (_state == StepState.Landing)
            {
                _progress = Mathf.Clamp01(_progress + Time.fixedDeltaTime * GetActionSpeedScale() / Mathf.Max(0.15f, LandingDuration));
                float blend = _progress * _progress * (3f - 2f * _progress);
                transform.position = Vector3.Lerp(_rootStart, _rootTarget, blend);
                ApplyLegPose(_active, transform.TransformPoint(_active.HipLocal),
                    Vector3.Lerp(_targetGround + Vector3.up * LiftHeight, _targetGround, blend));
                ApplySupportPose();

                if (_progress >= 1f)
                {
                    Vector3 oldSupportWorld = _supportFootWorld;
                    ApplyLegPose(_active, transform.TransformPoint(_active.HipLocal), _targetGround);
                    _active.FootWorld = _targetGround;
                    _support = _active;
                    _supportFootWorld = _targetGround;
                    _active = ActiveLegIsLeft ? _right : _left;
                    ActiveLegIsLeft = !ActiveLegIsLeft;
                    _active.FootWorld = oldSupportWorld;
                    _state = StepState.Planted;
                    IsHovering = false;
                    _releaseRequested = false;
                    _pressedButton = -1;
                }
            }
        }

        void BeginLift(bool left)
        {
            if (left != ActiveLegIsLeft)
                return;
            _active = left ? _left : _right;
            _support = left ? _right : _left;
            if (_active == null || _support == null || _support.FootBone == null)
                return;
            _supportFootWorld = _support.FootBone.position;
            _targetGround = ClampTarget(_targetGround, _active);
            _progress = 0f;
            _state = StepState.Lifting;
            IsHovering = false;
        }

void BeginLanding()
        {
            // Never derive the landing height from the lifted foot's current pose.
            _targetGround.y = _groundContactY;

            _rootStart = transform.position;
            Vector3 desiredHip = _targetGround
                - transform.TransformDirection(_active.HipToFootLocal);
            Vector3 calibratedTarget = desiredHip
                - transform.TransformVector(_active.HipLocal);
            calibratedTarget.y = _rootStart.y;
            _rootTarget = calibratedTarget;
            _progress = 0f;
            _state = StepState.Landing;
        }

bool TryGetGroundTarget(out Vector3 target)
        {
            target = _active != null ? _active.FootWorld : transform.position;
            if (InputCamera == null)
                InputCamera = Camera.main;
            if (InputCamera == null)
                return false;

            if (IsPrecisePlacementMode())
            {
                Ray ray = InputCamera.ScreenPointToRay(Input.mousePosition);
                Plane plane = new Plane(Vector3.up, new Vector3(0f, GroundHeight, 0f));
                float distance;
                if (!plane.Raycast(ray, out distance))
                    return false;
                target = ray.GetPoint(distance);
            }
            else
            {
                // Step direction is sampled on mouse-button press and stays
                // locked while the middle button controls balance.
                Vector3 direction = _lockedStepDirection;
                if (direction.sqrMagnitude < 0.0001f)
                    direction = transform.forward;

                // WASD nudges the step direction while the foot is held.
                float x = 0f;
                float z = 0f;
                if (Input.GetKey(KeyCode.A)) x -= 1f;
                if (Input.GetKey(KeyCode.D)) x += 1f;
                if (Input.GetKey(KeyCode.S)) z -= 1f;
                if (Input.GetKey(KeyCode.W)) z += 1f;
                Vector3 keyboardDirection = transform.right * x + transform.forward * z;
                if (keyboardDirection.sqrMagnitude > 0.0001f)
                    direction = (direction.normalized * 0.5f + keyboardDirection.normalized * 0.5f).normalized;

                target = _active.FootWorld + direction.normalized * GetCurrentStepDistance();
            }

            target.y = _groundContactY;
            return true;
        }

        Vector3 ClampTarget(Vector3 target, LegData leg)
        {
            Vector3 hip = transform.TransformPoint(leg.HipLocal);
            Vector3 offset = target - hip;
            offset.y = 0f;
            float maxReach = (leg.UpperLength + leg.LowerLength)
                * Mathf.Clamp(MaxReachScale, 0.5f, 0.98f);
            if (offset.magnitude > maxReach)
                offset = offset.normalized * maxReach;
            target = hip + offset;
            target.y = _groundContactY;
            return target;
        }

        void ApplySupportPose()
        {
            ApplyLegPose(_support, transform.TransformPoint(_support.HipLocal), _supportFootWorld);
        }

        void ApplyLegPose(LegData leg, Vector3 hip, Vector3 ankle)
        {
            Vector3 toTarget = ankle - hip;
            float distance = Mathf.Clamp(toTarget.magnitude,
                Mathf.Abs(leg.UpperLength - leg.LowerLength) + 0.01f,
                Mathf.Max(0.02f, leg.UpperLength + leg.LowerLength - 0.01f));
            Vector3 direction = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.down;
            Vector3 pole = transform.forward + Vector3.up * KneeForwardBias;
            Vector3 bend = Vector3.ProjectOnPlane(pole, direction).normalized;
            if (bend.sqrMagnitude < 0.0001f)
                bend = Vector3.up;

            float cosKnee = (leg.UpperLength * leg.UpperLength + distance * distance
                - leg.LowerLength * leg.LowerLength) / (2f * leg.UpperLength * distance);
            cosKnee = Mathf.Clamp(cosKnee, -1f, 1f);
            float sinKnee = Mathf.Sqrt(Mathf.Max(0f, 1f - cosKnee * cosKnee));
            Vector3 knee = hip + direction * (cosKnee * leg.UpperLength) + bend * (sinKnee * leg.UpperLength);

            SetSegment(leg.UpperProxy, hip, knee, leg.UpperTwist);
            SetSegment(leg.LowerProxy, knee, ankle, leg.LowerTwist);
            Vector3 footAxis = Vector3.ProjectOnPlane(leg.FootAxis, Vector3.up).normalized;
            if (footAxis.sqrMagnitude < 0.0001f)
                footAxis = transform.forward;
            Vector3 toe = ankle + footAxis * leg.FootLength;
            leg.FootProxy.position = Vector3.Lerp(ankle, toe, 0.5f);
            leg.FootProxy.rotation = leg.FootVisualRotation * Quaternion.Inverse(leg.FootVisualToProxyRotation);
        }

        void SetSegment(Transform proxy, Vector3 start, Vector3 end, Quaternion twist)
        {
            Vector3 axis = end - start;
            if (axis.sqrMagnitude < 0.000001f)
                axis = Vector3.up * 0.01f;
            proxy.position = Vector3.Lerp(start, end, 0.5f);
            proxy.rotation = Quaternion.FromToRotation(Vector3.up, axis.normalized) * twist;
        }
    }
}
