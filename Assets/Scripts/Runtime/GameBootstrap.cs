using UnityEngine;

namespace ClumsyRagdoll
{
    public sealed class MatchTuning : MonoBehaviour
    {
        [Range(0.05f, 0.95f)]
        public float Floppiness = 0.5f;

        public float MoveSpeed = 2.2f;
        public bool ShowHelp = true;
    }

    [DefaultExecutionOrder(-200)]
    public sealed class GameBootstrap : MonoBehaviour
    {
        MatchTuning _tuning;
        PuppetMotor _playerA;
        PuppetMotor _playerB;
        GUIStyle _label;
        GUIStyle _box;
        bool _stylesReady;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoStart()
        {
            if (FindAnyObjectByType<GameBootstrap>() != null)
                return;
            GameObject go = new GameObject("笨拙布娃娃");
            go.AddComponent<GameBootstrap>();
        }

        void Awake()
        {
            Physics.gravity = new Vector3(0f, -15f, 0f);
            Physics.defaultSolverIterations = 16;
            Physics.defaultSolverVelocityIterations = 6;
            Physics.defaultContactOffset = 0.012f;
            Physics.bounceThreshold = 1.2f;
            Time.fixedDeltaTime = 0.01f;
            Time.maximumDeltaTime = 0.08f;

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.58f, 0.64f, 0.7f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.55f, 0.74f, 0.88f);
            RenderSettings.fogStartDistance = 22f;
            RenderSettings.fogEndDistance = 58f;
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowDistance = 28f;

            foreach (Camera cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                cam.enabled = false;
                if (cam.CompareTag("MainCamera"))
                    cam.tag = "Untagged";
            }

            foreach (AudioListener listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                listener.enabled = false;

            foreach (Light existing in FindObjectsByType<Light>(FindObjectsSortMode.None))
                existing.enabled = false;

            GameObject sunGo = new GameObject("太阳");
            Light sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.12f;
            sun.color = new Color(1f, 0.97f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(54f, -32f, 0f);

            PlaygroundBuilder.Build(out Vector3 spawnA, out Vector3 spawnB);

            PuppetRig rigA = RagdollFactory.Create(
                "玩家一",
                0,
                new Color(0.93f, 0.45f, 0.28f),
                new Color(0.98f, 0.82f, 0.42f),
                spawnA,
                0f);
            PuppetRig rigB = RagdollFactory.Create(
                "玩家二",
                1,
                new Color(0.25f, 0.55f, 0.72f),
                new Color(0.62f, 0.9f, 0.84f),
                spawnB,
                0f);

            _tuning = gameObject.AddComponent<MatchTuning>();
            SharedScreenCamera view = SharedScreenCamera.Create(new[] { rigA.Pelvis.transform, rigB.Pelvis.transform });
            Physics.SyncTransforms();

            _playerA = rigA.gameObject.AddComponent<PuppetMotor>();
            _playerB = rigB.gameObject.AddComponent<PuppetMotor>();
            _playerA.View = view;
            _playerB.View = view;
            _playerA.Tuning = _tuning;
            _playerB.Tuning = _tuning;
            _playerA.Source = new KeyboardMouseInput();
            _playerB.Source = new ArrowKeysInput();
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.H))
                _tuning.ShowHelp = !_tuning.ShowHelp;
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                _tuning.Floppiness = Mathf.Clamp(_tuning.Floppiness - 0.05f, 0.05f, 0.95f);
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus))
                _tuning.Floppiness = Mathf.Clamp(_tuning.Floppiness + 0.05f, 0.05f, 0.95f);
            if (Input.GetKeyDown(KeyCode.F2))
            {
                _playerA.Respawn();
                _playerB.Respawn();
            }
        }

        void OnGUI()
        {
            EnsureStyles();
            string line = "松软 " + Mathf.RoundToInt(_tuning.Floppiness * 100f) + "    - 更僵硬    = 更松    H 说明    F2 双方重生";
            if (!_tuning.ShowHelp)
            {
                GUILayout.BeginArea(new Rect(12f, 8f, 720f, 40f), _box);
                GUILayout.Label(line, _label);
                GUILayout.EndArea();
                return;
            }

            GUILayout.BeginArea(new Rect(12f, 10f, 780f, 230f), _box);
            GUILayout.Label("笨拙布娃娃物理    本地同屏", _label);
            GUILayout.Label("玩家一（珊瑚）  WASD 移动    鼠标瞄准    按住左键抓取或攀爬    空格跳    R 重生", _label);
            GUILayout.Label("玩家二（青蓝）  方向键移动    IJKL 伸手    U/O 抬手或放手    右 Ctrl 抓    右 Shift 跳    M 重生", _label);
            GUILayout.Label("镜头  Q/E 旋转    滚轮远近。两人始终在同一画面里。", _label);
            GUILayout.Label("手会软软地跟过去。轻箱子拖得动，重箱子会把人拽倒，黄色把手和单杠可以抓。", _label);
            GUILayout.Label(line, _label);
            GUILayout.EndArea();
        }

        void EnsureStyles()
        {
            if (_stylesReady)
                return;

            Texture2D bg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bg.SetPixel(0, 0, new Color(0.07f, 0.09f, 0.11f, 0.82f));
            bg.Apply();
            bg.hideFlags = HideFlags.HideAndDontSave;

            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                richText = false,
                wordWrap = true,
                padding = new RectOffset(8, 8, 2, 2),
                normal = { textColor = new Color(0.96f, 0.96f, 0.94f) }
            };
            _box = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(8, 8, 8, 8),
                normal = { background = bg }
            };
            Font font = SurfaceKit.UiFont;
            if (font != null)
            {
                _label.font = font;
                const string sample =
                    "笨拙布娃娃物理 本地同屏 玩家一（珊瑚）WASD 移动 鼠标瞄准 按住左键抓取或攀爬 空格跳 R 重生 " +
                    "玩家二（青蓝）方向键 IJKL 伸手 U/O 抬手或放手 右 Ctrl 抓 右 Shift 跳 M " +
                    "镜头 Q/E 旋转 滚轮远近。两人始终在同一画面里。手会软软地跟过去。轻箱子拖得动，重箱子会把人拽倒，黄色把手和单杠可以抓。" +
                    "松软 0123456789 - 更僵硬 = 更松 H 说明 F2 双方重生 +";
                font.RequestCharactersInTexture(sample, _label.fontSize);
            }
            _stylesReady = true;
        }
    }
}
