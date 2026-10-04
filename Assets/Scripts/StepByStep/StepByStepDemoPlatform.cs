using UnityEngine;

namespace StepByStep
{
    /// <summary>新系统专用演示平台和测试方块。</summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(10000)]
    public sealed class StepByStepDemoPlatform : MonoBehaviour
    {
        public Vector3 PlatformSize = new Vector3(8f, 0.3f, 8f);
        public Vector3 PlatformPosition = new Vector3(0f, -0.15f, 0f);
        public bool CreateCube = false;
        public bool LockMainCameraDuringPlay = true;
        public Vector3 CubePosition = new Vector3(0.8f, 0.65f, 0f);
        public Vector3 CubeSize = new Vector3(0.9f, 0.9f, 0.9f);

        Camera _lockedCamera;
        Vector3 _cameraPosition;
        Quaternion _cameraRotation;

        void OnEnable()
        {
            EnsurePlatform();
            ApplyPlatformTransform();
            if (CreateCube) EnsureCube();
        }

        void Start()
        {
            if (!LockMainCameraDuringPlay)
                return;

            _lockedCamera = Camera.main;
            if (_lockedCamera != null)
            {
                _cameraPosition = _lockedCamera.transform.position;
                _cameraRotation = _lockedCamera.transform.rotation;
            }
        }

        void LateUpdate()
        {
            if (!Application.isPlaying || !LockMainCameraDuringPlay)
                return;

            if (_lockedCamera == null)
                _lockedCamera = Camera.main;
            if (_lockedCamera != null)
                _lockedCamera.transform.SetPositionAndRotation(_cameraPosition, _cameraRotation);
        }

        void OnValidate()
        {
            ApplyPlatformTransform();
        }

        void ApplyPlatformTransform()
        {
            Transform existing = transform.Find("DemoPlatform");
            if (existing == null)
                return;
            existing.position = PlatformPosition;
            existing.localScale = PlatformSize;
        }

        void EnsurePlatform()
        {
            Transform existing = transform.Find("DemoPlatform");
            if (existing != null) return;
            GameObject platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = "DemoPlatform";
            platform.transform.SetParent(transform, false);
            platform.transform.position = PlatformPosition;
            platform.transform.localScale = PlatformSize;
            Renderer renderer = platform.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = MakeMaterial(new Color(0.22f, 0.28f, 0.34f));
        }

        void EnsureCube()
        {
            Transform existing = transform.Find("DemoCube");
            if (existing != null) return;
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "DemoCube";
            cube.transform.SetParent(transform, false);
            cube.transform.position = CubePosition;
            cube.transform.localScale = CubeSize;
            Rigidbody body = cube.AddComponent<Rigidbody>();
            body.mass = 2f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            Renderer renderer = cube.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = MakeMaterial(new Color(0.9f, 0.42f, 0.12f));
        }

        static Material MakeMaterial(Color color)
        {
            Shader shader = Shader.Find("Standard");
            Material material = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            material.color = color;
            return material;
        }
    }
}
