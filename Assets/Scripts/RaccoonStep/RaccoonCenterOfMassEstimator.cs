using System.Collections.Generic;
using UnityEngine;

namespace RaccoonStep
{
    public struct RaccoonMassMeasurement
    {
        public readonly Vector3 CenterOfMassWorld;
        public readonly float TotalMass;
        public readonly bool IsValid;

        public RaccoonMassMeasurement(Vector3 centerOfMassWorld, float totalMass, bool isValid)
        {
            CenterOfMassWorld = centerOfMassWorld;
            TotalMass = totalMass;
            IsValid = isValid;
        }
    }

    /// <summary>Read-only mass and COM measurement for the proxy collection.</summary>
    public static class RaccoonCenterOfMassEstimator
    {
        public static RaccoonMassMeasurement Measure(IList<Rigidbody> bodies,
            Transform additionalMassRoot, float additionalMass, Vector3 additionalMassLocalOffset)
        {
            Vector3 weightedPosition = Vector3.zero;
            float mass = 0f;
            if (bodies != null)
            {
                for (int i = 0; i < bodies.Count; i++)
                {
                    Rigidbody body = bodies[i];
                    if (body == null || body.mass <= 0f)
                        continue;
                    weightedPosition += body.worldCenterOfMass * body.mass;
                    mass += body.mass;
                }
            }

            if (additionalMassRoot != null && additionalMass > 0f)
            {
                weightedPosition += additionalMassRoot.TransformPoint(additionalMassLocalOffset)
                    * additionalMass;
                mass += additionalMass;
            }

            if (mass <= 0f)
                return new RaccoonMassMeasurement(Vector3.zero, 0f, false);

            return new RaccoonMassMeasurement(weightedPosition / mass, mass, true);
        }
    }
}
