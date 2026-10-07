using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Applies balance corrections to rigidbodies. It consumes measurements
    /// and targets, but does not decide support state or fall transitions.
    /// </summary>
    public static class RaccoonBalanceController
    {
        public static Vector3 CalculateCorrection(Vector3 balanceError, float gain,
            float maxSpeed, float maxPerFixedStep, float deltaTime)
        {
            Vector3 correction = balanceError * gain * deltaTime;
            correction = Vector3.ClampMagnitude(correction, Mathf.Max(0f, maxSpeed) * deltaTime);
            return Vector3.ClampMagnitude(correction, Mathf.Max(0f, maxPerFixedStep));
        }

        public static Vector3 CalculatePositionAcceleration(Vector3 targetPosition,
            Vector3 currentPosition, Vector3 linearVelocity, float spring, float damper,
            float maxAcceleration)
        {
            Vector3 acceleration = (targetPosition - currentPosition) * spring
                - linearVelocity * damper;
            return Vector3.ClampMagnitude(acceleration, Mathf.Max(0f, maxAcceleration));
        }

        public static Vector3 CalculateUprightAcceleration(Transform body,
            Vector3 angularVelocity, float spring, float damper, float maxTorque)
        {
            if (body == null)
                return Vector3.zero;
            Vector3 tiltAxis = Vector3.Cross(body.up, Vector3.up);
            Vector3 torque = tiltAxis * spring - angularVelocity * damper;
            return Vector3.ClampMagnitude(torque, Mathf.Max(0f, maxTorque));
        }
    }
}
