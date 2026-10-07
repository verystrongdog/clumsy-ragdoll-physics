using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// Reports contact from any ragdoll limb to the aggregate grounding state.
    /// </summary>
    public sealed class LimbCollision : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;

        void OnCollisionEnter(Collision collision)
        {
            if (Ragdoll != null)
                Ragdoll.IsGrounded = true;
        }

        void OnCollisionStay(Collision collision)
        {
            if (Ragdoll != null)
                Ragdoll.IsGrounded = true;
        }
    }
}
