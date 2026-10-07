using System.Collections.Generic;
using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Owns the low-level lifecycle operations shared by the proxy rig.
    /// It deliberately contains no scene lookup or gameplay policy.
    /// </summary>
    public static class RaccoonPhysicsLifecycle
    {
        public static void EnsureAutomaticSimulation(bool enabled, Object owner)
        {
            if (!enabled || Physics.simulationMode == SimulationMode.FixedUpdate)
                return;

            Physics.simulationMode = SimulationMode.FixedUpdate;
            Debug.Log("[RaccoonStep] Enabled automatic FixedUpdate physics simulation.", owner);
        }

        public static void SetBodiesActive(IList<Rigidbody> bodies, bool active)
        {
            if (bodies == null)
                return;

            for (int i = 0; i < bodies.Count; i++)
            {
                Rigidbody body = bodies[i];
                if (body == null)
                    continue;

                body.isKinematic = !active;
                body.useGravity = active;
            }
        }

        public static void ClearVelocities(IList<Rigidbody> bodies)
        {
            if (bodies == null)
                return;

            for (int i = 0; i < bodies.Count; i++)
            {
                Rigidbody body = bodies[i];
                if (body == null || body.isKinematic)
                    continue;

                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
    }
}
