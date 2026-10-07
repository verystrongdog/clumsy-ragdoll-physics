using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// Combines pose modifiers before a pose reaches the physics driver.
    /// This class does not write to ConfigurableJoint.
    /// </summary>
    public sealed class RagdollPoseMixer
    {
        public Quaternion Compose(
            ClumsyPart part,
            string key,
            Quaternion baseTarget,
            ClumsyArmIK armIK,
            ClumsyWobble wobble,
            float deltaTime,
            out float ikWeight)
        {
            ikWeight = 0f;

            Quaternion ikTarget = Quaternion.identity;
            bool hasIK = armIK != null &&
                armIK.TryGetOverride(key, out ikTarget, out ikWeight);

            Quaternion result = baseTarget;

            if (wobble != null && !hasIK)
                result = wobble.Apply(key, part, result, deltaTime);

            if (hasIK)
                result = Quaternion.Slerp(result, ikTarget, Mathf.Clamp01(ikWeight));

            return result;
        }
    }
}
