using UnityEngine;

namespace RaccoonStep
{
    public struct RaccoonLegPose
    {
        public Vector3 Hip;
        public Vector3 Knee;
        public Vector3 Ankle;
    }

    /// <summary>Shared two-segment leg geometry. It does not move transforms.</summary>
    public static class RaccoonLegPoseSolver
    {
        public static bool TrySolve(Vector3 hip, Vector3 ankle, float upperLength,
            float lowerLength, Vector3 pole, float minimumDistance, float maximumDistance,
            out RaccoonLegPose pose)
        {
            pose = new RaccoonLegPose { Hip = hip, Ankle = ankle };
            float upper = Mathf.Max(0.001f, upperLength);
            float lower = Mathf.Max(0.001f, lowerLength);
            Vector3 toTarget = ankle - hip;
            float distance = Mathf.Clamp(toTarget.magnitude,
                Mathf.Max(Mathf.Abs(upper - lower) + 0.001f, minimumDistance),
                Mathf.Max(0.002f, Mathf.Min(upper + lower - 0.001f, maximumDistance)));
            Vector3 direction = toTarget.sqrMagnitude > 0.000001f
                ? toTarget.normalized : Vector3.down;
            Vector3 bend = Vector3.ProjectOnPlane(pole, direction).normalized;
            if (bend.sqrMagnitude < 0.000001f)
                bend = Vector3.up;

            float cosine = (upper * upper + distance * distance - lower * lower)
                / (2f * upper * distance);
            cosine = Mathf.Clamp(cosine, -1f, 1f);
            float sine = Mathf.Sqrt(Mathf.Max(0f, 1f - cosine * cosine));
            pose.Knee = hip + direction * (cosine * upper) + bend * (sine * upper);
            return true;
        }
    }
}
