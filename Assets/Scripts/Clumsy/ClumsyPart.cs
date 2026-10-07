using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// Runtime binding between one semantic ragdoll part and its scene objects.
    /// </summary>
    public sealed class ClumsyPart
    {
        public PartSpec Spec;
        public Transform Bone;
        public Rigidbody Body;
        public ConfigurableJoint Joint;
        public Collider Shape;
        public float Spring;
        public float Length;
        public Vector3 LocalRight;
        public Vector3 LocalUp;
        public Vector3 RestPosition;
        public Quaternion RestRotation;
        public Quaternion RestLocalToBody;
        public Quaternion RestLocal;

        public bool IsArmChain
        {
            get { return RagdollPartId.IsArmOrHand(Spec.Key); }
        }

        public bool IsElbowDownstream
        {
            get { return RagdollPartId.IsForearmOrHand(Spec.Key); }
        }

        public bool IsPelvis { get { return Spec.Role == PartRole.Pelvis; } }
        public bool HasJoint { get { return Joint != null; } }
    }
}
