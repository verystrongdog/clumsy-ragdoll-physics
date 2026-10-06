using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Minimal physics-proxy recovery test: from the settled fall pose to a
    /// seated pose. This intentionally does not share the walking controller's
    /// input or state machine.
    /// </summary>
    [DefaultExecutionOrder(22010)]
    [DisallowMultipleComponent]
    public sealed class RaccoonSitRecoveryController : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public RaccoonPhysicsRig PhysicsRig;
        public RaccoonCenterOfMassBalance Balance;
        public RaccoonStepCharacter Character;
        public bool EnableInput = true;
        public float Duration = 1.6f;
        public float GroundHeight;
        public float SeatedFootHeight = 0.045f;
        [Tooltip("Forward distance of the recovered foot measured from the locked pelvis, not from the character root.")]
        public float SeatedFootForward = 0.18f;
        public float SeatedFootSide = 0.11f;
        [Tooltip("Minimum horizontal distance between the locked pelvis and the recovered foot. Prevents the foot from folding directly under the hips.")]
        public float MinimumFootPelvisDistance = 0.24f;
        public float SeatedHipLift = 0.025f;
        [Tooltip("Biases the knee and thigh toward the character's forward direction during recovery.")]
        public float KneeForwardBias = 0.65f;
        [Tooltip("Raises the knee pole so the thigh lifts upward from the locked pelvis before folding forward.")]
        public float KneeUpBias = 0.75f;
        [Tooltip("Additional world-up lift of the knee during the middle of the recovery. This is the visible hip-flexion phase; the pelvis remains position-locked.")]
        public float KneeLiftHeight = 0.18f;
        [Tooltip("Minimum interior knee angle during recovery. Smaller values allow a tighter knee fold.")]
        public float MaxKneeBendDegrees = 75f;
        [Tooltip("Extra distance kept between the folded leg chain and the locked pelvis.")]
        public float KneeBodyClearance = 0.028f;
        [Tooltip("During recovery, constrain the requested leg to a sagittal vertical plane, equivalent to two parallel vertical walls.")]
        public bool ConstrainRecoveryLegToVerticalPlane = true;
        [Tooltip("Half-width of the virtual wall channel around the leg's vertical plane.")]
        public float VerticalPlaneHalfWidth = 0.035f;
        [Tooltip("After the torso sits up, automatically perform hand support, forward weight shift, squat, and stand.")]
        public bool EnableFullGetUpSequence = true;
        public bool UseLeftSupportHand = true;
        public float SupportHandForward = 0.22f;
        public float SupportHandSide = 0.18f;
        public float SupportHandHeight = 0.035f;
        public float ForwardPressDistance = 0.18f;
        public float SquatHipHeight = 0.28f;
        public float StandingHipHeight = 0.62f;

        RaccoonLegStepController _singleLegController;
        RaccoonAlternatingLegController _alternatingLegController;
        bool _singleLegWasEnabled;
        bool _alternatingLegWasEnabled;
        bool _ready;
        bool _frozenForRecovery;
        bool _leftRequested;
        bool _rightRequested;
        bool _leftFinished;
        bool _rightFinished;
        bool _torsoFinished;
        float _leftProgress;
        float _rightProgress;
        float _torsoProgress;
        float _sequenceProgress;
        RecoveryPhase _recoveryPhase;
        Vector3 _hipsPosition;
        Quaternion _spineStartRotation;
        Quaternion _headStartRotation;
        Vector3 _spineStartPosition;
        Vector3 _headStartPosition;
        LegTarget _left;
        LegTarget _right;
        ArmTarget _supportArm;

        void Awake()
        {
            if (BoneMap == null) BoneMap = GetComponent<RaccoonBoneMap>();
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();
            if (Balance == null) Balance = GetComponent<RaccoonCenterOfMassBalance>();
            if (Character == null) Character = GetComponent<RaccoonStepCharacter>();
            _singleLegController = GetComponent<RaccoonLegStepController>();
            _alternatingLegController = GetComponent<RaccoonAlternatingLegController>();
        }

        void Start()
        {
            if (BoneMap == null || PhysicsRig == null)
                return;

            BoneMap.BuildFrom(transform);
            _ready = BuildTargets();
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.R))
                RestoreWalkingControllers();

            if (!EnableInput || !_ready || !CanAcceptRecoveryInput())
                return;

            if (Input.GetMouseButtonDown(0))
                BeginLegRecovery(true);
            if (Input.GetMouseButtonDown(1))
                BeginLegRecovery(false);
        }

        void FixedUpdate()
        {
            if (!_frozenForRecovery)
                return;

            if (EnableFullGetUpSequence)
            {
                TickFullGetUpSequence();
                Physics.SyncTransforms();
                return;
            }

            if (_leftRequested)
                _leftProgress = Mathf.Clamp01(_leftProgress + Time.fixedDeltaTime / Mathf.Max(0.1f, Duration));
            if (_rightRequested)
                _rightProgress = Mathf.Clamp01(_rightProgress + Time.fixedDeltaTime / Mathf.Max(0.1f, Duration));

            float leftBlend = Smooth(_leftProgress);
            float rightBlend = Smooth(_rightProgress);
            if (!_torsoFinished)
            {
                _torsoProgress = Mathf.Clamp01(_torsoProgress + Time.fixedDeltaTime / Mathf.Max(0.1f, Duration));
                ApplyTorso(Smooth(_torsoProgress));
                if (_torsoProgress >= 1f)
                    _torsoFinished = true;
            }

            if (_torsoFinished)
            {
                ApplyLeg(_left, leftBlend, _leftRequested && ConstrainRecoveryLegToVerticalPlane);
                ApplyLeg(_right, rightBlend, _rightRequested && ConstrainRecoveryLegToVerticalPlane);
            }
            if (_leftRequested && _leftProgress >= 1f)
            {
                _leftFinished = true;
                PhysicsRig.SetRecoveryLegBodyCollision(BoneMap.leftUpperLeg, BoneMap.leftLowerLeg, BoneMap.leftFoot, false);
            }
            if (_rightRequested && _rightProgress >= 1f)
            {
                _rightFinished = true;
                PhysicsRig.SetRecoveryLegBodyCollision(BoneMap.rightUpperLeg, BoneMap.rightLowerLeg, BoneMap.rightFoot, false);
            }
            Physics.SyncTransforms();
        }

        public bool BeginLegRecovery(bool left)
        {
            if (!_ready || PhysicsRig == null || !CanAcceptRecoveryInput())
                return false;

            if (!_frozenForRecovery)
            {
                SuspendWalkingControllers();
                if (Balance != null && Balance.CanBeginRecovery)
                {
                    if (!Balance.PrepareForRecovery())
                        return false;
                }
                else if (!PhysicsRig.FreezeCurrentPoseForRecovery())
                    return false;

                _hipsPosition = PhysicsRig.GetProxyFor(BoneMap.hips).position;
                Transform spine = PhysicsRig.GetProxyFor(BoneMap.spine);
                Transform head = PhysicsRig.GetProxyFor(BoneMap.head);
                if (spine == null || head == null)
                    return false;

                _spineStartPosition = spine.position;
                _headStartPosition = head.position;
                _spineStartRotation = spine.rotation;
                _headStartRotation = head.rotation;
                _frozenForRecovery = true;
                _torsoProgress = 0f;
                _torsoFinished = false;
                _sequenceProgress = 0f;
                _recoveryPhase = EnableFullGetUpSequence ? RecoveryPhase.Torso : RecoveryPhase.ManualLegs;

                RefreshLegFromCurrentPose(ref _left, BoneMap.leftUpperLeg, BoneMap.leftLowerLeg, BoneMap.leftFoot, true);
                RefreshLegFromCurrentPose(ref _right, BoneMap.rightUpperLeg, BoneMap.rightLowerLeg, BoneMap.rightFoot, false);
                _supportArm = BuildArm(
                    UseLeftSupportHand ? BoneMap.leftUpperArm : BoneMap.rightUpperArm,
                    UseLeftSupportHand ? BoneMap.leftLowerArm : BoneMap.rightLowerArm,
                    UseLeftSupportHand ? BoneMap.leftHand : BoneMap.rightHand);
            }

            if (EnableFullGetUpSequence)
            {
                Debug.Log("[RaccoonStep] Full get-up sequence started: torso -> hand support -> forward press -> squat -> stand.", this);
                return true;
            }

            // This model has no waist joint. The first recovery command is
            // therefore reserved for rotating the whole spine/head block as a
            // rigid torso around the locked pelvis. Legs become available only
            // after the torso has reached the seated orientation.
            if (!_torsoFinished)
            {
                Debug.Log("[RaccoonStep] Torso sit-up started. Click again after it finishes to recover a leg.", this);
                return true;
            }

            if (left)
            {
                if (_leftFinished || (_rightRequested && !_rightFinished))
                    return false;
                _leftRequested = true;
                PhysicsRig.SetRecoveryLegBodyCollision(BoneMap.leftUpperLeg, BoneMap.leftLowerLeg, BoneMap.leftFoot, true);
            }
            else
            {
                if (_rightFinished || (!_leftFinished && _leftRequested))
                    return false;
                _rightRequested = true;
                PhysicsRig.SetRecoveryLegBodyCollision(BoneMap.rightUpperLeg, BoneMap.rightLowerLeg, BoneMap.rightFoot, true);
            }

            Debug.Log("[RaccoonStep] Recovery leg requested: " + (left ? "Left" : "Right") + " mouse button.", this);
            return true;
        }

        bool CanAcceptRecoveryInput()
        {
            if (Balance == null)
                return false;

            if (Character != null && Character.State == RaccoonStepState.Recovering)
                return true;

            return Balance.CanBeginRecovery;
        }

        void SuspendWalkingControllers()
        {
            if (_singleLegController != null)
            {
                _singleLegWasEnabled = _singleLegController.enabled;
                _singleLegController.enabled = false;
            }
            if (_alternatingLegController != null)
            {
                _alternatingLegWasEnabled = _alternatingLegController.enabled;
                _alternatingLegController.enabled = false;
            }
        }

        void RestoreWalkingControllers()
        {
            if (_singleLegController != null)
                _singleLegController.enabled = _singleLegWasEnabled;
            if (_alternatingLegController != null)
                _alternatingLegController.enabled = _alternatingLegWasEnabled;
            _frozenForRecovery = false;
            _leftRequested = false;
            _rightRequested = false;
            _leftProgress = 0f;
            _rightProgress = 0f;
            _leftFinished = false;
            _rightFinished = false;
            _torsoFinished = false;
            _torsoProgress = 0f;
            _sequenceProgress = 0f;
            _recoveryPhase = RecoveryPhase.None;
            if (PhysicsRig != null && BoneMap != null)
            {
                PhysicsRig.SetRecoveryLegBodyCollision(BoneMap.leftUpperLeg, BoneMap.leftLowerLeg, BoneMap.leftFoot, false);
                PhysicsRig.SetRecoveryLegBodyCollision(BoneMap.rightUpperLeg, BoneMap.rightLowerLeg, BoneMap.rightFoot, false);
            }
        }

        static float Smooth(float value)
        {
            return value * value * (3f - 2f * value);
        }

        void TickFullGetUpSequence()
        {
            _sequenceProgress = Mathf.Clamp01(_sequenceProgress + Time.fixedDeltaTime / Mathf.Max(0.1f, Duration));
            float blend = Smooth(_sequenceProgress);
            switch (_recoveryPhase)
            {
                case RecoveryPhase.Torso:
                    ApplyTorso(blend);
                    if (_sequenceProgress >= 1f)
                    {
                        _recoveryPhase = RecoveryPhase.HandSupport;
                        _sequenceProgress = 0f;
                    }
                    break;
                case RecoveryPhase.HandSupport:
                    ApplyTorsoLean(blend);
                    ApplySupportArm(blend, Vector3.zero);
                    if (_sequenceProgress >= 1f)
                    {
                        _recoveryPhase = RecoveryPhase.Squat;
                        _sequenceProgress = 0f;
                    }
                    break;
                case RecoveryPhase.Squat:
                    ApplyTorsoLean(1f);
                    ApplySupportArm(1f, transform.forward * ForwardPressDistance);
                    ApplyBothLegsToPose(SquatHipHeight, blend, true);
                    if (_sequenceProgress >= 1f)
                    {
                        Vector3 squatHips = _hipsPosition + Vector3.up * SquatHipHeight
                            + transform.forward * (ForwardPressDistance * 0.85f);
                        _left.Hip = squatHips;
                        _right.Hip = squatHips;
                        _left.StartFoot = MakeStandingFootTarget(true, true);
                        _right.StartFoot = MakeStandingFootTarget(false, true);
                        _recoveryPhase = RecoveryPhase.Stand;
                        _sequenceProgress = 0f;
                    }
                    break;
                case RecoveryPhase.Stand:
                    ApplyBothLegsToPose(StandingHipHeight, blend, false);
                    ApplyStandingTorso(blend);
                    ApplySupportArm(1f - blend, transform.forward * ForwardPressDistance);
                    if (_sequenceProgress >= 1f)
                    {
                        _recoveryPhase = RecoveryPhase.Complete;
                        _torsoFinished = true;
                    }
                    break;
            }
        }

        void ApplyTorsoLean(float blend)
        {
            Quaternion seated = Quaternion.LookRotation(transform.forward, Vector3.up);
            Quaternion lean = Quaternion.LookRotation((transform.forward * 0.78f + Vector3.up * 0.63f).normalized, Vector3.up);
            Transform hips = PhysicsRig.GetProxyFor(BoneMap.hips);
            ApplyRigidTorso(Quaternion.Slerp(seated, lean, blend), blend,
                ForwardPressDistance * blend, hips != null ? hips.position : _hipsPosition);
        }

        void ApplyStandingTorso(float blend)
        {
            Quaternion target = Quaternion.LookRotation(transform.forward, Vector3.up);
            Transform hips = PhysicsRig.GetProxyFor(BoneMap.hips);
            ApplyRigidTorso(target, blend, 0f, hips != null ? hips.position : _hipsPosition);
        }

        void ApplyRigidTorso(Quaternion targetRotation, float blend, float forwardOffset, Vector3? pivotOverride = null)
        {
            Transform spine = PhysicsRig.GetProxyFor(BoneMap.spine);
            Transform head = PhysicsRig.GetProxyFor(BoneMap.head);
            if (spine == null || head == null)
                return;

            Vector3 pivot = pivotOverride ?? _hipsPosition;
            Quaternion delta = targetRotation * Quaternion.Inverse(_spineStartRotation);
            Vector3 spineTarget = pivot + delta * (_spineStartPosition - _hipsPosition)
                + transform.forward * forwardOffset;
            Vector3 headTarget = pivot + delta * (_headStartPosition - _hipsPosition)
                + transform.forward * forwardOffset;
            PhysicsRig.DriveRecoveryPoint(spine, spineTarget,
                Quaternion.Slerp(spine.rotation, targetRotation, blend));
            PhysicsRig.DriveRecoveryPoint(head, headTarget,
                Quaternion.Slerp(head.rotation, targetRotation, blend));
        }

        void ApplySupportArm(float blend, Vector3 torsoOffset)
        {
            if (!_supportArm.IsValid)
                return;

            Vector3 target = _hipsPosition + transform.forward * SupportHandForward
                + transform.right * (UseLeftSupportHand ? -SupportHandSide : SupportHandSide);
            target.y = GroundHeight + SupportHandHeight;
            // During the push, the shoulder follows the torso while the hand
            // remains at the support point. This couples arm extension to
            // the forward COM transfer instead of moving the arm separately.
            Vector3 hip = _supportArm.Hip + torsoOffset;
            // The release end is deliberately above the old fallen pose. The
            // original hand position may already be inside the floor, so
            // returning to it would make the support hand look glued down.
            Vector3 release = _supportArm.StartHand + Vector3.up * 0.10f;
            Vector3 hand = Vector3.Lerp(release, target, blend);
            Vector3 toTarget = hand - hip;
            float distance = Mathf.Clamp(toTarget.magnitude,
                Mathf.Abs(_supportArm.UpperLength - _supportArm.LowerLength) + 0.01f,
                _supportArm.UpperLength + _supportArm.LowerLength - 0.01f);
            Vector3 direction = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.down;
            Vector3 bend = Vector3.ProjectOnPlane(Vector3.up + transform.forward, direction).normalized;
            if (bend.sqrMagnitude < 0.0001f) bend = Vector3.up;
            float cos = (_supportArm.UpperLength * _supportArm.UpperLength + distance * distance
                - _supportArm.LowerLength * _supportArm.LowerLength)
                / (2f * _supportArm.UpperLength * distance);
            cos = Mathf.Clamp(cos, -1f, 1f);
            Vector3 elbow = hip + direction * (cos * _supportArm.UpperLength)
                + bend * (Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos)) * _supportArm.UpperLength);
            PhysicsRig.DriveRecoverySegment(_supportArm.Upper, hip, elbow, _supportArm.UpperTwist);
            PhysicsRig.DriveRecoverySegment(_supportArm.Lower, elbow, hand, _supportArm.LowerTwist);
            PhysicsRig.DriveRecoveryPoint(_supportArm.Hand, hand,
                Quaternion.LookRotation(transform.forward, Vector3.up));
        }

        void ApplyBothLegsToPose(float hipHeight, float blend, bool squat)
        {
            Vector3 targetHips = _hipsPosition + Vector3.up * hipHeight
                + transform.forward * (squat ? ForwardPressDistance * 0.85f : 0f);
            Transform hips = PhysicsRig.GetProxyFor(BoneMap.hips);
            if (hips != null)
            {
                // Move the actual pelvis proxy together with the leg chain.
                // Leaving it at the fallen height while moving the spine was
                // the source of the severe skin stretch.
                Vector3 hipStart = squat ? _hipsPosition : hips.position;
                PhysicsRig.DriveRecoveryPoint(hips, Vector3.Lerp(hipStart, targetHips, blend), hips.rotation);
            }
            ApplyLegPose(_left, targetHips, MakeStandingFootTarget(true, squat), blend, false);
            ApplyLegPose(_right, targetHips, MakeStandingFootTarget(false, squat), blend, false);
        }

        Vector3 MakeStandingFootTarget(bool left, bool squat)
        {
            Vector3 target = _hipsPosition + transform.forward * (squat ? 0.16f : 0.08f)
                + transform.right * (left ? -SeatedFootSide : SeatedFootSide);
            target.y = GroundHeight + SeatedFootHeight;
            return target;
        }

        void ApplyLegPose(LegTarget leg, Vector3 targetHip, Vector3 targetFoot, float blend, bool constrainToPlane)
        {
            if (!leg.IsValid) return;
            Vector3 hip = Vector3.Lerp(leg.Hip, targetHip, blend);
            Vector3 ankle = Vector3.Lerp(leg.StartFoot, targetFoot, blend);
            ankle.y = Mathf.Max(GroundHeight + SeatedFootHeight, ankle.y);
            Vector3 toTarget = ankle - hip;
            float distance = Mathf.Clamp(toTarget.magnitude,
                Mathf.Abs(leg.UpperLength - leg.LowerLength) + 0.01f,
                leg.UpperLength + leg.LowerLength - 0.01f);
            Vector3 direction = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.down;
            Vector3 pole = transform.forward * (1f + KneeForwardBias) + Vector3.up * (KneeUpBias + KneeLiftHeight);
            Vector3 bend = Vector3.ProjectOnPlane(pole, direction).normalized;
            if (bend.sqrMagnitude < 0.0001f) bend = Vector3.up;
            float cos = (leg.UpperLength * leg.UpperLength + distance * distance
                - leg.LowerLength * leg.LowerLength) / (2f * leg.UpperLength * distance);
            cos = Mathf.Clamp(cos, -1f, 1f);
            Vector3 knee = hip + direction * (cos * leg.UpperLength)
                + bend * (Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos)) * leg.UpperLength);
            PhysicsRig.DriveRecoverySegment(leg.Upper, hip, knee, leg.UpperTwist);
            PhysicsRig.DriveRecoverySegment(leg.Lower, knee, ankle, leg.LowerTwist);
            PhysicsRig.DriveRecoveryPoint(leg.Foot, ankle,
                Quaternion.LookRotation(transform.forward, Vector3.up));
        }

        bool BuildTargets()
        {
            _left = BuildLeg(BoneMap.leftUpperLeg, BoneMap.leftLowerLeg, BoneMap.leftFoot, true);
            _right = BuildLeg(BoneMap.rightUpperLeg, BoneMap.rightLowerLeg, BoneMap.rightFoot, false);
            return _left.IsValid && _right.IsValid;
        }

        LegTarget BuildLeg(Transform upper, Transform lower, Transform foot, bool left)
        {
            LegTarget result = new LegTarget();
            if (upper == null || lower == null || foot == null)
                return result;

            result.Upper = PhysicsRig.GetProxyFor(upper);
            result.Lower = PhysicsRig.GetProxyFor(lower);
            result.Foot = PhysicsRig.GetProxyFor(foot);
            if (result.Upper == null || result.Lower == null || result.Foot == null)
                return result;

            result.Hip = upper.position;
            result.StartFoot = foot.position;
            result.TargetFoot = MakeSeatedFootTarget(left);
            result.UpperLength = Vector3.Distance(upper.position, lower.position);
            result.LowerLength = Vector3.Distance(lower.position, foot.position);
            result.UpperTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (lower.position - upper.position).normalized)) * result.Upper.rotation;
            result.LowerTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (foot.position - lower.position).normalized)) * result.Lower.rotation;
            result.IsValid = true;
            return result;
        }

        ArmTarget BuildArm(Transform upper, Transform lower, Transform hand)
        {
            ArmTarget result = new ArmTarget();
            if (upper == null || lower == null || hand == null || PhysicsRig == null)
                return result;
            result.Upper = PhysicsRig.GetProxyFor(upper);
            result.Lower = PhysicsRig.GetProxyFor(lower);
            result.Hand = PhysicsRig.GetProxyFor(hand);
            if (result.Upper == null || result.Lower == null || result.Hand == null)
                return result;
            result.Hip = upper.position;
            result.StartHand = hand.position;
            result.UpperLength = Vector3.Distance(upper.position, lower.position);
            result.LowerLength = Vector3.Distance(lower.position, hand.position);
            result.UpperTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (lower.position - upper.position).normalized)) * result.Upper.rotation;
            result.LowerTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (hand.position - lower.position).normalized)) * result.Lower.rotation;
            result.IsValid = true;
            return result;
        }

        void ApplyLeg(LegTarget leg, float blend, bool constrainToVerticalPlane)
        {
            if (!leg.IsValid)
                return;

            Vector3 hip = Vector3.Lerp(leg.Hip, _hipsPosition + (leg.Hip - _hipsPosition), blend);
            Vector3 ankle = Vector3.Lerp(leg.StartFoot, leg.TargetFoot, blend);
            if (constrainToVerticalPlane)
            {
                // The virtual walls are parallel to the character's forward
                // and world-up axes.  They remove only the lateral degree of
                // freedom, so the leg can demonstrate a genuine upward hip
                // flexion in one vertical plane.
                Vector3 wallNormal = transform.right;
                float lateral = Vector3.Dot(ankle - leg.Hip, wallNormal);
                ankle -= wallNormal * lateral;
                hip -= wallNormal * Vector3.Dot(hip - leg.Hip, wallNormal);
            }
            float floorY = GroundHeight + SeatedFootHeight;
            ankle.y = Mathf.Max(floorY, ankle.y);
            Vector3 toTarget = ankle - hip;
            float maxBend = Mathf.Clamp(MaxKneeBendDegrees, 20f, 170f) * Mathf.Deg2Rad;
            float minByAngle = Mathf.Sqrt(Mathf.Max(0.0001f,
                leg.UpperLength * leg.UpperLength + leg.LowerLength * leg.LowerLength
                - 2f * leg.UpperLength * leg.LowerLength * Mathf.Cos(maxBend)));
            float distance = Mathf.Clamp(toTarget.magnitude,
                Mathf.Max(Mathf.Abs(leg.UpperLength - leg.LowerLength) + 0.01f,
                    minByAngle + KneeBodyClearance),
                Mathf.Max(0.02f, leg.UpperLength + leg.LowerLength - 0.01f));
            Vector3 direction = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.down;
            // The pelvis is locked in translation, but the hip is not locked:
            // the upper-leg direction is rebuilt every physics tick from a
            // moving knee pole.  A sin-shaped lift makes the thigh visibly
            // flex upward first, then lets the foot settle toward the floor.
            // Using world-up here is deliberate because the character may be
            // lying on its back or front and its local up is then unreliable.
            float kneeLift = Mathf.Sin(Mathf.PI * Mathf.Clamp01(blend)) * Mathf.Max(0f, KneeLiftHeight);
            Vector3 pole = transform.forward * (1f + KneeForwardBias)
                + Vector3.up * (KneeUpBias + kneeLift * 4f);
            Vector3 bend = Vector3.ProjectOnPlane(pole, direction).normalized;
            if (bend.sqrMagnitude < 0.0001f) bend = Vector3.up;

            float cosKnee = (leg.UpperLength * leg.UpperLength + distance * distance
                - leg.LowerLength * leg.LowerLength) / (2f * leg.UpperLength * distance);
            cosKnee = Mathf.Clamp(cosKnee, -1f, 1f);
            float sinKnee = Mathf.Sqrt(Mathf.Max(0f, 1f - cosKnee * cosKnee));
            Vector3 knee = hip + direction * (cosKnee * leg.UpperLength)
                + bend * (sinKnee * leg.UpperLength);

            Vector3 fromBody = knee - _hipsPosition;
            if (fromBody.sqrMagnitude < KneeBodyClearance * KneeBodyClearance)
                knee = _hipsPosition + (fromBody.sqrMagnitude > 0.000001f
                    ? fromBody.normalized : bend) * KneeBodyClearance;

            PhysicsRig.DriveRecoverySegment(leg.Upper, hip, knee, leg.UpperTwist);
            PhysicsRig.DriveRecoverySegment(leg.Lower, knee, ankle, leg.LowerTwist);
            PhysicsRig.DriveRecoveryPoint(leg.Foot, ankle,
                Quaternion.LookRotation(transform.forward, Vector3.up));
        }

        void RefreshLegFromCurrentPose(ref LegTarget leg, Transform upper, Transform lower, Transform foot, bool left)
        {
            if (upper == null || lower == null || foot == null)
                return;

            leg.Upper = PhysicsRig.GetProxyFor(upper);
            leg.Lower = PhysicsRig.GetProxyFor(lower);
            leg.Foot = PhysicsRig.GetProxyFor(foot);
            leg.Hip = upper.position;
            leg.StartFoot = foot.position;
            leg.TargetFoot = MakeSeatedFootTarget(left);
            leg.UpperLength = Vector3.Distance(upper.position, lower.position);
            leg.LowerLength = Vector3.Distance(lower.position, foot.position);
            leg.UpperTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (lower.position - upper.position).normalized)) * leg.Upper.rotation;
            leg.LowerTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (foot.position - lower.position).normalized)) * leg.Lower.rotation;
            leg.IsValid = leg.Upper != null && leg.Lower != null && leg.Foot != null;
        }

        Vector3 MakeSeatedFootTarget(bool left)
        {
            // The pelvis is the fixed mechanical reference. Using the root
            // transform here made the recovered foot target too far away for
            // this character's proportions and left the knee nearly straight.
            Vector3 target = _hipsPosition
                + transform.forward * SeatedFootForward
                + transform.right * (left ? -SeatedFootSide : SeatedFootSide);
            Vector3 horizontal = Vector3.ProjectOnPlane(target - _hipsPosition, Vector3.up);
            float minimumDistance = Mathf.Max(0.05f, MinimumFootPelvisDistance);
            if (horizontal.sqrMagnitude < minimumDistance * minimumDistance)
            {
                horizontal = horizontal.sqrMagnitude > 0.000001f
                    ? horizontal.normalized * minimumDistance
                    : transform.forward * minimumDistance;
                target = _hipsPosition + horizontal;
            }
            target.y = GroundHeight + SeatedFootHeight;
            return target;
        }

        void ApplyTorso(float blend)
        {
            Transform spine = PhysicsRig.GetProxyFor(BoneMap.spine);
            Transform head = PhysicsRig.GetProxyFor(BoneMap.head);
            if (spine == null || head == null)
                return;

            Quaternion seatedRotation = Quaternion.LookRotation(transform.forward, Vector3.up);
            Quaternion torsoDelta = seatedRotation * Quaternion.Inverse(_spineStartRotation);
            Vector3 spineTarget = _hipsPosition + torsoDelta * (_spineStartPosition - _hipsPosition);
            Vector3 headTarget = _hipsPosition + torsoDelta * (_headStartPosition - _hipsPosition);

            // No waist joint: spine and head are rotated as one rigid block
            // around the pelvis, preserving their spacing while sitting up.
            PhysicsRig.DriveRecoveryPoint(spine, Vector3.Lerp(_spineStartPosition, spineTarget, blend),
                Quaternion.Slerp(_spineStartRotation, seatedRotation, blend));
            PhysicsRig.DriveRecoveryPoint(head, Vector3.Lerp(_headStartPosition, headTarget, blend),
                Quaternion.Slerp(_headStartRotation, seatedRotation, blend));
        }

        struct LegTarget
        {
            public Transform Upper;
            public Transform Lower;
            public Transform Foot;
            public Vector3 Hip;
            public Vector3 StartFoot;
            public Vector3 TargetFoot;
            public float UpperLength;
            public float LowerLength;
            public Quaternion UpperTwist;
            public Quaternion LowerTwist;
            public bool IsValid;
        }

        enum RecoveryPhase
        {
            None,
            ManualLegs,
            Torso,
            HandSupport,
            Squat,
            Stand,
            Complete
        }

        struct ArmTarget
        {
            public Transform Upper;
            public Transform Lower;
            public Transform Hand;
            public Vector3 Hip;
            public Vector3 StartHand;
            public float UpperLength;
            public float LowerLength;
            public Quaternion UpperTwist;
            public Quaternion LowerTwist;
            public bool IsValid;
        }
    }
}
