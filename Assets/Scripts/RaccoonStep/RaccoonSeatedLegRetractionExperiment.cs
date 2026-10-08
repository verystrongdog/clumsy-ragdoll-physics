using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Isolated first-step experiment for seated leg retraction. This
    /// component deliberately has no mouse input and never drives the pelvis,
    /// spine, head, arms, gravity, or the recovery state machine.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonSeatedLegRetractionExperiment : MonoBehaviour
    {
        public RaccoonBoneMap BoneMap;
        public RaccoonPhysicsRig PhysicsRig;
        public float GroundHeight;
        public float Duration = 1.2f;
        public float FootForward = 0.16f;
        public float FootSide = 0.11f;
        public float KneeForwardBias = 0.45f;
        public float KneeUpBias = 0.35f;

        public bool IsRunning { get; private set; }
        public bool IsLeftLeg { get; private set; }
        public float Progress { get; private set; }

        Leg _leg;
        Vector3 _targetFoot;

        void Awake()
        {
            if (BoneMap == null) BoneMap = GetComponent<RaccoonBoneMap>();
            if (PhysicsRig == null) PhysicsRig = GetComponent<RaccoonPhysicsRig>();
        }

        void FixedUpdate()
        {
            if (!IsRunning || !_leg.Valid || PhysicsRig == null)
                return;

            Progress = Mathf.Clamp01(Progress + Time.fixedDeltaTime /
                Mathf.Max(0.2f, Duration));
            ApplyLeg(Smooth(Progress));
            if (Progress >= 1f)
                IsRunning = false;
        }

        [ContextMenu("Request Left Leg Retraction")]
        public void RequestLeftLegRetraction() => Begin(true);

        [ContextMenu("Request Right Leg Retraction")]
        public void RequestRightLegRetraction() => Begin(false);

        public bool Begin(bool left)
        {
            if (IsRunning || BoneMap == null || PhysicsRig == null)
                return false;

            Transform upper = left ? BoneMap.leftUpperLeg : BoneMap.rightUpperLeg;
            Transform lower = left ? BoneMap.leftLowerLeg : BoneMap.rightLowerLeg;
            Transform foot = left ? BoneMap.leftFoot : BoneMap.rightFoot;
            _leg = BuildLeg(upper, lower, foot);
            if (!_leg.Valid)
                return false;

            IsLeftLeg = left;
            Progress = 0f;
            _targetFoot = _leg.Hip + transform.forward * FootForward
                + transform.right * (left ? -FootSide : FootSide);
            _targetFoot.y = PhysicsRig.RecoveryFootContactHeight(GroundHeight);

            // Only the selected leg is opened for this experiment. All
            // upper-body and pelvis controls remain untouched.
            PhysicsRig.SetRecoveryLegMotion(upper, lower, foot, true);
            PhysicsRig.SetRecoveryLegBodyCollision(upper, lower, foot, false);
            IsRunning = true;
            Debug.Log("[RaccoonStep] Isolated seated leg experiment started: "
                + (left ? "left" : "right"), this);
            return true;
        }

        public void Stop()
        {
            IsRunning = false;
            Progress = 0f;
        }

        void ApplyLeg(float blend)
        {
            Vector3 ankle = Vector3.Lerp(_leg.StartFoot, _targetFoot, blend);
            ankle.y = Mathf.Max(_targetFoot.y, ankle.y);
            Vector3 pole = transform.forward * (1f + KneeForwardBias)
                + Vector3.up * KneeUpBias;
            RaccoonLegPose pose;
            RaccoonLegPoseSolver.TrySolve(_leg.Hip, ankle, _leg.UpperLength,
                _leg.LowerLength, pole, 0.01f,
                _leg.UpperLength + _leg.LowerLength - 0.01f, out pose);
            PhysicsRig.DriveRecoverySegment(_leg.Upper, _leg.Hip, pose.Knee,
                _leg.UpperTwist);
            PhysicsRig.DriveRecoverySegment(_leg.Lower, pose.Knee, ankle,
                _leg.LowerTwist);
            PhysicsRig.DriveRecoveryPoint(_leg.Foot, ankle,
                Quaternion.LookRotation(transform.forward, Vector3.up));
            PhysicsRig.ClampRecoveryFootToGround(_leg.Foot, GroundHeight);
        }

        Leg BuildLeg(Transform upper, Transform lower, Transform foot)
        {
            Leg leg = new Leg();
            leg.Upper = PhysicsRig.GetProxyFor(upper);
            leg.Lower = PhysicsRig.GetProxyFor(lower);
            leg.Foot = PhysicsRig.GetProxyFor(foot);
            if (upper == null || lower == null || foot == null
                || leg.Upper == null || leg.Lower == null || leg.Foot == null)
                return leg;
            leg.Hip = upper.position;
            leg.StartFoot = foot.position;
            leg.UpperLength = Vector3.Distance(upper.position, lower.position);
            leg.LowerLength = Vector3.Distance(lower.position, foot.position);
            leg.UpperTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (lower.position - upper.position).normalized)) * leg.Upper.rotation;
            leg.LowerTwist = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up,
                (foot.position - lower.position).normalized)) * leg.Lower.rotation;
            leg.Valid = true;
            return leg;
        }

        static float Smooth(float value) => value * value * (3f - 2f * value);

        struct Leg
        {
            public Transform Upper, Lower, Foot;
            public Vector3 Hip, StartFoot;
            public float UpperLength, LowerLength;
            public Quaternion UpperTwist, LowerTwist;
            public bool Valid;
        }
    }
}
