using UnityEngine;

namespace RaccoonStep
{
    /// <summary>
    /// 将屏幕鼠标位置映射为脚步落点意图。
    /// 屏幕中心是中性位置，左右控制横向落点，上方控制前进方向。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaccoonMouseInput : MonoBehaviour
    {
        public RaccoonFootController FootController;
        public bool EnableInput = true;
        public Camera InputCamera;
        public float GroundHeight;

        void Awake()
        {
            if (FootController == null)
                FootController = GetComponent<RaccoonFootController>();
            if (InputCamera == null)
                InputCamera = Camera.main;
        }

        void Update()
        {
            if (!EnableInput || FootController == null || Screen.width <= 0 || Screen.height <= 0)
                return;

            FootController.UseMouseTarget = true;
            if (InputCamera == null)
                InputCamera = Camera.main;

            Ray ray = InputCamera != null
                ? InputCamera.ScreenPointToRay(Input.mousePosition)
                : new Ray(transform.position + Vector3.up * 2f, Vector3.down);
            Plane ground = new Plane(Vector3.up, new Vector3(0f, GroundHeight, 0f));
            float distance;
            Vector3 worldTarget;
            if (ground.Raycast(ray, out distance))
            {
                worldTarget = ray.GetPoint(distance);
            }
            else
            {
                worldTarget = transform.position + transform.forward * 1.0f;
                worldTarget.y = GroundHeight;
            }

            Vector3 localTarget = transform.InverseTransformPoint(worldTarget);
            Vector2 normalizedTarget = new Vector2(
                Mathf.Clamp(localTarget.x / 1.5f, -1f, 1f),
                Mathf.Clamp(localTarget.z / 2.5f, -1f, 1f));
            FootController.SetMouseTarget(normalizedTarget);

            if (Input.GetMouseButtonDown(0))
            {
                Debug.Log($"[RaccoonStep] Mouse click received: target={worldTarget}", this);
                FootController.RequestStep(worldTarget);
            }
        }
    }
}
