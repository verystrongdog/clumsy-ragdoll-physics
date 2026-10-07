using System.Collections.Generic;
using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// Centralizes proxy hierarchy lookup and generated-collider bookkeeping.
    /// The registry is intentionally runtime-only; serialized scene references
    /// remain owned by RaccoonPhysicsRig for backwards compatibility.
    /// </summary>
    public sealed class RaccoonProxyRegistry
    {
        readonly Dictionary<Transform, Transform> _proxies = new Dictionary<Transform, Transform>();

        public void Clear() => _proxies.Clear();

        public void Register(Transform visualBone, Transform proxy)
        {
            if (visualBone != null && proxy != null)
                _proxies[visualBone] = proxy;
        }

        public bool TryGet(Transform visualBone, out Transform proxy)
        {
            if (visualBone != null && _proxies.TryGetValue(visualBone, out proxy) && proxy != null)
                return true;

            proxy = null;
            return false;
        }

        public Transform FindOrRegister(Transform visualBone, Transform physicsRoot, string proxyName)
        {
            Transform proxy;
            if (TryGet(visualBone, out proxy))
                return proxy;

            if (physicsRoot == null || string.IsNullOrEmpty(proxyName))
                return null;

            proxy = physicsRoot.Find(proxyName);
            if (proxy != null)
                Register(visualBone, proxy);
            return proxy;
        }

        public static void IgnoreInternalCollisions(IList<Collider> colliders)
        {
            if (colliders == null)
                return;

            for (int i = 0; i < colliders.Count; i++)
            {
                for (int j = i + 1; j < colliders.Count; j++)
                {
                    if (colliders[i] != null && colliders[j] != null)
                        Physics.IgnoreCollision(colliders[i], colliders[j], true);
                }
            }
        }
    }
}
