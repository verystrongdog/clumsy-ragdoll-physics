using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 场景启动器：把教程 7 集里所有「场景侧设置」一次做完，并在 Play 时把浣熊拼成布娃娃。
    ///
    /// 对应教程的散装 Inspector 操作：
    ///   阶段0 步2/3 准备模型与动画 · 阶段1 P1 搭骨 · 阶段2 P2 自平衡 · 阶段3 P3 移动
    ///   阶段4/5 P4/P5 姿势 · 阶段6 P6 相机 · 阶段7 P7 质量与抓取。
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public sealed class ClumsyRagdollGame : MonoBehaviour
    {
        [Header("模型（指向 Assets/Models/Characters/111_raccoon_rigged.fbx）")]
        public GameObject ModelPrefab;

        [Header("配方（教程的全部常数都在这里）")]
        public RagdollRecipe Recipe = new RagdollRecipe();

        [Header("摆放")]
        public Vector3 SpawnPosition = new Vector3(0f, 0.05f, 0f);
        public float SpawnYaw = 0f;

        [Header("开关")]
        public bool BuildOnAwake = true;
        public bool BuildWorldIfMissing = true;
        public bool ShowHud = true;

        [Header("自动演示（进 Play 就能看，不用按键；按 0 关掉换成你自己操作）")]
        [Tooltip("按脚本走一遍：待机 → 走 → 冲刺 → 停 → 跳 → 走过去抓箱子 → 拖着走 → 松手，循环。")]
        public bool AutoDemo = false;
        [Header("新系统")]
        [Tooltip("启用真正独立的《一步一脚印》原型；不会创建旧 ClumsyRagdoll 系统。")]
        // 完整 ClumsyRagdoll 场景默认生成浣熊布娃娃；仅由 StepByStepTest 显式开启独立原型。
        public bool UseStandaloneStepByStep = false;

        [Header("运行期状态")]
        public ClumsyRagdoll Ragdoll;
        public ClumsyController Controller;
        public ClumsyPoseDriver Pose;
        public ClumsyCameraRig CameraRig;
        public ClumsyArmIK ArmIK;
        public HandGripper LeftGrip;
        public HandGripper RightGrip;
        public ClumsyInteraction Interaction;
        /// <summary>★ 2026-10-02 双手搬运（HFF 的抱东西）。</summary>
        public ClumsyCarry Carry;
        public ClumsyStepActivity StepActivity;
        /// <summary>《一步一脚印》的独立脚步/重心模拟器。启用后不走旧的 HFF 式控制器。</summary>
        public StepByStepMotor StepMotor;

        [Header("动画（教程那套「先有动画」的门槛）")]
        [Tooltip("AnimatorController。现在指 Assets/Animations/RaccoonActions.controller"
                 + "（Blender 手 K 的 9 段：基准帧 + Idle/Walk/Run/Air/Flail/Land/Push/Wave）。\n"
                 + "旧的那套烘出来的 RaccoonLocomotion.controller 仍在，用作 A/B 基线。\n"
                 + "为空 = 动画身体不工作，姿势驱动自动回落到程序化步态（不会卡住）。")]
        public RuntimeAnimatorController AnimatorController;
        public ClumsyAnimationSource Animation;
        public ClumsyWobble Wobble;
        /// <summary>教程 §三 的「人工力」层（方法2 弹簧 / 方法3 力矩，可 A/B）。</summary>
        public ClumsyBalance Balance;
        public readonly List<GameObject> Props = new List<GameObject>();

        public float RestPelvisHeight = 1f;
        public float StandingRun;
        public float StandingBest;

        GUIStyle _label;
        GUIStyle _box;
        bool _styles;

        void Awake()
        {
            if (UseStandaloneStepByStep)
            {
                StepByStep.StepByStepPrototype prototype = gameObject.AddComponent<StepByStep.StepByStepPrototype>();
                return;
            }
            ApplyEnvironment();
            if (BuildWorldIfMissing)
                EnsureWorld();
            if (BuildOnAwake)
                Build();
        }

        // ------------------------------------------------------------------

        public void ApplyEnvironment()
        {
            // ★ 一定要显式打开自动物理。Physics.simulationMode **是会写进 ProjectSettings 的持久设置**，
            //   编辑器里跑过诊断台/展示工具（它们要手动 Physics.Simulate）之后会留在 Script ——
            //   那样进 Play 之后整个物理是冻的：角色站着不动、速度恒为 0、动画也不推进。
            //   （实测就是这么踩到的：演示看起来完全没反应，查了半天才发现是这一条。）
            Physics.simulationMode = SimulationMode.FixedUpdate;
            Physics.gravity = new Vector3(0f, Recipe.GravityY, 0f);
            Time.fixedDeltaTime = Recipe.FixedDeltaTime;
            Time.maximumDeltaTime = 0.1f;
            Physics.defaultContactOffset = 0.01f;
            Physics.bounceThreshold = 2f;
            Physics.defaultSolverIterations = Recipe.SolverIterations;
            Physics.defaultSolverVelocityIterations = Recipe.SolverVelocityIterations;
            Physics.sleepThreshold = 0.005f;
            Physics.defaultMaxAngularSpeed = 25f;

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.65f, 0.7f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.56f, 0.72f, 0.86f);
            RenderSettings.fogStartDistance = 26f;
            RenderSettings.fogEndDistance = 110f;
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowDistance = 40f;
        }

        /// <summary>缺什么补什么：地面 + 灯 + 几个可抓的道具（教程 P7 步46）。</summary>
        public void EnsureWorld()
        {
            if (GameObject.Find("地面") == null)
            {
                GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ground.name = "地面";
                ground.transform.position = new Vector3(0f, -0.5f, 0f);
                ground.transform.localScale = new Vector3(240f, 1f, 240f);
                Paint(ground, new Color(0.44f, 0.46f, 0.49f));
            }

            if (Object.FindFirstObjectByType<Light>() == null)
            {
                GameObject sunGo = new GameObject("太阳");
                Light sun = sunGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 1.1f;
                sun.color = new Color(1f, 0.97f, 0.92f);
                sun.shadows = LightShadows.Soft;
                sunGo.transform.rotation = Quaternion.Euler(52f, -34f, 0f);
            }

            // ⚠️ 场景里可能躺着一份**旧的道具**（早先某次保存进去的）：那些箱子既没有 Grippable、
            //    也没摆在矮桌上（直接在地上 = 这只浣熊够不到）。只判断「有没有」就会把旧的当成好的，
            //    结果是抓取状态机永远 Search、演示里抓不到任何东西。
            //    所以判据改成「有没有 Grippable」—— 旧的就地重建。
            GameObject existing = GameObject.Find("道具");
            bool stale = existing != null && existing.GetComponentInChildren<Grippable>() == null;
            if (stale)
            {
                Debug.Log("[ClumsyRagdollGame] 场景里的「道具」是旧版本（没有 Grippable），重建。");
                Object.DestroyImmediate(existing);
                existing = null;
            }
            if (Props.Count == 0 && existing == null)
                BuildProps(new Vector3(1.6f, 0f, 1.6f));
        }

        public void BuildProps(Vector3 origin)
        {
            GameObject root = new GameObject("道具");
            float[] masses = { 2f, 6f, 14f };
            Vector3[] sizes = { new Vector3(0.30f, 0.30f, 0.30f), new Vector3(0.45f, 0.45f, 0.45f), new Vector3(0.60f, 0.60f, 0.60f) };

            // ---- 一张矮桌：**道具必须放在手够得着的高度** ----
            // 实测（ClumsyRagdollBench.ArmIKReport / CarryReport）：
            //   浣熊肩高 0.93m、左臂可达 0.476m ⟹ 可达空间是以肩为心、半径 0.476m 的球，
            //   y 大致在 **0.45m ~ 1.40m** 之间。**地面（y≈0）永远在这个球外面。**
            //   所以教程 P7 那种「箱子直接摆在地上」的场景在这只模型上是**不可交互**的：
            //   手会悬在箱子上方 0.5m 处干挠。这跟「手部 IK 能不能跑」是两回事 ——
            //   IK 本身已经验证到 4~7mm。要真能摸地面，得让 IK 连带弯腰（本工程没做，见交接 §八）。
            // ⚠️ 桌子要**窄**、箱子要摆在**靠人这一侧的边缘**。
            //    第一版桌子 3.8m 宽 1.3m 深、箱子摆在桌子正中 —— 浣熊只能站到桌子边上，
            //    箱子离肩 1.2m，远远超出 0.476m 的臂长，**永远够不到**（实测：演示里走过去就卡在桌沿）。
            //    窄桌 + 贴边摆，肩到箱子才落到 0.4m 以内。
            // ★ 2026-10-02 双手搬运：桌面 0.55 → **0.62**。
            //   可达判据（全部以**骨盆**为原点，肩 = 骨盆 + 前 0.14m↓ / 上 0.28m）：
            //       肩到抓取点 = √((F + 0.14)² + (箱子中心高 − 0.89)²) ≤ 可达 0.478m
            //   箱子中心高 = 桌面 + 半高。桌面 0.55 ⇒ 0.30 的箱子中心 0.70 ⇒ 竖直项 −0.19m
            //   ⇒ **水平 F 只允许 0.27m**；抬到 0.62 ⇒ 中心 0.77 ⇒ 竖直项 −0.12m ⇒ F 允许 0.32m。
            //   （越高越好，直到箱子中心 ≈ 肩高。）
            const float tableTop = 0.62f;
            GameObject table = GameObject.CreatePrimitive(PrimitiveType.Cube);
            table.name = "矮桌";
            table.transform.SetParent(root.transform, false);
            table.transform.position = origin + new Vector3(1.2f, tableTop - 0.05f, 0f);
            table.transform.localScale = new Vector3(3.0f, 0.10f, 0.70f);
            Paint(table, new Color(0.60f, 0.46f, 0.33f));

            for (int i = 0; i < 3; i++)
            {
                GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "箱子" + (i + 1) + "·" + masses[i] + "kg";
                box.transform.SetParent(root.transform, false);
                // ★ 2026-10-02 双手搬运：−0.15 → **−0.28**（箱子中心正好压在桌子近沿上，略微外悬）。
                //   桌子近沿在 origin.z − 0.35、深 0.70。原来摆 −0.15 等于**往桌里缩了 0.20m**：
                //   浣熊走到桌边被挡住时骨盆离箱子 0.42m，而上面那条判据只允许 0.32m
                //   ⇒ **走过去永远够不到**（owner 原话：「物品放在平台上，你直接走过去拿不到」）。
                //   中心压到近沿（−0.28）之后 F ≈ 0.29m，两只手才真的搭得上。重心仍在桌面内 ⇒ 不会倒。
                box.transform.position = origin + new Vector3(i * 0.9f, tableTop + sizes[i].y * 0.5f + 0.01f, -0.28f);
                box.transform.localScale = sizes[i];
                Paint(box, Color.HSVToRGB(0.09f + i * 0.05f, 0.5f, 0.9f));
                Rigidbody rb = box.AddComponent<Rigidbody>();
                rb.mass = masses[i];

                // 每个物体自己说「被抓住时能怎么动」—— 这是教程 P7 的 tag 做不到的事。
                // 越重的箱子抓得越死（能在手里晃的幅度越小），于是「拖一个大箱子」和
                // 「拎一个小方块」手感不同，而代码里没有任何 if (mass > ...) 的分支。
                Grippable grip = box.AddComponent<Grippable>();
                grip.UseCustomMotions = true;
                grip.Motions = JointMotionsConfig.Make(ConfigurableJointMotion.Limited, 38f - i * 12f);
                if (TagExists(Recipe.GrabbableTag))
                    box.tag = Recipe.GrabbableTag;
                Props.Add(box);
            }

            // 一根单杠，用来试「抓住能不能吊住自己」
            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            bar.name = "单杠";
            bar.transform.SetParent(root.transform, false);
            // 高度也按可达球定：直立时手最高能摸到肩 + 0.476 ≈ 1.40m，
            // 原来的 1.6m 在可达球外面（得跳起来碰运气）。改成 1.30m。
            bar.transform.position = origin + new Vector3(1.2f, 1.30f, 2.2f);
            bar.transform.localScale = new Vector3(0.07f, 1.1f, 0.07f);
            bar.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            Paint(bar, new Color(0.95f, 0.85f, 0.3f));
            if (TagExists(Recipe.GrabbableTag))
                bar.tag = Recipe.GrabbableTag;

            // 单杠：由**物体自己**声明「被抓住时能怎么动」（Sergio 的 Grippable + JointMotionsConfig）。
            // 单杠是静止的（没有 Rigidbody），所以抓取关节的 connectedBody 会是 null = 连到**世界**：
            // 手被钉在杠上，人就能吊住自己。三个角运动 Locked = 焊死（教程 P7 的 FixedJoint 语义）。
            Grippable barGrip = bar.AddComponent<Grippable>();
            barGrip.UseCustomMotions = true;
            barGrip.Motions = JointMotionsConfig.Make(ConfigurableJointMotion.Locked, 0f);
            barGrip.BreakForce = 90000f;
                bar.tag = Recipe.GrabbableTag;
        }

        static void Paint(GameObject go, Color c)
        {
            Renderer r = go.GetComponent<Renderer>();
            if (r == null)
                return;
            r.material.color = c;
        }

        public static bool TagExists(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return false;
            try
            {
                GameObject probe = new GameObject("tag-probe");
                probe.tag = tag;
                if (Application.isPlaying)
                    Object.Destroy(probe);
                else
                    Object.DestroyImmediate(probe);
                return true;
            }
            catch (UnityException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------

public void Build()
        {
            if (Ragdoll != null)
                return;
            if (ModelPrefab == null)
            {
                Debug.LogError("[ClumsyRagdollGame] ModelPrefab 没赋值，指向 " + RaccoonSkeleton.ModelPath);
                return;
            }

            GameObject model = Instantiate(ModelPrefab, SpawnPosition, Quaternion.Euler(0f, SpawnYaw, 0f));
            model.name = "浣熊（布娃娃）";

            // ---- P1 搭布娃娃 + P2 自平衡 ----
            Ragdoll = ClumsyRagdoll.Build(model, Recipe);
            if (Ragdoll == null)
                return;

            // 出生姿态的碰撞体底面贴到地面，避免一开始就被挤出去
            Ragdoll.SnapAboveGround(0f, 0.004f);
            RestPelvisHeight = Mathf.Max(0.1f, Ragdoll.Hips.RestPosition.y);

            // ---- P3 步8：每个部件挂 LimbCollision ----
            for (int i = 0; i < Ragdoll.Parts.Count; i++)
            {
                ClumsyPart p = Ragdoll.Parts[i];
                LimbCollision lc = p.Bone.gameObject.AddComponent<LimbCollision>();
                lc.Ragdoll = Ragdoll;
            }
            // ---- 教程 §三：撑直（方法2 稳定器弹簧 / 方法3 自己施加力矩）----
            // 必须在布娃娃建好之后 —— 它每 FixedUpdate 直接往 hips 上施加力矩。
            Balance = model.AddComponent<ClumsyBalance>();
            Balance.Setup(Ragdoll, Recipe);


            // ---- P3：PlayerController 挂在「hips」上，力加到 hips 的 Rigidbody ----
            Controller = model.AddComponent<ClumsyController>();
            Controller.Ragdoll = Ragdoll;
            Controller.Recipe = Recipe;
            Controller.Facing = Ragdoll.Root;
            Controller.Source = new KeyboardInput();

            // 单脚选择落点的登山式活动层。它接管输入，但仍复用 Controller 的脚种植步态、
            // 物理脚底摩擦和重心反馈；按 0 关闭演示后即可逐步迈步。
            if (UseStandaloneStepByStep)
            {
                StepActivity = model.AddComponent<ClumsyStepActivity>();
                StepActivity.Setup(Ragdoll, Controller, Ragdoll.Root);

            // 新 locomotion 只把“输入”交给物理：支撑脚锚点、摆动脚轨迹和重心反馈
            // 会在 StepByStepMotor 内部完成。旧 Controller/StepActivity/Balance 的
            // 移动力与托举力必须关闭，否则两套模拟会同时抢同一组刚体。
            StepMotor = model.AddComponent<StepByStepMotor>();
            StepMotor.Ragdoll = Ragdoll;
            StepMotor.Recipe = Recipe;
            StepMotor.Facing = Ragdoll.Root;
            StepMotor.Source = Controller.Source;
            StepMotor.InputController = Controller;
            Controller.enabled = false;
            StepActivity.enabled = false;
                Balance.enabled = false;
            }
            else
            {
                // 完整 ClumsyRagdoll 模式必须保留旧控制器和平衡层，否则生成后会摊倒。
                // 手动抬脚输入只属于 StepByStep 场景；即使模型预制体或热重载残留了
                // ClumsyStepActivity，也不能让它在完整模式中接管鼠标左右键。
                Controller.MovementMode = ClumsyMovementMode.AutomaticGait;
                Controller.ManualStepActive = false;
                ClumsyStepActivity[] strayStepActivities =
                    model.GetComponentsInChildren<ClumsyStepActivity>(true);
                for (int i = 0; i < strayStepActivities.Length; i++)
                {
                    strayStepActivities[i].Enabled = false;
                    Destroy(strayStepActivities[i]);
                }
                Controller.enabled = true;
                Balance.enabled = true;
            }

            // ---- P4/P5：程序化 targetRotation ----
            Pose = model.AddComponent<ClumsyPoseDriver>();
            Pose.Ragdoll = Ragdoll;
            Pose.Controller = Controller;
            Pose.Recipe = Recipe;

            // ---- 动画身体（教程的双身体架构）+ 笨拙层 ----
            // 动画身体必须在这里建：姿势驱动 Pose() 每帧要去读它的骨骼局部旋转。
            Animation = model.AddComponent<ClumsyAnimationSource>();
            Animation.Setup(Ragdoll, Recipe, Controller, ModelPrefab, AnimatorController);
            Pose.Animation = Animation;

            Wobble = model.AddComponent<ClumsyWobble>();
            Wobble.Setup(Ragdoll, Recipe);
            Pose.Wobble = Wobble;
            // 姿态驱动只负责关节的肌肉目标，不负责移动或位移；脚步仍完全由
            // StepByStepMotor 产生。关闭它会让当前关节在重力下立即塌陷。
            Pose.enabled = true;
            Recipe.UseAnimation = false;
            Animation.enabled = false;
            Wobble.enabled = false;

            // ---- 手部两骨 IK（EP13 的 Two Bone IK + Multi Rotation，自己算）----
            // **必须在姿势驱动之前建好并把引用交进去**：IK 只算 override，写关节的仍然是 Pose
            // （保持「一个关节一个写入者」）。
            ArmIK = model.AddComponent<ClumsyArmIK>();
            ArmIK.Setup(Ragdoll, Recipe);
            Pose.ArmIK = ArmIK;

            // ---- 手部抓取（Sergio 的 GripModule）----
            LeftGrip = AttachGripper(Ragdoll.Find("handl"), "l", 0, 0);
            RightGrip = AttachGripper(Ragdoll.Find("handr"), "r", 1, 1);
            // 基础脚步模式暂时清空模型上的抓取输入；左右鼠标键只服务于左右脚落点。
            if (LeftGrip != null) LeftGrip.enabled = false;
            if (RightGrip != null) RightGrip.enabled = false;

            // ---- 环境交互状态机（EP13 的 Environment Interaction State Machine）----
            Interaction = model.AddComponent<ClumsyInteraction>();
            Interaction.Setup(Ragdoll, Recipe, ArmIK, LeftGrip, RightGrip);

            // ---- ★ 2026-10-02 双手搬运（owner：「实现类似人类一败涂地的搬运物体的互动逻辑」）----
            // 两只手都按抓取键 + 同一个物体都够得到 ⇒ 走这一层（一只手仍然走上面那套旧路径）。
            Carry = model.AddComponent<ClumsyCarry>();
            Carry.Setup(Ragdoll, Recipe, ArmIK, LeftGrip, RightGrip, Interaction);
            if (Controller != null)
                Controller.Carry = Carry;
            // 搬运不再给骨盆施加 HFF 式“抱起/托举”力；抓取组件仍可用于手部交互。
            Carry.enabled = false;

            // ---- P6：相机 ----
            GameObject camGo = new GameObject("相机台");
            CameraRig = camGo.AddComponent<ClumsyCameraRig>();
            CameraRig.Setup(Ragdoll, Recipe);
        }

        /// <summary>
        /// 把抓取器挂到**手骨**上。
        ///
        /// 改之前挂的是「手下面一个带触发球的空物体 + HandGrab」，两个坑：
        ///   ① 那个脚本里 <c>GetComponent&lt;Rigidbody&gt;()</c> 取不到手的刚体（刚体在父级），抓取静默失效
        ///      —— 交接文档 §三 最后一条记的就是这个；
        ///   ② 触发球半径 0.208m 把手自己的 shoulder / arm / forearm / hand 四根骨头全罩进去了，
        ///      而 tag 过滤挡不住它们（它们也带 item tag），于是「抓取范围里 6 个 collider 有 4 个是自己」。
        /// 现在：挂在手骨上（天然拿得到 Rigidbody），检测改成以手为心的 OverlapSphere 查询
        /// （<see cref="HandGripper.Poll"/>），并用「是不是自己身上的一部分」正面挡掉自抓。
        /// </summary>
        HandGripper AttachGripper(ClumsyPart hand, string suffix, int handIndex, int mouseButton)
        {
            if (hand == null || hand.Bone == null)
                return null;

            HandGripper g = hand.Bone.gameObject.AddComponent<HandGripper>();
            g.Setup(Ragdoll, Recipe, ArmIK, suffix, handIndex, mouseButton);
            return g;
        }

        // ------------------------------------------------------------------

        void Update()
        {
            if (Ragdoll == null || Ragdoll.Hips == null || Ragdoll.Hips.Body == null)
                return;

            // 基础「一步一脚印」模式接管角色输入：旧的 WASD、跳跃、调试和抓取按键
            // 不再作用于模型，避免多个输入系统同时写入布娃娃。
            if (StepActivity != null && StepActivity.Enabled)
                return;

            if (Input.GetKeyDown(KeyCode.Alpha0))
            {
                AutoDemo = !AutoDemo;
                if (!AutoDemo && Controller != null)
                {
                    if (StepActivity != null && StepActivity.Enabled)
                        StepActivity.TakeInput();
                    else
                        Controller.Source = new KeyboardInput();
                }
            }
            if (AutoDemo)
                StepDemo(Time.deltaTime);

            float hipY = Ragdoll.Hips.Body.position.y;
            bool standing = Ragdoll.Upright >= 0.85f && hipY >= RestPelvisHeight * 0.85f;
            StandingRun = standing ? StandingRun + Time.deltaTime : 0f;
            if (StandingRun > StandingBest)
                StandingBest = StandingRun;

            if (Input.GetKeyDown(KeyCode.R))
            {
                Ragdoll.Respawn();
                StandingRun = 0f;
            }
            if (Input.GetKeyDown(KeyCode.P) && Pose != null)
            {
                Recipe.UsePoseDriver = !Recipe.UsePoseDriver;
                if (!Recipe.UsePoseDriver)
                    Pose.ResetToBindPose();
            }
            if (Input.GetKeyDown(KeyCode.F1))
                Ragdoll.Push(Vector3.forward * 150f);
            if (Input.GetKeyDown(KeyCode.F2))
                Ragdoll.Push(Vector3.right * 150f);
            if (Input.GetKeyDown(KeyCode.F3))
                Ragdoll.Push(Vector3.up * 260f);
            if (Input.GetKeyDown(KeyCode.F4))
            {
                Recipe.HipsSpringMargin = Mathf.Max(0.25f, Recipe.HipsSpringMargin - 0.25f);
                Ragdoll.ApplySprings();
            }
            if (Input.GetKeyDown(KeyCode.F5))
            {
                Recipe.HipsSpringMargin = Mathf.Min(20f, Recipe.HipsSpringMargin + 0.25f);
                Ragdoll.ApplySprings();
            }
            if (Input.GetKeyDown(KeyCode.F6))
            {
                Recipe.LimbSpringRatio = Mathf.Max(0.02f, Recipe.LimbSpringRatio - 0.02f);
                Ragdoll.ApplySprings();
            }
            if (Input.GetKeyDown(KeyCode.F7))
            {
                Recipe.LimbSpringRatio = Mathf.Min(1.5f, Recipe.LimbSpringRatio + 0.02f);
                Ragdoll.ApplySprings();
            }
            if (Input.GetKeyDown(KeyCode.H))
                ShowHud = !ShowHud;
            // ★ 2026-10-02 owner：相机跟随的开关（默认关，见 Recipe.CameraFollowRagdoll）
            if (Input.GetKeyDown(KeyCode.C) && CameraRig != null)
                CameraRig.SetFollowTarget(!Recipe.CameraFollowRagdoll);

            // ---- 手部与环境交互的实时开关（人眼验收用，不用重启）----
            if (Input.GetKeyDown(KeyCode.I))
            {
                Recipe.UseArmIK = !Recipe.UseArmIK;
                if (!Recipe.UseArmIK && ArmIK != null)
                    ArmIK.ClearAllTargets();
            }
            if (Input.GetKeyDown(KeyCode.J))
            {
                // 角运动是在建关节那一刻定下的，所以运行时改要直接改关节（不能只改 Recipe）。
                Recipe.FreeElbowAngular = !Recipe.FreeElbowAngular;
                ConfigurableJointMotion m = Recipe.FreeElbowAngular
                    ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
                for (int i = 0; i < Ragdoll.Parts.Count; i++)
                {
                    ClumsyPart p = Ragdoll.Parts[i];
                    if (p.Joint == null || !p.IsElbowDownstream)
                        continue;
                    p.Joint.angularXMotion = m;
                    p.Joint.angularYMotion = m;
                    p.Joint.angularZMotion = m;
                }
            }
            if (Input.GetKeyDown(KeyCode.K))
                Recipe.HandPalmAlignWeight = Recipe.HandPalmAlignWeight > 0.5f ? 0f : 1f;
            if (Input.GetKeyDown(KeyCode.L))
                Recipe.GripOnlyWhenReaching = !Recipe.GripOnlyWhenReaching;
            if (Input.GetKeyDown(KeyCode.G))
                Recipe.UseInteraction = !Recipe.UseInteraction;

            // ---- 动画 / 笨拙度 ----
            if (Input.GetKeyDown(KeyCode.O))
            {
                // A/B：动画片段 vs 程序化步态。两者产出同一个 targetRotation。
                Recipe.UseAnimation = !Recipe.UseAnimation;
                if (Wobble != null) Wobble.Reset();
            }
            if (Input.GetKeyDown(KeyCode.Comma))
            {
                Recipe.Clumsiness = Mathf.Max(0f, Recipe.Clumsiness - 0.1f);
                if (Wobble != null) Wobble.Reset();
            }
            if (Input.GetKeyDown(KeyCode.Period))
            {
                Recipe.Clumsiness = Mathf.Min(1f, Recipe.Clumsiness + 0.1f);
                if (Wobble != null) Wobble.Reset();
            }

            // ---- 撑直方式（教程 §三 三种做法的 A/B）----
            if (Input.GetKeyDown(KeyCode.B))
            {
                Recipe.Balance = Recipe.Balance == BalanceMode.StabilizerJoint
                    ? BalanceMode.UprightTorque : BalanceMode.StabilizerJoint;
                // 弹簧是建关节那一刻写进驱动的，改配方要重新落一遍
                // （ClumsyRagdoll.SpringFor 在方法3 下把 hips 弹簧返回 0）；力矩那一侧是每帧现算。
                Ragdoll.ApplySprings();
            }
        }

        // ==================================================================
        // 自动演示：进 Play 就按脚本走一遍，不用按任何键
        // ==================================================================

        struct DemoPhase
        {
            public float Seconds;
            public string Caption;
            public Vector2 Move;
            public bool Sprint;
            public bool JumpOnce;
            public bool Grip;
            public bool SeekBox;
            /// <summary>进入这一段时先把角色送回出生点 —— 不然走远了就再也够不到箱子（演示要能循环）。</summary>
            public bool ResetFirst;
            /// <summary>★ 2026-10-02：找箱子时「在质量不超过它的那些里挑最重的」（0 = 就挑最近的）。
            /// 抱着走那两段要用它 —— 2kg 的小方块看不出重量反馈。</summary>
            public float SeekMaxMass;
            public DemoPhase(float s, string cap, float mx, float my, bool sprint, bool jump, bool grip, bool seek, bool reset, float seekMaxMass)
            { Seconds = s; Caption = cap; Move = new Vector2(mx, my); Sprint = sprint; JumpOnce = jump; Grip = grip; SeekBox = seek; ResetFirst = reset; SeekMaxMass = seekMaxMass; }
        }

        /// <summary>
        /// 演示脚本。每一段都在展示一个具体机制，字幕写在 HUD 上：
        ///   待机/走/冲刺 = 三段动画片段 + 1D 混合树；
        ///   停 = 笨拙层的目标侧晃动；跳 = Air 片段 + 落地踉跄；
        ///   抓 = 环境交互状态机（搜 → 够 → 抓）。
        /// </summary>
        static readonly DemoPhase[] DemoPhases =
        {
            // 顺序是算过的：**先抓再走远**。箱子在出生点旁边 2.1m，
            // 反过来（先走再抓）角色会跑出 20 多米，6 秒根本走不回来 —— 实测就是抓不到。
            new DemoPhase(3.5f, "待机 —— Idle 片段（3s 循环）：呼吸 + 轻微摆动", 0f, 0f, false, false, false, false, true, 0f),
            // ★ 2026-10-02 双手搬运：这两段是 owner 要看的《人类一败涂地》式搬运。
            //   挑**抱得动的里面最重的那一个**（8kg 上限 ⇒ 挑中 6kg 的箱子2），
            //   2kg 的小方块看不太出重量反馈。
            new DemoPhase(9.0f, "★ 走过去，两只手一起按住 → **双手抱住箱子**（HFF 式搬运）", 0f, 0f, true, false, true, true, false, 8f),
            // ★ 2026-10-02：这一段走的是**后退**。箱子是刚体、又是在**桌沿**上抱起来的，
            //   往前走会连人带箱子压在桌子上（实测：刚抱住就掉）。退着走才是人真实会做的事。
            new DemoPhase(6.0f, "★ **抱着走**（往后退 —— 箱子刚从桌上抱起来，往前会被桌子拦住）：更慢、步幅更短、骨盆更低", 0f, -1f, false, false, true, false, false, 0f),
            new DemoPhase(2.5f, "★ **松手 = 放下**；走动着松手 = 丢出去", 0f, 0f, false, false, false, false, false, 0f),
            new DemoPhase(5.0f, "走 —— Walk 片段，1 秒一个完整步态周期", 0f, 1f, false, false, false, false, false, 0f),
            new DemoPhase(3.0f, "冲刺 —— 混合树按速度切到 Walk×1.7；看腿摆得多快", 0f, 1f, true, false, false, false, false, 0f),
            new DemoPhase(2.5f, "停 —— 看笨拙层：目标自己慢半拍 + 回弹（按 , . 改笨拙度）", 0f, 0f, false, false, false, false, false, 0f),
            new DemoPhase(3.5f, "跳 —— 空中播 Air 片段，落地那一下触发踉跄（屈膝 + 塌腰）", 0f, 0f, false, true, false, false, false, 0f),
        };

        public int DemoPhaseIndex { get; private set; }
        public string DemoCaption { get; private set; }
        float _demoTime;
        DemoInput _demoInput;

        void StepDemo(float dt)
        {
            if (Controller == null) return;
            if (_demoInput == null) _demoInput = new DemoInput();
            if (Controller.Source != _demoInput) Controller.Source = _demoInput;

            _demoTime += dt;
            DemoPhase ph = DemoPhases[DemoPhaseIndex];
            DemoCaption = ph.Caption;
            if (_demoTime >= ph.Seconds)
            {
                _demoTime = 0f;
                DemoPhaseIndex = (DemoPhaseIndex + 1) % DemoPhases.Length;
                _demoJumpLatch = false;
                ph = DemoPhases[DemoPhaseIndex];
                DemoCaption = ph.Caption;
                if (ph.ResetFirst && Ragdoll != null)
                {
                    Ragdoll.Respawn();
                    if (Wobble != null) Wobble.Reset();
                }
            }

            Vector2 move = ph.Move;
            if (ph.SeekBox)
            {
                GameObject box = NearestGrippable(ph.SeekMaxMass);
                if (box != null)
                {
                    Vector3 to = Vector3.ProjectOnPlane(box.transform.position - Ragdoll.PelvisPosition, Vector3.up);
                    float dist = to.magnitude;
                    // 0.40 而不是 0.55：肩→箱子要在 0.45m 以内手才够得到，
                    // 而「骨盆→箱心 0.40m」对应的肩→箱心大约就是 0.42m。
                    // ★ 2026-10-02 双手搬运：0.40 是**单手**拖箱子时的距离，两只手抱要更近。
                    //   0.45m 的箱子摆 0.40m 外时，两肋到肩 0.56m > 臂长 0.478m ⇒ 两只手都够不到。
                    // ★ 2026-10-02：0.28 → 0.35。木箱现在摆在桌沿上，人走到桌子挡住的位置时
                    //   骨盆到箱心就是 ~0.29m；阈值再小就会一直«顶着桌子往前推»。
                    if (dist > 0.35f)
                    {
                        Vector3 d = to.normalized;
                        Vector3 fwd = Ragdoll.Root != null ? Ragdoll.Root.forward : Vector3.forward;
                        Vector3 right = Ragdoll.Root != null ? Ragdoll.Root.right : Vector3.right;
                        move = new Vector2(Vector3.Dot(d, right), Vector3.Dot(d, fwd));
                        if (move.sqrMagnitude > 1f) move = move.normalized;
                    }
                    else move = Vector2.zero;
                }
            }

            _demoInput.MoveValue = move;
            _demoInput.SprintValue = ph.Sprint;
            // ★ 起跳要**重试**：原来只按一帧，而奔跑中那一帧脚可能正好离地，
            //   `JumpPressed && IsGroundedNow` 就不成立 —— 整段跳就没了（实测落地踉跄 0 次）。
            //   改成「还在地上就一直按」，直到真的离地才锁存。
            bool wantJump = ph.JumpOnce && !_demoJumpLatch && Ragdoll != null && Ragdoll.IsGroundedNow;
            if (ph.JumpOnce && Ragdoll != null && !Ragdoll.IsGroundedNow) _demoJumpLatch = true;
            _demoInput.JumpHeldValue = wantJump;

            bool grip = ph.Grip;
            if (LeftGrip != null) { LeftGrip.ScriptedInput = false; LeftGrip.GripHeld = grip; }
            if (RightGrip != null) { RightGrip.ScriptedInput = false; RightGrip.GripHeld = grip; }
        }

        bool _demoJumpLatch;

        /// <summary>
        /// 找演示要用的道具。<paramref name="maxMass"/> &gt; 0 时在**质量不超过它的那些里挑最重的**
        /// （★ 2026-10-02：抱着走那两段要挑一个有分量的箱子，2kg 的小方块看不出重量反馈）。
        /// </summary>
        GameObject NearestGrippable(float maxMass)
        {
            Grippable[] all = Object.FindObjectsByType<Grippable>(FindObjectsSortMode.None);
            GameObject best = null;
            float bestD = float.MaxValue;
            float bestMass = -1f;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                GameObject go = all[i].gameObject;
                Rigidbody rb = go.GetComponentInParent<Rigidbody>();
                if (maxMass > 0f)
                {
                    // 挑最重的（质量并列时才看距离）
                    float m = rb != null ? rb.mass : -1f;
                    if (m <= 0f || m > maxMass) continue;
                    if (m > bestMass) { bestMass = m; bestD = Vector3.Distance(go.transform.position, Ragdoll.PelvisPosition); best = go; }
                    continue;
                }
                float d = Vector3.Distance(go.transform.position, Ragdoll.PelvisPosition);
                if (d < bestD) { bestD = d; best = go; }
            }
            return best;
        }

        sealed class DemoInput : IClumsyInput
        {
            public Vector2 MoveValue;
            public bool SprintValue;
            public bool JumpHeldValue;
            public Vector2 Move { get { return MoveValue; } }
            public bool Sprint { get { return SprintValue; } }
            public bool JumpHeld { get { return JumpHeldValue; } }
        }

        // ------------------------------------------------------------------

        void OnGUI()
        {
            if (!ShowHud || Ragdoll == null || Ragdoll.Hips == null || Ragdoll.Hips.Body == null)
                return;
            EnsureStyles();

            Rigidbody b = Ragdoll.Hips.Body;
            GUILayout.BeginArea(new Rect(12f, 10f, 560f, 340f), _box);
            GUILayout.Label("一步一脚印 · 独立脚步布娃娃", _label);
            if (AutoDemo)
            {
                GUILayout.Label("【演示 " + (DemoPhaseIndex + 1) + "/" + DemoPhases.Length + "】" + DemoCaption, _label);
                // ★ 这一行是补的：AutoDemo 默认开着，键盘被 StepDemo 完全接管，
                //   而原来的 HUD 只显示「【演示】」、从不告诉玩家按什么键能接管 ——
                //   进 Play 按 WASD 没反应，就是卡在这儿。
                GUILayout.Label("★ 想自己操作：按 0 关掉演示，切回 WASD 键盘控制", _label);
            }
            if (Animation != null)
                GUILayout.Label("当前动画状态：" + Animation.AnimStateName
                    + (Animation.AnimStateName == "InTheAir" ? "（Air 片段）" : "（Idle/Walk 混合树）"), _label);
            GUILayout.Label("【新键位】Q/E 选左/右脚 · 鼠标选落点 · Space/Enter 迈步 · 左右键只负责抓取", _label);
            GUILayout.Label("【系统】0 演示/手动 · R 重生 · C 相机跟随 · H 面板 · F1-F7 调试", _label);
            if (StepMotor != null)
                GUILayout.Label("支撑脚 " + StepMotor.SupportFoot + "   步进 "
                    + (StepMotor.StepActive ? (StepMotor.StepProgress * 100f).ToString("F0") + "%" : "等待重心换脚"), _label);
            GUILayout.Label("★ 相机跟随 = " + (Recipe.CameraFollowRagdoll ? "开" : "**关**（先看手：按 C 切换）")
                + "   ·   按住左键 = 伸左手 / 松开 = 回落；按住右键 = 伸右手", _label);
            GUILayout.Label("★ 抓取：**只按左键** = 一只手拖着走；**左键+右键一起** = 两只手抱住（HFF 式搬运，抱过之后要两只手都松开才能再抱）", _label);
            GUILayout.Label("1/2/3 推一把   -/= hips 弹簧   [/] 四肢弹簧   P 姿势驱动   "
                + "I 手臂 IK   J 肘解锁   K 掌心对位   L 够到才抓   G 环境交互   H 面板", _label);
            GUILayout.Label("O 动画片段/程序化步态（A/B）   ,/. 改笨拙度   —— 笨拙效果靠这两项", _label);
            GUILayout.Label("B 撑直方式（弹簧 ↔ 力矩，教程 §三 的方法2/方法3）", _label);
            GUILayout.Label("相机（Blender 那套）：中键拖 = 绕角色转（绕枢轴）   Shift+中键 = 平移   Ctrl+中键 / 滚轮 = 推拉（最近 0.25m）   Alt+中键 = 以光标处为中心   Home = 复位", _label);
            GUILayout.Space(4f);
            GUILayout.Label(string.Format("骨盆 y {0:F3} / 出生 {1:F3}      直立度 {2:F3}",
                b.position.y, RestPelvisHeight, Ragdoll.Upright), _label);
            GUILayout.Label(string.Format("站立 {0:F2}s   最长 {1:F2}s   落地 {2}", StandingRun, StandingBest, Ragdoll.IsGrounded), _label);
            GUILayout.Label(string.Format("撑直 {0}   hips 弹簧 {1:F0}   四肢弹簧 {2:F0}（M {3:F1}kg，h_com {4:F3}m）",
                Recipe.Balance, Ragdoll.HipsSpring, Ragdoll.LimbSpring, Ragdoll.TotalMass, Ragdoll.ComHeightSafe), _label);
            if (Balance != null)
                GUILayout.Label(string.Format("力矩上限 {0:F0} N·m（{1:F1}× 重力力矩）  当前力矩 {2:F0}   倾斜 {3:F1}°   转身误差 {4:F1}°",
                    Balance.TorqueCeiling, Recipe.UprightTorqueMargin, Balance.UprightTorque,
                    Balance.TiltDegrees, Balance.YawErrorDegrees), _label);
            GUILayout.Label(string.Format("速度 {0:F2} m/s   姿势驱动 {1}   部件 {2}",
                Controller != null ? Controller.SpeedMeasured : 0f,
                Recipe.UsePoseDriver ? "开" : "关", Ragdoll.Parts.Count), _label);
            if (CameraRig != null)
            {
                GUILayout.Label(string.Format("相机 距离 {0:F2}m   绕轨 {1:F0}° / 俯仰 {2:F0}°   枢轴偏移 {3:F2}m（Alt+中键可重设）",
                    Recipe.CameraDistance * CameraRig.DistanceScale,
                    CameraRig.OrbitYaw, CameraRig.OrbitPitch, CameraRig.PivotOffset.magnitude), _label);
            }
            GUILayout.Space(4f);
            GUILayout.Label("—— 手部与环境交互 ——", _label);
            if (Interaction != null)
            {
                GUILayout.Label("交互状态 " + Interaction.StateName
                    + "   活动手 " + (Interaction.ActiveHand != null ? Interaction.ActiveHand.Suffix : "无")
                    + "   目标 " + (Interaction.CurrentTarget != null ? Interaction.CurrentTarget.name : "无"), _label);
            }
            if (ArmIK != null)
            {
                ArmIKChain cl = ArmIK.Get("l");
                ArmIKChain cr = ArmIK.Get("r");
                GUILayout.Label(string.Format(
                    "IK 权重 左 {0:F2} / 右 {1:F2}   手误差 左 {2:F3}m / 右 {3:F3}m",
                    ArmIK.WeightOf("l"), ArmIK.WeightOf("r"),
                    cl != null ? cl.HandErrorRaw : 0f,
                    cr != null ? cr.HandErrorRaw : 0f), _label);
            }
            if (Animation != null)
            {
                GUILayout.Label("动画 " + Animation.AnimStateName
                    + string.Format("  v {0:F1} m/s  接地 {1}  骨骼 {2}",
                        Animation.AnimSpeed, Animation.AnimGrounded, Animation.Bones.Count)
                    + string.Format("   ★姿态差 {0:F1}°", Animation.PoseGapDegrees()), _label);
            }
            if (Wobble != null)
            {
                GUILayout.Label(string.Format("笨拙度 {0:F2}   晃动偏移 {1:F1}°   落地踉跄 {2:F2}（累计 {3} 次）",
                    Recipe.Clumsiness, Wobble.WobbleOffsetDegrees, Wobble.ImpactAmount, Wobble.ImpactCount), _label);
            }
            if (Carry != null)
                GUILayout.Label("搬运 " + Carry.Report(), _label);
            if (LeftGrip != null)
                GUILayout.Label("左手 " + LeftGrip.Report(), _label);
            if (RightGrip != null)
                GUILayout.Label("右手 " + RightGrip.Report(), _label);
            GUILayout.EndArea();
        }

        void EnsureStyles()
        {
            if (_styles)
                return;
            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = false,
                padding = new RectOffset(6, 6, 1, 1),
                normal = { textColor = new Color(0.96f, 0.97f, 0.94f) }
            };
            _box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 8, 8) };
            Font font = Resources.Load<Font>("Ui");
            if (font != null)
            {
                _label.font = font;
                font.RequestCharactersInTexture(
                    "笨拙布娃娃物理教程版风格移动冲刺跳鼠标转向左右键抓重生推一把改弹簧关姿势驱动收面板" +
                    "骨盆直立度站立最长落地速度部件出生", _label.fontSize);
            }
            _styles = true;
        }
    }
}
