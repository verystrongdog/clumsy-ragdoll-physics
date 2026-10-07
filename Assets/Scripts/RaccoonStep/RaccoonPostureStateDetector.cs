namespace RaccoonStep
{
    public enum RaccoonPostureState
    {
        Standing,
        Sitting,
        Fallen,
        Recovering
    }

    /// <summary>Maps character/fall signals to a recovery-facing posture.</summary>
    public static class RaccoonPostureStateDetector
    {
        public static RaccoonPostureState Detect(RaccoonStepState characterState,
            bool isFalling, bool recoveryActive, bool canBeginRecovery)
        {
            if (recoveryActive || characterState == RaccoonStepState.Recovering)
                return RaccoonPostureState.Recovering;
            if (isFalling && canBeginRecovery)
                return RaccoonPostureState.Fallen;
            if (isFalling)
                return RaccoonPostureState.Sitting;
            return RaccoonPostureState.Standing;
        }
    }
}
