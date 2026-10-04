using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>双手搬运（像《人类一败涂地》那样抱东西）的状态。</summary>
    public enum ECarryState
    {
        /// <summary>没在搬。</summary>
        Idle = 0,
        /// <summary>两只手都按住了抓取键，正在朝物体的两肋伸手。</summary>
        Reaching = 1,
        /// <summary>抱住了（两只手都建立了搬运关节）。</summary>
        Holding = 2
    }

    /// <summary>
    /// ★ 2026-10-02 · **双手搬运层**。owner 口径：
    /// 「**实现类似人类一败涂地的搬运物体的互动逻辑**」，
    /// 并在两个选项里选了「**A 双手抱住 + 重量反馈**」与「**真的被压下去**」。
    ///
    /// <b>这一层补的是什么</b>：在这之前，抓取只有「一只手按抓取键 → 物体被
    /// <c>ConfigurableJoint</c> 线性 Locked **焊死**在手上」——能拖、能在手里晃，
    /// 但**没有「抱」这个动作**，物体的重量也**一点都不压在身体上**
    /// （托举层的反重力只托角色自己）。现在：
    ///
    /// <list type="number">
    ///   <item><b>两只手都按抓取键 + 同一个物体都够得到 ⇒ 进入搬运。</b>
    ///         只按一只手仍然走旧路径（拖着走 / 抓单杠吊住自己），一行都没改。</item>
    ///   <item><b>分两段走，这是被实测逼出来的</b>：
    ///         <b>伸手段</b>手的目标点是**物体此刻的两肋**（物体在哪就抓哪，与人走近没关系）；
    ///         <b>抱住段</b>才换成**骨盆前那个固定的「携带位」**，于是物体被拉进怀里。
    ///         一开始两段都用携带位，实测**根本抱不上**：携带位是按骨盆算的，
    ///         而站立时骨盆会漂 26cm、躯干会歪，物体明明在正前方 0.57m，
    ///         两只手却在够一个离箱子 30cm 的空点（手误差 0.22m，2.5s 超时）。</item>
    ///   <item><b>「携带位」锚在**骨盆**上，不锚在物体上。</b>
    ///         位置 = 骨盆 + 前(`CarrySlotForward` + 物体半深×`CarrySlotDepthScale`) + 上 `CarrySlotUp`；
    ///         朝向 = 角色朝向（`Root.forward`）摆正。**这是唯一能避开死循环的定义方式** ——
    ///         若把两只手的目标点定义在物体身上，就变成「手追物体、物体又被手拽」，
    ///         求解器会自己跟自己打架（本工程在交互状态机里已经踩过一次同源的坑：
    ///         `ClumsyInteraction._grabLocalOffset` 那一段注释）。</item>
    ///   <item><b>手↔物体是软弹簧，不是 Locked。</b>
    ///         物体«挂»在两只手上，重量经弹簧 → 手臂 → 躯干 → 托举层
    ///         ⇒ **人下沉、走慢、步幅收**（读数见诊断台 `CarryTwoHandedReport`）。</item>
    ///   <item><b>搬运时手臂加硬</b>（`CarryArmStiffnessScale`）。本工程的胳膊是«面条»
    ///         （四肢弹簧 33、上半身 0.12 倍），不加硬**抱不住任何东西**：
    ///         物体会直接把两条胳膊压直、掉到大腿旁边。加硬多少 = «抱得动多重»的手感旋钮。
    ///         **伸手段也要加硬** —— 否则 IK 写进去的关节角被重力吃掉，手停在半路够不着。</item>
    ///   <item><b>伸手段关掉「手 ↔ 物体」的碰撞</b>（与交互状态机同一个理由：
    ///         手骨的碰撞体是腕部一颗半径 6.5cm 的球，会顶在箱子上把手拧歪，
    ///         而且反作用力会**把整个人推开** —— 实测伸手 3s 里骨盆横漂 26cm）。</item>
    ///   <item><b>松手 = 放下</b>（物体自由落体）；**走动着松手 = 丢出去**
    ///         （沿当时的水平运动方向补 `CarryReleaseThrowSpeed`）。</item>
    ///   <item><b>抱过之后必须把两只手都松开，才能再抱。</b>
    ///         不然「松手 → 还在范围内 → 立刻又抱住」会变成抽搐；
    ///         而且物体一旦掉在地上，这只浣熊**够不到地面**（臂长 0.476m、肩高 0.93m，
    ///         地面永远在可达球外，见 `ClumsyRagdollGame.BuildProps` 的注释）。</item>
    /// </list>
    ///
    /// <b>执行顺序</b>：`[DefaultExecutionOrder(-60)]` —— 必须在 `ClumsyPoseDriver`
    /// **之前**跑（IK 目标由姿势驱动消费）。`ClumsyInteraction` 没有声明顺序，
    /// 所以它有一帧的滞后，这里不跟它一样。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-60)]
    public sealed class ClumsyCarry : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;
        public ClumsyArmIK ArmIK;
        public HandGripper Left;
        public HandGripper Right;
        public ClumsyInteraction Interaction;

        // ---- 状态 ----

        public ECarryState State { get; private set; }
        public float StateTime { get; private set; }
        public string LastTransition = "";

        /// <summary>正在搬的物体（没在搬时为 null）。</summary>
        public GameObject CarryObject { get; private set; }
        public Rigidbody CarryBody { get; private set; }

        /// <summary>被搬物体的质量（kg）。控制器用它算重量反馈。</summary>
        public float HeldMass { get; private set; }

        /// <summary>被搬质量 / 角色总质量。托举层与步态的折减都按它算。</summary>
        public float LoadRatio { get; private set; }

        public bool IsHolding { get { return State == ECarryState.Holding; } }

        /// <summary>当前这一帧手的目标点是「物体此刻的两肋」还是「骨盆前的携带位」。</summary>
        public bool TargetFromObject { get; private set; }

        /// <summary>进入过几次搬运 / 放下过几次（诊断用）。</summary>
        public int EnterCount;
        public int DropCount;

        // ---- 每帧几何（诊断与证据用）----

        public Vector3 SlotWorld;
        public Vector3 GripLeftWorld;
        public Vector3 GripRightWorld;
        /// <summary>携带位因为«两只手够不着»被往回拉了多少（m）。大箱子会自己«抱近一点»。</summary>
        public float SlotClampDistance;
        /// <summary>物体中心相对携带位的误差（前后 / 上下 / 左右，m）。抱稳了三个都该 ≈ 0。</summary>
        public float SlotErrorForward;
        public float SlotErrorUp;
        public float SlotErrorLateral;
        /// <summary>物体的局部上轴与世界竖直的夹角（度）。物体在怀里歪没歪。</summary>
        public float BoxTiltDegrees;
        /// <summary>两只手离各自目标的距离（m）。</summary>
        public float HandErrorLeft;
        public float HandErrorRight;
        /// <summary>此刻给被搬物体加的回正力矩大小（N·m）。诊断用。</summary>
        public float UprightTorqueNow;

        // ---- ★ 2026-10-02 owner 报「伸出双手接触物品之后不能确定是不是真的抓住」----
        /// <summary>伸手段：两只手各自离抓取位还差多远（m）、到位没有。</summary>
        public float ReachDistL, ReachDistR;
        public bool ReachOkL, ReachOkR;
        /// <summary>HUD 用的一句话：现在卡在哪。</summary>
        public string Hint = "";
        /// <summary>抱住时给物体染的色（松手还原）。「抓住没有」要一眼看得出来。</summary>
        public bool TintHeldObject = true;
        public Color HeldTint = new Color(0.35f, 1f, 0.75f);

        // ---- 内部 ----

        GameObject _target;
        Vector3 _anchorLeft, _anchorRight;
        /// <summary>★ 胸口那个「抱住」关节（见 CarryHoldOnChest）。</summary>
        ConfigurableJoint _chestJoint;
        /// <summary>建关节那一刻物体的朝向 —— `targetRotation` 是**相对它**的（见 ApplyChestHold）。</summary>
        Quaternion _boxRotAtHold = Quaternion.identity;
        bool _leftAtGrip, _rightAtGrip;
        /// <summary>两手连线在**物体局部坐标**里的那根单位轴（±X/±Y/±Z）。回正力矩要用它。</summary>
        Vector3 _grabAxisLocal = Vector3.right;
        float _reachTime;
        bool _needRelease;

        Collider _handColL, _handColR;
        Collider[] _targetCols;
        Collider[] _bodyCols;
        GameObject _ignoreTarget;

        static readonly string[] StateNames = { "空闲", "伸手抱", "抱着" };

        // 加硬覆盖的那几根骨头（搬运结束要一根不差地还回去）
        static readonly string[] ArmKeys = { "shoulder", "arm", "forearm", "hand" };
        static readonly string[] TorsoKeys = { "spine", "neck" };

        public string StateName { get { return StateNames[(int)State]; } }

        public void Setup(ClumsyRagdoll ragdoll, RagdollRecipe recipe, ClumsyArmIK armIK,
                          HandGripper left, HandGripper right, ClumsyInteraction interaction)
        {
            Ragdoll = ragdoll;
            Recipe = recipe;
            ArmIK = armIK;
            Left = left;
            Right = right;
            Interaction = interaction;
            State = ECarryState.Idle;
        }

        // ------------------------------------------------------------------

        void FixedUpdate()
        {
            if (Recipe == null || !Recipe.UseCarry || Ragdoll == null || Ragdoll.Hips == null
                || Ragdoll.Hips.Body == null)
                return;

            float dt = Time.fixedDeltaTime;
            StateTime += dt;

            switch (State)
            {
                case ECarryState.Idle: TickIdle(); break;
                case ECarryState.Reaching: TickReaching(dt); break;
                case ECarryState.Holding: TickHolding(); break;
            }

            // 这一套状态机只管**一只手**，两只手去抱同一个物体它做不到 —— 搬运期间让它别动手。
            if (Interaction != null)
                Interaction.Suppressed = State != ECarryState.Idle;
        }

        // ---- Idle：找「两只手都够得到的那一个」 ---------------------------

        void TickIdle()
        {
            HeldMass = 0f;
            LoadRatio = 0f;
            CarryObject = null;
            CarryBody = null;
            SlotClampDistance = 0f;

            if (Left == null || Right == null)
                return;

            bool both = Left.GripHeld && Right.GripHeld;
            if (!both)
            {
                _needRelease = false;    // 松开过 = 允许下一次抱
                _target = null;
                return;
            }
            if (_needRelease)
                return;                  // 抱过一次之后必须先全松开

            _target = FindCarryTarget();
            if (_target == null)
                return;

            _reachTime = 0f;
            _leftAtGrip = _rightAtGrip = false;
            StiffenArms(Recipe.CarryArmStiffnessScale);   // 伸手段就要加硬（见类注释）
            StiffenTorso(Recipe.CarryTorsoStiffnessScale);
            SetGrabCollisionIgnored(_target, true);
            // ★ 伸手段就要把「物体 ↔ 身体」也关掉：这只浣熊胳膊短，抱着的东西**必然**贴着肚子，
            //   不关的话躯干碰撞体会在伸手期间就把箱子顶出去 ——
            //   实测箱子被顶到骨盆前 0.44m（臂长只够 0.478m，还要减去肩在骨盆后 0.14m），
            //   于是两只手永远差 0.15m 够不到，搬运时好时坏。
            SetBodyCollisionIgnored(_target, Recipe.CarryIgnoreBodyCollision);
            Enter(ECarryState.Reaching, "两只手都按住了 → 抱 " + _target.name);
        }

        /// <summary>找身前最近、两只手都够得着的可搬物体。</summary>
        GameObject FindCarryTarget()
        {
            // 已经有一只手抓着东西（单手拖着走）⇒ 直接把这件东西升级成双手抱。
            GameObject held = HeldObjectOf(Left);
            if (held == null)
                held = HeldObjectOf(Right);
            if (held != null && held.GetComponentInParent<Rigidbody>() != null)
                return held;

            Vector3 fwd = ForwardFlat();
            Vector3 pelvis = Ragdoll.Hips.Body.position;
            float cosHalf = Mathf.Cos(Recipe.CarrySearchAngle * 0.5f * Mathf.Deg2Rad);
            float best = float.MaxValue;
            GameObject bestGo = null;

            Grippable[] marked = Object.FindObjectsByType<Grippable>(FindObjectsSortMode.None);
            for (int i = 0; i < marked.Length; i++)
            {
                GameObject go = marked[i] != null ? marked[i].gameObject : null;
                if (!Carryable(go))
                    continue;
                Vector3 to = go.transform.position - pelvis;
                Vector3 flat = new Vector3(to.x, 0f, to.z);
                float d = flat.magnitude;
                if (d > Recipe.CarrySearchRadius)
                    continue;
                if (d > 0.05f && Vector3.Dot(flat / d, fwd) < cosHalf)
                    continue;            // 只在身前找
                float score = d + go.GetComponentInParent<Rigidbody>().mass * Recipe.CarrySearchMassBias;
                if (score < best)
                {
                    best = score;
                    bestGo = go;
                }
            }
            return bestGo;
        }

        bool Carryable(GameObject go)
        {
            if (go == null)
                return false;
            Rigidbody rb = go.GetComponentInParent<Rigidbody>();
            if (rb == null || rb.isKinematic)
                return false;                       // 单杠那种「连到世界」的抓法不走搬运
            if (Ragdoll != null && (go == Ragdoll.gameObject || go.transform.IsChildOf(Ragdoll.transform)))
                return false;                       // 不许抱自己身上的部件
            if (Recipe.CarryMaxMass > 0f && rb.mass > Recipe.CarryMaxMass)
                return false;                       // ⚠️ 0 = **不设上限**（写成 `mass > 0` 会把所有东西都挡掉）
            return true;
        }

        static GameObject HeldObjectOf(HandGripper hand)
        {
            return hand != null && hand.HasGrip ? hand.GrabbedObject : null;
        }

        // ---- Reaching：两只手分别伸手到位，然后各自建搬运关节 -------------

        void TickReaching(float dt)
        {
            _reachTime += dt;

            if (_target == null)
            {
                CancelReach("目标没了");
                return;
            }
            if (!Left.GripHeld || !Right.GripHeld)
            {
                CancelReach("松开了一只");
                return;
            }

            // ★ 伸手段：目标点 = **物体此刻的两肋**（物体在哪就抓哪）。
            //   抱住段才换成「骨盆前的携带位」（见 ComputeCarryFrame 的 fromObject）。
            Vector3 slot, gripL, gripR, palmL, palmR;
            if (!ComputeCarryFrame(_target, true, out slot, out gripL, out gripR, out palmL, out palmR))
            {
                CancelReach("取不到抓取点");
                return;
            }

            if (ArmIK != null)
            {
                ArmIK.SetTarget(Left.Suffix, gripL, true, palmL, 1f);
                ArmIK.SetTarget(Right.Suffix, gripR, true, palmR, 1f);
            }

            // ★ 到位进度（HUD 用）：这一条是给 owner 的«抓没抓住»的答案 ——
            //   以前两只手伸出去之后界面上什么都没变，只能靠«箱子有没有跟过来»猜。
            ReachDistL = Left != null && Left.HandBody != null ? Vector3.Distance(Left.HandBody.position, gripL) : -1f;
            ReachDistR = Right != null && Right.HandBody != null ? Vector3.Distance(Right.HandBody.position, gripR) : -1f;
            ReachOkL = Reached(Left, gripL);
            ReachOkR = Reached(Right, gripR);
            Hint = "伸手抱 " + (_target != null ? _target.name : "?")
                 + "：左手 " + (ReachOkL ? "**到位**" : "差 " + (ReachDistL * 100f).ToString("F0") + "cm")
                 + " · 右手 " + (ReachOkR ? "**到位**" : "差 " + (ReachDistR * 100f).ToString("F0") + "cm")
                 + (_target != null && !ReachOkL && !ReachOkR ? "（再走近一点）" : "");

            bool lOk, rOk;
            if (Recipe.CarryHoldOnChest)
            {
                // ★ 胸口模式：两只手只要**到位**就算数，不建手关节（见 CarryHoldOnChest 的注释）。
                //   闩锁：到位就记住，手被撞开也不取消（否则会在"抱住 ↔ 没抱住"之间反复横跳）。
                if (!_leftAtGrip && Reached(Left, gripL)) _leftAtGrip = true;
                if (!_rightAtGrip && Reached(Right, gripR)) _rightAtGrip = true;
                lOk = _leftAtGrip;
                rOk = _rightAtGrip;
            }
            else
            {
                lOk = Left.HasGrip && Left.GrabbedObject == _target;
                rOk = Right.HasGrip && Right.GrabbedObject == _target;
                if (!lOk && Reached(Left, gripL))
                    lOk = Attach(Left, _anchorLeft);
                if (!rOk && Reached(Right, gripR))
                    rOk = Attach(Right, _anchorRight);
            }

            if (lOk && rOk)
            {
                EnterHolding();
                return;
            }

            if (_reachTime > Recipe.CarryReachTimeout)
                CancelReach("够不到（" + _reachTime.ToString("F1") + "s 超时，" + DescribeReach(gripL, gripR) + "）");
        }

        string DescribeReach(Vector3 gripL, Vector3 gripR)
        {
            Vector3 hl = Left != null && Left.HandBody != null ? Left.HandBody.position : Vector3.zero;
            Vector3 hr = Right != null && Right.HandBody != null ? Right.HandBody.position : Vector3.zero;
            return "手距目标 " + Vector3.Distance(hl, gripL).ToString("F3") + "/"
                 + Vector3.Distance(hr, gripR).ToString("F3") + "m，容差 " + Recipe.CarryGrabTolerance.ToString("F3");
        }

        bool Reached(HandGripper hand, Vector3 gripWorld)
        {
            if (hand == null || hand.HandBody == null)
                return false;
            // ⚠️ 判据要用 **IK 夹紧之后**的手误差，不是«手到原始抓取点»的距离。
            //   大箱子的两肋本来就可能在臂长之外（0.60m 的箱子：抓取点到肩 0.48m ≈ 可达 0.478m），
            //   IK 会把手停在可达面上。用原始距离的话**永远判不成到位** ⇒ 大箱子抱不起来。
            ArmIKChain c = ArmIK != null ? ArmIK.Get(hand.Suffix) : null;
            float err = c != null && c.HasTarget
                ? c.HandError
                : Vector3.Distance(hand.HandBody.position, gripWorld);
            if (err > Recipe.CarryGrabTolerance)
                return false;
            // …但夹紧之后还得确认手**真的贴到了物体**：不然几米外的大箱子也会被判成到位，
            //    接着被胸口关节从远处拽过来（那看着像隔空取物）。
            return Vector3.Distance(hand.HandBody.position, gripWorld) <= Recipe.CarryGrabGoalSlack;
        }

        /// <summary>
        /// 让一只手握住物体。**已经单手抓着**的（从旧路径升级上来的）只转换关节参数，
        /// 不重新建 —— 重新建要先松手，而这只浣熊够不到地面，东西一落地就再也拿不起来了。
        /// </summary>
        bool Attach(HandGripper hand, Vector3 anchorLocal)
        {
            if (hand == null || _target == null)
                return false;
            // ⚠️ **一定要用物体自己的 Rigidbody，不能用字段 `CarryBody`。**
            //    `CarryBody` 是「抱住」那一刻才赋值的，而这里跑在**伸手段** ——
            //    它还是 null。`connectedBody = null` 在 Unity 里**不是报错，是连到世界**：
            //    手被钉在世界的 (±0.22, 0, 0) 附近（等于抓住了一根看不见的单杠），
            //    箱子照样掉在地上。实测症状：状态一路「抱着」，人却一步都走不动
            //    （4s 走 0.1m），箱子留在原地。
            Rigidbody rb = _target.GetComponentInParent<Rigidbody>();
            if (rb == null)
                return false;
            float spring, damper, maxForce;
            HoldSprings(out spring, out damper, out maxForce);

            if (hand.HasGrip && hand.GrabbedObject == _target)
            {
                hand.SetCarryHold(anchorLocal, spring, damper, maxForce, Recipe.CarryAngularDamping);
                return true;
            }
            return hand.TryGrabForCarry(_target, rb, anchorLocal, spring, damper, maxForce,
                                        Recipe.CarryAngularDamping);
        }

        // ---- Holding：抱着 ------------------------------------------------

        void TickHolding()
        {
            if (_target == null || CarryBody == null)
            {
                Drop("物体没了", false);
                return;
            }
            if (!Left.GripHeld || !Right.GripHeld)
            {
                Drop("松手", true);
                return;
            }
            if (Recipe.CarryHoldOnChest)
            {
                if (_chestJoint == null)
                {
                    Drop("抱持关节没了", false);
                    return;
                }
            }
            else if (!Left.HasGrip || !Right.HasGrip)
            {
                Drop("搬运关节断了", false);
                return;
            }

            HeldMass = CarryBody.mass;
            float total = Mathf.Max(0.1f, Ragdoll.TotalMass);
            LoadRatio = HeldMass / total;

            if (Recipe.CarryHoldOnChest)
            {
                // 胸口关节每帧刷新（携带位跟着骨盆走、朝向跟着角色走）
                ApplyChestHold();
            }
            else
            {
                // 弹簧按质量缩放：不然越重的物体在手里越«软»，手感与质量反相关。
                float spring, damper, maxForce;
                HoldSprings(out spring, out damper, out maxForce);
                Left.SetCarryHold(_anchorLeft, spring, damper, maxForce, Recipe.CarryAngularDamping);
                Right.SetCarryHold(_anchorRight, spring, damper, maxForce, Recipe.CarryAngularDamping);
            }

            // 抱住之后才用「骨盆前的携带位」——物体被拉进怀里。
            Vector3 slot, gripL, gripR, palmL, palmR;
            if (ComputeCarryFrame(_target, false, out slot, out gripL, out gripR, out palmL, out palmR)
                && ArmIK != null)
            {
                ArmIK.SetTarget(Left.Suffix, gripL, true, palmL, 1f);
                ArmIK.SetTarget(Right.Suffix, gripR, true, palmR, 1f);
                TargetFromObject = false;
            }

            if (!Recipe.CarryHoldOnChest)
                ApplyUprightTorque();

            // 诊断读数：物体有没有真的待在携带位上。
            Vector3 fwd = ForwardFlat();
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 d = _target.transform.position - slot;
            SlotErrorForward = Vector3.Dot(d, fwd);
            SlotErrorUp = Vector3.Dot(d, Vector3.up);
            SlotErrorLateral = Vector3.Dot(d, right);
            BoxTiltDegrees = Vector3.Angle(_target.transform.up, Vector3.up);
            HandErrorLeft = Left.HandBody != null ? Vector3.Distance(Left.HandBody.position, gripL) : 0f;
            HandErrorRight = Right.HandBody != null ? Vector3.Distance(Right.HandBody.position, gripR) : 0f;
        }

        /// <summary>
        /// ★ **只把被搬物体的«自转»摆正**（一个 PD 力矩，作用在物体自己身上）。
        ///
        /// 为什么必须有这一条：两只手各是一个**点约束**，它们只能定住物体的一条轴
        /// （两手连线那根局部轴）。**绕这条轴的自转是自由的** —— 重力对它没有力矩
        /// （质心就在轴上），所以没有任何东西把它摆回来：进入搬运那一刻物体是什么滚转角，
        /// 它就一直是那个角度。
        ///
        /// ⚠️ **只许纠«绕两手连线»这一个自由度，不许纠整个朝向。**
        /// 第一版写成「把物体转到角色朝向」（`LookRotation(fwd, up)`），
        /// 结果它和点约束**互相打架**：约束要求物体的轴贴着«两只手实际的连线»，
        /// 回正却要求它贴着«角色的右边» —— 而手本来就因为臂软落后于目标，
        /// 两个要求打架 ⇒ 极限环，实测歪角在 34°~125° 之间乱跳（同一组参数不同轮都不重样）。
        /// 现在把回正力矩**只加在两手上**（阻尼也只扣这个分量），与约束同向，不抢方向。
        ///
        /// 手法与工程里其它几处一致（`ClumsyBalance` 的撑直力矩、`StepYawServo` 的转身伺服）：
        /// 一个 PD、**按质量缩放**、有出力上限。
        /// </summary>
        void ApplyUprightTorque()
        {
            UprightTorqueNow = 0f;
            if (CarryBody == null || CarryBody.isKinematic || _target == null)
                return;
            float k = Recipe.CarryUprightStiffnessPerKg * CarryBody.mass;
            if (k <= 0f || Left == null || Right == null || Left.HandBody == null || Right.HandBody == null)
                return;

            // 约束轴：**两只手实际的连线**（不是我们希望它在哪）
            Vector3 aW = Right.HandBody.position - Left.HandBody.position;
            if (aW.sqrMagnitude < 1e-6f)
                return;
            aW.Normalize();

            // 物体上与抓取轴垂直的那根局部轴，现在指向哪里
            Vector3 bLocal = Vector3.zero;
            bLocal[Mathf.Abs(_grabAxisLocal.x) > 0.5f ? 1 : 0] = 1f;
            Vector3 bWorld = CarryBody.rotation * bLocal;

            // 只留«垂直于 aW»的分量，再把世界竖直投到同一个平面上
            Vector3 bPerp = Vector3.ProjectOnPlane(bWorld, aW);
            Vector3 upWant = Vector3.ProjectOnPlane(Vector3.up, aW);
            if (bPerp.sqrMagnitude < 1e-6f || upWant.sqrMagnitude < 1e-6f)
                return;
            bPerp.Normalize();
            upWant.Normalize();

            float ang = Mathf.Atan2(Vector3.Cross(bPerp, upWant).magnitude, Vector3.Dot(bPerp, upWant));
            float sign = Vector3.Dot(Vector3.Cross(bPerp, upWant), aW) >= 0f ? 1f : -1f;
            float wSpin = Vector3.Dot(CarryBody.angularVelocity, aW);

            Vector3 tau = aW * (ang * sign * k - wSpin * (Recipe.CarryUprightDampingPerKg * CarryBody.mass));
            float cap = Recipe.CarryUprightMaxTorquePerKg * CarryBody.mass;
            tau = Vector3.ClampMagnitude(tau, cap);
            CarryBody.AddTorque(tau, ForceMode.Force);
            UprightTorqueNow = tau.magnitude;
        }

        /// <summary>
        /// ★ **建「胸口抱住」关节**（`CarryHoldOnChest`）。关节加在**物体**上，
        /// `connectedBody` = 骨盆；线性 + 角运动各一路弹簧驱动 ⇒ **物体的位姿完全由这一个关节决定**。
        ///
        /// 为什么最后走的是这条路（而不是两只手各一个点约束）：见 `CarryHoldOnChest` 的注释。
        /// </summary>
        void BuildChestHold()
        {
            if (CarryBody == null || Ragdoll == null || Ragdoll.Hips == null || Ragdoll.Hips.Body == null)
                return;
            if (_chestJoint != null)
                Destroy(_chestJoint);

            ConfigurableJoint j = CarryBody.gameObject.AddComponent<ConfigurableJoint>();
            j.connectedBody = Ragdoll.Hips.Body;
            j.autoConfigureConnectedAnchor = false;
            j.anchor = Vector3.zero;                 // 物体的中心
            j.connectedAnchor = Ragdoll.Hips.Body.transform.InverseTransformPoint(SlotWorld);
            j.axis = Vector3.right;
            j.secondaryAxis = Vector3.up;
            j.rotationDriveMode = RotationDriveMode.XYAndZ;
            j.enablePreprocessing = true;
            j.enableCollision = false;

            j.xMotion = ConfigurableJointMotion.Free;
            j.yMotion = ConfigurableJointMotion.Free;
            j.zMotion = ConfigurableJointMotion.Free;
            j.angularXMotion = ConfigurableJointMotion.Free;
            j.angularYMotion = ConfigurableJointMotion.Free;
            j.angularZMotion = ConfigurableJointMotion.Free;
            j.breakForce = Recipe.CarryBreakForce > 0f ? Recipe.CarryBreakForce : Recipe.GripBreakForce;
            j.breakTorque = j.breakForce;

            _chestJoint = j;
            _boxRotAtHold = CarryBody.rotation;
            ApplyChestHold();
        }

        /// <summary>
        /// 每帧刷「胸口抱住」关节：携带位（跟着骨盆）、朝向（跟着角色）、
        /// 以及按质量缩放的弹簧 / 阻尼。
        ///
        /// ⚠️ **`targetRotation` 的语义是实测出来的**（`ConfigurableJoint` 文档没写清）：
        /// 用「一个静止刚体 + 一个自由刚体 + 角驱动」的隔离实验量到
        /// **末朝向 = 起始朝向 × targetRotation⁻¹**（起始 40°/目标 40° ⇒ 末 0°；
        /// 起始 0°/目标 90° ⇒ 末 270°）。所以「想让物体转到 `want`」要写
        /// `targetRotation = want⁻¹ × 建关节那一刻的朝向`。
        /// </summary>
        void ApplyChestHold()
        {
            if (_chestJoint == null || CarryBody == null || Ragdoll == null || Ragdoll.Hips == null
                || Ragdoll.Hips.Body == null)
                return;

            float m = Mathf.Max(0.1f, CarryBody.mass);
            _chestJoint.connectedAnchor = Ragdoll.Hips.Body.transform.InverseTransformPoint(SlotWorld);

            JointDrive lin = new JointDrive();
            lin.positionSpring = Recipe.CarryChestSpringPerKg * m;
            lin.positionDamper = lin.positionSpring * Recipe.CarryChestDamperRatio;
            lin.maximumForce = Recipe.CarryHoldMaxForce;
            _chestJoint.xDrive = lin;
            _chestJoint.yDrive = lin;
            _chestJoint.zDrive = lin;

            JointDrive ang = new JointDrive();
            ang.positionSpring = Recipe.CarryAngularSpringPerKg * m;
            ang.positionDamper = ang.positionSpring * Recipe.CarryAngularDamperRatio;
            ang.maximumForce = Recipe.CarryHoldMaxForce;
            _chestJoint.angularXDrive = ang;
            _chestJoint.angularYZDrive = ang;

            _chestJoint.targetRotation = Quaternion.Inverse(DesiredBoxRotation()) * _boxRotAtHold;
        }

        /// <summary>物体«应该»的朝向：抓取轴（两手连线那根局部轴）朝角色右边，另两轴里挑一根朝上。</summary>
        Quaternion DesiredBoxRotation()
        {
            Vector3 fwd = ForwardFlat();
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 aLocal = _grabAxisLocal;
            Vector3 bLocal = Vector3.zero;
            bLocal[Mathf.Abs(aLocal.x) > 0.5f ? 1 : 0] = 1f;
            Vector3 aW = right;
            Vector3 bW = Vector3.up;
            Vector3 cW = Vector3.Cross(aW, bW);
            Vector3 cLocal = Vector3.Cross(aLocal, bLocal);
            return Quaternion.LookRotation(cW, bW) * Quaternion.Inverse(Quaternion.LookRotation(cLocal, bLocal));
        }

        /// <summary>
        /// ★ 2026-10-02：抱住时把物体**染一个颜色**（松手还原）。
        /// owner 报「伸出双手接触物品之后不能确定是不是真的抓住物品了」——
        /// 状态机进了 Holding 是代码里的事，玩家只能靠«箱子有没有跟过来»猜。
        /// 染色是最直接的答案（物体是运行时 `CreatePrimitive` 出来的，材质是各自一份实例，改色安全）。
        /// </summary>
        void SetHoldTint(bool on)
        {
            if (_tintRends != null)
            {
                for (int i = 0; i < _tintRends.Length; i++)
                {
                    if (_tintRends[i] == null || _tintRends[i].material == null) continue;
                    if (_tintColors != null && i < _tintColors.Length)
                        _tintRends[i].material.color = _tintColors[i];
                }
                _tintRends = null;
                _tintColors = null;
            }
            if (!on || !TintHeldObject || _target == null)
                return;
            Renderer[] rs = _target.GetComponentsInChildren<Renderer>();
            var cols = new Color[rs.Length];
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null || rs[i].material == null) continue;
                cols[i] = rs[i].material.color;
                rs[i].material.color = Color.Lerp(cols[i], HeldTint, 0.45f);
            }
            _tintRends = rs;
            _tintColors = cols;
        }

        Renderer[] _tintRends;
        Color[] _tintColors;

        void HoldSprings(out float spring, out float damper, out float maxForce)
        {
            float m = CarryBody != null ? CarryBody.mass : 1f;
            spring = Recipe.CarryHoldSpringPerKg * Mathf.Max(0.5f, m);
            damper = spring * Recipe.CarryHoldDamperRatio;
            maxForce = Recipe.CarryHoldMaxForce;
        }

        // ---- 进 / 出 ------------------------------------------------------

        void Enter(ECarryState next, string why)
        {
            if (State != next)
                LastTransition = StateNames[(int)State] + " → " + StateNames[(int)next] + "：" + why;
            State = next;
            StateTime = 0f;
        }

        void EnterHolding()
        {
            CarryObject = _target;
            CarryBody = _target != null ? _target.GetComponentInParent<Rigidbody>() : null;
            // ★ 抱住之后**不要**恢复「手 ↔ 物体」的碰撞。
            //   胸口模式下两只手**没有关节**，只是 IK 跟过去搭在箱子侧面 ——
            //   碰撞一恢复，手腕那颗半径 6.5cm 的球就会一直砸箱子：箱子被顶走 →
            //   胸口关节把它拉回来 → 再被顶走 …… **这就是«抱住之后剧烈抖动»的主因**
            //   （两条手链模式下由关节自己的 enableCollision=false 管，所以那边恢复也不要紧）。
            if (!Recipe.CarryIgnoreHandCollision || !Recipe.CarryHoldOnChest)
                RestoreGrabCollision();
            SetBodyCollisionIgnored(_target, Recipe.CarryIgnoreBodyCollision);
            if (Recipe.CarryHoldOnChest)
            {
                if (Left != null) Left.Release("改用双手抱");
                if (Right != null) Right.Release("改用双手抱");
                BuildChestHold();
            }
            SetHoldTint(true);
            EnterCount++;
            _needRelease = false;
            Hint = "抱住了 " + (_target != null ? _target.name : "?") + "（再按一下松手 = 放下）";
            Enter(ECarryState.Holding, "抱住了 " + (_target != null ? _target.name : "?"));
        }

        void CancelReach(string why)
        {
            if (ArmIK != null && Left != null) ArmIK.ClearTarget(Left.Suffix);
            if (ArmIK != null && Right != null) ArmIK.ClearTarget(Right.Suffix);
            SetHoldTint(false);
            RestoreGrabCollision();
            RestoreBodyCollision();
            RestoreArms();
            _leftAtGrip = _rightAtGrip = false;
            _target = null;
            _needRelease = true;
            Enter(ECarryState.Idle, "放弃：" + why);
        }

        /// <summary>
        /// 放下。<paramref name="throwIt"/> = 走动着松手 ⇒ **丢出去**：
        /// 沿当时的水平运动方向补一个速度（站着松手就只是掉下去）。
        /// 设计口径来自 owner 选的「松手=放下，走动着丢出去」。
        /// </summary>
        void Drop(string why, bool throwIt)
        {
            if (throwIt && CarryBody != null && Recipe.CarryReleaseThrowSpeed > 0f)
            {
                Vector3 v = CarryBody.linearVelocity;
                Vector3 flat = new Vector3(v.x, 0f, v.z);
                Vector3 dir = flat.magnitude > Recipe.CarryReleaseThrowMinSpeed
                    ? flat.normalized : ForwardFlat();
                Vector3 add = dir * Recipe.CarryReleaseThrowSpeed + Vector3.up * Recipe.CarryReleaseThrowLift;
                CarryBody.AddForce(add * CarryBody.mass, ForceMode.Impulse);
            }

            if (Left != null) Left.Release(why);
            if (Right != null) Right.Release(why);
            if (ArmIK != null)
            {
                if (Left != null) ArmIK.ClearTarget(Left.Suffix);
                if (Right != null) ArmIK.ClearTarget(Right.Suffix);
            }
            if (_chestJoint != null)
            {
                Destroy(_chestJoint);
                _chestJoint = null;
            }
            SetHoldTint(false);
            RestoreGrabCollision();
            RestoreBodyCollision();
            RestoreArms();

            DropCount++;
            _target = null;
            CarryObject = null;
            CarryBody = null;
            HeldMass = 0f;
            LoadRatio = 0f;
            _needRelease = true;      // 必须先全松开才能再抱
            _reachTime = 0f;
            Enter(ECarryState.Idle, "放下：" + why);
        }

        void StiffenArms(float scale)
        {
            if (Ragdoll == null)
                return;
            // ★ scale ≤ 1 = 还回原样：这时必须用**不带阻尼**的那个重载
            //   （Recipe.DampingRatio = 0 是这只浣熊的手感基础，不能被搬运层改掉）。
            bool restore = scale <= 1.001f;
            for (int s = 0; s < 2; s++)
            {
                string suf = s == 0 ? "l" : "r";
                for (int i = 0; i < ArmKeys.Length; i++)
                {
                    if (restore) Ragdoll.SetSpringScale(ArmKeys[i] + suf, scale);
                    else Ragdoll.SetSpringScale(ArmKeys[i] + suf, scale, Recipe.CarryStiffnessDampingRatio);
                }
            }
        }

        void RestoreArms()
        {
            // 还回去的时候要**用原来的阻尼比**（Recipe.DampingRatio = 0 ⇒ 无阻尼），
            // 所以这一路走的是不带阻尼参数的那个重载。
            StiffenArms(1f);
            StiffenTorso(1f);
        }

        /// <summary>
        /// ★ 躯干也要加硬。只加硬手臂的话，重量仍然会把脊椎压弯 ——
        /// 实测两只肩的高度差 0.19m（左 0.78 / 右 0.97），携带位跟着歪，箱子在怀里打滚。
        /// </summary>
        void StiffenTorso(float scale)
        {
            if (Ragdoll == null)
                return;
            bool restore = scale <= 1.001f;
            for (int i = 0; i < TorsoKeys.Length; i++)
            {
                if (restore) Ragdoll.SetSpringScale(TorsoKeys[i], scale);
                else Ragdoll.SetSpringScale(TorsoKeys[i], scale, Recipe.CarryStiffnessDampingRatio);
            }
        }

        // ---- 碰撞：伸手段与抱住段各关一对（见类注释） ----------------------

        void SetGrabCollisionIgnored(GameObject target, bool ignore)
        {
            RestoreGrabCollision();
            if (!ignore || target == null)
                return;
            Collider hl = Left != null && Left.HandPart != null ? Left.HandPart.Shape : null;
            Collider hr = Right != null && Right.HandPart != null ? Right.HandPart.Shape : null;
            Collider[] cols = target.GetComponentsInChildren<Collider>();
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == null || cols[i].isTrigger)
                    continue;
                if (hl != null) Physics.IgnoreCollision(hl, cols[i], true);
                if (hr != null) Physics.IgnoreCollision(hr, cols[i], true);
            }
            _handColL = hl;
            _handColR = hr;
            _targetCols = cols;
        }

        void RestoreGrabCollision()
        {
            if (_targetCols != null)
            {
                for (int i = 0; i < _targetCols.Length; i++)
                {
                    if (_targetCols[i] == null)
                        continue;
                    if (_handColL != null) Physics.IgnoreCollision(_handColL, _targetCols[i], false);
                    if (_handColR != null) Physics.IgnoreCollision(_handColR, _targetCols[i], false);
                }
            }
            _handColL = null;
            _handColR = null;
            _targetCols = null;
        }

        void SetBodyCollisionIgnored(GameObject target, bool ignore)
        {
            RestoreBodyCollision();
            if (!ignore || target == null || Ragdoll == null)
                return;
            Collider[] cols = target.GetComponentsInChildren<Collider>();
            var parts = new System.Collections.Generic.List<Collider>();
            for (int p = 0; p < Ragdoll.Parts.Count; p++)
            {
                Collider pc = Ragdoll.Parts[p].Shape;
                if (pc == null)
                    continue;
                for (int i = 0; i < cols.Length; i++)
                {
                    if (cols[i] == null || cols[i].isTrigger)
                        continue;
                    Physics.IgnoreCollision(pc, cols[i], true);
                }
                parts.Add(pc);
            }
            _bodyCols = parts.ToArray();
            _ignoreTarget = target;
            _targetColsBody = cols;
        }

        Collider[] _targetColsBody;

        void RestoreBodyCollision()
        {
            if (_bodyCols != null && _targetColsBody != null)
            {
                for (int p = 0; p < _bodyCols.Length; p++)
                {
                    if (_bodyCols[p] == null)
                        continue;
                    for (int i = 0; i < _targetColsBody.Length; i++)
                    {
                        if (_targetColsBody[i] == null)
                            continue;
                        Physics.IgnoreCollision(_bodyCols[p], _targetColsBody[i], false);
                    }
                }
            }
            _bodyCols = null;
            _targetColsBody = null;
            _ignoreTarget = null;
        }

        // ---- 携带位的几何 --------------------------------------------------

        Vector3 ForwardFlat()
        {
            Vector3 f = Ragdoll != null && Ragdoll.Root != null ? Ragdoll.Root.forward : transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f)
                f = Vector3.forward;
            return f.normalized;
        }

        /// <summary>物体在其**局部轴**上的半尺寸（换算到世界单位）。</summary>
        static Vector3 HalfExtents(GameObject go)
        {
            Vector3 ls = go.transform.lossyScale;
            BoxCollider bc = go.GetComponentInChildren<BoxCollider>();
            if (bc != null)
                return Vector3.Scale(bc.size * 0.5f, bc.transform.lossyScale);
            Renderer r = go.GetComponentInChildren<Renderer>();
            if (r != null)
                return Vector3.Scale(r.localBounds.extents, ls);
            return Vector3.one * 0.15f;
        }

        /// <summary>物体沿某个**世界方向**的半尺寸（取最贴近那个方向的局部轴）。</summary>
        static float ExtentAlong(GameObject go, Vector3 worldDir, Vector3 ext)
        {
            Vector3 l = go.transform.InverseTransformDirection(worldDir);
            float ax = Mathf.Abs(l.x), ay = Mathf.Abs(l.y), az = Mathf.Abs(l.z);
            if (ax >= ay && ax >= az)
                return ext.x;
            return ay >= az ? ext.y : ext.z;
        }

        /// <summary>
        /// 算这一帧两只手该去哪。<paramref name="fromObject"/> 决定用哪一套参照系：
        ///
        /// <list type="bullet">
        ///   <item><b>true（伸手段）</b>：目标点 = **物体此刻的两肋**
        ///         （把物体局部的那两个抓取点变换到世界）。物体在哪就抓哪 ——
        ///         人手去够东西本来就是这样，而不是「先假定东西在怀里、再去找那个空位」。</item>
        ///   <item><b>false（抱住段）</b>：目标点 = **骨盆前的携带位**。
        ///         所有量都定义在「骨盆 + 角色朝向」这个参照系里，
        ///         **一个量都不引用物体当前的位置或朝向**（引用就会变成死循环，见类注释）。</item>
        /// </list>
        /// </summary>
        bool ComputeCarryFrame(GameObject go, bool fromObject, out Vector3 slot, out Vector3 gripL,
                               out Vector3 gripR, out Vector3 palmL, out Vector3 palmR)
        {
            slot = gripL = gripR = Vector3.zero;
            palmL = -Vector3.right;
            palmR = Vector3.right;
            SlotClampDistance = 0f;
            if (go == null || Ragdoll == null || Ragdoll.Hips == null || Ragdoll.Hips.Body == null)
                return false;

            Vector3 up = Vector3.up;
            Vector3 fwd = ForwardFlat();
            Vector3 right = Vector3.Cross(up, fwd).normalized;
            Vector3 pelvis = Ragdoll.Hips.Body.position;
            Vector3 ext = HalfExtents(go);

            // ① 抓哪两肋：物体局部轴里最贴着«角色右边»的那一根。
            //    （不能写死成本地 X —— 箱子被撞歪过之后本地 X 未必还朝着侧面。）
            Vector3 localRight = go.transform.InverseTransformDirection(right);
            int axis = 0;
            float best = Mathf.Abs(localRight.x);
            if (Mathf.Abs(localRight.y) > best) { axis = 1; best = Mathf.Abs(localRight.y); }
            if (Mathf.Abs(localRight.z) > best) { axis = 2; best = Mathf.Abs(localRight.z); }
            Vector3 axisLocal = Vector3.zero;
            axisLocal[axis] = localRight[axis] >= 0f ? 1f : -1f;
            _grabAxisLocal = axisLocal;
            float halfAlong = ext[axis];

            // 物体局部坐标里的抓取点（连带把世界单位的 standoff 换算回局部）。
            float half = halfAlong + Recipe.CarryGripStandoff;
            Vector3 ls = go.transform.lossyScale;
            Vector3 invScale = new Vector3(1f / Mathf.Max(0.001f, ls.x),
                                           1f / Mathf.Max(0.001f, ls.y),
                                           1f / Mathf.Max(0.001f, ls.z));
            _anchorLeft = Vector3.Scale(-axisLocal * half, invScale);
            _anchorRight = Vector3.Scale(axisLocal * half, invScale);

            float halfDepth = ExtentAlong(go, fwd, ext);
            float slotFwd = Recipe.CarrySlotForward + halfDepth * Recipe.CarrySlotDepthScale;
            slot = pelvis + fwd * slotFwd + up * Recipe.CarrySlotUp;
            SlotWorld = slot;
            TargetFromObject = fromObject;

            if (fromObject)
            {
                // 伸手段：直接抓物体现在的那两个点
                gripL = go.transform.TransformPoint(_anchorLeft);
                gripR = go.transform.TransformPoint(_anchorRight);
                Vector3 center = go.transform.position;
                Vector3 nl = gripL - center;
                Vector3 nr = gripR - center;
                palmL = nl.sqrMagnitude > 1e-8f ? nl.normalized : -right;
                palmR = nr.sqrMagnitude > 1e-8f ? nr.normalized : right;
                // ⚠️ 诊断字段在这里也要落 —— 漏了的话 HUD/读数会显示上一次的残留值
                //    （实测就吃过一次：伸手段的 `GripLeftWorld` 一直是 (0,0,0)，
                //     看着像«手的目标点在原点»，其实是没赋值）。
                GripLeftWorld = gripL;
                GripRightWorld = gripR;
                return true;
            }

            gripL = slot - right * half;
            gripR = slot + right * half;
            palmL = -right;     // 目标表面的**外法线**：左手那面朝 −右 ⇒ 掌心朝 +右（朝里）
            palmR = right;

            // ② 够不着就把携带位整体往回拉 —— 大箱子自动«往怀里抱近一点»，
            //    不然 IK 会把两只手各自夹到可达球面上**不同**的地方，箱子被拧。
            if (Recipe.CarrySlotClampToReach && ArmIK != null)
            {
                float over = Mathf.Max(ReachOvershoot("l", gripL), ReachOvershoot("r", gripR));
                if (over > 0f)
                {
                    SlotClampDistance = over;
                    Vector3 back = fwd * over;
                    slot -= back;
                    gripL -= back;
                    gripR -= back;
                    SlotWorld = slot;
                }
            }

            GripLeftWorld = gripL;
            GripRightWorld = gripR;
            return true;
        }

        float ReachOvershoot(string suffix, Vector3 world)
        {
            ArmIKChain c = ArmIK.Get(suffix);
            if (c == null || !c.Ready)
                return 0f;
            float d = Vector3.Distance(c.ShoulderWorld, world);
            return Mathf.Max(0f, d - c.MaxReach * Mathf.Clamp(Recipe.CarryReachMargin, 0.3f, 1f));
        }

        /// <summary>诊断台 / HUD 用的一行摘要。</summary>
        public string Report()
        {
            string s = "搬运 " + StateName;
            if (State == ECarryState.Holding)
            {
                s += "  " + (CarryObject != null ? CarryObject.name : "?")
                   + "  " + HeldMass.ToString("F1") + "kg（体重的 " + (LoadRatio * 100f).ToString("F0") + "%）"
                   + "  携带位误差 前 " + (SlotErrorForward * 100f).ToString("F1")
                   + " / 上 " + (SlotErrorUp * 100f).ToString("F1")
                   + " / 侧 " + (SlotErrorLateral * 100f).ToString("F1") + " cm"
                   + "  歪 " + BoxTiltDegrees.ToString("F1") + "°"
                   + "  手误差 " + (HandErrorLeft * 100f).ToString("F1")
                   + "/" + (HandErrorRight * 100f).ToString("F1") + " cm"
                   + (SlotClampDistance > 0.001f ? "  抱近 " + (SlotClampDistance * 100f).ToString("F1") + " cm" : "");
            }
            else if (State == ECarryState.Reaching)
            {
                s += "  " + _reachTime.ToString("F2") + "s  左手 "
                   + (ReachOkL ? "到位" : (ReachDistL * 100f).ToString("F0") + "cm")
                   + " / 右手 " + (ReachOkR ? "到位" : (ReachDistR * 100f).ToString("F0") + "cm");
            }
            if (!string.IsNullOrEmpty(LastTransition))
                s += "   [" + LastTransition + "]";
            return s;
        }
    }
}
