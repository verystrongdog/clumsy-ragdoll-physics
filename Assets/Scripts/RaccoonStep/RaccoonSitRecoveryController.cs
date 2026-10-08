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
        [Tooltip("After the torso sits up, automatically perform side lean, hand support, sequential leg recovery, and stand.")]
        public bool EnableFullGetUpSequence = true;
        [Tooltip("Stop after the first ragdoll torso sit-up. No hand, leg, or standing phase is started.")]
        public bool TorsoOnlyRecovery = true;
        [Tooltip("Run only torso sit-up followed by the side-lean phase. Leg recovery is not started.")]
        public bool SideLeanOnlyRecovery = false;
        [Tooltip("After sitting, enable direct WASD control of the upper-body rigidbodies. The pelvis stays fixed.")]
        public bool EnableSeatedWasdControl = true;
        public float SeatedWasdSideDistance = 0.22f;
        public float SeatedWasdForwardDistance = 0.18f;
        public float SeatedWasdLeanDegrees = 28f;
        [Tooltip("In torso-only mode, keep the completed seated pose after the mouse is released.")]
        public bool HoldSeatedPoseAfterRelease = true;
        [Tooltip("How strongly the torso leans toward the ground point clicked by the mouse while being pulled up.")]
        public float MousePullLean = 0.65f;
        [Tooltip("The spine-to-ground 90 degree posture is considered seated when its axis is within this many degrees of vertical.")]
        public float TorsoUprightTolerance = 12f;
        [Tooltip("Duration of the residual pull after a short mouse click.")]
        public float ShortPressForceDuration = 0.42f;
        public bool UseLeftSupportHand = true;
        public float SupportHandForward = 0.22f;
        public float SupportHandSide = 0.18f;
        public float SupportHandHeight = 0.035f;
        [Tooltip("How far the free hand reaches away from the support-hand side during the sit-up.")]
        public float BalanceHandReach = 0.34f;
        [Tooltip("Forward reach of the free hand used to counterbalance the sit-up.")]
        public float BalanceHandForward = 0.20f;
        public float BalanceHandHeight = 0.16f;
        [Tooltip("Strength of the torso position assist during the supported sit-up. Lower values make the hands and contacts contribute more visibly.")]
        public float SupportedTorsoPositionStrength = 0.55f;
        [Tooltip("Sideways center-of-mass shift toward the support hand before the first leg is loaded.")]
        public float SideLeanDistance = 0.14f;
        [Tooltip("Roll angle toward the support hand during the side-lean phase.")]
        public float SideLeanDegrees = 22f;
        [Tooltip("Position-drive strength used only to move the pelvis into the side-support base.")]
        public float SideLeanPositionStrength = 1f;
        [Tooltip("Height of the pelvis while the first leg is brought underneath the body.")]
        public float SingleLegSupportHipHeight = 0.34f;
        [Tooltip("The first leg to come underneath the body. Set false for the right leg.")]
        public bool FirstLegIsLeft = false;
        [Tooltip("Stop after both feet are planted and the body has reached a squat. Stand-up is implemented separately.")]
        public bool StopAtSquat = true;
        public bool HoldSquatPoseAfterRelease = true;
        [Tooltip("A single click starts the full staged recovery; releasing the mouse does not interrupt the active sequence.")]
        public bool AutoAdvanceFullGetUpAfterClick = true;
        public float ForwardPressDistance = 0.18f;
        public float SquatHipHeight = 0.28f;
        public float StandingHipHeight = 0.62f;

        public RaccoonPostureState PostureState { get; private set; }
        public bool IsSeatedRecoveryComplete
        {
            get { return _frozenForRecovery
                && _recoveryPhase == RaccoonRecoveryPhase.Complete
                && TorsoOnlyRecovery && !SideLeanOnlyRecovery; }
        }

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
        RaccoonRecoveryPhase _recoveryPhase;
        readonly RaccoonRecoveryStateMachine _recoveryStateMachine = new RaccoonRecoveryStateMachine();
        bool _torsoDriveLogged;
        float _torsoDiagnosticTimer;
        Vector3 _recoveryPullDirection;
        Vector3 _recoveryFacing;
        bool _pullButtonHeld;
        float _shortPressForceRemaining;
        Vector3 _hipsPosition;
        Quaternion _spineStartRotation;
        Quaternion _headStartRotation;
        Vector3 _spineStartAxis;
        Vector3 _headStartAxis;
        Vector3 _spineStartPosition;
        Vector3 _headStartPosition;
        LegTarget _left;
        LegTarget _right;
        ArmTarget _supportArm;
        ArmTarget _balanceArm;
        Vector2 _lastSeatedWasdInput;

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

            if (!_ready && BoneMap != null && PhysicsRig != null)
            {
                BoneMap.BuildFrom(transform);
                _ready = BuildTargets();
            }

            if (!EnableInput || !_ready)
                return;

            if (IsSeatedRecoveryComplete && Input.GetMouseButtonDown(0))
            {
                RaccoonSeatedLegRetractionExperiment experiment =
                    GetComponent<RaccoonSeatedLegRetractionExperiment>();
                if (experiment != null && experiment.Begin(true))
                {
                    Debug.Log("[RaccoonStep] Second-click left leg retraction requested.", this);
                    return;
                }
            }

            if (Input.GetMouseButtonDown(0) && BeginLegRecovery(true))
            {
                _pullButtonHeld = true;
                _shortPressForceRemaining = ShortPressForceDuration;
            }
            if (Input.GetMouseButtonDown(1) && BeginLegRecovery(false))
            {
                _pullButtonHeld = true;
                _shortPressForceRemaining = ShortPressForceDuration;
            }
            if (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1))
            {
                _pullButtonHeld = Input.GetMouseButton(0) || Input.GetMouseButton(1);
                if (!_pullButtonHeld && _frozenForRecovery
                    && !ShouldHoldCompletedPose() && !ShouldAutoAdvanceFullGetUp())
                {
                    // Releasing the mouse must release the recovery drive;
                    // the last joint target must not keep applying torque.
                    PhysicsRig.SetRecoverySpineDrive(false);
                    PhysicsRig.SetRecoveryGravity(true);
                }
            }
        }

        void FixedUpdate()
        {
            PostureState = RaccoonPostureStateDetector.Detect(
                Character != null ? Character.State : RaccoonStepState.Stable,
                Balance != null && Balance.IsFalling,
                _frozenForRecovery,
                Balance != null && Balance.CanBeginRecovery);

            if (!_frozenForRecovery)
                return;

            bool holdCompletedSeatedPose = ShouldHoldCompletedPose();
            if (!_pullButtonHeld && !holdCompletedSeatedPose
                && !ShouldAutoAdvanceFullGetUp())
            {
                PhysicsRig.SetRecoverySpineDrive(false);
                PhysicsRig.SetRecoveryGravity(true);
                return;
            }
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
            // Recovery is intentionally one-shot for this test. A second
            // click must not reuse a partially recovered ragdoll, stale
            // start-pose samples, or an existing recovery joint. Press R to
            // reset the fall and start a new clean attempt.
            if (!_ready || PhysicsRig == null || _frozenForRecovery
                || !CanAcceptRecoveryInput())
                return false;

            _recoveryPullDirection = GetMousePullDirection();

            SuspendWalkingControllers();
            if (Balance != null && Balance.CanBeginRecovery)
            {
                if (!Balance.PrepareForRecovery())
                    return false;
            }
            else if (!PhysicsRig.FreezeCurrentPoseForRecovery())
                return false;

            // Keep every proxy dynamic, but remove gravity while the torso is
            // extracted from the floor contact.
            PhysicsRig.SetRecoveryGravity(false);

            _hipsPosition = PhysicsRig.GetProxyFor(BoneMap.hips).position;
            Transform spine = PhysicsRig.GetProxyFor(BoneMap.spine);
            Transform head = PhysicsRig.GetProxyFor(BoneMap.head);
            if (spine == null || head == null)
                return false;

                _spineStartPosition = spine.position;
                _headStartPosition = head.position;
                _spineStartRotation = spine.rotation;
                _headStartRotation = head.rotation;
                _spineStartAxis = spine.up;
                _headStartAxis = head.up;
                _recoveryFacing = ResolveRecoveryFacing(spine);
                PhysicsRig.SetRecoveryTorsoMotion(spine, true);
                // Keep the head-neck joint constrained. The head is allowed
                // to follow the torso through its normal angular limits, but
                // must not become a free rigidbody during recovery.
                PhysicsRig.SetRecoveryTorsoMotion(head, false);
                // The arms are still connected to the spine and their normal
                // +/-35 degree joint limits can feed a large counter-torque
                // back into the torso while the character is lying down.
                // Release the upper-body chain during the initial sit-up;
                // hand support is imposed explicitly in the next phase.
                PhysicsRig.SetRecoveryTorsoMotion(BoneMap.leftUpperArm, true);
                PhysicsRig.SetRecoveryTorsoMotion(BoneMap.leftLowerArm, true);
                PhysicsRig.SetRecoveryTorsoMotion(BoneMap.leftHand, true);
                PhysicsRig.SetRecoveryTorsoMotion(BoneMap.rightUpperArm, true);
                PhysicsRig.SetRecoveryTorsoMotion(BoneMap.rightLowerArm, true);
                PhysicsRig.SetRecoveryTorsoMotion(BoneMap.rightHand, true);
                PhysicsRig.SetRecoveryJointDrives(false);
                // SetRecoveryJointDrives(false) intentionally releases the
                // torso chain. Restore only the head-neck joint's normal
                // limits and damping so the head cannot swing independently.
                PhysicsRig.SetRecoveryTorsoMotion(head, false);
                PhysicsRig.BeginRecoverySpineJoint(spine, PhysicsRig.GetProxyFor(BoneMap.hips));
                _frozenForRecovery = true;
                _torsoProgress = 0f;
                _torsoFinished = false;
                _sequenceProgress = 0f;
                SetRecoveryPhase(EnableFullGetUpSequence
                    ? RaccoonRecoveryPhase.Torso : RaccoonRecoveryPhase.ManualLegs);
                _torsoDriveLogged = false;
                _torsoDiagnosticTimer = 0f;

                RefreshLegFromCurrentPose(ref _left, BoneMap.leftUpperLeg, BoneMap.leftLowerLeg, BoneMap.leftFoot, true);
                RefreshLegFromCurrentPose(ref _right, BoneMap.rightUpperLeg, BoneMap.rightLowerLeg, BoneMap.rightFoot, false);
                _supportArm = BuildArm(
                    UseLeftSupportHand ? BoneMap.leftUpperArm : BoneMap.rightUpperArm,
                    UseLeftSupportHand ? BoneMap.leftLowerArm : BoneMap.rightLowerArm,
                    UseLeftSupportHand ? BoneMap.leftHand : BoneMap.rightHand);
                _balanceArm = BuildArm(
                    UseLeftSupportHand ? BoneMap.rightUpperArm : BoneMap.leftUpperArm,
                    UseLeftSupportHand ? BoneMap.rightLowerArm : BoneMap.leftLowerArm,
                    UseLeftSupportHand ? BoneMap.rightHand : BoneMap.leftHand);

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
                Debug.Log("[RaccoonStep] One-shot torso sit-up started. Press R to reset before another attempt.", this);
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

            PostureState = RaccoonPostureStateDetector.Detect(
                Character != null ? Character.State : RaccoonStepState.Stable,
                Balance.IsFalling, _frozenForRecovery, Balance.CanBeginRecovery);
            if (PostureState == RaccoonPostureState.Recovering)
                return true;

            return PostureState == RaccoonPostureState.Fallen;
        }

        public void ResetRecovery()
        {
            RestoreWalkingControllers();
        }

        void SetRecoveryPhase(RaccoonRecoveryPhase phase)
        {
            if (_recoveryPhase != phase)
                Debug.Log("[RaccoonStep] Recovery phase: " + _recoveryPhase + " -> " + phase, this);
            _recoveryPhase = phase;
            if (phase == RaccoonRecoveryPhase.None)
                _recoveryStateMachine.Reset();
            else
                _recoveryStateMachine.SetPhase(phase);
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
                _singleLegController.enabled = _frozenForRecovery ? _singleLegWasEnabled : true;
            if (_alternatingLegController != null)
                _alternatingLegController.enabled = _frozenForRecovery ? _alternatingLegWasEnabled : true;
            if (PhysicsRig != null)
                PhysicsRig.ResetToInitialPose();
            if (Balance != null)
                Balance.ResetAfterFall();
            if (Character != null)
                Character.State = RaccoonStepState.Stable;
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
            SetRecoveryPhase(RaccoonRecoveryPhase.None);
            PostureState = RaccoonPostureState.Standing;
            _pullButtonHeld = false;
            _shortPressForceRemaining = 0f;
            if (PhysicsRig != null && BoneMap != null)
            {
                PhysicsRig.EndRecoverySpineJoint();
                PhysicsRig.SetRecoveryGravity(true);
                PhysicsRig.SetRecoveryJointDrives(true);
                PhysicsRig.SetRecoveryTorsoMotion(BoneMap.spine, false);
                PhysicsRig.SetRecoveryTorsoMotion(BoneMap.head, false);
                PhysicsRig.SetRecoveryLegMotion(BoneMap.leftUpperLeg,
                    BoneMap.leftLowerLeg, BoneMap.leftFoot, false);
                PhysicsRig.SetRecoveryLegMotion(BoneMap.rightUpperLeg,
                    BoneMap.rightLowerLeg, BoneMap.rightFoot, false);
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
                case RaccoonRecoveryPhase.Torso:
                    // Keep sampling the cursor while the torso is still
                    // rising. The click starts recovery; the live cursor
                    // direction continuously steers the pulling force.
                    _recoveryPullDirection = GetMousePullDirection();
                    // A click starts recovery, but the torso must remain
                    // actively driven for the whole torso phase. Previously
                    // this fell to zero after ShortPressForceDuration, which
                    // left the spine parked around the intermediate ~45 deg
                    // pose when the mouse button was released.
                    float pulseStrength = 1f;
                    _shortPressForceRemaining = Mathf.Max(0f,
                        _shortPressForceRemaining - Time.fixedDeltaTime);
                    if (!SideLeanOnlyRecovery)
                    {
                        // Restore the stable supported-sit baseline: the
                        // near hand, balancing hand and folded legs assist the
                        // torso while it rises. WASD is only evaluated after
                        // this phase reaches Complete.
                        ApplySupportArm(blend, Vector3.zero);
                        ApplyBalanceArm(blend);
                        float legBlend = Smooth(Mathf.Clamp01(blend * 0.9f));
                        ApplyLegPose(_left, _hipsPosition, _left.TargetFoot, legBlend, false);
                        ApplyLegPose(_right, _hipsPosition, _right.TargetFoot, legBlend, false);
                    }
                    ApplyTorso(blend, pulseStrength);
                    // A noisy contact/joint angle must not block all later
                    // phases. The torso drive continues correcting the angle
                    // while the state machine proceeds.
                    if (_sequenceProgress >= 0.9f)
                    {
                        SetRecoveryPhase(TorsoOnlyRecovery && !SideLeanOnlyRecovery
                            ? RaccoonRecoveryPhase.Complete : RaccoonRecoveryPhase.SideLean);
                        _sequenceProgress = 0f;
                        _torsoFinished = true;
                        if (TorsoOnlyRecovery && !SideLeanOnlyRecovery)
                            PhysicsRig.SetRecoveryGravity(true);
                    }
                    break;
                case RaccoonRecoveryPhase.SideLean:
                    PhysicsRig.SetRecoveryGravity(true);
                    ApplySideLean(blend);
                    if (!SideLeanOnlyRecovery)
                        ApplySupportArm(blend, SideSupportOffset(blend));
                    if (_sequenceProgress >= 1f)
                    {
                        SetRecoveryPhase(SideLeanOnlyRecovery
                            ? RaccoonRecoveryPhase.Complete : RaccoonRecoveryPhase.HandSupport);
                        _sequenceProgress = 0f;
                    }
                    break;
                case RaccoonRecoveryPhase.HandSupport:
                    PhysicsRig.SetRecoveryGravity(true);
                    ApplySideLean(1f);
                    ApplySupportArm(blend, SideSupportOffset(1f));
                    if (_sequenceProgress >= 1f)
                    {
                        SetRecoveryLegCollision(FirstLegIsLeftForSupport());
                        SetRecoveryPhase(RaccoonRecoveryPhase.FirstLeg);
                        _sequenceProgress = 0f;
                    }
                    break;
                case RaccoonRecoveryPhase.FirstLeg:
                    PhysicsRig.SetRecoveryGravity(true);
                    ApplySideLean(1f);
                    ApplySupportArm(1f, SideSupportOffset(1f) + transform.forward * ForwardPressDistance * blend);
                    ApplyFirstLegToPose(blend);
                    if (_sequenceProgress >= 1f)
                    {
                        Vector3 squatHips = _hipsPosition + Vector3.up * SingleLegSupportHipHeight
                            + SupportSideVector()
                            + transform.forward * (ForwardPressDistance * 0.85f);
                        _left.Hip = squatHips;
                        _right.Hip = squatHips;
                        SetRecoveryPhase(RaccoonRecoveryPhase.SecondLeg);
                        _sequenceProgress = 0f;
                    }
                    break;
                case RaccoonRecoveryPhase.SecondLeg:
                    PhysicsRig.SetRecoveryGravity(true);
                    ApplySideLean(Mathf.Lerp(1f, 0f, blend));
                    ApplySupportArm(1f - blend, SideSupportOffset(1f) + transform.forward * ForwardPressDistance);
                    ApplyBothLegsToPose(SquatHipHeight, blend, true);
                    if (_sequenceProgress >= 1f)
                    {
                        SetRecoveryLegCollision(!FirstLegIsLeftForSupport());
                        Vector3 squatHips = _hipsPosition + Vector3.up * SquatHipHeight
                            + transform.forward * (ForwardPressDistance * 0.85f);
                        _left.Hip = squatHips;
                        _right.Hip = squatHips;
                        _left.StartFoot = MakeStandingFootTarget(true, true);
                        _right.StartFoot = MakeStandingFootTarget(false, true);
                        SetRecoveryPhase(StopAtSquat
                            ? RaccoonRecoveryPhase.Squat : RaccoonRecoveryPhase.Stand);
                        _sequenceProgress = 0f;
                    }
                    break;
                case RaccoonRecoveryPhase.Squat:
                    PhysicsRig.SetRecoveryGravity(true);
                    // Both feet remain planted under the pelvis while the
                    // support hand is released. This is the requested end
                    // state before a separate stand-up phase is introduced.
                    ApplyBothLegsToPose(SquatHipHeight, 1f, true);
                    ApplySupportArm(0f, transform.forward * ForwardPressDistance);
                    if (!StopAtSquat)
                    {
                        SetRecoveryPhase(RaccoonRecoveryPhase.Stand);
                        _sequenceProgress = 0f;
                    }
                    break;
                case RaccoonRecoveryPhase.Stand:
                    PhysicsRig.SetRecoveryGravity(true);
                    ApplyBothLegsToPose(StandingHipHeight, blend, false);
                    ApplyStandingTorso(blend);
                    ApplySupportArm(1f - blend, transform.forward * ForwardPressDistance);
                    if (_sequenceProgress >= 1f)
                    {
                        SetRecoveryPhase(RaccoonRecoveryPhase.Complete);
                        _torsoFinished = true;
                        PhysicsRig.SetRecoverySpineDrive(false);
                        PhysicsRig.SetRecoveryGravity(true);
                    }
                    break;
                case RaccoonRecoveryPhase.Complete:
                    if (TorsoOnlyRecovery && !SideLeanOnlyRecovery)
                    {
                        // Keep the seated pose stable only while the mouse is
                        // held. FixedUpdate exits earlier and disables this
                        // drive immediately when the button is released.
                        PhysicsRig.SetRecoverySpineDrive(true);
                        PhysicsRig.SetRecoveryGravity(true);
                        ApplyTorso(1f);
                        // Once the torso has reached the seated posture, the
                        // planted hand and balancing arm are released back
                        // toward the body instead of remaining glued to the
                        // floor or held in a balancing pose.
                        ApplySupportArm(0f, Vector3.zero);
                        ApplyBalanceArm(0f);
                        ApplySeatedWasdControl();
                    }
                    else if (SideLeanOnlyRecovery)
                    {
                        PhysicsRig.SetRecoverySpineDrive(true);
                        PhysicsRig.SetRecoveryGravity(true);
                        ApplySideLean(1f);
                    }
                    else
                    {
                        PhysicsRig.SetRecoverySpineDrive(false);
                        PhysicsRig.SetRecoveryGravity(true);
                    }
                    break;
            }
        }

        void ApplySeatedWasdControl()
        {
            if (!EnableSeatedWasdControl)
                return;

            Vector2 input = Vector2.zero;
            if (Input.GetKey(KeyCode.A)) input.x -= 1f;
            if (Input.GetKey(KeyCode.D)) input.x += 1f;
            if (Input.GetKey(KeyCode.S)) input.y -= 1f;
            if (Input.GetKey(KeyCode.W)) input.y += 1f;
            if (input.sqrMagnitude > 1f)
                input.Normalize();

            if ((input - _lastSeatedWasdInput).sqrMagnitude > 0.0001f)
            {
                Debug.Log("[RaccoonStep] Seated WASD input: " + input, this);
                _lastSeatedWasdInput = input;
            }

            Transform spine = PhysicsRig.GetProxyFor(BoneMap.spine);
            Transform head = PhysicsRig.GetProxyFor(BoneMap.head);
            if (spine == null || head == null)
                return;

            Quaternion seated = RaccoonRecoveryPosePlanner.SeatedTorsoRotation(transform);
            Vector3 offset = transform.right * (input.x * SeatedWasdSideDistance)
                + transform.forward * (input.y * SeatedWasdForwardDistance);
            Quaternion sideTilt = Quaternion.AngleAxis(-input.x * SeatedWasdLeanDegrees,
                transform.forward);
            Quaternion forwardTilt = Quaternion.AngleAxis(input.y * SeatedWasdLeanDegrees,
                transform.right);
            Quaternion targetRotation = sideTilt * forwardTilt * seated;
            Quaternion delta = seated * Quaternion.Inverse(_spineStartRotation);
            Vector3 spineBase = _hipsPosition + delta * (_spineStartPosition - _hipsPosition);
            Vector3 headBase = _hipsPosition + delta * (_headStartPosition - _hipsPosition);

            // Only spine and head are moved here. The pelvis remains the
            // seated anchor, making the rigidbody shift visible in the debug
            // rig rather than moving the entire character root.
            PhysicsRig.DriveRecoverySpineJoint(targetRotation, 1f);
            PhysicsRig.DriveRecoveryPointScaled(spine, spineBase + offset,
                targetRotation, 0.8f);
            PhysicsRig.DriveRecoveryPointScaled(head, headBase + offset,
                targetRotation, 0.8f);
        }

        Vector3 SupportSideVector()
        {
            return transform.right * (UseLeftSupportHand ? -SideLeanDistance : SideLeanDistance);
        }

        bool FirstLegIsLeftForSupport()
        {
            // The first leg is always opposite the planted support hand.
            return !UseLeftSupportHand;
        }

        void SetRecoveryLegCollision(bool left)
        {
            if (left)
            {
                PhysicsRig.SetRecoveryLegMotion(BoneMap.leftUpperLeg,
                    BoneMap.leftLowerLeg, BoneMap.leftFoot, true);
                PhysicsRig.SetRecoveryLegBodyCollision(BoneMap.leftUpperLeg,
                    BoneMap.leftLowerLeg, BoneMap.leftFoot, true);
            }
            else
            {
                PhysicsRig.SetRecoveryLegMotion(BoneMap.rightUpperLeg,
                    BoneMap.rightLowerLeg, BoneMap.rightFoot, true);
                PhysicsRig.SetRecoveryLegBodyCollision(BoneMap.rightUpperLeg,
                    BoneMap.rightLowerLeg, BoneMap.rightFoot, true);
            }
        }

        bool ShouldHoldCompletedPose()
        {
            return ((TorsoOnlyRecovery || SideLeanOnlyRecovery) && HoldSeatedPoseAfterRelease
                    && _recoveryPhase == RaccoonRecoveryPhase.Complete)
                || (StopAtSquat && HoldSquatPoseAfterRelease
                    && _recoveryPhase == RaccoonRecoveryPhase.Squat);
        }

        bool ShouldAutoAdvanceFullGetUp()
        {
            return AutoAdvanceFullGetUpAfterClick && EnableFullGetUpSequence
                && !TorsoOnlyRecovery && _frozenForRecovery
                && _recoveryPhase != RaccoonRecoveryPhase.None
                && _recoveryPhase != RaccoonRecoveryPhase.Complete;
        }

        Vector3 SideSupportOffset(float blend)
        {
            return SupportSideVector() * Mathf.Clamp01(blend)
                + transform.forward * (ForwardPressDistance * 0.25f * Mathf.Clamp01(blend));
        }

        void ApplySideLean(float blend)
        {
            Quaternion seated = RaccoonRecoveryPosePlanner.SeatedTorsoRotation(transform);
            float sign = UseLeftSupportHand ? 1f : -1f;
            Quaternion sideLean = Quaternion.AngleAxis(sign * SideLeanDegrees, transform.forward) * seated;
            Vector3 pivot = _hipsPosition + SupportSideVector() * Mathf.Clamp01(blend);
            ApplyRigidTorso(Quaternion.Slerp(seated, sideLean, Mathf.Clamp01(blend)), blend,
                ForwardPressDistance * 0.25f * Mathf.Clamp01(blend), pivot);
            Transform hips = PhysicsRig.GetProxyFor(BoneMap.hips);
            if (hips != null)
                PhysicsRig.DriveRecoveryPointScaled(hips, pivot, hips.rotation,
                    Mathf.Clamp01(SideLeanPositionStrength));
        }

        void ApplyFirstLegToPose(float blend)
        {
            float t = Mathf.Clamp01(blend);
            bool firstLegIsLeft = FirstLegIsLeftForSupport();
            float firstLegLift = Mathf.Min(SingleLegSupportHipHeight,
                Mathf.Max(0.05f, firstLegLength(firstLegIsLeft) - 0.035f));
            Vector3 targetHips = _hipsPosition + Vector3.up * firstLegLift
                + SupportSideVector() + transform.forward * (ForwardPressDistance * 0.85f);
            Transform hips = PhysicsRig.GetProxyFor(BoneMap.hips);
            if (hips != null)
                PhysicsRig.DriveRecoveryPoint(hips, Vector3.Lerp(_hipsPosition, targetHips, t), hips.rotation);

            LegTarget first = firstLegIsLeft ? _left : _right;
            LegTarget second = firstLegIsLeft ? _right : _left;
            ApplyLegPose(first, targetHips,
                MakeFirstSupportFootTarget(targetHips, firstLegIsLeft), t, false);
            // Keep the second leg folded while the first leg becomes the
            // temporary support leg. It is driven only to its seated target.
            ApplyLegPose(second, targetHips, second.TargetFoot, t, false);
        }

        float firstLegLength(bool left)
        {
            LegTarget leg = left ? _left : _right;
            return leg.UpperLength + leg.LowerLength;
        }

        Vector3 MakeFirstSupportFootTarget(Vector3 targetHips, bool left)
        {
            // Place the first support foot in front of the corresponding
            // buttock. The collider bottom, not the foot-bone origin, is the
            // ground-contact reference.
            Vector3 target = targetHips + transform.forward * 0.16f
                + transform.right * (left ? -SeatedFootSide : SeatedFootSide);
            target.y = FootContactHeight();
            return target;
        }

        void ApplyTorsoLean(float blend)
        {
            Quaternion seated = RaccoonRecoveryPosePlanner.SeatedTorsoRotation(transform);
            Quaternion lean = RaccoonRecoveryPosePlanner.LeaningTorsoRotation(transform);
            Transform hips = PhysicsRig.GetProxyFor(BoneMap.hips);
            ApplyRigidTorso(Quaternion.Slerp(seated, lean, blend), blend,
                ForwardPressDistance * blend, hips != null ? hips.position : _hipsPosition);
        }

        void ApplyStandingTorso(float blend)
        {
            Quaternion target = RaccoonRecoveryPosePlanner.SeatedTorsoRotation(transform);
            Transform hips = PhysicsRig.GetProxyFor(BoneMap.hips);
            ApplyRigidTorso(target, blend, 0f, hips != null ? hips.position : _hipsPosition);
        }

        void ApplyRigidTorso(Quaternion targetRotation, float blend, float forwardOffset, Vector3? pivotOverride = null)
        {
            Transform spine = PhysicsRig.GetProxyFor(BoneMap.spine);
            Transform head = PhysicsRig.GetProxyFor(BoneMap.head);
            Transform hips = PhysicsRig.GetProxyFor(BoneMap.hips);
            if (spine == null || head == null)
                return;

            Vector3 pivot = pivotOverride ?? _hipsPosition;
            Quaternion delta = targetRotation * Quaternion.Inverse(_spineStartRotation);
            Vector3 spineTarget = pivot + delta * (_spineStartPosition - _hipsPosition)
                + transform.forward * forwardOffset;
            Vector3 headTarget = pivot + delta * (_headStartPosition - _hipsPosition)
                + transform.forward * forwardOffset;
            float positionStrength = Mathf.Clamp01(SupportedTorsoPositionStrength);
            PhysicsRig.DriveRecoveryPointScaled(spine, spineTarget,
                Quaternion.Slerp(spine.rotation, targetRotation, blend), positionStrength);
            PhysicsRig.DriveRecoveryPointScaled(head, headTarget,
                Quaternion.Slerp(head.rotation, targetRotation, blend), positionStrength);
            PhysicsRig.DriveRecoveryPointScaled(hips, pivot,
                Quaternion.Slerp(hips != null ? hips.rotation : targetRotation, targetRotation, blend),
                positionStrength);
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

        void ApplyBalanceArm(float blend)
        {
            if (!_balanceArm.IsValid)
                return;

            Vector3 awayFromSupport = UseLeftSupportHand ? transform.right : -transform.right;
            Vector3 target = _hipsPosition
                + transform.forward * BalanceHandForward
                + awayFromSupport * BalanceHandReach
                + Vector3.up * BalanceHandHeight;
            Vector3 release = _balanceArm.StartHand + Vector3.up * 0.10f;
            Vector3 hand = Vector3.Lerp(release, target, Mathf.Clamp01(blend));
            Vector3 hip = _balanceArm.Hip;
            Vector3 toTarget = hand - hip;
            float distance = Mathf.Clamp(toTarget.magnitude,
                Mathf.Abs(_balanceArm.UpperLength - _balanceArm.LowerLength) + 0.01f,
                _balanceArm.UpperLength + _balanceArm.LowerLength - 0.01f);
            Vector3 direction = toTarget.sqrMagnitude > 0.0001f
                ? toTarget.normalized : transform.forward;
            // Bend the balancing elbow upward and slightly toward the back;
            // this keeps the arm from collapsing into a straight, anatomical
            // impossibility while the hand reaches away from the torso.
            Vector3 bend = Vector3.ProjectOnPlane(Vector3.up - transform.forward, direction).normalized;
            if (bend.sqrMagnitude < 0.0001f)
                bend = Vector3.up;
            float cos = (_balanceArm.UpperLength * _balanceArm.UpperLength
                + distance * distance - _balanceArm.LowerLength * _balanceArm.LowerLength)
                / (2f * _balanceArm.UpperLength * distance);
            cos = Mathf.Clamp(cos, -1f, 1f);
            Vector3 elbow = hip + direction * (cos * _balanceArm.UpperLength)
                + bend * (Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos)) * _balanceArm.UpperLength);
            PhysicsRig.DriveRecoverySegment(_balanceArm.Upper, hip, elbow, _balanceArm.UpperTwist);
            PhysicsRig.DriveRecoverySegment(_balanceArm.Lower, elbow, hand, _balanceArm.LowerTwist);
            PhysicsRig.DriveRecoveryPoint(_balanceArm.Hand, hand,
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
            target.y = FootContactHeight();
            return target;
        }

        float FootContactHeight()
        {
            return PhysicsRig != null
                ? PhysicsRig.RecoveryFootContactHeight(GroundHeight)
                : GroundHeight + SeatedFootHeight;
        }

        void ApplyLegPose(LegTarget leg, Vector3 targetHip, Vector3 targetFoot, float blend, bool constrainToPlane)
        {
            if (!leg.IsValid) return;
            Vector3 hip = Vector3.Lerp(leg.Hip, targetHip, blend);
            Vector3 ankle = Vector3.Lerp(leg.StartFoot, targetFoot, blend);
            ankle.y = Mathf.Max(FootContactHeight(), ankle.y);
            Vector3 pole = transform.forward * (1f + KneeForwardBias) + Vector3.up * (KneeUpBias + KneeLiftHeight);
            RaccoonLegPose pose;
            RaccoonLegPoseSolver.TrySolve(hip, ankle, leg.UpperLength, leg.LowerLength,
                pole, 0.01f, leg.UpperLength + leg.LowerLength - 0.01f, out pose);
            Vector3 knee = pose.Knee;
            PhysicsRig.DriveRecoverySegment(leg.Upper, hip, knee, leg.UpperTwist);
            PhysicsRig.DriveRecoverySegment(leg.Lower, knee, ankle, leg.LowerTwist);
            PhysicsRig.DriveRecoveryPoint(leg.Foot, ankle,
                Quaternion.LookRotation(transform.forward, Vector3.up));
            PhysicsRig.ClampRecoveryFootToGround(leg.Foot, GroundHeight);
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
            float maxBend = Mathf.Clamp(MaxKneeBendDegrees, 20f, 170f) * Mathf.Deg2Rad;
            float minByAngle = Mathf.Sqrt(Mathf.Max(0.0001f,
                leg.UpperLength * leg.UpperLength + leg.LowerLength * leg.LowerLength
                - 2f * leg.UpperLength * leg.LowerLength * Mathf.Cos(maxBend)));
            // The pelvis is locked in translation, but the hip is not locked:
            // the upper-leg direction is rebuilt every physics tick from a
            // moving knee pole.  A sin-shaped lift makes the thigh visibly
            // flex upward first, then lets the foot settle toward the floor.
            // Using world-up here is deliberate because the character may be
            // lying on its back or front and its local up is then unreliable.
            float kneeLift = Mathf.Sin(Mathf.PI * Mathf.Clamp01(blend)) * Mathf.Max(0f, KneeLiftHeight);
            Vector3 pole = transform.forward * (1f + KneeForwardBias)
                + Vector3.up * (KneeUpBias + kneeLift * 4f);
            RaccoonLegPose pose;
            RaccoonLegPoseSolver.TrySolve(hip, ankle, leg.UpperLength, leg.LowerLength,
                pole, minByAngle + KneeBodyClearance,
                Mathf.Max(0.02f, leg.UpperLength + leg.LowerLength - 0.01f), out pose);
            Vector3 knee = pose.Knee;

            Vector3 fromBody = knee - _hipsPosition;
            if (fromBody.sqrMagnitude < KneeBodyClearance * KneeBodyClearance)
            {
                Vector3 fallbackDirection = (knee - hip).sqrMagnitude > 0.000001f
                    ? (knee - hip).normalized : transform.up;
                knee = _hipsPosition + (fromBody.sqrMagnitude > 0.000001f
                    ? fromBody.normalized : fallbackDirection) * KneeBodyClearance;
            }

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
            Vector3 target = RaccoonRecoveryPosePlanner.SeatedFootTarget(
                _hipsPosition, transform, left, SeatedFootForward,
                SeatedFootSide, MinimumFootPelvisDistance,
                GroundHeight, SeatedFootHeight);
            target.y = FootContactHeight();
            return target;
        }

        Vector3 GetMousePullDirection()
        {
            Camera camera = Camera.main;
            if (camera != null)
            {
                Ray ray = camera.ScreenPointToRay(Input.mousePosition);
                Plane ground = new Plane(Vector3.up, new Vector3(0f, GroundHeight, 0f));
                float distance;
                if (ground.Raycast(ray, out distance))
                {
                    Vector3 direction = ray.GetPoint(distance) - transform.position;
                    direction.y = 0f;
                    if (direction.sqrMagnitude > 0.0001f)
                        return direction.normalized;
                }
            }
            return transform.forward;
        }

        void ApplyTorso(float blend, float driveStrength = 1f)
        {
            Transform spine = PhysicsRig.GetProxyFor(BoneMap.spine);
            Transform head = PhysicsRig.GetProxyFor(BoneMap.head);
            if (spine == null || head == null)
                return;

            // During the torso phase the target is unambiguously vertical.
            // Mouse pull is reserved for the later hand-support/weight-shift
            // phases; mixing it into the torso target left the spine settling
            // at an intermediate angle in the proxy's local joint frame.
            Vector3 targetUp = Vector3.up;
            // The target must equal the fallen pose at blend=0. The old
            // calculation immediately rotated from the fallen pose toward a
            // tilted target, which caused a small snap and let the completion
            // test fire before the actual lift. Interpolate the measured
            // fallen axis toward vertical instead.
            Quaternion seatedRotation = Quaternion.LookRotation(_recoveryFacing, Vector3.up);
            Quaternion targetRotation = Quaternion.Slerp(
                _spineStartRotation, seatedRotation, Mathf.Clamp01(blend));
            Quaternion headTargetRotation = Quaternion.Slerp(
                _headStartRotation, seatedRotation, Mathf.Clamp01(blend));

            if (!_torsoDriveLogged)
            {
                Rigidbody body = spine.GetComponent<Rigidbody>();
                Debug.Log($"[RaccoonStep] Torso drive active: blend={blend:F3}, angle={Quaternion.Angle(spine.rotation, targetRotation):F1}, kinematic={(body != null && body.isKinematic)}", this);
                _torsoDriveLogged = true;
            }

            // The pelvis is the controlled root; the spine is driven only by
            // torque. Head, arms and their spacing are then solved by the
            // connected ragdoll joints instead of being positioned directly.
            // Drive the spine from its measured current axis only. Combining
            // this with the accumulated-start-pose quaternion drive produced
            // opposing torques and stabilized the body around ~67 degrees.
            PhysicsRig.DriveRecoverySpineJoint(targetRotation, driveStrength);
            // The temporary joint preserves the pelvis-to-spine anchor, but
            // its target is expressed in joint space and can be absorbed by
            // the floor/contact solver.  Correct the measured current axis as
            // well, so the torso cannot remain parked at the fallen pose.
            PhysicsRig.DriveRecoveryUpright(spine, driveStrength);
            PhysicsRig.DriveRecoveryRotation(head, headTargetRotation, driveStrength * 0.35f);
            // The pelvis is dynamic during recovery. Hold its position with
            // a bounded PD target while driving its rotation too, so the
            // torso solve is shared by HipsBody and SpineBody instead of
            // appearing as a head/neck-only rotation.
            Transform hips = PhysicsRig.GetProxyFor(BoneMap.hips);
            PhysicsRig.DriveRecoveryPoint(hips, _hipsPosition, targetRotation);

            // Add a direct current-pose correction as well. This removes the
            // accumulated-start-pose dependency and keeps driving until the
            // measured spine axis is actually vertical.

            _torsoDiagnosticTimer += Time.fixedDeltaTime;
            if (_torsoDiagnosticTimer >= 0.5f)
            {
                Rigidbody body = spine.GetComponent<Rigidbody>();
                Transform hipsProxy = PhysicsRig.GetProxyFor(BoneMap.hips);
                Rigidbody hipsBody = hipsProxy != null ? hipsProxy.GetComponent<Rigidbody>() : null;
                float axisAngle = Mathf.Min(
                    Vector3.Angle(spine.up, Vector3.up),
                    Vector3.Angle(-spine.up, Vector3.up));
                Debug.Log($"[RaccoonStep] Torso diagnostic: phase={_recoveryPhase}, blend={blend:F2}, "
                    + $"targetAngle={Quaternion.Angle(spine.rotation, targetRotation):F1}, "
                    + $"axisToUp={axisAngle:F1}, angularVelocity={(body != null ? body.angularVelocity.ToString("F2") : "null")}, "
                    + $"hipsKinematic={(hipsBody != null && hipsBody.isKinematic)}, hipsAngularVelocity={(hipsBody != null ? hipsBody.angularVelocity.ToString("F2") : "null")}, "
                    + $"strength={driveStrength:F2}, spineFree={(spine.GetComponent<ConfigurableJoint>() != null && spine.GetComponent<ConfigurableJoint>().angularXMotion == ConfigurableJointMotion.Free)}", this);
                _torsoDiagnosticTimer = 0f;
            }
        }

        bool IsTorsoUpright()
        {
            Transform spine = PhysicsRig.GetProxyFor(BoneMap.spine);
            if (spine == null)
                return false;

            // The proxy capsule axis represents the spine bone segment. A
            // spine perpendicular to the ground plane is parallel to the
            // ground normal, so this is independent of the head pose.
            return RaccoonRecoveryPosePlanner.IsUpright(spine.up, TorsoUprightTolerance);
        }

        Vector3 ResolveRecoveryFacing(Transform spine)
        {
            Vector3 facing = spine != null
                ? Vector3.ProjectOnPlane(spine.forward, Vector3.up)
                : Vector3.zero;
            if (facing.sqrMagnitude < 0.0001f)
                facing = Vector3.ProjectOnPlane(_recoveryPullDirection, Vector3.up);
            if (facing.sqrMagnitude < 0.0001f)
                facing = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            return facing.normalized;
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
