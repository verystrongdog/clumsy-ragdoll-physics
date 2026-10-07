using UnityEngine;

namespace RaccoonStep
{
    /// <summary>Creates and drives the temporary spine recovery joint only.</summary>
    public static class RaccoonRecoveryJoint
    {
        public static ConfigurableJoint Create(Transform spine, Transform hips, ConfigurableJoint original)
        {
            if (spine == null || hips == null)
                return null;

            Rigidbody childBody = spine.GetComponent<Rigidbody>();
            Rigidbody parentBody = hips.GetComponent<Rigidbody>();
            if (childBody == null || parentBody == null)
                return null;

            ConfigurableJoint joint = spine.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = parentBody;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = original != null ? original.anchor : Vector3.zero;
            joint.connectedAnchor = original != null ? original.connectedAnchor : Vector3.zero;
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.configuredInWorldSpace = true;
            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.slerpDrive = new JointDrive
            {
                positionSpring = 2200f,
                positionDamper = 220f,
                maximumForce = Mathf.Infinity
            };
            return joint;
        }

        public static void Drive(ConfigurableJoint joint, Quaternion worldTarget, float strength)
        {
            if (joint == null)
                return;

            float s = Mathf.Clamp01(strength);
            JointDrive drive = joint.slerpDrive;
            drive.positionSpring = Mathf.Lerp(0f, 2200f, s);
            drive.positionDamper = Mathf.Lerp(0f, 220f, s);
            drive.maximumForce = Mathf.Infinity;
            joint.slerpDrive = drive;
            joint.targetRotation = worldTarget;
        }

        public static void SetDriveEnabled(ConfigurableJoint joint, bool enabled)
        {
            if (joint == null)
                return;

            JointDrive drive = joint.slerpDrive;
            drive.positionSpring = enabled ? 2200f : 0f;
            drive.positionDamper = enabled ? 220f : 0f;
            drive.maximumForce = enabled ? Mathf.Infinity : 0f;
            joint.slerpDrive = drive;
        }

        public static void Release(ConfigurableJoint joint)
        {
            if (joint != null)
                Object.Destroy(joint);
        }
    }
}
