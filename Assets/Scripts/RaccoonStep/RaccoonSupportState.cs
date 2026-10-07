using UnityEngine;

namespace RaccoonStep
{
    public enum RaccoonSupportMode
    {
        None,
        SingleFoot,
        DoubleFoot,
        Invalid
    }

    /// <summary>Snapshot of support measurements used by balance and recovery.</summary>
    public struct RaccoonSupportState
    {
        public RaccoonSupportMode Mode;
        public Vector3 SupportPointWorld;
        public Vector3 BalanceError;
        public Vector3 FallDirectionWorld;
        public float FeetDistance;
        public float EffectiveSupportRadius;
        public bool IsStable => Mode == RaccoonSupportMode.SingleFoot
            || Mode == RaccoonSupportMode.DoubleFoot;
    }
}
