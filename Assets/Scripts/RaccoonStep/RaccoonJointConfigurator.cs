using UnityEngine;

namespace RaccoonStep
{
    /// <summary>Single source of truth for normal walking joint limits/drives.</summary>
    public static class RaccoonJointConfigurator
    {
        public static void Configure(ConfigurableJoint joint, Rigidbody connectedBody,
            Vector3 worldAnchor, bool allowTranslation, float spring, float damper)
        {
            joint.connectedBody = connectedBody;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = joint.transform.InverseTransformPoint(worldAnchor);
            joint.connectedAnchor = connectedBody != null
                ? connectedBody.transform.InverseTransformPoint(worldAnchor)
                : worldAnchor;
            ConfigurableJointMotion translation = allowTranslation
                ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            joint.xMotion = translation;
            joint.yMotion = translation;
            joint.zMotion = translation;
            joint.angularXMotion = ConfigurableJointMotion.Limited;
            joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Limited;
            joint.lowAngularXLimit = new SoftJointLimit { limit = -35f };
            joint.highAngularXLimit = new SoftJointLimit { limit = 35f };
            joint.angularYLimit = new SoftJointLimit { limit = 35f };
            joint.angularZLimit = new SoftJointLimit { limit = 35f };
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.slerpDrive = new JointDrive
            {
                positionSpring = spring,
                positionDamper = damper,
                maximumForce = Mathf.Infinity
            };
        }
    }
}
