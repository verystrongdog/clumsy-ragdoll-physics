using UnityEngine;

namespace ClumsyRagdoll
{
    public sealed class SharedScreenCamera : MonoBehaviour
    {
        public Transform[] Targets;
        public float Yaw;
        public float Pitch = 32f;
        public float UserDistance = 7.2f;

        Vector3 _focus;
        bool _focusReady;

        public static SharedScreenCamera Create(Transform[] targets)
        {
            GameObject go = new GameObject("同屏镜头");
            Camera camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.74f, 0.88f);
            camera.fieldOfView = 52f;
            camera.nearClipPlane = 0.08f;
            camera.farClipPlane = 80f;
            camera.tag = "MainCamera";
            go.AddComponent<AudioListener>();

            SharedScreenCamera rig = go.AddComponent<SharedScreenCamera>();
            rig.Targets = targets;
            rig.Yaw = 0f;
            return rig;
        }

        void LateUpdate()
        {
            if (Input.GetKey(KeyCode.Q))
                Yaw -= 70f * Time.deltaTime;
            if (Input.GetKey(KeyCode.E))
                Yaw += 70f * Time.deltaTime;

            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0.001f)
                UserDistance = Mathf.Clamp(UserDistance - wheel * 8f, 4.2f, 16f);

            if (Targets == null || Targets.Length == 0)
                return;

            Vector3 mid = Vector3.zero;
            int count = 0;
            float span = 0f;
            for (int i = 0; i < Targets.Length; i++)
            {
                if (Targets[i] == null)
                    continue;
                mid += Targets[i].position;
                count++;
            }
            if (count == 0)
                return;
            mid /= count;

            for (int i = 0; i < Targets.Length; i++)
            {
                if (Targets[i] == null)
                    continue;
                span = Mathf.Max(span, Vector3.Distance(Targets[i].position, mid));
            }

            Vector3 focus = mid + Vector3.up * 0.85f;
            if (!_focusReady)
            {
                _focus = focus;
                _focusReady = true;
            }
            else
            {
                _focus = Vector3.Lerp(_focus, focus, 1f - Mathf.Exp(-4.5f * Time.deltaTime));
            }

            float distance = UserDistance + span * 0.85f;
            Quaternion orbit = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 desired = _focus + orbit * new Vector3(0f, 0f, -distance);
            Vector3 delta = desired - _focus;
            float len = delta.magnitude;
            if (len > 0.2f && Physics.SphereCast(_focus, 0.25f, delta / len, out RaycastHit hit, len, GameLayers.WorldMask, QueryTriggerInteraction.Ignore))
                desired = _focus + delta / len * Mathf.Max(0.8f, hit.distance - 0.2f);

            transform.position = Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-6f * Time.deltaTime));
            Vector3 look = _focus - transform.position;
            if (look.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        }
    }
}
