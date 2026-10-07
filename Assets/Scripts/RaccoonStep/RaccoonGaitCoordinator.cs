using UnityEngine;

namespace RaccoonStep
{
    /// <summary>Data-only gait coordinator; execution remains in leg controllers.</summary>
    public sealed class RaccoonGaitCoordinator
    {
        public RaccoonStepCommand CurrentCommand { get; private set; }

        public RaccoonStepCommand BuildCommand(bool hasStep, bool leftFoot,
            Vector3 targetPosition, float progress)
        {
            CurrentCommand = new RaccoonStepCommand
            {
                HasStep = hasStep,
                LeftFoot = leftFoot,
                TargetPosition = targetPosition,
                Progress = Mathf.Clamp01(progress)
            };
            return CurrentCommand;
        }

        public void Reset() => CurrentCommand = RaccoonStepCommand.None;
    }
}
