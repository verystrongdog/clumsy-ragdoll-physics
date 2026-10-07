using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// Converts semantic pose requests into ConfigurableJoint targets.
    /// It owns bind-pose offsets, animation-pose conversion and the modifier/
    /// physics application pipeline. It does not calculate gait angles.
    /// </summary>
    public sealed class RagdollPoseWriter
    {
        readonly Dictionary<string, Quaternion> _base =
            new Dictionary<string, Quaternion>();

        readonly RagdollPoseMixer _mixer = new RagdollPoseMixer();
        readonly RagdollJointDriver _jointDriver = new RagdollJointDriver();

        public bool HasBasePose
        {
            get { return _base.Count > 0; }
        }

        public void BuildArmRestPose(ClumsyRagdoll ragdoll, RagdollRecipe recipe)
        {
            _base.Clear();
            if (ragdoll == null || recipe == null)
                return;

            float down = recipe.ArmDownDegrees;
            float share = Mathf.Clamp01(recipe.ShoulderDownShare);
            float shoulder = down * share;
            float upper = down * (1f - share);

            _base[RagdollPartId.ShoulderLeft] =
                JointTarget(+shoulder, ForwardAxis(ragdoll.Find(RagdollPartId.ShoulderLeft)));
            _base[RagdollPartId.ArmLeft] =
                JointTarget(+upper, ForwardAxis(ragdoll.Find(RagdollPartId.ArmLeft)));
            _base[RagdollPartId.ShoulderRight] =
                JointTarget(-shoulder, ForwardAxis(ragdoll.Find(RagdollPartId.ShoulderRight)));
            _base[RagdollPartId.ArmRight] =
                JointTarget(-upper, ForwardAxis(ragdoll.Find(RagdollPartId.ArmRight)));

            _base[RagdollPartId.ForearmLeft] =
                JointTarget(-recipe.ForeArmBendDegrees,
                    RightAxis(ragdoll.Find(RagdollPartId.ForearmLeft)));
            _base[RagdollPartId.ForearmRight] =
                JointTarget(-recipe.ForeArmBendDegrees,
                    RightAxis(ragdoll.Find(RagdollPartId.ForearmRight)));
        }

        public void Write(
            ClumsyRagdoll ragdoll,
            RagdollRecipe recipe,
            ClumsyController controller,
            ClumsyAnimationSource animation,
            ClumsyArmIK armIK,
            ClumsyWobble wobble,
            string key,
            float aboutRight,
            float aboutUp,
            float aboutForward,
            float deltaTime)
        {
            if (ragdoll == null || recipe == null)
                return;

            ClumsyPart part = ragdoll.Find(key);
            if (part == null || part.Joint == null)
                return;

            if (part.Joint.angularXMotion == ConfigurableJointMotion.Locked)
                return;

            Quaternion wanted;
            bool useAnimationPose =
                recipe.UseAnimation && animation != null && animation.Ready;

            if (useAnimationPose && animation.TryGetTargetRotation(part, out wanted))
            {
                Apply(ragdoll, recipe, controller, armIK, wobble,
                    part, key, wanted, deltaTime);
                return;
            }

            wanted = Quaternion.identity;
            if (Mathf.Abs(aboutRight) > 1e-4f)
                wanted = JointTarget(aboutRight, part.LocalRight) * wanted;
            if (Mathf.Abs(aboutUp) > 1e-4f)
                wanted = JointTarget(aboutUp, part.LocalUp) * wanted;
            if (Mathf.Abs(aboutForward) > 1e-4f)
                wanted = JointTarget(aboutForward, ForwardAxis(part)) * wanted;

            Quaternion basis;
            if (_base.TryGetValue(key, out basis))
                wanted = basis * wanted;

            Apply(ragdoll, recipe, controller, armIK, wobble,
                part, key, wanted, deltaTime);
        }

        public void WriteIKOnly(
            ClumsyRagdoll ragdoll,
            RagdollRecipe recipe,
            ClumsyController controller,
            ClumsyArmIK armIK,
            ClumsyWobble wobble,
            string key,
            float deltaTime)
        {
            if (ragdoll == null || recipe == null)
                return;

            ClumsyPart part = ragdoll.Find(key);
            if (part == null || part.Joint == null)
                return;

            if (part.Joint.angularXMotion == ConfigurableJointMotion.Locked)
                return;

            Quaternion basis;
            if (!_base.TryGetValue(key, out basis))
                basis = Quaternion.identity;

            Apply(ragdoll, recipe, controller, armIK, wobble,
                part, key, basis, deltaTime);
        }

        public void ResetToBindPose(ClumsyRagdoll ragdoll)
        {
            _jointDriver.ResetToBindPose(ragdoll);
        }

        void Apply(
            ClumsyRagdoll ragdoll,
            RagdollRecipe recipe,
            ClumsyController controller,
            ClumsyArmIK armIK,
            ClumsyWobble wobble,
            ClumsyPart part,
            string key,
            Quaternion wanted,
            float deltaTime)
        {
            float ikWeight;
            wanted = _mixer.Compose(
                part,
                key,
                wanted,
                armIK,
                wobble,
                deltaTime,
                out ikWeight);

            float slew = Mathf.Lerp(
                recipe.PoseSlewDegreesPerSecond,
                recipe.ArmIKSlewDegreesPerSecond,
                Mathf.Clamp01(ikWeight));

            if (recipe.UseFootGait
                && controller != null
                && controller.PlantFoot != null
                && RagdollPartId.IsLegOrFoot(key))
            {
                slew = Mathf.Max(slew, recipe.GaitPoseSlewDegreesPerSecond);
            }

            _jointDriver.Apply(part, key, wanted, deltaTime, slew);
        }

        static Quaternion JointTarget(float degrees, Vector3 localAxis)
        {
            return Quaternion.AngleAxis(-degrees, localAxis);
        }

        static Vector3 ForwardAxis(ClumsyPart part)
        {
            return part == null ? Vector3.forward : Vector3.Cross(part.LocalRight, part.LocalUp);
        }

        static Vector3 RightAxis(ClumsyPart part)
        {
            return part == null ? Vector3.right : part.LocalRight;
        }
    }
}
