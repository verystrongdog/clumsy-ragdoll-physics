using UnityEngine;

namespace RaccoonStep
{
    /// <summary>Pure construction helpers for a single proxy body.</summary>
    public static class RaccoonProxyBuilder
    {
        public static void GetSegmentPose(Transform visualBone, Transform visualChild,
            out Vector3 start, out Vector3 end, out Vector3 midpoint, out Quaternion rotation, out float length)
        {
            start = visualBone.position;
            end = visualChild != null ? visualChild.position : start + visualBone.up * 0.12f;
            Vector3 axis = end - start;
            length = Mathf.Max(0.08f, axis.magnitude);
            midpoint = Vector3.Lerp(start, end, 0.5f);
            rotation = axis.sqrMagnitude > 0.0001f
                ? Quaternion.FromToRotation(Vector3.up, axis.normalized)
                : visualBone.rotation;
        }

        public static void ConfigureCapsule(CapsuleCollider capsule, float radius, float length, Vector3 center)
        {
            capsule.direction = 1;
            capsule.radius = Mathf.Max(0.001f, radius);
            capsule.height = length + capsule.radius * 2f;
            capsule.center = center;
        }

        public static void ConfigureBody(Rigidbody body, float mass, float minimumMass,
            int solverIterations, int solverVelocityIterations, float maxDepenetrationVelocity)
        {
            body.mass = Mathf.Max(mass, minimumMass);
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.maxDepenetrationVelocity = Mathf.Max(0.01f, maxDepenetrationVelocity);
            body.solverIterations = Mathf.Max(1, solverIterations);
            body.solverVelocityIterations = Mathf.Max(1, solverVelocityIterations);
            body.isKinematic = true;
            body.useGravity = false;
        }
    }
}
