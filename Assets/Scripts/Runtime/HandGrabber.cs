using UnityEngine;

namespace ClumsyRagdoll
{
    public sealed class HandGrabber : MonoBehaviour
    {
        public PuppetRig Rig;
        public bool Holding;
        public bool ClimbAnchor;
        public Rigidbody Held;

        readonly Collider[] _hits = new Collider[24];
        FixedJoint _joint;
        float _cooldown;

        public void SetGrab(bool want)
        {
            if (_cooldown > 0f)
                _cooldown -= Time.fixedDeltaTime;

            if (!want)
            {
                if (Holding)
                    Release();
                return;
            }

            if (!Holding && _cooldown <= 0f)
                TryGrab();
        }

        public void Release()
        {
            if (_joint != null)
                Destroy(_joint);
            Clear();
        }

        void OnJointBreak(float breakForce)
        {
            _joint = null;
            Clear();
            _cooldown = 0.28f;
        }

        void OnDisable()
        {
            Release();
        }

        void Clear()
        {
            _joint = null;
            Holding = false;
            ClimbAnchor = false;
            Held = null;
        }

        void TryGrab()
        {
            int count = Physics.OverlapSphereNonAlloc(transform.position, 0.2f, _hits, ~0, QueryTriggerInteraction.Ignore);
            Collider best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Collider col = _hits[i];
                if (col == null || col.GetComponent<NotGrabbable>() != null)
                    continue;

                BodyPart part = col.GetComponentInParent<BodyPart>();
                if (part != null)
                {
                    PuppetRig owner = part.GetComponentInParent<PuppetRig>();
                    if (owner == Rig)
                        continue;
                }

                Vector3 closest = col.ClosestPoint(transform.position);
                float score = Vector3.Distance(transform.position, closest);
                Rigidbody rb = col.attachedRigidbody;
                bool dynamicBody = rb != null && !rb.isKinematic;
                if (dynamicBody)
                    score -= 0.04f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = col;
                }
            }

            if (best == null || bestScore > 0.22f)
                return;

            Rigidbody connected = best.attachedRigidbody;
            FixedJoint joint = gameObject.AddComponent<FixedJoint>();
            joint.connectedBody = connected;
            joint.enableCollision = false;
            joint.breakForce = connected == null || connected.isKinematic
                ? 1700f
                : Mathf.Clamp(380f + connected.mass * 28f, 380f, 2400f);
            joint.breakTorque = joint.breakForce * 0.65f;

            _joint = joint;
            Holding = true;
            Held = connected;
            ClimbAnchor = connected == null || connected.isKinematic;
            Rigidbody self = GetComponent<Rigidbody>();
            if (self != null)
                self.WakeUp();
            if (connected != null)
                connected.WakeUp();
        }
    }
}
