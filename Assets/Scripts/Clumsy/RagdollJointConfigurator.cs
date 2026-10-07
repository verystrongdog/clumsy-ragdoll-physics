using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// Applies the angular drive policy to an already-created ragdoll joint.
    /// Joint creation and ragdoll lifetime remain outside this class.
    /// </summary>
    public sealed class RagdollJointConfigurator
    {
        public void Configure(
            ClumsyPart part,
            ConfigurableJoint joint,
            RagdollRecipe recipe,
            float hipsSpring,
            float limbSpring,
            float upperSpring,
            float torsoSpring)
        {
            float spring = GetSpring(
                part,
                recipe,
                hipsSpring,
                limbSpring,
                upperSpring,
                torsoSpring);
            ConfigureSpring(part, joint, recipe, spring, recipe.DampingRatio);
        }

        /// <summary>
        /// Applies a runtime spring override without touching targetRotation.
        /// </summary>
        public void ConfigureSpring(
            ClumsyPart part,
            ConfigurableJoint joint,
            RagdollRecipe recipe,
            float spring,
            float dampingRatio)
        {
            if (part == null || joint == null || recipe == null)
                return;
            part.Spring = spring;

            float damper = 0f;
            if (dampingRatio > 0f && part.Body != null)
            {
                Vector3 inertia = part.Body.inertiaTensor;
                float referenceInertia = Mathf.Max(
                    inertia.x,
                    Mathf.Max(inertia.y, inertia.z));
                damper = 2f * dampingRatio
                    * Mathf.Sqrt(Mathf.Max(spring * referenceInertia, 1e-6f));
            }

            joint.angularXDrive = MakeDrive(
                joint.angularXDrive,
                spring,
                damper,
                recipe.MaxDriveForce);
            joint.angularYZDrive = MakeDrive(
                joint.angularYZDrive,
                spring,
                damper,
                recipe.MaxDriveForce);
        }

        public float GetSpring(
            ClumsyPart part,
            RagdollRecipe recipe,
            float hipsSpring,
            float limbSpring,
            float upperSpring,
            float torsoSpring)
        {
            if (part.IsPelvis)
            {
                if (recipe != null && recipe.Balance != BalanceMode.StabilizerJoint)
                    return 0f;
                return hipsSpring;
            }

            string key = part.Spec.Key;
            if (key.StartsWith("shoulder")
                || key.StartsWith("arm")
                || key.StartsWith("forearm")
                || key.StartsWith("hand"))
                return upperSpring;

            if (key == "spine" || key == "neck" || key == "head")
                return torsoSpring;

            return limbSpring;
        }

        static JointDrive MakeDrive(
            JointDrive drive,
            float spring,
            float damper,
            float maximumForce)
        {
            drive.positionSpring = spring;
            drive.positionDamper = damper;
            if (maximumForce > 0f)
                drive.maximumForce = maximumForce;
            return drive;
        }
    }
}
