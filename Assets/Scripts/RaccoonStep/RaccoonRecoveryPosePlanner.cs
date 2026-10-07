using UnityEngine;

namespace RaccoonStep
{
    /// <summary>Pure recovery pose planning; it never touches rigidbodies.</summary>
    public static class RaccoonRecoveryPosePlanner
    {
        public static Quaternion SeatedTorsoRotation(Transform root)
        {
            return Quaternion.LookRotation(root.forward, Vector3.up);
        }

        public static Quaternion LeaningTorsoRotation(Transform root)
        {
            Vector3 up = (root.forward * 0.78f + Vector3.up * 0.63f).normalized;
            return Quaternion.LookRotation(up, Vector3.up);
        }

        public static Vector3 InterpolateAxis(Vector3 startAxis, Vector3 targetAxis, float blend)
        {
            Vector3 start = startAxis.sqrMagnitude > 0.000001f ? startAxis.normalized : Vector3.up;
            Vector3 target = targetAxis.sqrMagnitude > 0.000001f ? targetAxis.normalized : Vector3.up;
            return Vector3.Slerp(start, target, Mathf.Clamp01(blend)).normalized;
        }

        public static bool IsUpright(Vector3 axis, float toleranceDegrees)
        {
            if (axis.sqrMagnitude < 0.000001f)
                return false;
            float angle = Mathf.Min(Vector3.Angle(axis, Vector3.up), Vector3.Angle(-axis, Vector3.up));
            return angle <= Mathf.Max(1f, toleranceDegrees);
        }

        public static Vector3 SeatedFootTarget(Vector3 hipsPosition, Transform root,
            bool left, float forward, float side, float minimumDistance,
            float groundHeight, float footHeight)
        {
            Vector3 target = hipsPosition + root.forward * forward
                + root.right * (left ? -side : side);
            Vector3 horizontal = Vector3.ProjectOnPlane(target - hipsPosition, Vector3.up);
            float minimum = Mathf.Max(0.05f, minimumDistance);
            if (horizontal.sqrMagnitude < minimum * minimum)
            {
                horizontal = horizontal.sqrMagnitude > 0.000001f
                    ? horizontal.normalized * minimum
                    : root.forward * minimum;
                target = hipsPosition + horizontal;
            }
            target.y = groundHeight + footHeight;
            return target;
        }
    }
}
