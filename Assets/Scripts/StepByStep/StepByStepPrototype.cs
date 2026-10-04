using UnityEngine;

namespace StepByStep
{
    /// <summary>平台观察测试入口：只创建摄像机，不创建角色或骨骼。</summary>
    public sealed class StepByStepPrototype : MonoBehaviour
    {
        // 由 ClumsyRagdollGame 的独立原型入口注入；该观察入口暂时不生成角色，
        // 但保留字段可避免切换入口时破坏 Unity 编译。
        public GameObject ModelPrefab;
        public Vector3 Spawn;

        void Awake()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject go = new GameObject("StepByStep_MainCamera");
                go.tag = "MainCamera";
                camera = go.AddComponent<Camera>();
            }
            camera.targetDisplay = 0;
            camera.enabled = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.18f, 0.25f, 1f);
            camera.fieldOfView = 55f;
            camera.transform.position = new Vector3(0f, 1.8f, -4.5f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0.5f, 0f) - camera.transform.position, Vector3.up);
            if (camera.GetComponent<StepByStepFreeCamera>() == null)
                camera.gameObject.AddComponent<StepByStepFreeCamera>();
        }
    }
}
