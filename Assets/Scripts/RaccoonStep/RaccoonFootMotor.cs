using UnityEngine;

namespace RaccoonStep
{
    /// <summary>Applies a bounded position servo to one foot proxy.</summary>
    public static class RaccoonFootMotor
    {
        public static Vector3 CalculateAcceleration(Vector3 targetWorld, Vector3 currentWorld,
            Vector3 linearVelocity, float spring, float damper, float maxAcceleration)
        {
            Vector3 acceleration = (targetWorld - currentWorld) * spring
                - linearVelocity * damper;
            return Vector3.ClampMagnitude(acceleration, Mathf.Max(0f, maxAcceleration));
        }
    }
}
