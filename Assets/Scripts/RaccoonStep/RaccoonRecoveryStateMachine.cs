namespace RaccoonStep
{
    /// <summary>
    /// Small state holder for recovery orchestration. Pose planning and joint
    /// driving remain outside this class.
    /// </summary>
    public sealed class RaccoonRecoveryStateMachine
    {
        public RaccoonRecoveryPhase Phase { get; private set; }
        public bool IsActive => Phase != RaccoonRecoveryPhase.None
            && Phase != RaccoonRecoveryPhase.Complete;

        public void Reset() => Phase = RaccoonRecoveryPhase.None;

        public void Begin(bool fullSequence)
        {
            Phase = fullSequence ? RaccoonRecoveryPhase.Torso : RaccoonRecoveryPhase.ManualLegs;
        }

        public void SetPhase(RaccoonRecoveryPhase phase) => Phase = phase;
    }

    public enum RaccoonRecoveryPhase
    {
        None,
        ManualLegs,
        Torso,
        SideLean,
        HandSupport,
        FirstLeg,
        SecondLeg,
        Squat,
        Stand,
        Complete
    }
}
