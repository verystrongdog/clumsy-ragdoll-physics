using UnityEngine;

namespace ClumsyRagdoll
{
    public static class IkMath
    {
        public static Quaternion SafeLook(Vector3 forward, Vector3 up)
        {
            if (forward.sqrMagnitude < 1e-8f)
                forward = Vector3.forward;
            Vector3 f = forward.normalized;
            Vector3 u = up.sqrMagnitude < 1e-8f ? Vector3.up : up.normalized;
            if (Mathf.Abs(Vector3.Dot(f, u)) > 0.998f)
                u = Mathf.Abs(f.y) < 0.9f ? Vector3.up : Vector3.right;
            Vector3 right = Vector3.Cross(u, f);
            if (right.sqrMagnitude < 1e-8f)
                return Quaternion.identity;
            u = Vector3.Cross(f, right.normalized);
            return Quaternion.LookRotation(f, u);
        }

        /// <summary>
        /// 让 transform.up 对齐骨骼方向，hint 决定扭转，避免手臂绕自身乱转。
        /// </summary>
        public static Quaternion RotationFromUp(Vector3 upDirection, Vector3 hintForward)
        {
            if (upDirection.sqrMagnitude < 1e-8f)
                return Quaternion.identity;
            Vector3 up = upDirection.normalized;
            Vector3 hint = Vector3.ProjectOnPlane(hintForward, up);
            if (hint.sqrMagnitude < 1e-8f)
                hint = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (hint.sqrMagnitude < 1e-8f)
                hint = Vector3.ProjectOnPlane(Vector3.right, up);
            return SafeLook(hint, up);
        }

        public static void SolveTwoBone(
            Vector3 root,
            Vector3 target,
            Vector3 pole,
            float lengthA,
            float lengthB,
            out Vector3 dirA,
            out Vector3 dirB)
        {
            Vector3 to = target - root;
            float max = lengthA + lengthB - 0.001f;
            float min = Mathf.Abs(lengthA - lengthB) + 0.001f;
            float dist = to.magnitude;
            if (dist < 1e-5f)
                to = Vector3.down * min;
            dist = Mathf.Clamp(dist, min, Mathf.Max(min, max));
            Vector3 toN = to.sqrMagnitude < 1e-8f ? Vector3.down : to.normalized;
            Vector3 reach = root + toN * dist;

            float cosRoot = (lengthA * lengthA + dist * dist - lengthB * lengthB) / (2f * lengthA * dist);
            cosRoot = Mathf.Clamp(cosRoot, -1f, 1f);
            float sinRoot = Mathf.Sqrt(Mathf.Max(0f, 1f - cosRoot * cosRoot));

            Vector3 bend = Vector3.ProjectOnPlane(pole - root, toN);
            if (bend.sqrMagnitude < 1e-8f)
                bend = Vector3.ProjectOnPlane(Vector3.forward, toN);
            if (bend.sqrMagnitude < 1e-8f)
                bend = Vector3.ProjectOnPlane(Vector3.right, toN);
            bend.Normalize();

            Vector3 mid = root + toN * (cosRoot * lengthA) + bend * (sinRoot * lengthA);
            dirA = mid - root;
            dirB = reach - mid;
        }

        public static Vector3 PdTorque(
            Quaternion current,
            Quaternion target,
            Vector3 angularVelocity,
            float stiffness,
            float damping,
            float maxTorque)
        {
            Quaternion delta = target * Quaternion.Inverse(current);
            if (delta.w < 0f)
            {
                delta.x = -delta.x;
                delta.y = -delta.y;
                delta.z = -delta.z;
                delta.w = -delta.w;
            }

            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-8f || angle < 0.05f)
                return Vector3.ClampMagnitude(-angularVelocity * damping, maxTorque);

            if (angle > 180f)
                angle -= 360f;

            Vector3 torque = axis.normalized * (angle * Mathf.Deg2Rad * stiffness) - angularVelocity * damping;
            return Vector3.ClampMagnitude(torque, maxTorque);
        }
    }
}
