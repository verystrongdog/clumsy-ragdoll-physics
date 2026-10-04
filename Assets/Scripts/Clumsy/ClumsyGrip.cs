using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 「这个物体被抓住的时候能怎么动」。
    /// 来源：Sergio 的 <c>Grippable.JointMotionsConfig</c>（见 `ref-ActiveRagdolls-视频教程.md` §五 5.3 ③）。
    ///
    /// 教程 P7 的做法是往物体上焊一个 <c>FixedJoint</c>（位置 + 旋转全锁死），
    /// 于是球抓在手里也只能一动不动。这里把角运动开放出来：
    /// 球可以随便转、把手只能在某个锥角内摆 —— 都只靠给物体挂一个 <see cref="Grippable"/> 配一下。
    /// </summary>
    [System.Serializable]
    public struct JointMotionsConfig
    {
        public ConfigurableJointMotion AngularX;
        public ConfigurableJointMotion AngularY;
        public ConfigurableJointMotion AngularZ;

        public float LimitX;
        public float LimitY;
        public float LimitZ;

        public static JointMotionsConfig Make(ConfigurableJointMotion motion, float limit)
        {
            JointMotionsConfig c = new JointMotionsConfig();
            c.AngularX = motion;
            c.AngularY = motion;
            c.AngularZ = motion;
            c.LimitX = limit;
            c.LimitY = limit;
            c.LimitZ = limit;
            return c;
        }

        /// <summary>把配置写到关节上。Locked 时三个 Limit 不起作用（Unity 的语义）。</summary>
        public void ApplyTo(ConfigurableJoint joint)
        {
            if (joint == null)
                return;
            joint.angularXMotion = AngularX;
            joint.angularYMotion = AngularY;
            joint.angularZMotion = AngularZ;

            // X 轴是不对称的（low/high），Y / Z 是对称的 —— Unity 的 ConfigurableJoint 语义。
            SoftJointLimit lx = new SoftJointLimit();
            lx.limit = -Mathf.Abs(LimitX);
            joint.lowAngularXLimit = lx;
            SoftJointLimit hx = new SoftJointLimit();
            hx.limit = Mathf.Abs(LimitX);
            joint.highAngularXLimit = hx;

            SoftJointLimit ly = new SoftJointLimit();
            ly.limit = Mathf.Abs(LimitY);
            joint.angularYLimit = ly;

            SoftJointLimit lz = new SoftJointLimit();
            lz.limit = Mathf.Abs(LimitZ);
            joint.angularZLimit = lz;
        }
    }

    /// <summary>
    /// 可抓标记。来源：Sergio 的 <c>Grippable</c>。
    ///
    /// 它替代教程 P7 的 <c>tag == "item"</c>：给物体挂一个组件，比给物体打一个 tag
    /// 多带一样东西 —— **这个物体该被怎么抓**。tag 那条路作为兼容口径保留
    /// （<see cref="RagdollRecipe.GripRequireGrippableComponent"/> = false 时两者都认）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Grippable : MonoBehaviour
    {
        [Tooltip("勾上才用下面的 Motions；不勾 = 用 Recipe 的默认抓法。")]
        public bool UseCustomMotions = false;

        [Tooltip("被抓住时的角运动与锥角限制。想「焊死在手里」就把三个都设成 Locked。")]
        public JointMotionsConfig Motions = JointMotionsConfig.Make(ConfigurableJointMotion.Limited, 25f);

        [Tooltip("本物体专用的 breakForce，0 = 用 Recipe.GripBreakForce。")]
        public float BreakForce = 0f;

        [Tooltip("本物体专用的 breakTorque，0 = 用 Recipe 的。")]
        public float BreakTorque = 0f;
    }

    /// <summary>
    /// 手部抓取器。**挂在手的骨骼上**（那一根骨头本来就有 Rigidbody）。
    ///
    /// 来源：Sergio 的 <c>GripModule</c> + <c>Gripper</c>（`ref-ActiveRagdolls-视频教程.md` §五 5.3）。
    /// 与教程 P7 的四处不同，逐条列出，都是有意为之：
    ///
    /// <list type="number">
    ///   <item><b>关节加在手上，不是加在被抓物体上。</b>
    ///         加在物体上会污染物体自己的关节链，而且「谁在抓它」这件事会写在物体身上；
    ///         加在手上则松手时 Destroy 自己就够了。教程 P7 那个 <c>AddComponent&lt;FixedJoint&gt;</c>
    ///         是加在物体上的。</item>
    ///   <item><b><c>ConfigurableJoint</c> 而不是 <c>FixedJoint</c>。</b>
    ///         线性 Locked、角运动由物体的 <see cref="Grippable"/> 决定 —— 球能在手里转，
    ///         把手只能在锥角内摆。FixedJoint 做不到这件事。</item>
    ///   <item><b>检测用 <c>Physics.OverlapSphere</c> 查询，不是 Trigger 回调。</b>
    ///         三个理由：① 编辑器的诊断台里 <c>Physics.Simulate</c> 不派发 trigger 消息，
    ///         只有查询能做确定性验证；② 查询能挑「最近的」目标，回调只能拿到「最后进入的」；
    ///         ③ 查询不做成 Collider，就没有「自己罩住自己」的噪音 ——
    ///         实测旧的触发球半径 0.208m 把 shoulder/arm/forearm/hand 四根骨头全罩进去了
    ///         （`ClumsyRagdollBench.CarryReport` 的读数：触发球里 6 个 collider，其中 4 个是自己的）。</item>
    ///   <item><b>「够到了才能抓」。</b> Sergio 把 <c>Gripper.enabled</c> 直接接在手臂 IK 权重上
    ///         （阈值 0.5）—— 于是「抬手去够」和「抓住」是同一个动作的自然结果，
    ///         不需要额外的按键状态机。这里照抄，见 <see cref="CanGrip"/>。</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HandGripper : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;
        public ClumsyArmIK ArmIK;

        /// <summary>"l" / "r"。</summary>
        public string Suffix = "l";

        /// <summary>0 = 左手、1 = 右手。</summary>
        public int HandIndex;

        /// <summary>用哪个鼠标键。左手 = 左键(0)，右手 = 右键(1)。</summary>
        public int MouseButton;

        public ClumsyPart HandPart;
        public Rigidbody HandBody;

        /// <summary>脚本化输入（诊断台用）：置 true 之后不再读鼠标，只认 <see cref="GripHeld"/>。</summary>
        public bool ScriptedInput;

        /// <summary>玩家是否正按住抓取键。</summary>
        public bool GripHeld;

        /// <summary>最近一次 <see cref="Poll"/> 找到的候选物体。</summary>
        public GameObject CandidateObject;
        public Rigidbody CandidateBody;
        public float CandidateDistance;

        public GameObject GrabbedObject;
        public Rigidbody GrabbedBody;
        public ConfigurableJoint Joint;

        /// <summary>
        /// ★ 2026-10-02：这个抓取是不是**搬运关节**（`ClumsyCarry` 建的软弹簧抓法）。
        /// 与普通抓取的区别见 <see cref="TryGrabForCarry"/>。
        /// </summary>
        public bool CarryMode;

        /// <summary>上一次抓取是怎么断的（诊断用）。</summary>
        public string LastReleaseReason = "";

        readonly Collider[] _hits = new Collider[24];

        public bool HasGrip { get { return Joint != null && GrabbedObject != null; } }

        /// <summary>
        /// 现在允许不允许建立抓取。来源：Sergio 的 <c>UseLeftGrip(weight)</c> —— 手抬得够高才「能抓」。
        /// </summary>
        public bool CanGrip
        {
            get
            {
                if (Recipe == null)
                    return false;
                if (!Recipe.GripOnlyWhenReaching)
                    return true;
                return ArmIK != null && ArmIK.WeightOf(Suffix) >= Recipe.GripReachWeightThreshold;
            }
        }

        public void Setup(ClumsyRagdoll ragdoll, RagdollRecipe recipe, ClumsyArmIK armIK, string suffix, int handIndex, int mouseButton)
        {
            Ragdoll = ragdoll;
            Recipe = recipe;
            ArmIK = armIK;
            Suffix = suffix;
            HandIndex = handIndex;
            MouseButton = mouseButton;

            if (Ragdoll != null)
                HandPart = Ragdoll.Find("hand" + suffix);
            if (HandPart != null)
                HandBody = HandPart.Body;
            if (HandBody == null)
                HandBody = GetComponentInParent<Rigidbody>();
            if (HandBody == null)
                Debug.LogError("[HandGripper] " + name + " 取不到手的 Rigidbody，抓取不会工作。"
                    + "（这一条踩过：脚本必须挂在手骨上，不能挂在手下面的空物体上。）");
        }

        void FixedUpdate()
        {
            if (!ScriptedInput)
                GripHeld = Input.GetMouseButton(MouseButton);
            Poll();
        }

        /// <summary>找出「手边上最近的那个可抓物体」。查询球以手骨原点为中心。</summary>
        public void Poll()
        {
            CandidateObject = null;
            CandidateBody = null;
            CandidateDistance = float.MaxValue;

            if (HandBody == null || Recipe == null)
                return;

            Vector3 center = HandBody.position;
            float radius = Mathf.Max(0.01f, Recipe.GripQueryRadius);
            int n = Physics.OverlapSphereNonAlloc(center, radius, _hits, ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < n; i++)
            {
                Collider col = _hits[i];
                if (col == null)
                    continue;

                Rigidbody rb = col.attachedRigidbody;
                if (rb == null)
                    rb = col.GetComponentInParent<Rigidbody>();
                GameObject go = rb != null ? rb.gameObject : col.gameObject;

                if (go == null)
                    continue;
                if (rb == HandBody)
                    continue;

                // 不许抓自己（Sergio 的 canGripYourself，默认 false）。
                if (!Recipe.CanGripYourself && Ragdoll != null
                    && (go == Ragdoll.gameObject || go.transform.IsChildOf(Ragdoll.transform)))
                    continue;

                // 已经被另一只手抓着的东西不再抢。
                if (rb != null && rb == GrabbedBody)
                    continue;

                // 「可抓」的判据：Grippable 组件，或（兼容口径）tag == GrabbableTag。
                Grippable g = go.GetComponent<Grippable>();
                if (g == null)
                    g = col.GetComponent<Grippable>();
                bool byComponent = g != null;
                bool byTag = !string.IsNullOrEmpty(Recipe.GrabbableTag) && go.tag == Recipe.GrabbableTag;
                if (Recipe.GripRequireGrippableComponent ? !byComponent : !(byComponent || byTag))
                    continue;

                float d = Vector3.Distance(center, col.ClosestPoint(center));
                if (d < CandidateDistance)
                {
                    CandidateDistance = d;
                    CandidateObject = go;
                    CandidateBody = rb;
                }
            }
        }

        /// <summary>
        /// 建立抓取。关节加在**手**上，<c>connectedBody</c> = 被抓物体
        /// （物体没有 Rigidbody 时连到世界 —— 比如场景里那根静止的单杠，
        /// 连到世界就等于「手被钉在杠上」，人就能吊住自己）。
        /// </summary>
        public bool TryGrab()
        {
            if (HasGrip || CandidateObject == null || HandBody == null || Recipe == null)
                return false;
            if (!CanGrip)
                return false;

            GameObject target = CandidateObject;
            Rigidbody other = CandidateBody;

            ConfigurableJoint joint = HandBody.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = other;
            // 运行期新建关节时，Unity 会把 connectedAnchor 设成「当前位置对应的物体局部点」，
            // 于是手停在它现在所在的位置不动 —— 这正是要的。
            joint.autoConfigureConnectedAnchor = true;
            joint.anchor = Vector3.zero;
            joint.axis = Vector3.right;
            joint.secondaryAxis = Vector3.up;
            joint.rotationDriveMode = RotationDriveMode.XYAndZ;
            joint.enablePreprocessing = true;
            joint.enableCollision = false;   // 手与物体不再互撞（否则会互相挤）

            ConfigurableJointMotion linear = Recipe.GripLockLinear
                ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Free;
            joint.xMotion = linear;
            joint.yMotion = linear;
            joint.zMotion = linear;

            // 角运动：物体自己说了算（Sergio 的 JointMotionsConfig.ApplyTo）。
            Grippable g = target.GetComponent<Grippable>();
            if (g != null && g.UseCustomMotions)
                g.Motions.ApplyTo(joint);
            else
                JointMotionsConfig.Make(Recipe.GripDefaultAngularMotion, Recipe.GripDefaultAngularLimit)
                    .ApplyTo(joint);

            float bf = g != null && g.BreakForce > 0f ? g.BreakForce : Recipe.GripBreakForce;
            float bt = g != null && g.BreakTorque > 0f ? g.BreakTorque
                : (Recipe.GripBreakTorque > 0f ? Recipe.GripBreakTorque : bf);
            joint.breakForce = bf;
            joint.breakTorque = bt;

            Joint = joint;
            GrabbedObject = target;
            GrabbedBody = other;
            CarryMode = false;   // ★ 普通抓取（焊死）
            LastReleaseReason = "";
            return true;
        }

        public void Release()
        {
            Release("松手");
        }

        public void Release(string reason)
        {
            if (Joint != null)
                Destroy(Joint);
            Joint = null;
            GrabbedObject = null;
            GrabbedBody = null;
            CarryMode = false;
            LastReleaseReason = reason;
        }

        /// <summary>Unity 在 breakForce / breakTorque 被超过时销毁关节并调这个。</summary>
        void OnJointBreak(float breakForce)
        {
            Joint = null;
            GrabbedObject = null;
            GrabbedBody = null;
            CarryMode = false;
            LastReleaseReason = "关节被拉断（力 " + breakForce.ToString("F0") + "）";
        }


        /// <summary>
        /// ★ 2026-10-02 双手搬运：**为「抱住」而建的抓取**（见 <see cref="ClumsyCarry"/>）。
        ///
        /// 与 <see cref="TryGrab"/> 的三处不同，都是刻意的：
        /// <list type="number">
        ///   <item><b>线性 = Free + 弹簧驱动</b>（不是 Locked）。物体是«软软地挂在手上»，
        ///         重量经这条弹簧传到手臂 —— 「抱不动就往下沉」就是从这儿来的。
        ///         Locked 是刚性约束，力会不封顶地传，抱重物时会直接把布娃娃顶飞。</item>
        ///   <item><b><c>connectedAnchor</c> 由调用者指定</b>（物体**局部**坐标里的抓取点，
        ///         两只手各一个），而不是「抓上那一刻手在哪」。抱东西要抓在物体的**两肋**，
        ///         不是随便一个角 —— 抓在角上，物体会在怀里斜着挂。</item>
        ///   <item><b>角运动 Free + 只留角阻尼</b>：朝向由**两只手的位置**定（两个点约束），
        ///         阻尼那条自由度按掉«绕两手连线自转»。用 Limited 锥角的话，
        ///         锥角是以«对齐姿态»为零点的，而抓取时刻的相对姿态是任意的，会自己往锥里拧。</item>
        /// </list>
        /// </summary>
        public bool TryGrabForCarry(GameObject target, Rigidbody other, Vector3 connectedLocalAnchor,
                                    float spring, float damper, float maxForce, float angularDamping)
        {
            if (HasGrip)
                Release("改用双手抱");
            if (target == null || HandBody == null || Recipe == null)
                return false;

            ConfigurableJoint joint = HandBody.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = other;
            // ⚠️ 必须显式关掉自动配置：开着的话 Unity 会在建关节那一刻把 connectedAnchor
            //    算成«手现在对应的物体局部点»，我们指定的两肋抓取点就被覆盖掉了。
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = Vector3.zero;
            joint.connectedAnchor = connectedLocalAnchor;
            joint.axis = Vector3.right;
            joint.secondaryAxis = Vector3.up;
            joint.rotationDriveMode = RotationDriveMode.XYAndZ;
            joint.enablePreprocessing = true;
            joint.enableCollision = false;   // 手与物体不互撞（否则掌心会一直顶箱子）

            joint.xMotion = ConfigurableJointMotion.Free;
            joint.yMotion = ConfigurableJointMotion.Free;
            joint.zMotion = ConfigurableJointMotion.Free;
            ApplyCarryDrive(joint, spring, damper, maxForce, angularDamping);

            float bf = Recipe.GripBreakForce;
            joint.breakForce = bf;
            joint.breakTorque = bf > 0f ? bf : 0f;

            Joint = joint;
            GrabbedObject = target;
            GrabbedBody = other;
            CarryMode = true;
            LastReleaseReason = "";
            return true;
        }

        /// <summary>
        /// ★ 2026-10-02：把**已经建好的**抓取关节就地改成搬运关节（弹簧 / 阻尼 / 抓取点都可以每帧刷）。
        ///
        /// 为什么要有这条：从「单手拖着走」升级成双手抱的时候，若先松手再重抓，
        /// 物体在半空中会掉一下 —— 而这只浣熊**够不到地面**（臂长 0.476m、肩高 0.93m），
        /// 东西一落地就再也拿不起来了。就地改参数没有这个窗口。
        /// </summary>
        public void SetCarryHold(Vector3 connectedLocalAnchor, float spring, float damper,
                                 float maxForce, float angularDamping)
        {
            if (Joint == null)
                return;
            Joint.autoConfigureConnectedAnchor = false;
            Joint.connectedAnchor = connectedLocalAnchor;
            Joint.xMotion = ConfigurableJointMotion.Free;
            Joint.yMotion = ConfigurableJointMotion.Free;
            Joint.zMotion = ConfigurableJointMotion.Free;
            ApplyCarryDrive(Joint, spring, damper, maxForce, angularDamping);
            CarryMode = true;
        }

        /// <summary>搬运关节的三个线性驱动 + 一个角阻尼。见 <see cref="TryGrabForCarry"/>。</summary>
        static void ApplyCarryDrive(ConfigurableJoint joint, float spring, float damper,
                                    float maxForce, float angularDamping)
        {
            JointDrive lin = new JointDrive();
            lin.positionSpring = spring;
            lin.positionDamper = damper;
            lin.maximumForce = maxForce > 0f ? maxForce : float.MaxValue;
            joint.xDrive = lin;
            joint.yDrive = lin;
            joint.zDrive = lin;

            JointDrive ang = new JointDrive();
            ang.positionSpring = 0f;            // 不复位（朝向交给两只手的位置定）
            ang.positionDamper = angularDamping;
            ang.maximumForce = maxForce > 0f ? maxForce : float.MaxValue;
            joint.angularXDrive = ang;
            joint.angularYZDrive = ang;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;

            joint.targetPosition = Vector3.zero;
            joint.targetVelocity = Vector3.zero;
            joint.targetRotation = Quaternion.identity;
        }

        /// <summary>诊断台用的一行摘要。</summary>
        public string Report()
        {
            return "[" + Suffix + "] 按住=" + (GripHeld ? "是" : "否")
                + "  能抓=" + (CanGrip ? "是" : "否")
                + "  候选=" + (CandidateObject != null
                    ? CandidateObject.name + "(" + CandidateDistance.ToString("F3") + "m)" : "无")
                + "  抓着=" + (HasGrip ? GrabbedObject.name : "无")
                + (HasGrip ? "" : (string.IsNullOrEmpty(LastReleaseReason) ? "" : "  上次断开：" + LastReleaseReason));
        }
    }
}
