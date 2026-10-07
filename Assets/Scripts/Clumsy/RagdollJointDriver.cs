using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// Owns the final application of a pose to ConfigurableJoint instances.
    /// Pose calculation systems should provide a target rotation; this class
    /// handles slew limiting, target state and the actual physics write.
    /// </summary>
    public sealed class RagdollJointDriver
    {
        readonly Dictionary<string, Quaternion> _targets =
            new Dictionary<string, Quaternion>();

        public void Apply(
            ClumsyPart part,
            string key,
            Quaternion wanted,
            float deltaTime,
            float degreesPerSecond)
        {
            if (part == null || part.Joint == null || string.IsNullOrEmpty(key))
                return;

            Quaternion previous;
            if (!_targets.TryGetValue(key, out previous))
                previous = wanted;

            float maxStep = Mathf.Max(0f, degreesPerSecond) * Mathf.Max(0f, deltaTime);
            Quaternion next = Quaternion.RotateTowards(previous, wanted, maxStep);

            _targets[key] = next;
            part.Joint.targetRotation = next;
        }

        public void ResetToBindPose(ClumsyRagdoll ragdoll)
        {
            _targets.Clear();

            if (ragdoll == null || ragdoll.Parts == null)
                return;

            for (int i = 0; i < ragdoll.Parts.Count; i++)
            {
                ClumsyPart part = ragdoll.Parts[i];
                if (part != null && part.Joint != null)
                    part.Joint.targetRotation = Quaternion.identity;
            }
        }
    }
}
