using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ClumsyRagdoll.EditorTools
{
    /// <summary>
    /// 教程里那些「在 Inspector / Project Settings 里手点」的步骤，做成菜单：
    ///   P1 步7  —— 新建图层 No Self Collision
    ///   P1 步8  —— Physics → Layer Collision Matrix 取消 No Self Collision × No Self Collision
    ///   P7 步46 —— 加 Tag「item」
    ///   阶段0   —— 建演示场景（地面/灯/道具/启动器）
    /// </summary>
    public static class ClumsyRagdollSetup
    {
        public const string LayerName = "No Self Collision";

        /// <summary>
        /// ★ 2026-10-01：**手臂链单独一层**。
        /// owner 报「倒地/被推/被抓时胳膊折进身体里」—— 根因是布娃娃自碰撞整个关着；
        /// 但自碰撞开不全（关节附近到处是构造性重叠），而**上臂/前臂/手 × 躯干是唯一一组互不重叠的**。
        /// 所以：手臂单独一层，与身体层互碰、与自身不碰。详见 `ClumsyRagdoll.ApplyLimbLayerCollision`。
        /// </summary>
        public const string LimbLayerName = "Ragdoll Limbs";
        public const string GrabbableTag = "item";
        public const string ScenePath = "Assets/Scenes/ClumsyRagdoll.unity";

        [MenuItem("笨拙布娃娃/1. 建立图层 / Tag / 碰撞矩阵", false, 1)]
        public static void SetupLayersAndTags()
        {
            int layer = EnsureLayer(LayerName);
            int limb = EnsureLayer(LimbLayerName);
            EnsureTag(GrabbableTag);
            DisableSelfCollision(layer);
            // 手臂层：× 自己 = 关（同一只手内部本来就有构造性重叠：上臂×手 80mm）
            //           × 身体层 = 开
            SetLayerPair(limb, limb, false);
            SetLayerPair(limb, layer, true);
            SetLayerPair(limb, 0, true);
            AssetDatabase.SaveAssets();
            Debug.Log("[ClumsyRagdollSetup] 图层「" + LayerName + "」= " + layer
                + "、「" + LimbLayerName + "」= " + limb
                + "；Tag「" + GrabbableTag + "」就绪；已取消同层自碰撞，手臂层与身体层互通。");
        }

        /// <summary>改「层 a × 层 b 是否碰撞」这一个格子（矩阵是 32 个 uint，第 i 个元素第 j 位）。</summary>
        public static void SetLayerPair(int a, int b, bool collide)
        {
            if (a < 0 || b < 0)
                return;
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[ClumsyRagdollSetup] 读不到 DynamicsManager.asset，运行期会用 Physics.IgnoreLayerCollision 兜底。");
                return;
            }
            SerializedObject dynamics = new SerializedObject(assets[0]);
            SerializedProperty matrix = dynamics.FindProperty("m_LayerCollisionMatrix");
            if (matrix == null || !matrix.isArray)
                return;
            SerializedProperty ra = matrix.GetArrayElementAtIndex(a);
            long ma = ra.longValue;
            if (collide)
                ma |= (1L << b);
            else
                ma &= ~(1L << b);
            ra.longValue = ma;
            SerializedProperty rb = matrix.GetArrayElementAtIndex(b);
            long mb = rb.longValue;
            if (collide)
                mb |= (1L << a);
            else
                mb &= ~(1L << a);
            rb.longValue = mb;
            dynamics.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------

        public static int EnsureLayer(string name)
        {
            int existing = LayerMask.NameToLayer(name);
            if (existing >= 0)
                return existing;

            SerializedObject tagManager = LoadTagManager();
            if (tagManager == null)
                return -1;
            SerializedProperty layers = tagManager.FindProperty("layers");
            // 0-7 是 Unity 内置层，只从 8 起找空位
            for (int i = 8; i < layers.arraySize; i++)
            {
                SerializedProperty slot = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = name;
                    tagManager.ApplyModifiedProperties();
                    return i;
                }
            }
            Debug.LogError("[ClumsyRagdollSetup] 没有空图层槽了。");
            return -1;
        }

        public static void EnsureTag(string tag)
        {
            SerializedObject tagManager = LoadTagManager();
            if (tagManager == null)
                return;
            SerializedProperty tags = tagManager.FindProperty("tags");
            for (int i = 0; i < tags.arraySize; i++)
                if (tags.GetArrayElementAtIndex(i).stringValue == tag)
                    return;
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
            tagManager.ApplyModifiedProperties();
        }

        static SerializedObject LoadTagManager()
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError("[ClumsyRagdollSetup] 读不到 ProjectSettings/TagManager.asset");
                return null;
            }
            return new SerializedObject(assets[0]);
        }

        /// <summary>教程 P1 步8：取消 No Self Collision × No Self Collision 的勾（写进 DynamicsManager.asset）。</summary>
        public static void DisableSelfCollision(int layer)
        {
            if (layer < 0)
                return;
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[ClumsyRagdollSetup] 读不到 DynamicsManager.asset，运行期会用 Physics.IgnoreLayerCollision 兜底。");
                return;
            }
            SerializedObject dynamics = new SerializedObject(assets[0]);
            SerializedProperty matrix = dynamics.FindProperty("m_LayerCollisionMatrix");
            if (matrix == null || !matrix.isArray)
            {
                Debug.LogWarning("[ClumsyRagdollSetup] 找不到 m_LayerCollisionMatrix。");
                return;
            }
            // 矩阵是 32 个 uint：第 i 个元素的第 j 位 = 层 i 与层 j 是否碰撞。
            SerializedProperty row = matrix.GetArrayElementAtIndex(layer);
            long mask = row.longValue;
            mask &= ~(1L << layer);
            row.longValue = mask;
            SerializedProperty mirrorRow = matrix.GetArrayElementAtIndex(layer);
            mirrorRow.longValue = mask;
            dynamics.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------

        [MenuItem("笨拙布娃娃/2. 生成演示场景", false, 2)]
        public static void BuildDemoScene()
        {
            SetupLayersAndTags();

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(RaccoonSkeleton.ModelPath);
            if (model == null)
            {
                Debug.LogError("[ClumsyRagdollSetup] 找不到模型 " + RaccoonSkeleton.ModelPath);
                return;
            }

            UnityEngine.SceneManagement.Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 地面
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "地面";
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(240f, 1f, 240f);
            Paint(ground, new Color(0.44f, 0.46f, 0.49f));

            // 参照物：两根柱子，方便一眼看出倒了没有
            for (int i = 0; i < 2; i++)
            {
                GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pole.name = "参照柱" + (i + 1);
                pole.transform.position = new Vector3(i == 0 ? -2.2f : 2.2f, 1.6f, 2.4f);
                pole.transform.localScale = new Vector3(0.18f, 3.2f, 0.18f);
                Paint(pole, new Color(0.88f, 0.88f, 0.92f));
            }

            // 灯
            GameObject sunGo = new GameObject("太阳");
            Light sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.color = new Color(1f, 0.97f, 0.92f);
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(52f, -34f, 0f);

            // 道具（P7 步46：加 Rigidbody + Tag = item）
            GameObject propRoot = new GameObject("道具");
            float[] masses = { 2f, 6f, 14f };
            float[] sizes = { 0.30f, 0.45f, 0.60f };
            for (int i = 0; i < 3; i++)
            {
                GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "箱子" + (i + 1) + "·" + masses[i] + "kg";
                box.transform.SetParent(propRoot.transform, false);
                box.transform.position = new Vector3(1.6f + i * 1.1f, sizes[i] * 0.5f + 0.02f, 1.6f);
                box.transform.localScale = Vector3.one * sizes[i];
                Paint(box, Color.HSVToRGB(0.09f + i * 0.05f, 0.5f, 0.9f));
                Rigidbody rb = box.AddComponent<Rigidbody>();
                rb.mass = masses[i];
                box.tag = GrabbableTag;
            }

            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            bar.name = "单杠";
            bar.transform.SetParent(propRoot.transform, false);
            bar.transform.position = new Vector3(2.8f, 1.6f, 3.8f);
            bar.transform.localScale = new Vector3(0.07f, 1.1f, 0.07f);
            bar.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            Paint(bar, new Color(0.95f, 0.85f, 0.3f));
            bar.tag = GrabbableTag;

            // 启动器
            GameObject gameGo = new GameObject("布娃娃启动器");
            ClumsyRagdollGame game = gameGo.AddComponent<ClumsyRagdollGame>();
            game.ModelPrefab = model;

            EditorSceneManager.MarkSceneDirty(scene);
            System.IO.Directory.CreateDirectory(Application.dataPath + "/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.Refresh();
            Debug.Log("[ClumsyRagdollSetup] 演示场景已生成：" + ScenePath);
        }

        static void Paint(GameObject go, Color color)
        {
            Renderer r = go.GetComponent<Renderer>();
            if (r != null)
                r.material.color = color;
        }

        [MenuItem("笨拙布娃娃/3. 只在当前场景补图层与 Tag", false, 3)]
        public static void SetupOnly()
        {
            SetupLayersAndTags();
        }
   /// <summary>
        /// 把场景里那个 ClumsyRagdollGame 的 Recipe 换成一个全新的代码默认值。
        ///
        /// **为什么必须有这个菜单**：Recipe 是 [Serializable] 的，场景把它连同旧值一起存了下来。
        /// 改了 C# 里的字段初始值，**旧字段不会跟着变**（只有新加的字段会拿到初始值）。
        /// 实测就是因为这个，JumpSpeed 一直停在 5.5、GrabTriggerScale 一直停在 2.2，
        /// 而在 Inspector 里看不出任何异常。
        /// </summary>
        [MenuItem("笨拙布娃娃/4. 重置配方为代码默认值（改过默认值后必跑）", false, 4)]
        public static void ResetRecipeToDefaults()
        {
            ClumsyRagdollGame game = Object.FindFirstObjectByType<ClumsyRagdollGame>();
            if (game == null)
            {
                Debug.LogError("[ClumsyRagdollSetup] 当前场景里没有布娃娃启动器。");
                return;
            }
            game.Recipe = new RagdollRecipe();
            EditorUtility.SetDirty(game);
            UnityEngine.SceneManagement.Scene scene = game.gameObject.scene;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ClumsyRagdollSetup] 配方已重置为代码默认值并保存场景：" + scene.path
                + "（JumpSpeed=" + game.Recipe.JumpSpeed
                + " GrabTriggerScale=" + game.Recipe.GrabTriggerScale
                + " ArmDownDegrees=" + game.Recipe.ArmDownDegrees
                + " LockPassengerAngular=" + game.Recipe.LockPassengerAngular + "）");
        }

        }

}
