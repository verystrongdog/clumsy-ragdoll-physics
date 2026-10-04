        // 断言行数：590
using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 环境交互状态。来源：`ref-Unity动画系列-全集.md` EP13 的
    /// <c>EEnvironmentInteractionState</c>（她列的五个：Search / Approach / Rise / Touch / Reset）。
    ///
    /// ⚠️ **教程只列了名字，没讲每个态干什么** —— EP13 原话是「all that's missing is a little bit
    /// of code next time」，具体逻辑留在了不在本工作区里的下一集。所以下面是**本工程的读法**，
    /// 不是从教程抄的：
    /// <list type="bullet">
    ///   <item><b>Search</b>：没有手在够。找「附近、在正前方、够得着」的可抓物体。</item>
    ///   <item><b>Approach</b>：手目标点从当前位置朝抓取点移动（带速度上限）。</item>
    ///   <item><b>Rise</b>：手已经到位，做最后的对准 —— 掌心转向表面、目标点锁死在抓取点上。</item>
    ///   <item><b>Touch</b>：手贴到抓取点，建立抓取关节；此后手跟着物体走。</item>
    ///   <item><b>Reset</b>：松手、收回手臂、IK 权重归零，回 Search。</item>
    /// </list>
    /// 这几个名字在机器人抓取里是通用的（reach → align → grasp → retract），
    /// 与教程的分段方式吻合；但要清楚**这一步是推断**。
    ///
    /// 状态机管的是**一只**手（<see cref="ActiveHand"/>），另一只手这一帧不做 IK。
    /// EP13 那边也只建了一套 <c>leftIKConstraint</c> / <c>rightIKConstraint</c> 却没有说两套状态机怎么共存。
    /// </summary>
    public enum EEnvironmentInteractionState
    {
        Search = 0,
        Approach = 1,
        Rise = 2,
        Touch = 3,
        Reset = 4
    }

    /// <summary>
    /// 手部与环境交互。挂在浣熊根物体上（与 <see cref="ClumsyPoseDriver"/> 同一层）。
    ///
    /// 每物理步在 **<see cref="ClumsyPoseDriver"/> 之前**跑：它只负责
    /// 「这一帧手该去哪、要不要抓」，真正的关节写入由姿势驱动统一做
    /// （<see cref="ClumsyArmIK.Solve"/> → <see cref="ClumsyPoseDriver.Pose"/>）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClumsyInteraction : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;
        public ClumsyArmIK ArmIK;
        public HandGripper Left;
        public HandGripper Right;

        /// <summary>
        /// ★ 2026-10-02：**让位开关**。双手搬运（<see cref="ClumsyCarry"/>）接管期间置 true，
        /// 这套状态机就整帧不动手。
        ///
        /// 为什么必须让位：本状态机**只管一只手**（<see cref="ActiveHand"/>，另一只手这一帧
        /// 连 IK 都不做）—— 而「抱住」要求两只手同时去同一个物体上。
        /// 不让位的话，两只手都按住时它会去抢左手、把右手的目标清掉。
        /// </summary>
        public bool Suppressed;

        public EEnvironmentInteractionState State { get; private set; }
        public float StateTime { get; private set; }
        public HandGripper ActiveHand { get; private set; }
        public GameObject CurrentTarget { get; private set; }
        public Vector3 GraspPoint { get; private set; }
        public Vector3 SurfaceNormal { get; private set; } = Vector3.up;

        /// <summary>手目标点的当前插值值。</summary>
        public Vector3 HandGoal { get; private set; }
        public bool HasGoal { get; private set; }

        /// <summary>状态迁移计数（诊断用）。</summary>
        public int TransitionCount { get; private set; }
        public string LastTransition = "";

        readonly List<GameObject> _candidates = new List<GameObject>();
        float _refreshTimer;

        /// <summary>
        /// 抓住那一刻，手在**物体局部坐标**里的位置，以及当时的手拿法线。
        /// 之后手就锁在这两个量上（每帧用物体的变换反算回世界），**不再每帧重算抓取点**。
        /// 不锁的话会变成一场拔河：手被 IK 拉向抓取点 → 箱子被关节带着动 →
        /// 抓取点又跟着箱子跑 → 手继续追。实测拖着箱子在台面上滑了 0.10m/1.2s。
        /// </summary>
        Vector3 _grabLocalOffset;
        Vector3 _grabLocalNormal = Vector3.up;
        bool _hasGrabOffset;

        // ---- 伸手期间把「手 ↔ 目标」的碰撞关掉 ----
        // 实测（2026-10-01）：手骨的碰撞体是**手腕处**一个半径 6.5cm 的球，而手掌平面还在它前面 6cm。
        // 伸手时这个球会顶在箱子上，接触力直接把手拧歪 —— 实测掌心残差 65~77°、
        // 而且箱体被顶得滑走。把这一对碰撞关掉之后，同一个姿势下掌心残差回到「无接触」的水平
        // （无接触时实测 0.0~0.6°，见交接文档 §五）。
        // 松手 / 换目标 / 回 Search 时恢复。
        Collider _handColIgnore;
        Collider[] _targetColIgnore;
        GameObject _ignoreTarget;

        static readonly string[] StateNames = { "Search", "Approach", "Rise", "Touch", "Reset" };

        public string StateName { get { return StateNames[(int)State]; } }

        // ------------------------------------------------------------------

        public void Setup(ClumsyRagdoll ragdoll, RagdollRecipe recipe, ClumsyArmIK armIK,
                          HandGripper left, HandGripper right)
        {
            Ragdoll = ragdoll;
            Recipe = recipe;
            ArmIK = armIK;
            Left = left;
            Right = right;
            State = EEnvironmentInteractionState.Search;
        }

        // ------------------------------------------------------------------

        void FixedUpdate()
        {
            if (Recipe == null || !Recipe.UseInteraction || ArmIK == null)
                return;
            // ★ 2026-10-02 双手搬运接管中：这一帧不动手（见 Suppressed 的注释）。
            if (Suppressed)
                return;

            float dt = Time.fixedDeltaTime;
            StateTime += dt;

            HandGripper wanted = null;
            if (Left != null && Left.GripHeld)
                wanted = Left;
            else if (Right != null && Right.GripHeld)
                wanted = Right;

            switch (State)
            {
                case EEnvironmentInteractionState.Search:
                    TickSearch(wanted);
                    break;
                case EEnvironmentInteractionState.Approach:
                    TickApproach(wanted, dt, false);
                    break;
                case EEnvironmentInteractionState.Rise:
                    TickApproach(wanted, dt, true);
                    break;
                case EEnvironmentInteractionState.Touch:
                    TickTouch(wanted, dt);
                    break;
                case EEnvironmentInteractionState.Reset:
                    TickReset(dt);
                    break;
            }
        }


        static readonly string[] ReachArmKeys = { "shoulder", "arm", "forearm", "hand" };

        /// <summary>
        /// ★ 2026-10-02：**伸手段把两条手臂加硬一点**（按 `ReachOutOnGrip` 与否开关）。
        ///
        /// 为什么要：手臂的驱动弹簧只有 3.3 N·m/rad（四肢档 × 上半身 0.12），
        /// 而 `Recipe.DampingRatio = 0` ⇒ 无阻尼。直接写 IK 目标的结果是«手停在目标下方 0.33m»——
        /// 看着像伸手，其实是一条垂着的胳膊。加硬 6 倍 + 阻尼比 3 之后手才真的到位。
        /// 与 `ClumsyCarry` 那一套是同一个手法（那边加硬 14 倍，因为还要抱住东西）。
        /// </summary>
        void ApplyReachStiffness(bool on)
        {
            if (Ragdoll == null || Recipe == null)
                return;
            for (int s = 0; s < 2; s++)
            {
                string suf = s == 0 ? "l" : "r";
                for (int i = 0; i < ReachArmKeys.Length; i++)
                {
                    if (on)
                        Ragdoll.SetSpringScale(ReachArmKeys[i] + suf, Recipe.ReachOutArmStiffnessScale,
                                               Recipe.ReachOutArmDampingRatio);
                    else
                        Ragdoll.SetSpringScale(ReachArmKeys[i] + suf, 1f);   // 还回原样（无阻尼那一档）
                }
            }
        }

        // ---- Search ------------------------------------------------------

        void TickSearch(HandGripper wanted)
        {
            // 两只手都收回来（权重渐变回 0）。
            ClearOtherHand(null);
            // ★ 按住键就加硬（松手还原）—— 见 ApplyReachStiffness 的注释。
            ApplyReachStiffness(wanted != null && Recipe.ReachOutOnGrip);

            if (wanted == null)
                return;


            GameObject go;
            Vector3 point, normal;
            if (!FindTarget(wanted, out go, out point, out normal))
            {
                // ★ 2026-10-02 owner：「点击左键伸出左手，松开左键左手回落」。
                //   这一条以前是**没有的** —— 状态机在 Search 里找不到可抓的东西就直接 return，
                //   于是「按住键但附近没东西」时手一动不动，看不出按键到底有没有生效。
                //   现在：按住键就把这只手伸到身前的**默认伸手位**（与有没有目标无关），
                //   松开键由上面的 `ClearOtherHand(null)` 收回去（IK 权重渐变回 0 = 回落）。
                if (Recipe.ReachOutOnGrip)
                    SetReachOutTarget(wanted);
                return;
            }
            ActiveHand = wanted;
            CurrentTarget = go;
            GraspPoint = point;
            SurfaceNormal = normal;
            HandGoal = wanted.HandPart != null && wanted.HandPart.Body != null
                ? wanted.HandPart.Body.position : point;
            HasGoal = true;
            _hasGrabOffset = false;   // 新目标：上一件东西的「手在物体局部的位置」作废
            // 伸手期间关掉「手 ↔ 这个目标」的碰撞 —— 手腕那颗球会顶在箱子上把手拧歪。
            SetReachCollisionIgnored(wanted, go, true);
            ToState(EEnvironmentInteractionState.Approach);
        }


        /// <summary>
        /// ★ 2026-10-02：**按住键就把这只手伸到身前的默认位**（不依赖附近有没有可抓的东西）。
        ///
        /// 位置 = 肩 + 前 `ReachOutForward` + 侧 ±`ReachOutLateral` + 上 `ReachOutUp`
        /// （都在**角色朝向**的坐标系里算，所以转身之后伸手也跟着转）。
        /// 掌心朝下（绑定姿势就是掌心朝下，所以看着自然）。
        ///
        /// 为什么要有这一条：以前「按住键」这件事**没有任何可见反馈** ——
        /// 附近没有可抓物体时状态机直接 return，手一动不动（owner 报的
        /// 「不能确定是不是真的抓住」有它一份）。现在按下去手一定伸出来，
        /// 松开（`ClearOtherHand(null)` 清目标）一定收回去。
        /// </summary>
        void SetReachOutTarget(HandGripper hand)
        {
            if (hand == null || ArmIK == null)
                return;
            ArmIKChain c = ArmIK.Get(hand.Suffix);
            if (c == null || !c.Ready || Ragdoll == null)
                return;

            Vector3 fwd = Ragdoll.Root != null ? Ragdoll.Root.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            float side = hand.Suffix == "l" ? -1f : 1f;

            Vector3 target = c.ShoulderWorld
                           + fwd * Recipe.ReachOutForward
                           + right * (side * Recipe.ReachOutLateral)
                           + Vector3.up * Recipe.ReachOutUp;
            ArmIK.SetTarget(hand.Suffix, target, true, Vector3.up, 1f);
        }

        // ---- Approach / Rise ---------------------------------------------

        void TickApproach(HandGripper wanted, float dt, bool rise)
        {
            if (!GuardActive(wanted))
                return;

            // 抓取点每帧重算 —— 物体可能正在被推走 / 被拖着走。
            Vector3 point, normal;
            if (ComputeGraspPoint(ActiveHand, CurrentTarget, out point, out normal))
            {
                GraspPoint = point;
                SurfaceNormal = normal;
            }

            if (!HasGoal)
            {
                HandGoal = HandOf(ActiveHand);
                HasGoal = true;
            }

            HandGoal = Vector3.MoveTowards(HandGoal, GraspPoint,
                Mathf.Max(0.05f, Recipe.InteractionHandApproachSpeed) * dt);

            // 掌心朝向**从 Approach 就开**，不再等到 Rise 才开。
            // 实测：原来只在 Rise 开，而 Rise 只持续 ~0.1s（到位判据用的是 IK 夹紧后的手误差，
            // Approach 里就已经很小了），根本来不及转 —— 掌心残差 72°。
            // 从 Approach 起给整段伸手时间去对准，残差才降得下来
            // （无接触时 IK 本身只有 0.0~0.6°，见交接文档 §五）。
            ArmIK.SetTarget(ActiveHand.Suffix, HandGoal, true, SurfaceNormal, 1f);

            // 到位判据用 **IK 解算后的手误差**，不是「手到抓取点的距离」：
            // 目标够不着时 IK 会把目标夹到可达球面上，那时手永远到不了抓取点，
            // 用原始距离就会永远卡在 Approach。
            float err = HandError(ActiveHand);
            if (!rise)
            {
                if (err <= Mathf.Max(0.005f, Recipe.InteractionArriveDistance))
                    ToState(EEnvironmentInteractionState.Rise);
            }
            else
            {
                if (err <= Mathf.Max(0.002f, Recipe.InteractionTouchDistance))
                    ToState(EEnvironmentInteractionState.Touch);
            }
        }

        // ---- Touch -------------------------------------------------------

        void TickTouch(HandGripper wanted, float dt)
        {
            if (!GuardActive(wanted))
                return;

            if (!ActiveHand.GripHeld)
            {
                ToState(EEnvironmentInteractionState.Reset);
                return;
            }

            // 建立了抓取之后：手锁在「抓住那一刻它在物体局部坐标里的位置」，跟着物体走。
            if (ActiveHand.HasGrip)
            {
                if (Recipe.InteractionTrackGrabbed && CurrentTarget != null)
                {
                    if (!_hasGrabOffset)
                    {
                        // 只在抓上的那一帧算一次，之后一直用它。
                        _grabLocalOffset = CurrentTarget.transform.InverseTransformPoint(HandOf(ActiveHand));
                        _grabLocalNormal = CurrentTarget.transform.InverseTransformDirection(SurfaceNormal);
                        _hasGrabOffset = true;
                    }
                    HandGoal = CurrentTarget.transform.TransformPoint(_grabLocalOffset);
                    SurfaceNormal = CurrentTarget.transform.TransformDirection(_grabLocalNormal).normalized;
                }
                ArmIK.SetTarget(ActiveHand.Suffix, HandGoal, true, SurfaceNormal, 1f);

                // 关节被拉得太长（东西被卡住/被拽走）-> 松手回 Reset。
                // 判据是「手到它该呆的那个点」的距离，也就是关节的相对滑移量。
                float d = Vector3.Distance(HandOf(ActiveHand), HandGoal);
                if (d > Mathf.Max(0.1f, Recipe.InteractionReleaseDistance))
                    ToState(EEnvironmentInteractionState.Reset);
                return;
            }

            // 还没抓住：手停在抓取点上，每帧试一次。
            HandGoal = GraspPoint;
            ArmIK.SetTarget(ActiveHand.Suffix, HandGoal, true, SurfaceNormal, 1f);

            ActiveHand.Poll();
            if (ActiveHand.CanGrip && ActiveHand.TryGrab())
            {
                // 抓上了：恢复手与目标的碰撞（抓取关节自己的 enableCollision = false 管这一段）。
                RestoreReachCollision();
                ArmIK.SetTarget(ActiveHand.Suffix, HandGoal, true, SurfaceNormal, 1f);
            }
        }

        // ---- Reset -------------------------------------------------------

        void TickReset(float dt)
        {
            ApplyReachStiffness(false);
            if (ActiveHand != null)
            {
                if (ActiveHand.HasGrip)
                    ActiveHand.Release("松手");
                ArmIK.ClearTarget(ActiveHand.Suffix);
                if (ArmIK.WeightOf(ActiveHand.Suffix) <= Mathf.Max(0f, Recipe.InteractionResetWeight))
                {
                    ActiveHand = null;
                    CurrentTarget = null;
                    HasGoal = false;
                    _hasGrabOffset = false;
                    RestoreReachCollision();
                    ToState(EEnvironmentInteractionState.Search);
                }
            }
            else
            {
                ToState(EEnvironmentInteractionState.Search);
            }
        }

        // ------------------------------------------------------------------

        bool GuardActive(HandGripper wanted)
        {
            if (ActiveHand == null || CurrentTarget == null)
            {
                ToState(EEnvironmentInteractionState.Reset);
                return false;
            }
            // 换手 = 先松开再重来。
            if (wanted != null && wanted != ActiveHand)
            {
                ToState(EEnvironmentInteractionState.Reset);
                return false;
            }
            if (!ActiveHand.GripHeld && !ActiveHand.HasGrip)
            {
                ToState(EEnvironmentInteractionState.Reset);
                return false;
            }
            return true;
        }

        void ClearOtherHand(HandGripper keep)
        {
            if (Left != null && Left != keep && !Left.HasGrip)
                ArmIK.ClearTarget(Left.Suffix);
            if (Right != null && Right != keep && !Right.HasGrip)
                ArmIK.ClearTarget(Right.Suffix);
        }

        static Vector3 HandOf(HandGripper hand)
        {
            return hand != null && hand.HandPart != null && hand.HandPart.Body != null
                ? hand.HandPart.Body.position : Vector3.zero;
        }

        float HandError(HandGripper hand)
        {
            ArmIKChain c = ArmIK.Get(hand.Suffix);
            if (c != null && c.HasTarget)
                return c.HandError;
            return Vector3.Distance(HandOf(hand), GraspPoint);
        }

        void ToState(EEnvironmentInteractionState next)
        {
            if (State == next)
                return;
            LastTransition = StateNames[(int)State] + " → " + StateNames[(int)next]
                + "（" + StateTime.ToString("F2") + "s：" + DescribeTarget() + "）";
            State = next;
            StateTime = 0f;
            TransitionCount++;
        }

        string DescribeTarget()
        {
            return CurrentTarget != null ? CurrentTarget.name : "无目标";
        }

        // ------------------------------------------------------------------
        // 找目标 / 算抓取点
        // ------------------------------------------------------------------

        /// <summary>刷新候选列表（每 0.5s 一次，不用每帧扫全场）。</summary>
        void RefreshCandidates()
        {
            _candidates.Clear();

            Grippable[] marked = Object.FindObjectsByType<Grippable>(FindObjectsSortMode.None);
            for (int i = 0; i < marked.Length; i++)
            {
                if (marked[i] == null)
                    continue;
                _candidates.Add(marked[i].gameObject);
            }

            // 兼容口径：教程 P7 的 tag。已经挂过 Grippable 的不重复加。
            if (!string.IsNullOrEmpty(Recipe.GrabbableTag))
            {
                GameObject[] tagged;
                try
                {
                    tagged = GameObject.FindGameObjectsWithTag(Recipe.GrabbableTag);
                }
                catch (UnityException)
                {
                    tagged = new GameObject[0];
                }
                for (int i = 0; i < tagged.Length; i++)
                    if (tagged[i] != null && !_candidates.Contains(tagged[i]))
                        _candidates.Add(tagged[i]);
            }
        }

        bool FindTarget(HandGripper hand, out GameObject go, out Vector3 point, out Vector3 normal)
        {
            go = null;
            point = Vector3.zero;
            normal = Vector3.up;

            ArmIKChain c = ArmIK.Get(hand.Suffix);
            if (c == null || !c.Ready)
                return false;

            // ① 手边最近的（HandGripper.Poll 的查询球已经做过「可抓 + 不是自己」的过滤）。
            hand.Poll();
            if (hand.CandidateObject != null
                && ComputeGraspPoint(hand, hand.CandidateObject, out point, out normal)
                && Reachable(c, point))
            {
                go = hand.CandidateObject;
                return true;
            }

            // ② 扫环境：半径内、正前方、够得着、最近的。
            _refreshTimer -= Time.fixedDeltaTime;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = 0.5f;
                RefreshCandidates();
            }

            Vector3 shoulder = c.ShoulderWorld;
            Vector3 fwd = Ragdoll != null && Ragdoll.Root != null ? Ragdoll.Root.forward : transform.forward;
            float radius = Mathf.Max(0.1f, Recipe.InteractionSearchRadius);
            float halfAngle = Mathf.Clamp(Recipe.InteractionSearchAngle, 10f, 360f) * 0.5f;

            float best = float.MaxValue;
            for (int i = 0; i < _candidates.Count; i++)
            {
                GameObject cand = _candidates[i];
                if (cand == null)
                    continue;
                if (Ragdoll != null && cand.transform.IsChildOf(Ragdoll.transform))
                    continue;
                if ((Left != null && Left.GrabbedObject == cand) || (Right != null && Right.GrabbedObject == cand))
                    continue;

                Vector3 cp = cand.transform.position;
                Vector3 to = cp - shoulder;
                float dist = to.magnitude;
                if (dist > radius || dist < 1e-4f)
                    continue;

                Vector3 flatTo = Vector3.ProjectOnPlane(to, Vector3.up);
                Vector3 flatFwd = Vector3.ProjectOnPlane(fwd, Vector3.up);
                if (flatTo.sqrMagnitude > 1e-6f && flatFwd.sqrMagnitude > 1e-6f
                    && Vector3.Angle(flatFwd, flatTo) > halfAngle)
                    continue;

                Vector3 p2, n2;
                if (!ComputeGraspPoint(hand, cand, out p2, out n2))
                    continue;
                if (!Reachable(c, p2))
                    continue;

                if (dist < best)
                {
                    best = dist;
                    go = cand;
                    point = p2;
                    normal = n2;
                }
            }

            return go != null;
        }

        /// <summary>够不着就别选它（否则会卡在 Approach）。留 2% 余量避开伸直奇点。</summary>
        static bool Reachable(ArmIKChain c, Vector3 graspPoint)
        {
            return Vector3.Distance(c.ShoulderWorld, graspPoint) <= c.MaxReach * 0.98f;
        }

        /// <summary>
        /// 抓取点 = 物体表面离肩最近的点 + 沿外法线退开一个手心距离。
        /// 「退开」是必须的：手骨上有个半径 6.5cm 的球碰撞体，直接怼在表面会被 PhysX 挤开。
        /// </summary>
        public bool ComputeGraspPoint(HandGripper hand, GameObject target, out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            if (hand == null || target == null || ArmIK == null)
                return false;

            ArmIKChain c = ArmIK.Get(hand.Suffix);
            if (c == null || !c.Ready)
                return false;

            Vector3 shoulder = c.ShoulderWorld;
            Collider col = target.GetComponentInChildren<Collider>();
            Vector3 surface;
            if (col != null)
                surface = col.ClosestPoint(shoulder);
            else
                surface = target.transform.position;

            Vector3 outward = shoulder - surface;
            if (outward.sqrMagnitude < 1e-8f)
                outward = Vector3.up;
            outward.Normalize();

            float handRadius = c.Hand != null && c.Hand.Spec != null ? c.Hand.Spec.Radius : 0.06f;
            float standoff = Mathf.Max(0f, Recipe.InteractionGraspStandoff) * handRadius;

            point = surface + outward * standoff;
            normal = outward;
            return true;
        }

        /// <summary>
        /// 伸手期间把「手骨碰撞体 ↔ 目标的所有碰撞体」的碰撞关掉（松手/换目标时恢复）。
        /// 用 Physics.IgnoreCollision，不改图层、不动碰撞矩阵 —— 只影响这一对。
        ///
        /// 为什么需要：手骨的碰撞体是**手腕处**一个半径 6.5cm 的球，手掌平面还在它前面 6cm。
        /// 伸手时这个球会顶在箱子上，接触力直接把手拧歪 —— 实测掌心残差 65~77°、
        /// 箱体也会被顶得滑走。关掉之后同一个姿势的掌心残差回到「无接触」的水平
        /// （无接触时实测 0.0~0.6°）。
        /// </summary>
        void SetReachCollisionIgnored(HandGripper hand, GameObject target, bool ignore)
        {
            if (!ignore)
            {
                RestoreReachCollision();
                return;
            }
            if (hand == null || hand.HandPart == null || target == null)
                return;
            Collider handCol = hand.HandPart.Shape;
            if (handCol == null)
                return;
            if (_ignoreTarget == target && _handColIgnore == handCol)
                return;

            RestoreReachCollision();

            Collider[] cols = target.GetComponentsInChildren<Collider>();
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == null || cols[i].isTrigger)
                    continue;
                Physics.IgnoreCollision(handCol, cols[i], true);
            }
            _handColIgnore = handCol;
            _targetColIgnore = cols;
            _ignoreTarget = target;
        }

        void RestoreReachCollision()
        {
            if (_handColIgnore != null && _targetColIgnore != null)
            {
                for (int i = 0; i < _targetColIgnore.Length; i++)
                    if (_targetColIgnore[i] != null)
                        Physics.IgnoreCollision(_handColIgnore, _targetColIgnore[i], false);
            }
            _handColIgnore = null;
            _targetColIgnore = null;
            _ignoreTarget = null;
        }

        // ------------------------------------------------------------------

        public string Report()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("== 环境交互状态机 ==");
            sb.AppendLine("状态 " + StateName + "（已停 " + StateTime.ToString("F2") + "s，迁移 "
                + TransitionCount + " 次）");
            sb.AppendLine("  " + LastTransition);
            sb.AppendLine("活动手 " + (ActiveHand != null ? ActiveHand.Suffix : "无")
                + "   目标 " + DescribeTarget()
                + "   抓取点 " + GraspPoint.ToString("F3")
                + "   表面法线 " + SurfaceNormal.ToString("F3"));
            sb.AppendLine("手目标 " + HandGoal.ToString("F3")
                + "   IK 权重 左 " + (ArmIK != null ? ArmIK.WeightOf("l").ToString("F3") : "-")
                + " / 右 " + (ArmIK != null ? ArmIK.WeightOf("r").ToString("F3") : "-"));
            if (Left != null)
                sb.AppendLine("  " + Left.Report());
            if (Right != null)
                sb.AppendLine("  " + Right.Report());
            return sb.ToString();
        }
    }
}
