using UnityEngine;

namespace RaccoonStep
{
    public struct RaccoonStepCommand
    {
        public bool HasStep;
        public bool LeftFoot;
        public Vector3 TargetPosition;
        public float Progress;

        public static RaccoonStepCommand None => new RaccoonStepCommand
        {
            HasStep = false,
            LeftFoot = true,
            TargetPosition = Vector3.zero,
            Progress = 0f
        };
    }
}
