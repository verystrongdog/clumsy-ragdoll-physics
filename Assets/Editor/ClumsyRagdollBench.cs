using System;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ClumsyRagdoll.EditorTools
{
    /// <summary>
    /// 确定性诊断台（仅编辑器）。
    ///
    /// 为什么需要它：编辑器里没有 Update/FixedUpdate，MCP 调用期间编辑器又被占住，
    /// 「进 Play 等一会儿看结果」在这个链路里做不了。所以这里用反射手动调三个组件的
    /// FixedUpdate，再用 <c>Physics.Simulate(dt)</c> 手动步进 —— 完全确定、无需 Play、无需读 Console。
    /// 做法沿用本工程上一轮验证过的 RagdollBench。
    ///
    /// 铁律（上一轮踩过的）：
    ///   ① 相与相之间**重建世界**，不要用 Respawn（上一相倒地的残留姿态会把下一步抽飞）。
    ///   ② 单次跑没有意义，要看系综。
    ///   ③ 结束时复位 Physics.simulationMode 与 Physics.gravity。
    /// </summary>
    public static class ClumsyRagdollBench
    {
        public const float Dt = 0.02f;

        public sealed class Sim
        {
            public GameObject World;
            public GameObject Model;
            public ClumsyRagdoll Rag;
            public ClumsyController Ctrl;
            public ClumsyPoseDriver Pose;
            public ClumsyArmIK ArmIK;
            public HandGripper LeftGrip;
            public HandGripper RightGrip;
            public ClumsyInteraction Interact;
            /// <summary>★ 2026-10-02 双手搬运（HFF 的抱东西）。</summary>
            public ClumsyCarry Carry;
            public ClumsyAnimationSource Anim;
            public ClumsyWobble Wob;
            public ClumsyBalance Balance;
            public float RestHipY;
            public string Name;
        }

        struct Trace
        {
            public float Best;        // 最长连续站立
            public float FirstFall;   // 第一次由站转倒的时刻（-1 = 没倒）
            public float FinalUp;
            public float FinalY;
            public float HipAt1s;
            public float MinUp;
            public float MaxY;
            public float Moved;       // 水平位移
        }

        static MethodInfo _ragStep, _ctrlStep, _poseStep, _interactStep, _balanceStep;
        /// <summary>★ 2026-10-02 双手搬运层。</summary>
        static MethodInfo _carryStep;

        /// <summary>★ 2026-10-02：隔离实验用 —— 反射调姿势驱动自己的 Pose()，基准姿势/slew 都与真步态一致。</summary>
        static MethodInfo _poseSet;

        /// <summary>诊断台用的转身角速度（度/秒）。控制器不写 Root（那是相机脚本的活），所以这里直接转。</summary>
        static float _turnRate;

        /// <summary>诊断台用的假输入：能脚本化地「按住 / 松开」跳跃键，走的是和真键盘完全一样的路径。
        /// （直接写 Ctrl.JumpPressed 会绕开控制器里的边沿锁存，测不出真行为。）</summary>
        sealed class BenchInput : IClumsyInput
        {
            public Vector2 MoveValue;
            public bool SprintValue;
            public bool JumpHeldValue;
            public Vector2 Move { get { return MoveValue; } }
            public bool Sprint { get { return SprintValue; } }
            public bool JumpHeld { get { return JumpHeldValue; } }
        }

        /// <summary>求解器迭代次数（Locked 约束的「硬度」直接由它决定；上一轮的 RagdollBench 里 32/12 是有效杠杆）。</summary>
        
        

        static void EnsureReflection()
        {
            if (_ragStep != null)
                return;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            _ragStep = typeof(ClumsyRagdoll).GetMethod("FixedUpdate", F);
            _ctrlStep = typeof(ClumsyController).GetMethod("FixedUpdate", F);
            _poseStep = typeof(ClumsyPoseDriver).GetMethod("FixedUpdate", F);
            _interactStep = typeof(ClumsyInteraction).GetMethod("FixedUpdate", F);
            _balanceStep = typeof(ClumsyBalance).GetMethod("FixedUpdate", F);
            _carryStep = typeof(ClumsyCarry).GetMethod("FixedUpdate", F);
        }

        // ------------------------------------------------------------------

        static Sim BuildSim(RagdollRecipe recipe, bool withController, bool withPose)
        {
            EnsureReflection();

            Physics.simulationMode = SimulationMode.Script;
            Physics.gravity = new Vector3(0f, recipe.GravityY, 0f);
            Time.fixedDeltaTime = recipe.FixedDeltaTime;
            Physics.defaultContactOffset = 0.01f;
            Physics.bounceThreshold = 2f;
            Physics.defaultSolverIterations = recipe.SolverIterations;
            Physics.defaultSolverVelocityIterations = recipe.SolverVelocityIterations;

            Sim sim = new Sim();
            sim.World = new GameObject("诊断台·世界");

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "地面";
            ground.transform.SetParent(sim.World.transform, false);
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(60f, 1f, 60f);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RaccoonSkeleton.ModelPath);
            if (prefab == null)
            {
                UnityEngine.Object.DestroyImmediate(sim.World);
                throw new InvalidOperationException("找不到模型 " + RaccoonSkeleton.ModelPath);
            }

            sim.Model = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
            sim.Model.name = "诊断台·浣熊";
            sim.Model.transform.SetParent(sim.World.transform, true);

            sim.Rag = ClumsyRagdoll.Build(sim.Model, recipe);
            sim.Rag.SnapAboveGround(0f, 0.004f);
            sim.RestHipY = sim.Rag.Hips.RestPosition.y;
            // 真游戏里 ClumsyRagdollGame 给每个部件都挂了 LimbCollision —— 诊断台也要照做，
            // 否则 Ragdoll.IsGrounded 永远是 false（虽然编辑模式本来也不派发碰撞消息，
            // 但 Ragdoll.IsGroundedNow 那条兜底路径要看到脚上的碰撞体）。
            for (int pi = 0; pi < sim.Rag.Parts.Count; pi++)
            {
                LimbCollision lc = sim.Rag.Parts[pi].Bone.gameObject.AddComponent<LimbCollision>();
                lc.Ragdoll = sim.Rag;
            }
            // 教程 §三 的「人工力」层（方法2 弹簧 / 方法3 力矩 + 二次角阻尼）。
            // 建在这里 = **所有**诊断都跑在同一套撑直配置下（Recipe.Balance 控制），
            // 于是 StandReport / WalkReport 这些老读数可以直接与新配置对比。
            sim.Balance = sim.Model.AddComponent<ClumsyBalance>();
            sim.Balance.Setup(sim.Rag, recipe);

            sim.Name = "trial";

            if (withController)
            {
                sim.Ctrl = sim.Model.AddComponent<ClumsyController>();
                sim.Ctrl.Ragdoll = sim.Rag;
                sim.Ctrl.Recipe = recipe;
                sim.Ctrl.Facing = sim.Rag.Root;
                sim.Ctrl.Source = null;
            }
            if (withPose)
            {
                sim.Pose = sim.Model.AddComponent<ClumsyPoseDriver>();
                sim.Pose.Ragdoll = sim.Rag;
                sim.Pose.Controller = sim.Ctrl;
                sim.Pose.Recipe = recipe;
            }
            Physics.SyncTransforms();
            return sim;
        }

        public static void TearDown(Sim sim)
        {
            if (sim == null || sim.World == null)
                return;
            // 兜底：Root 如果不在 sim.World 底下（老版本是场景根物体），单独删掉。
            if (sim.Rag != null && sim.Rag.Root != null
                && !sim.Rag.Root.IsChildOf(sim.World.transform))
                UnityEngine.Object.DestroyImmediate(sim.Rag.Root.gameObject);
            UnityEngine.Object.DestroyImmediate(sim.World);
            sim.World = null;
            sim.Model = null;
        }

        static Trace Run(Sim sim, float seconds, float pushAt, Vector3 pushImpulse, Vector2 move, bool sprint)
        {
            int steps = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));
            Trace tr = new Trace();
            tr.FirstFall = -1f;
            tr.MinUp = 1f;
            tr.HipAt1s = -1f;

            float run = 0f;
            float best = 0f;
            bool pushed = false;
            bool fallen = false;
            Vector3 start = sim.Rag.PelvisPosition;

            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt;
                if (_turnRate != 0f && sim.Rag != null && sim.Rag.Root != null)
                {
                    sim.Rag.Root.rotation = Quaternion.Euler(0f, sim.Rag.Root.eulerAngles.y + _turnRate * Dt, 0f);
                }

                if (sim.Ctrl != null)
                {
                    sim.Ctrl.Move = move;
                    sim.Ctrl.Sprint = sprint;
                }
                if (sim.Rag != null)
                    _ragStep.Invoke(sim.Rag, null);
                if (sim.Ctrl != null)
                    _ctrlStep.Invoke(sim.Ctrl, null);
                if (sim.Carry != null)
                    _carryStep.Invoke(sim.Carry, null);
                if (sim.Interact != null)
                    _interactStep.Invoke(sim.Interact, null);
                if (sim.Balance != null)
                    _balanceStep.Invoke(sim.Balance, null);
                if (sim.Pose != null)
                    _poseStep.Invoke(sim.Pose, null);

                if (pushAt > 0f && !pushed && t >= pushAt)
                {
                    sim.Rag.Push(pushImpulse);
                    pushed = true;
                }

                Physics.Simulate(Dt);

                float up = sim.Rag.Upright;
                float y = sim.Rag.PelvisPosition.y;
                bool standing = up >= 0.85f && y >= sim.RestHipY * 0.85f;

                if (standing)
                {
                    run += Dt;
                    if (run > best)
                        best = run;
                    fallen = false;
                }
                else
                {
                    if (!fallen && best > 0.4f && tr.FirstFall < 0f)
                        tr.FirstFall = t;
                    fallen = true;
                    run = 0f;
                }

                if (up < tr.MinUp) tr.MinUp = up;
                if (y > tr.MaxY) tr.MaxY = y;
                if (tr.HipAt1s < 0f && t >= 1f) tr.HipAt1s = y;
            }

            tr.Best = best;
            tr.FinalUp = sim.Rag.Upright;
            tr.FinalY = sim.Rag.PelvisPosition.y;
            Vector3 d = sim.Rag.PelvisPosition - start;
            d.y = 0f;
            tr.Moved = d.magnitude;
            if (tr.HipAt1s < 0f) tr.HipAt1s = tr.FinalY;
            return tr;
        }

        static string Row(string label, Trace t, Sim sim)
        {
            return string.Format("{0,-26} 站中位{1,6:F2}s 首倒{2,6:F2}s  末up{3,6:F3} 末y{4,6:F3} 1s时y{5,6:F3} minUp{6,6:F2} 位移{7,5:F2}m",
                label, t.Best, t.FirstFall, t.FinalUp, t.FinalY, t.HipAt1s, t.MinUp, t.Moved);
        }

        // ==================================================================
        // 入口（MCP 用反射调用这些 public static string）
        // ==================================================================

        [MenuItem("笨拙布娃娃/诊断/A 静止站立", false, 20)]
        public static void MenuStand()
        {
            Debug.Log(StandReport());
        }

        [MenuItem("笨拙布娃娃/诊断/B 弹簧扫描", false, 21)]
        public static void MenuSweep()
        {
            Debug.Log(SpringSweep());
        }

        [MenuItem("笨拙布娃娃/诊断/C 推倒回弹", false, 22)]
        public static void MenuPush()
        {
            Debug.Log(PushReport());
        }

        [MenuItem("笨拙布娃娃/诊断/D 走起来", false, 23)]
        public static void MenuWalk()
        {
            Debug.Log(WalkReport());
        }

        [MenuItem("笨拙布娃娃/诊断/E 全量", false, 24)]
        public static void MenuAll()
        {
            Debug.Log(FullReport());
        }

public static string JumpReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 跳跃：只按一下 vs 按住不放（各 4s；走真键盘那条输入路径）==");
            try
            {
                bool[] held = { false, true };
                for (int i = 0; i < held.Length; i++)
                {
                    RagdollRecipe rr = new RagdollRecipe();
                    Sim sim = BuildSim(rr, true, true);
                    BenchInput input = new BenchInput();
                    sim.Ctrl.Source = input;
                    Run(sim, 1f, 0f, Vector3.zero, Vector2.zero, false);
                    float restY = sim.Rag.PelvisPosition.y;
                    float maxY = restY;
                    int steps = Mathf.RoundToInt(4f / Dt);
                    for (int k = 0; k < steps; k++)
                    {
                        input.MoveValue = Vector2.zero;
                        sim.Rag.IsGrounded = true;              // 模拟脚还踩着地面（CollisionStay 每帧会这么干）
                        input.JumpHeldValue = (k == 0) || held[i];
                        _ragStep.Invoke(sim.Rag, null);
                        _ctrlStep.Invoke(sim.Ctrl, null);
                        _poseStep.Invoke(sim.Pose, null);
                        Physics.Simulate(Dt);
                        float y = sim.Rag.PelvisPosition.y;
                        if (y > maxY) maxY = y;
                    }
                    sb.AppendLine(string.Format("{0}：起跳前骨盆 y {1:F3} → 最高 {2:F3}，跳起 {3:F3} m",
                        held[i] ? "按住不放  " : "只按一下  ", restY, maxY, maxY - restY));
                    if (sim.Ctrl != null)
                    sb.AppendLine("   种植脚这一趟最大偏离锚点 = " + (sim.Ctrl.PlantSlipMax * 100f).ToString("F1") + " cm");
                TearDown(sim);
                }
                RagdollRecipe rr2 = new RagdollRecipe();
                sb.AppendLine("（JumpSpeed = " + rr2.JumpSpeed + " m/s；单发理论跳起高度 = v²/2g ≈ "
                    + (rr2.JumpSpeed * rr2.JumpSpeed / (2f * 9.81f)).ToString("F3") + " m）");
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>
        /// ★ 2026-10-01：**Play 忠实版**跳跃诊断。
        ///
        /// 为什么另开一个：<see cref="JumpReport"/> 每一步都硬写 <c>sim.Rag.IsGrounded = true</c>
        /// （模拟 LimbCollision.OnCollisionStay），而真 Play 里那个标志是**碰撞消息**写的 ——
        /// 起跳后骨盆飞了、脚（面条）可能还挂在地上 ⇒ 标志被刷回 true ⇒ <c>IsGroundedNow</c> 为真
        /// ⇒ 宽限步数一过，托举层重新接管，把刚扔出去的身体**拽回目标高度**。
        /// 本方法把「碰撞消息」按地面接触复现出来（任何部件的碰撞体碰到地面就算落地），
        /// 逐步打表，看跳跃到底死在哪一步、死在哪一条。
        /// </summary>
        public static string JumpPlayReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 跳跃（Play 忠实版）：IsGrounded 由「真有部件压在地面上」写，不硬写 ==");
            try
            {
                RagdollRecipe rr = new RagdollRecipe();
                Sim sim = BuildSim(rr, true, true);
                BenchInput input = new BenchInput();
                sim.Ctrl.Source = input;
                Run(sim, 1f, 0f, Vector3.zero, Vector2.zero, false);
                float restY = sim.Rag.PelvisPosition.y;
                sb.AppendLine(string.Format("起跳前：骨盆 {0:F3}  GroundY {1:F3}  脚最低 {2:F3}  腹部高度 {3:F3}",
                    restY, sim.Rag.GroundY, sim.Rag.LowestSupportY, sim.Rag.CenterOfMassHeight));
                float maxY = restY;
                int jumpStep = -1;
                int airSteps = 0;   // ★ 滞空步数（起跳后到再次接地）
                float peakImpact = 0f;   // ★ 落地踉跄峰值
                float airFootGap = 0f;   // ★ 空中「脚到髖」最大距离
                int steps = Mathf.RoundToInt(2.5f / Dt);
                for (int k = 0; k < steps; k++)
                {
                    input.MoveValue = Vector2.zero;
                    input.JumpHeldValue = (k == 1);
                    // ---- Play 的碰撞消息：进物理步之前，先按「贴地」把标志刷一遍 ----
                    sim.Rag.IsGrounded = AnyTouchingGround(sim, 0.002f);
                    _ragStep.Invoke(sim.Rag, null);
                    _ctrlStep.Invoke(sim.Ctrl, null);
                    _poseStep.Invoke(sim.Pose, null);
                    Physics.Simulate(Dt);
                    // ---- 出物理步之后再刷一遍（碰撞已经发生）----
                    if (AnyTouchingGround(sim, 0.002f))
                        sim.Rag.IsGrounded = true;
                    if (sim.Ctrl.LastJumpTime > 0f && jumpStep < 0)
                        jumpStep = k;
                    float y = sim.Rag.PelvisPosition.y;
                    if (y > maxY) maxY = y;
                    if (jumpStep >= 0 && !sim.Rag.IsGroundedNow) airSteps++;
                    if (sim.Wob != null && sim.Wob.ImpactAmount > peakImpact) peakImpact = sim.Wob.ImpactAmount;
                    if (jumpStep >= 0 && !sim.Rag.IsGroundedNow && k > 5)
                    {
                        var fpart = sim.Rag.Find("footl");
                        if (fpart != null && fpart.Body != null)
                        {
                            float gp = sim.Rag.Hips.Body.position.y - fpart.Body.position.y;
                            if (gp > airFootGap) airFootGap = gp;
                        }
                    }
                    if (jumpStep >= 0 && k % 6 == 0)
                    {
                        var hb = sim.Rag.Hips.Body;
                        sb.AppendLine(string.Format("      t={0:F2}s 骨盆y {1:F3} upright {2:F2} 髖轴离竖直 {3:F0}° 角速度 {4:F1}",
                            k * Dt, hb.position.y, sim.Rag.Upright,
                            Vector3.Angle(hb.rotation * Vector3.up, Vector3.up), hb.angularVelocity.magnitude));
                    }
                    if (k <= 30 || (k % 10) == 0)
                        sb.AppendLine(string.Format(
                            "  step {0,3} t={1:F2}s 骨盆 {2:F3} vy {3,6:F2} 脚最低 {4,7:F3} grounded {5,-5} now {6,-5} carry {7,-5} {8}",
                            k, k * Dt, y, sim.Rag.Hips.Body.linearVelocity.y, sim.Rag.LowestSupportY,
                            sim.Rag.IsGrounded, sim.Rag.IsGroundedNow, sim.Ctrl.CarryActiveNow,
                            k == jumpStep ? "← 起跳" : ""));
                }
                sb.AppendLine(string.Format("滞空 {0:F2} s（{1} 个物理步）", airSteps * Dt, airSteps));
                sb.AppendLine(string.Format("落地踉跄峰值 {0:F2}（自己跳的应当很小）· 空中脚到髖最大 {1:F1} cm", peakImpact, airFootGap * 100f));
                sb.AppendLine(string.Format("起跳发生在 step {0}；最高 {1:F3} ⇒ 跳起 {2:F3} m（单发理论 {3:F3} m）",
                    jumpStep, maxY, maxY - restY, rr.JumpSpeed * rr.JumpSpeed / (2f * 9.81f)));
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>有没有部件的碰撞体底画压到了地面（地面顶面 y=0）—— 代替 Play 里的碰撞消息。</summary>
        static bool AnyTouchingGround(Sim sim, float eps)
        {
            for (int i = 0; i < sim.Rag.Parts.Count; i++)
            {
                ClumsyPart p = sim.Rag.Parts[i];
                if (p.Shape == null)
                    continue;
                if (p.Shape.bounds.min.y <= eps)
                    return true;
            }
            return false;
        }


                static readonly string[] TorsoKeys = { "hips", "spine", "neck" };
        static readonly string[] ArmBones = { "mixamorig:LeftShoulder", "mixamorig:LeftArm", "mixamorig:LeftForeArm", "mixamorig:LeftHand",
                                             "mixamorig:RightShoulder", "mixamorig:RightArm", "mixamorig:RightForeArm", "mixamorig:RightHand" };
        static readonly string[] ArmKeys = { "shoulderl", "arml", "forearml", "handl",
                                             "shoulderr", "armr", "forearmr", "handr" };

        /// <summary>
        /// ★ 2026-10-01：**胳膊穿模到底穿在哪里**。
        ///
        /// 两个读数一起看：
        ///   ① **碰撞体**：手臂链 × 躯干（hips/spine/neck）的穿透深度（<c>Physics.ComputePenetration</c>）；
        ///   ② **网格**：手臂各子网格的顶点里有多少落进了**别的部件**的碰撞体内部
        ///      （蒙皮后的世界坐标 × <c>Collider.ClosestPoint</c> 的自反性）—— 那才是眼睛看到的「穿模」。
        ///      手指骨（HandIndex*）归进「手」那一档：这只浣熊的手掌/手指占了手臂网格的大半。
        /// </summary>
        public static string ArmClipReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 胳膊穿模：① 手臂 × 躯干 碰撞体穿透  ② 手臂网格顶点落进别的部件碰撞体 ==");
            try
            {
                RagdollRecipe rr = new RagdollRecipe();
                Sim sim = BuildSim(rr, true, true);
                sb.AppendLine(SampleClip(sim, "站立 1s（无输入）", 1f, Vector2.zero));
                sb.AppendLine(SampleClip(sim, "走路 2s", 2f, new Vector2(0f, 1f)));
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        static string SampleClip(Sim sim, string label, float seconds, Vector2 move)
        {
            int steps = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));
            for (int k = 0; k < steps; k++)
            {
                if (sim.Ctrl != null) { sim.Ctrl.Move = move; sim.Ctrl.Sprint = false; }
                _ragStep.Invoke(sim.Rag, null);
                _ctrlStep.Invoke(sim.Ctrl, null);
                _poseStep.Invoke(sim.Pose, null);
                Physics.Simulate(Dt);
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("── " + label + "（骨盆 " + sim.Rag.PelvisPosition.y.ToString("F3") + "）──");

            for (int a = 0; a < ArmKeys.Length; a++)
            {
                ClumsyPart ap = sim.Rag.Find(ArmKeys[a]);
                if (ap == null || ap.Shape == null)
                    continue;
                float worst = 0f;
                string who = "-";
                for (int t = 0; t < TorsoKeys.Length; t++)
                {
                    ClumsyPart tp = sim.Rag.Find(TorsoKeys[t]);
                    if (tp == null || tp.Shape == null)
                        continue;
                    Vector3 dir;
                    float dist;
                    if (Physics.ComputePenetration(ap.Shape, ap.Shape.transform.position, ap.Shape.transform.rotation,
                                                   tp.Shape, tp.Shape.transform.position, tp.Shape.transform.rotation,
                                                   out dir, out dist) && dist > worst)
                    {
                        worst = dist;
                        who = tp.Spec.Key;
                    }
                }
                sb.AppendLine(string.Format("   碰撞体 {0,-11} 躯干最深穿透 {1,6:F1} mm  （{2}）",
                    ap.Spec.Key, worst * 1000f, who));
            }

            SkinnedMeshRenderer smr = sim.Model.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr == null)
                return sb.ToString();
            Mesh mesh = smr.sharedMesh;
            Vector3[] verts = mesh.vertices;
            BoneWeight[] bw = mesh.boneWeights;
            Transform[] bones = smr.bones;
            Matrix4x4[] bind = mesh.bindposes;
            Matrix4x4[] skin = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                skin[i] = bones[i] != null ? bones[i].localToWorldMatrix * bind[i] : Matrix4x4.identity;

            int nOther = 0;
            for (int i = 0; i < sim.Rag.Parts.Count; i++)
            {
                bool isA = false;
                for (int a = 0; a < ArmKeys.Length; a++)
                    if (sim.Rag.Parts[i].Spec.Key == ArmKeys[a]) { isA = true; break; }
                if (!isA)
                    nOther++;
            }
            Collider[] other = new Collider[nOther];
            string[] otherKey = new string[nOther];
            int oi = 0;
            for (int i = 0; i < sim.Rag.Parts.Count; i++)
            {
                ClumsyPart q = sim.Rag.Parts[i];
                bool isA = false;
                for (int a = 0; a < ArmKeys.Length; a++)
                    if (q.Spec.Key == ArmKeys[a]) { isA = true; break; }
                if (isA)
                    continue;
                other[oi] = q.Shape;
                otherKey[oi] = q.Spec.Key;
                oi++;
            }

            int[] inside = new int[ArmKeys.Length];
            int[] total = new int[ArmKeys.Length];
            int[,] hit = new int[ArmKeys.Length, nOther];
            for (int vi = 0; vi < verts.Length; vi++)
            {
                BoneWeight w = bw[vi];
                float bw0 = w.weight0, bw1 = w.weight1, bw2 = w.weight2, bw3 = w.weight3;
                int bi0 = w.boneIndex0, bi1 = w.boneIndex1, bi2 = w.boneIndex2, bi3 = w.boneIndex3;
                int dom = bi0;
                float domW = bw0;
                if (bw1 > domW) { dom = bi1; domW = bw1; }
                if (bw2 > domW) { dom = bi2; domW = bw2; }
                if (bw3 > domW) { dom = bi3; domW = bw3; }
                if (dom < 0 || dom >= bones.Length || bones[dom] == null)
                    continue;
                string bn = bones[dom].name;
                int slot = -1;
                for (int a = 0; a < ArmBones.Length; a++)
                    if (bn == ArmBones[a]) { slot = a; break; }
                if (slot < 0)
                {
                    if (bn == "mixamorig:LeftHandIndex2" || bn == "mixamorig:LeftHandIndex4")
                        slot = 3;
                    else if (bn == "mixamorig:RightHandIndex1" || bn == "mixamorig:RightHandIndex4")
                        slot = 7;
                }
                if (slot < 0)
                    continue;
                total[slot]++;
                Vector3 p = Vector3.zero;
                if (bw0 > 0f) p += skin[bi0].MultiplyPoint3x4(verts[vi]) * bw0;
                if (bw1 > 0f) p += skin[bi1].MultiplyPoint3x4(verts[vi]) * bw1;
                if (bw2 > 0f) p += skin[bi2].MultiplyPoint3x4(verts[vi]) * bw2;
                if (bw3 > 0f) p += skin[bi3].MultiplyPoint3x4(verts[vi]) * bw3;
                for (int t = 0; t < other.Length; t++)
                {
                    if (other[t] == null)
                        continue;
                    Vector3 cp = other[t].ClosestPoint(p);
                    if ((cp - p).sqrMagnitude < 1e-10f)
                    {
                        hit[slot, t]++;
                        inside[slot]++;
                    }
                }
            }
            for (int a = 0; a < ArmKeys.Length; a++)
            {
                float pct = total[a] > 0 ? inside[a] * 100f / total[a] : 0f;
                sb.Append("   网格 " + ArmKeys[a].PadRight(11) + " 顶点 " + total[a].ToString().PadLeft(6)
                          + "，落进别的碰撞体 " + inside[a].ToString().PadLeft(6) + "（" + pct.ToString("F1") + "%）");
                string detail = "";
                for (int t = 0; t < other.Length; t++)
                    if (hit[a, t] > 0)
                        detail += " " + otherKey[t] + "=" + hit[a, t];
                sb.AppendLine(detail == "" ? "" : "   →" + detail);
            }
            return sb.ToString();
        }

        /// <summary>
        /// ★ 2026-10-01：**哪些部件对天生就重叠**（决定自碰撞开不开得起来）。
        ///
        /// owner 报「倒地/被推/被抓时胳膊折进身体里」—— 根因是布娃娃**自碰撞整个关掉了**
        /// （`Physics.IgnoreLayerCollision(No Self Collision, 同层, true)`，教程 P1 步8）。
        /// 直接把自碰撞全部打开会炸：关节附近本来就有大量**构造性重叠**（实测肩×脊柱 62–76 mm）。
        /// 所以先逐对量一遍，看看哪些对必须继续 IgnoreCollision。
        /// </summary>
        /// <summary>
        /// ★ 2026-10-01：**走路到底是不是«用脚走»**。
        ///
        /// owner 报：「前进更像是有人拽着浣熊模型而不是双脚移动」。这个报告就把那句话量化：
        ///   ① **接触占空比**：一只脚有几个物理步是沾着地的（人走路大约 60%；100% = 一直在地上拖）；
        ///   ② **种植期滑移**：沾地期间脚在水平方向跑了多少（cm/步）—— 真正«粘在地上»应当 ≈ 0；
        ///   ③ **摆动相**：脚到底抬起来了几次、最高离地多少 cm（抬不起来 = 拖）。
        /// 走路 6s，按住 W。
        /// </summary>
        public static string GaitSlipReport()
        {
            return GaitSlipReport(SceneRecipe());
        }

        public static string GaitSlipReport(RagdollRecipe rr)
        {
            return GaitSlipReport(rr, new Vector2(0f, 1f));
        }

        /// <summary>★ 2026-10-02：加了个方向参数 —— 后退（0,−1）走的是同一条路，只是符号不同。</summary>
        public static string GaitSlipReport(RagdollRecipe rr, Vector2 move)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 走路是不是用脚走（6s，输入 " + move + "）==");
            sb.AppendLine("脚摩擦=" + rr.FootDynamicFriction.ToString("F2") + "  脚种植步态=" + rr.UseFootGait
                + "  种植摩擦=" + rr.FootGripFriction.ToString("F2") + "  拉髖弹簧=" + rr.GaitPullStiffness.ToString("F1"));
            try
            {
                Sim sim = BuildArmedSim(rr, true, true);
                var fl = sim.Rag.Find("footl");
                var fr = sim.Rag.Find("footr");
                ClumsyPart[] feet = { fl, fr };
                float[] lastX = new float[2];
                float[] lastZ = new float[2];
                bool[] touch = new bool[2];
                int[] contactSteps = new int[2];
                int[] plantedSteps = new int[2];
                float[] slipSum = new float[2];
                int[] swings = new int[2];
                float[] maxLift = new float[2];
                Vector3 start = sim.Rag.PelvisPosition;
                float minPelvisY = start.y;
                float maxPelvisY = start.y;
                int steps = 300;
                float eps = 0.012f;
                for (int k = 0; k < steps; k++)
                {
                    sim.Ctrl.Move = move;
                    _ragStep.Invoke(sim.Rag, null);
                    _ctrlStep.Invoke(sim.Ctrl, null);
                    _poseStep.Invoke(sim.Pose, null);
                    Physics.Simulate(Dt);
                    float py = sim.Rag.PelvisPosition.y;
                    if (py < minPelvisY) minPelvisY = py;
                    if (py > maxPelvisY) maxPelvisY = py;
                    for (int i = 0; i < 2; i++)
                    {
                        ClumsyPart p = feet[i];
                        if (p == null || p.Shape == null)
                            continue;
                        Vector3 wp = p.Body.position;
                        float lift = p.Shape.bounds.min.y;
                        bool c = lift <= eps;
                        if (c)
                        {
                            contactSteps[i]++;
                            if (touch[i])
                            {
                                float slip = new Vector2(wp.x - lastX[i], wp.z - lastZ[i]).magnitude;
                                slipSum[i] += slip;
                                if (slip < 0.002f)
                                    plantedSteps[i]++;
                            }
                            touch[i] = true;
                        }
                        else
                        {
                            if (touch[i])
                                swings[i]++;                 // 刚离地 = 一次摆动相
                            touch[i] = false;
                            if (lift > maxLift[i])
                                maxLift[i] = lift;
                        }
                        lastX[i] = wp.x;
                        lastZ[i] = wp.z;
                    }
                }
                Vector3 moved = Vector3.ProjectOnPlane(sim.Rag.PelvisPosition - start, Vector3.up);
                float seconds = steps * Dt;
                // ★ 带符号：沿角色朝向的位移（负 = 真的在后退）
                Vector3 fwd = sim.Rag.Root != null ? sim.Rag.Root.forward : Vector3.forward;
                fwd.y = 0f;
                float signedFwd = Vector3.Dot(Vector3.ProjectOnPlane(sim.Rag.PelvisPosition - start, Vector3.up), fwd.normalized);
                sb.AppendLine(string.Format("沿朝向位移 {0:F2} m（负=后退）⇒ 平均速度 {1:F2} m/s；骨盆起伏 {2:F3}–{3:F3} m",
                    signedFwd, signedFwd / seconds, minPelvisY, maxPelvisY));
                string[] names = { "footl", "footr" };
                for (int i = 0; i < 2; i++)
                {
                    int contactMoves = contactSteps[i] > 0 ? contactSteps[i] : 1;
                    sb.AppendLine(string.Format(
                        "   {0}：沾地 {1}/{2}（{3:F0}%）  每步滑移 {4:F1} cm  其中«粘住»(<2mm/步) {5:F0}%  摆动相 {6} 次  最高离地 {7:F1} cm",
                        names[i], contactSteps[i], steps, contactSteps[i] * 100f / steps,
                        slipSum[i] / contactMoves * 100f,
                        contactSteps[i] > 0 ? plantedSteps[i] * 100f / contactMoves : 0f,
                        swings[i], maxLift[i] * 100f));
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>
        /// ★ 2026-10-02：**摆动脚到底刮不刮地面**（owner：「走路还是会脚底跟地面剐蹭」）。
        ///
        /// §十四 修完之后摆动脚仍有 3–5% 的帧穿地、最低 −1.6 cm。这个报告把**摆动期**
        /// 按进度 u 分成 10 档逐个打表（摆动脚自己的最低点），并且**同时打支撑脚** ——
        /// 用来分辨两种完全不同的毛病：
        ///   ① **姿势错**：摆动脚轨迹本身太低 ⇒ 改摆动腿的角度剖面；
        ///   ② **找地回路错**：`LowestSupportY` 取的是**两只脚**的最低点，摆动脚一沉，
        ///      「找地」就把**整个人往上顶**（`targetY -= (负值)×增益`）⇒ 支撑脚被抬离地面，
        ///      紧接着又砸下去 ⇒ 观感就是「脚底在地上刮」。
        ///      判据：摆动脚最低的那一帧，支撑脚是不是被顶到了正的高度上。
        /// </summary>
        public static string SwingScrapeReport()
        {
            return SwingScrapeReport(SceneRecipe());
        }

        public static string SwingScrapeReport(RagdollRecipe rr)
        {
            return SwingScrapeReport(rr, new Vector2(0f, 1f));
        }

        /// <summary>★ 2026-10-02：带方向（后退传 (0,−1)）。</summary>
        public static string SwingScrapeReport(RagdollRecipe rr, Vector2 move)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 摆动脚 vs 地面（6s，按住 W；离地量 = 碰撞体底面 − 地面，单位 cm）==");
            sb.AppendLine("脚种植=" + rr.UseFootGait
                + "  找地=" + rr.CarryGroundSeek + "（增益 " + rr.CarryGroundSeekGain.ToString("F2")
                + " / 预压 " + (rr.CarryGroundPress * 1000f).ToString("F1") + " mm）"
                + "  踝摆平=" + rr.FootLevelAnkle + "（摆动档 " + rr.SwingAnkleScale.ToString("F2") + "）"
                + "  步长=" + rr.GaitStepLength.ToString("F2"));
            try
            {
                Sim sim = BuildArmedSim(rr, true, true);
                ClumsyPart fl = sim.Rag.Find("footl");
                ClumsyPart fr = sim.Rag.Find("footr");
                ClumsyPart[] feet = { fl, fr };

                const int Bins = 10;
                int[] n = new int[Bins];
                float[] sum = new float[Bins];
                float[] minY = new float[Bins];
                for (int i = 0; i < Bins; i++) minY[i] = float.PositiveInfinity;

                int swingFrames = 0, scrapeFrames = 0, underFrames = 0;
                float swingMin = float.PositiveInfinity;
                float supportSum = 0f, supportMin = float.PositiveInfinity;
                int supportFrames = 0, supportAirFrames = 0, supportDownFrames = 0, supportAirRun = 0, supportAirMax = 0;
                float worstSwing = float.PositiveInfinity, worstSupportAtWorst = 0f;

                int steps = 300;
                for (int k = 0; k < steps; k++)
                {
                    sim.Ctrl.Move = move;
                    sim.Ctrl.Sprint = false;
                    _ragStep.Invoke(sim.Rag, null);
                    _ctrlStep.Invoke(sim.Ctrl, null);
                    _poseStep.Invoke(sim.Pose, null);
                    Physics.Simulate(Dt);

                    if (!sim.Ctrl.GaitActive)
                        continue;
                    bool rightSwings = sim.Ctrl.SwingFoot == "footr";
                    float u = Mathf.Clamp01(sim.Ctrl.SwingProgress);
                    ClumsyPart sw = feet[rightSwings ? 1 : 0];
                    ClumsyPart su = feet[rightSwings ? 0 : 1];
                    float swY = (sw.Shape.bounds.min.y) * 100f;
                    float suY = (su.Shape.bounds.min.y) * 100f;

                    int b = Mathf.Clamp((int)(u * Bins), 0, Bins - 1);
                    n[b]++;
                    sum[b] += swY;
                    if (swY < minY[b]) minY[b] = swY;

                    swingFrames++;
                    if (swY < 2f) scrapeFrames++;
                    if (swY < 0f) underFrames++;
                    if (swY < swingMin) swingMin = swY;
                    if (swY < worstSwing) { worstSwing = swY; worstSupportAtWorst = suY; }

                    supportFrames++;
                    supportSum += suY;
                    if (suY < supportMin) supportMin = suY;
                    if (suY > 2f) supportAirFrames++;
                    if (suY < 1f) { supportDownFrames++; supportAirRun = 0; }
                    else { supportAirRun++; if (supportAirRun > supportAirMax) supportAirMax = supportAirRun; }
                }

                sb.AppendLine(string.Format("摆动期 {0} 帧；离地 <2cm（刮蹭）{1} 帧 = {2:F0}%；穿地(<0) {3} 帧 = {4:F0}%；最低 {5:F1} cm",
                    swingFrames, scrapeFrames, swingFrames > 0 ? scrapeFrames * 100f / swingFrames : 0f,
                    underFrames, swingFrames > 0 ? underFrames * 100f / swingFrames : 0f, swingMin));
                sb.AppendLine(string.Format("★ 支撑相里支撑脚«真踩在地上»(<1cm) {0}/{1}（{2:F0}%）  最长连续«相位说支撑、脚却在空中» {3} 帧（{4:F2}s）",
                    supportDownFrames, supportFrames, supportFrames > 0 ? supportDownFrames * 100f / supportFrames : 0f,
                    supportAirMax, supportAirMax * Dt));
                sb.AppendLine(string.Format("支撑脚：均 {0:F1} cm  最低 {1:F1} cm  离地 >2cm 的帧 {2}/{3}（{4:F0}%）",
                    supportFrames > 0 ? supportSum / supportFrames : 0f, supportMin, supportAirFrames, supportFrames,
                    supportFrames > 0 ? supportAirFrames * 100f / supportFrames : 0f));
                sb.AppendLine(string.Format("摆动脚最低那一帧：摆动脚 {0:F1} cm 时，支撑脚在 {1:F1} cm  ← 支撑脚被抬高＝找地在顶整个人",
                    worstSwing, worstSupportAtWorst));
                sb.AppendLine("摆动进度 u 分档（摆动脚离地量，cm）：");
                for (int i = 0; i < Bins; i++)
                {
                    if (n[i] == 0) continue;
                    sb.AppendLine(string.Format("   u {0,3}-{1,3}%  均 {2,6:F1}  最低 {3,6:F1}   样本 {4}",
                        i * 100 / Bins, (i + 1) * 100 / Bins, sum[i] / n[i], minY[i], n[i]));
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>
        /// ★ 2026-10-02：**诊断台该用的那份配方**。
        ///
        /// 场景里 `UseAnimation = false`（owner 要看布娃娃本身），而 `new RagdollRecipe()`
        /// 的代码默认是 `true` —— 量走路/姿势时用错那份会走「动画姿势源」那条分支，
        /// 程序化腿角根本不生效（同一个步态：true 时「沾地 94% / 滑移 2.6 cm」，
        /// false 时「沾地 41% / 滑移 1.0 cm」）。本会话第一遍就踩了这个坑：
        /// 摆动脚穿地率量到 36%（true）vs 7%（false），差了五倍。
        ///
        /// 做法：拿一份 `new RagdollRecipe()`，再把**场景里那份序列化的 Recipe** 逐字段盖上去
        /// （场景里那份是权威 —— 改 C# 默认值不会改到它，见交接 §★★★ 第 1 条）。
        /// 找不到场景实例就只把 `UseAnimation` 关掉。
        /// </summary>
        public static RagdollRecipe SceneRecipe()
        {
            RagdollRecipe rr = new RagdollRecipe();
            rr.UseAnimation = false;
            ClumsyRagdollGame game = null;
            foreach (ClumsyRagdollGame g in UnityEngine.Object.FindObjectsByType<ClumsyRagdollGame>(FindObjectsSortMode.None))
            {
                game = g;
                break;
            }
            if (game != null && game.Recipe != null)
            {
                FieldInfo[] fs = typeof(RagdollRecipe).GetFields(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < fs.Length; i++)
                    fs[i].SetValue(rr, fs[i].GetValue(game.Recipe));
            }
            return rr;
        }

        /// <summary>
        /// ★ 2026-10-02：**隔离实验 —— 髋/膝角到底把脚送到哪里**（治「脚刮地面」的地基读数）。
        ///
        /// 步态的摆动腿高度是**髋/膝两个角**决定的，而这两个角之间没有解析关系
        /// （§11.4 已经吃过一次亏：解析式「踝 = −(髋+膝)」实测把鞋底摆斜 78°）。
        /// 所以直接把左腿摆到一张网格上，逐格量「鞋底离地多少 + 脚相对髖在前后哪个位置」。
        ///
        /// 口径：托举层照常开着（Move = 0，托举把髖托在出生高度），右腿保持站立角，
        /// 左腿用**姿势驱动自己的 Pose()**（反射调用，所以基准姿势与 slew 限制都与真步态一致）。
        /// 角度符号与 `ClumsyPoseDriver` 的 `legL = -HipSwingDegrees * f` 同一套。
        /// </summary>
        public static string LegAngleProbe()
        {
            return LegAngleProbe(SceneRecipe());
        }

        public static string LegAngleProbe(RagdollRecipe rr)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 隔离实验：左腿髋/膝角 → 脚在哪（托举开、Move=0、右腿站直）==");
            sb.AppendLine("行=髋角（度，**负 = 往前**，与 legL = −髋摆幅×f 同号）  列=膝角（度，正 = 屈膝）");
            sb.AppendLine("每格：「鞋底离地 cm / 脚相对髖的前后偏移 cm（+ = 在髖前面）」");
            float[] hips = { -60f, -45f, -30f, -15f, 0f, 15f, 30f, 45f };
            float[] knees = { 0f, 15f, 30f, 45f, 60f };
            try
            {
                Sim sim = BuildArmedSim(rr, true, true);
                sim.Rag.SetLegSpringScale("footl", rr.GaitSwingStiffnessScale);
                sim.Rag.SetLegSpringScale("footr", rr.GaitSupportStiffnessScale);
                ClumsyPart fl = sim.Rag.Find("footl");
                ClumsyPart hipsPart = sim.Rag.Find("hips");
                sb.Append("  髋\\膝 ");
                for (int j = 0; j < knees.Length; j++)
                    sb.Append(("k" + knees[j].ToString("F0")).PadLeft(15));
                sb.AppendLine();
                for (int i = 0; i < hips.Length; i++)
                {
                    sb.Append(("h" + hips[i].ToString("F0")).PadLeft(7) + " ");
                    for (int j = 0; j < knees.Length; j++)
                    {
                        // 每次换格子前先把腿放回中立并稳定一会儿，避免上一次的残留姿态带进来
                        Settle(sim, 0f, 0f, 25);
                        Settle(sim, hips[i], knees[j], 45);
                        Vector3 fp = fl.Body.position;
                        Vector3 hp = hipsPart.Body.position;
                        float up = (fl.Shape.bounds.min.y - sim.Rag.GroundY) * 100f;
                        float lead = Vector3.Dot(fp - hp, sim.Rag.Root.forward) * 100f;
                        sb.Append(string.Format("{0,6:F1}/{1,6:F1}", up, lead).PadLeft(15));
                    }
                    sb.AppendLine();
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>把左腿摆到 (髋, 膝)，步进若干步让它稳定下来。</summary>
        static void Settle(Sim sim, float hip, float knee, int steps)
        {
            for (int k = 0; k < steps; k++)
            {
                sim.Ctrl.Move = Vector2.zero;
                sim.Ctrl.Sprint = false;
                _ragStep.Invoke(sim.Rag, null);
                _ctrlStep.Invoke(sim.Ctrl, null);
                PoseSet(sim, "uplegl", hip);
                PoseSet(sim, "legl", knee);
                PoseSet(sim, "footl", 0f);
                PoseSet(sim, "uplegr", 0f);
                PoseSet(sim, "legr", 0f);
                PoseSet(sim, "footr", 0f);
                Physics.Simulate(Dt);
            }
        }

        static void PoseSet(Sim sim, string key, float aboutRight)
        {
            if (sim.Pose == null)
                return;
            if (_poseSet == null)
                _poseSet = typeof(ClumsyPoseDriver).GetMethod("Pose",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            _poseSet.Invoke(sim.Pose, new object[] { key, aboutRight, 0f, 0f, Dt });
        }

        /// <summary>
        /// ★ 2026-10-02：**逐步打表**（走路时两只脚 + 骨盆 + 找地读数）。
        ///
        /// 用来判断「相位说的那只支撑脚」与「物理上真的踩在地上的那只脚」是不是同一只。
        /// §14 的读数里支撑脚平均离地 2.7 cm、最低 −4.3 cm，这不正常 —— 要么相位与落地错位，
        /// 要么「找地」在摆动脚沉下去的帧里把**整个人**顶起来（它取的是两只脚的最低点）。
        /// </summary>
        public static string GaitTraceReport()
        {
            return GaitTraceReport(SceneRecipe(), 70);
        }

        public static string GaitTraceReport(RagdollRecipe rr, int steps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 步态逐步打表（按住 W；长度单位 cm）==");
            sb.AppendLine("找地=" + rr.CarryGroundSeek + "（增益 " + rr.CarryGroundSeekGain.ToString("F2")
                + " / 预压 " + (rr.CarryGroundPress * 1000f).ToString("F1") + " mm）"
                + "  髋摆=" + rr.HipSwingDegrees.ToString("F0") + "°  膝弯=" + rr.KneeBendDegrees.ToString("F0") + "°");
            sb.AppendLine("摆动腿的«竖直落差»：髖→膝 / 膝→踝 / 踝→鞋底（三段落差决定鞋底的高度）");
            sb.AppendLine(" 步  相位    u   摆脚 摆脚离地 | 髖→膝 膝→踝 踝→底 | 撑脚 撑脚离地 | 骨盆Y  两脚最低");
            try
            {
                Sim sim = BuildArmedSim(rr, true, true);
                ClumsyPart fl = sim.Rag.Find("footl");
                ClumsyPart fr = sim.Rag.Find("footr");
                for (int k = 0; k < steps; k++)
                {
                    sim.Ctrl.Move = new Vector2(0f, 1f);
                    sim.Ctrl.Sprint = false;
                    _ragStep.Invoke(sim.Rag, null);
                    _ctrlStep.Invoke(sim.Ctrl, null);
                    _poseStep.Invoke(sim.Pose, null);
                    Physics.Simulate(Dt);

                    if (k % 2 != 0)
                        continue;
                    bool rightSwings = sim.Ctrl.SwingFoot == "footr";
                    ClumsyPart swFoot = rightSwings ? fr : fl;
                    ClumsyPart suFoot = rightSwings ? fl : fr;
                    string s = rightSwings ? "r" : "l";
                    ClumsyPart thigh = sim.Rag.Find("upleg" + s);
                    ClumsyPart shank = sim.Rag.Find("leg" + s);
                    float thighY = thigh.Body.position.y;
                    float kneeY = shank.Body.position.y;
                    float ankleY = swFoot.Body.position.y;
                    float swMin = swFoot.Shape.bounds.min.y;
                    float suMin = suFoot.Shape.bounds.min.y;
                    float lY = fl.Shape.bounds.min.y;
                    float rY = fr.Shape.bounds.min.y;
                    sb.AppendLine(string.Format(
                        "{0,4}  {1:F2}  {2:F2}  {3}  {4,6:F1} | {5,5:F1} {6,5:F1} {7,5:F1} | {8}  {9,6:F1}  | {10,6:F3}  {11,6:F1}",
                        k, sim.Ctrl.GaitPhase, sim.Ctrl.SwingProgress,
                        rightSwings ? "footr" : "footl", (swMin - sim.Rag.GroundY) * 100f,
                        (thighY - kneeY) * 100f, (kneeY - ankleY) * 100f, (ankleY - swMin) * 100f,
                        rightSwings ? "footl" : "footr", (suMin - sim.Rag.GroundY) * 100f,
                        sim.Rag.PelvisPosition.y, Mathf.Min(lY, rY) * 100f));
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>
        /// ★ 2026-10-02：**走路的时候托举层是不是一直在生效**。
        ///
        /// `ClumsyRagdollControl.FixedUpdate` 里有一条：
        /// <code>if (carryActive &amp;&amp; Recipe.CarrySuspendOnJump &amp;&amp; (_carryOffSteps &gt; 0 || !Ragdoll.IsGroundedNow)) carryActive = false;</code>
        /// 它的本意是「跳 = 把角色扔出去」（起跳那一瞬托举全撤，落地再接住）。
        /// 但判据用的是 `!IsGroundedNow` —— 而走路时**两只脚会同时离地超过 5 cm**，
        /// 那几帧托举就整个撤掉了 ⇒ 角色进入**自由落体**，腿也在空中乱甩，
        /// 落地那一下就是砸下来（观感「脚底在地上刮」）。
        /// 这个报告把这件事量出来：走路 6 s 里有多少帧托举是关着的、多少帧两只脚都在 5 cm 以上。
        /// </summary>
        public static string WalkAirborneReport()
        {
            return WalkAirborneReport(SceneRecipe(), 300);
        }

        public static string WalkAirborneReport(RagdollRecipe rr, int steps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 走路时托举/接地占空（6s，按住 W）==");
            sb.AppendLine("CarrySuspendOnJump=" + rr.CarrySuspendOnJump + "  CarryGroundSeek=" + rr.CarryGroundSeek
                + "（增益 " + rr.CarryGroundSeekGain.ToString("F2") + "）");
            try
            {
                Sim sim = BuildArmedSim(rr, true, true);
                ClumsyPart fl = sim.Rag.Find("footl");
                ClumsyPart fr = sim.Rag.Find("footr");
                int carryOff = 0, notGrounded = 0, bothUp2 = 0, bothUp5 = 0, anyUnder = 0;
                int longestAir = 0, runAir = 0;
                float pelvisMin = float.PositiveInfinity, pelvisMax = float.NegativeInfinity;
                StringBuilder transitions = new StringBuilder();
                bool prevCarry = true;
                for (int k = 0; k < steps; k++)
                {
                    sim.Ctrl.Move = new Vector2(0f, 1f);
                    sim.Ctrl.Sprint = false;
                    _ragStep.Invoke(sim.Rag, null);
                    _ctrlStep.Invoke(sim.Ctrl, null);
                    _poseStep.Invoke(sim.Pose, null);
                    Physics.Simulate(Dt);

                    float lY = (fl.Shape.bounds.min.y) * 100f;
                    float rY = (fr.Shape.bounds.min.y - sim.Rag.GroundY) * 100f;
                    float low = Mathf.Min(lY, rY);
                    if (!sim.Ctrl.CarryActiveNow) { carryOff++; runAir++; if (runAir > longestAir) longestAir = runAir; }
                    else runAir = 0;
                    if (!sim.Rag.IsGroundedNow) notGrounded++;
                    if (low > 2f) bothUp2++;
                    if (low > 5f) bothUp5++;
                    if (low < 0f) anyUnder++;
                    float py = sim.Rag.PelvisPosition.y;
                    if (py < pelvisMin) pelvisMin = py;
                    if (py > pelvisMax) pelvisMax = py;
                    if (sim.Ctrl.CarryActiveNow != prevCarry && k > 20 && transitions.Length < 400)
                    {
                        transitions.Append((sim.Ctrl.CarryActiveNow ? " 开@" : " 关@") + k);
                        prevCarry = sim.Ctrl.CarryActiveNow;
                    }
                }
                sb.AppendLine(string.Format("托举关着的帧 {0}/{1}（{2:F0}%），最长连续 {3} 帧（{4:F2} s）",
                    carryOff, steps, carryOff * 100f / steps, longestAir, longestAir * Dt));
                sb.AppendLine(string.Format("`IsGroundedNow == false` 的帧 {0}/{1}（{2:F0}%）",
                    notGrounded, steps, notGrounded * 100f / steps));
                sb.AppendLine(string.Format("两只脚都 >2 cm 的帧 {0}（{1:F0}%），都 >5 cm 的帧 {2}（{3:F0}%）；有脚穿地(<0) 的帧 {4}（{5:F0}%）",
                    bothUp2, bothUp2 * 100f / steps, bothUp5, bothUp5 * 100f / steps, anyUnder, anyUnder * 100f / steps));
                sb.AppendLine(string.Format("骨盆 {0:F3}–{1:F3} m（起伏 {2:F1} cm）", pelvisMin, pelvisMax, (pelvisMax - pelvisMin) * 100f));
                sb.AppendLine("托举开关切换：" + (transitions.Length == 0 ? "（全程没变）" : transitions.ToString()));
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>
        /// ★ 2026-10-02：**相位窗口与«脚真的在地上»到底对没对上**。
        ///
        /// owner 说「前进逻辑仍然残留着向前拉动的力，而不是单纯的靠双脚」。
        /// 这句话在读数上的样子就是：**相位说 A 脚是支撑脚，可 A 脚根本不在下面**
        /// —— 身体在有脚没脚的空档里前进 ⇒ 看着就是被拉。
        ///
        /// 这个报告把一整个周期的相位分成 20 格，逐格统计「左脚在<1cm / 右脚在<1cm / 至少一只在」，
        /// 于是「声明的支撑窗」与「真实的接地窗」差了多远一眼就能看出来。
        /// </summary>
        public static string GaitDutyByPhaseReport()
        {
            return GaitDutyByPhaseReport(SceneRecipe(), 600);
        }

        public static string GaitDutyByPhaseReport(RagdollRecipe rr, int steps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 相位 × 真实接地（走路，" + (steps * Dt).ToString("F0") + "s）==");
            sb.AppendLine("相位说明：相位 p ∈ [0,1)，一个周期两步。**声明的**支撑脚 = 左脚 [0,0.5)、右脚 [0.5,1)；"
                + "摆动脚 = 右 [0.25,0.75)、左 [0.75,1.25)。");
            try
            {
                Sim sim = BuildArmedSim(rr, true, true);
                ClumsyPart fl = sim.Rag.Find("footl");
                ClumsyPart fr = sim.Rag.Find("footr");
                const int Bins = 20;
                int[] n = new int[Bins], downL = new int[Bins], downR = new int[Bins];
                int bothUp = 0, total = 0;
                for (int k = 0; k < steps; k++)
                {
                    sim.Ctrl.Move = new Vector2(0f, 1f);
                    sim.Ctrl.Sprint = false;
                    _ragStep.Invoke(sim.Rag, null);
                    _ctrlStep.Invoke(sim.Ctrl, null);
                    _poseStep.Invoke(sim.Pose, null);
                    Physics.Simulate(Dt);
                    if (!sim.Ctrl.GaitActive)
                        continue;
                    int b = Mathf.Clamp((int)(sim.Ctrl.GaitPhase * Bins), 0, Bins - 1);
                    bool l = fl.Shape.bounds.min.y < 0.01f;
                    bool r = fr.Shape.bounds.min.y < 0.01f;
                    n[b]++;
                    if (l) downL[b]++;
                    if (r) downR[b]++;
                    total++;
                    if (!l && !r) bothUp++;
                }
                sb.AppendLine(string.Format("总帧 {0}；**两只脚都在 1cm 以上（没有任何脚在地上）的帧 {1}（{2:F0}%）**",
                    total, bothUp, total > 0 ? bothUp * 100f / total : 0f));
                sb.AppendLine("  相位   样本   左脚在  右脚在  声明支撑脚   声明支撑脚真的在?");
                for (int i = 0; i < Bins; i++)
                {
                    if (n[i] == 0) continue;
                    float p = (i + 0.5f) / Bins;
                    string declared = p < 0.5f ? "footl" : "footr";
                    int declDown = p < 0.5f ? downL[i] : downR[i];
                    sb.AppendLine(string.Format("  {0:F2}   {1,4}   {2,5:F0}%  {3,5:F0}%   {4}        {5,5:F0}%",
                        p, n[i], downL[i] * 100f / n[i], downR[i] * 100f / n[i], declared, declDown * 100f / n[i]));
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>
        /// ★ 2026-10-02：**脚尖朝哪**（治 owner 报的「走路像外八字」）。
        ///
        /// 两处外摆会一起把脚尖拧向外侧：
        ///   ① `GaitStanceAbductDegrees`（默认 7°）——**两条腿一起往外**，按驱动量缩放，走路时全程都在；
        ///   ② `GaitSwingAbductDegrees`（默认 12°）——摆动腿中段再往外。
        /// 它们都是绕**角色前方轴**转大腿，整条腿跟着转，鞋头的方向也就跟着拧出去了。
        ///
        /// 这个报告量的是：鞋子的长轴（碰撞盒的局部 +Z）与角色朝向的**有符号夹角**，
        /// 按支撑/摆动分开统计（正 = 鞋尖朝右/外侧，负 = 朝左/内侧），
        /// 顺带数一遍两脚碰撞盒相交的帧（外摆当初就是为了治那个才加的）。
        /// </summary>
        public static string FootYawReport()
        {
            return FootYawReport(SceneRecipe(), 300);
        }

        public static string FootYawReport(RagdollRecipe rr, int steps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 鞋尖朝向（走路 " + (steps * Dt).ToString("F1") + "s）==");
            sb.AppendLine("基础外摆=" + rr.GaitStanceAbductDegrees.ToString("F1") + "°  摆动外摆="
                + rr.GaitSwingAbductDegrees.ToString("F1") + "°（符号 " + rr.GaitAbductSign.ToString("F0") + "）");
            try
            {
                Sim sim = BuildArmedSim(rr, true, true);
                ClumsyPart fl = sim.Rag.Find("footl");
                ClumsyPart fr = sim.Rag.Find("footr");
                ClumsyPart[] feet = { fl, fr };
                string[] names = { "footl(左)", "footr(右)" };
                float[] sumStance = new float[2], sumSwing = new float[2];
                float[] maxAbs = new float[2];
                int[] nStance = new int[2], nSwing = new int[2];
                int cross = 0, deep = 0;
                float deepest = 0f;
                float sepSum = 0f, sepMin = float.PositiveInfinity; int sepN = 0;
                for (int k = 0; k < steps; k++)
                {
                    sim.Ctrl.Move = new Vector2(0f, 1f);
                    sim.Ctrl.Sprint = false;
                    _ragStep.Invoke(sim.Rag, null);
                    _ctrlStep.Invoke(sim.Ctrl, null);
                    _poseStep.Invoke(sim.Pose, null);
                    Physics.Simulate(Dt);

                    Vector3 fwd = sim.Rag.Root.forward;
                    fwd.y = 0f;
                    for (int i = 0; i < 2; i++)
                    {
                        if (feet[i] == null || feet[i].Shape == null)
                            continue;
                        Vector3 toe = feet[i].Shape.transform.forward;
                        toe.y = 0f;
                        float yaw = Vector3.SignedAngle(fwd, toe, Vector3.up);
                        bool swinging = sim.Ctrl.GaitActive && sim.Ctrl.SwingFoot == feet[i].Spec.Key;
                        if (swinging) { sumSwing[i] += yaw; nSwing[i]++; }
                        else { sumStance[i] += yaw; nStance[i]++; }
                        if (Mathf.Abs(yaw) > maxAbs[i]) maxAbs[i] = Mathf.Abs(yaw);
                    }
                    if (fl != null && fr != null)
                    {
                        // 左右脚的横向间距（踝，沿角色右轴）—— 判«罗圈腿/外八字»的另一半
                        Vector3 rr2 = sim.Rag.Root.right; rr2.y = 0f;
                        float sep = Vector3.Dot(fr.Body.position - fl.Body.position, rr2);
                        sepSum += sep; sepN++;
                        if (sep < sepMin) sepMin = sep;
                        Vector3 dir; float dist;
                        bool hit = Physics.ComputePenetration(fl.Shape, fl.Shape.transform.position, fl.Shape.transform.rotation,
                            fr.Shape, fr.Shape.transform.position, fr.Shape.transform.rotation,
                            out dir, out dist);
                        if (hit && dist > 0.0005f)
                        {
                            cross++;
                            if (dist > 0.005f) deep++;
                            if (dist > deepest) deepest = dist;
                        }
                    }
                }
                for (int i = 0; i < 2; i++)
                {
                    sb.AppendLine(string.Format(
                        "  {0}：支撑相鞋尖偏角 均 {1,6:F1}°（{2} 帧）   摆动相 均 {3,6:F1}°（{4} 帧）   最大 |{5:F1}°|",
                        names[i],
                        nStance[i] > 0 ? sumStance[i] / nStance[i] : 0f, nStance[i],
                        nSwing[i] > 0 ? sumSwing[i] / nSwing[i] : 0f, nSwing[i],
                        maxAbs[i]));
                }
                sb.AppendLine(string.Format("  左右踝横向间距 均 {0:F1} cm  最小 {1:F1} cm", sepSum / Mathf.Max(1, sepN) * 100f, sepMin * 100f));
                sb.AppendLine(string.Format("  两脚碰撞盒相交 {0}/{1} 帧，>5mm 的 {2} 帧，最深 {3:F1} mm",
                    cross, steps, deep, deepest * 1000f));
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>
        /// ★ 2026-10-02：**转身（左转 / 右转）走到不到位**。
        ///
        /// 诊断台里没有相机，而 **yaw 的权威在相机那边**（`ClumsyRagdollCamera` 每帧写
        /// `Root.rotation`）—— 所以这里**照着相机那样**把 `Root` 的 yaw 按控制器给的
        /// `TurnRateCommand` 推进去，再看：
        ///   ① 身体（髖的 yaw）有没有跟上 `Root`（yaw 伺服的效果）；
        ///   ② 两只脚的沾地占空 / 每步滑移 —— 转身是«一只脚当支点、身体绕着它转»，
        ///      所以支点脚应当基本不滑、两只脚应当交替离地；
        ///   ③ 摆动脚的离地高度（别在地上搓）。
        /// </summary>
        public static string TurnReport()
        {
            return TurnReport(SceneRecipe(), 130f, 360);
        }

        public static string TurnReport(RagdollRecipe rr, float degreesPerSecond, int steps)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 转身（" + (steps * Dt).ToString("F1") + "s，指令 " + degreesPerSecond.ToString("F0") + " 度/秒）==");
            sb.AppendLine("TurnWithAD=" + rr.TurnWithAD + "  yaw 伺服 " + rr.TurnYawStiffness.ToString("F2")
                + "/" + rr.TurnYawDamping.ToString("F2") + " 上限 " + rr.TurnMaxTorque.ToString("F0")
                + "  每步 " + rr.TurnStepDegrees.ToString("F0") + " 度  摆幅比例 " + rr.TurnGaitStrideScale.ToString("F2"));
            try
            {
                Sim sim = BuildArmedSim(rr, true, true);
                ClumsyPart fl = sim.Rag.Find("footl");
                ClumsyPart fr = sim.Rag.Find("footr");
                ClumsyPart[] feet = { fl, fr };
                string[] names = { "footl", "footr" };
                float rootYaw = sim.Rag.Root != null ? sim.Rag.Root.eulerAngles.y : 0f;
                float startRoot = rootYaw;
                float bodyYawStart = sim.Rag.Hips.Body.rotation.eulerAngles.y;
                int[] contact = new int[2];
                float[] slip = new float[2];
                float[] lastX = new float[2];
                float[] lastZ = new float[2];
                bool[] touch = new bool[2];
                int[] swings = new int[2];
                float[] maxLift = new float[2];
                float eps = 0.012f;
                int turnedSteps = 0;
                float cmdSum = 0f;
                float rootYawAcc = 0f;
                float bodyYawAcc = 0f;
                float prevBodyYaw = bodyYawStart;
                float prevRootYaw = rootYaw;
                for (int k = 0; k < steps; k++)
                {
                    sim.Ctrl.Move = new Vector2(1f, 0f);          // 按 D = 右转
                    // 模拟相机：把控制器的转身指令累加到 Root 的 yaw 上
                    cmdSum += sim.Ctrl.TurnRateCommand * Dt;
                    // ⚠️ 不用再«模拟相机»了：2026-10-02 起**转身的权威在控制器里**（它自己推 Root 的 yaw），
                    //   诊断台跑的这条路径与游戏里逐位一致。
                    rootYaw = sim.Rag.Root != null ? sim.Rag.Root.eulerAngles.y : 0f;
                    rootYawAcc += Mathf.DeltaAngle(prevRootYaw, rootYaw);
                    prevRootYaw = rootYaw;
                    _ragStep.Invoke(sim.Rag, null);
                    _ctrlStep.Invoke(sim.Ctrl, null);
                    _poseStep.Invoke(sim.Pose, null);
                    Physics.Simulate(Dt);
                    if (sim.Ctrl.GaitActive)
                        turnedSteps++;
                    float bodyYawNow = sim.Rag.Hips.Body.rotation.eulerAngles.y;
                    bodyYawAcc += Mathf.DeltaAngle(prevBodyYaw, bodyYawNow);
                    prevBodyYaw = bodyYawNow;
                    for (int a = 0; a < 2; a++)
                    {
                        ClumsyPart p2 = feet[a];
                        if (p2 == null || p2.Shape == null)
                            continue;
                        Vector3 wp = p2.Body.position;
                        float lift = p2.Shape.bounds.min.y;
                        bool ct = lift <= eps;
                        if (ct)
                        {
                            contact[a]++;
                            if (touch[a])
                                slip[a] += new Vector2(wp.x - lastX[a], wp.z - lastZ[a]).magnitude;
                            touch[a] = true;
                        }
                        else
                        {
                            if (touch[a])
                                swings[a]++;
                            touch[a] = false;
                            if (lift > maxLift[a])
                                maxLift[a] = lift;
                        }
                        lastX[a] = wp.x;
                        lastZ[a] = wp.z;
                    }
                }
                // ⚠️ 不能直接用 DeltaAngle(起点, 终点) —— 转超过 180° 会被环绕成反方向（实测骗了一轮）
                float rootTurned = rootYawAcc;
                float bodyTurned = bodyYawAcc;
                float seconds = steps * Dt;
                sb.AppendLine(string.Format("Root 转了 {0:F0} 度 ⇒ {1:F0} 度/秒（控制器指令累计 {2:F0} 度）",
                    rootTurned, rootTurned / seconds, cmdSum));
                sb.AppendLine(string.Format("★ **身体转了 {0:F0} 度** ⇒ 跟随率 {1:F0}%",
                    bodyTurned, Mathf.Abs(rootTurned) > 1f ? bodyTurned / rootTurned * 100f : 0f));
                sb.AppendLine(string.Format("步态生效帧 {0}/{1}", turnedSteps, steps));
                for (int a = 0; a < 2; a++)
                {
                    int cm = contact[a] > 0 ? contact[a] : 1;
                    sb.AppendLine(string.Format("   {0}：沾地 {1}/{2}（{3:F0}%）  每步滑移 {4:F1} cm  摆动相 {5} 次  最高离地 {6:F1} cm",
                        names[a], contact[a], steps, contact[a] * 100f / steps, slip[a] / cm * 100f, swings[a], maxLift[a] * 100f));
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string PairPenetrationReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 部件对穿透审计（>5 mm 的对）==");
            try
            {
                RagdollRecipe rr = new RagdollRecipe();
                Sim sim = BuildSim(rr, true, true);
                sb.AppendLine(AuditPairs(sim, "站立 1s", 1f, Vector2.zero));
                sb.AppendLine(AuditPairs(sim, "走路 3s", 3f, new Vector2(0f, 1f)));
                sb.AppendLine(AuditPairs(sim, "推倒后 2s", 2f, Vector2.zero));
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        static string AuditPairs(Sim sim, string label, float seconds, Vector2 move)
        {
            int steps = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));
            bool pushed = false;
            for (int k = 0; k < steps; k++)
            {
                if (sim.Ctrl != null) { sim.Ctrl.Move = move; sim.Ctrl.Sprint = false; }
                if (label.StartsWith("推倒") && !pushed && k == 20)
                {
                    sim.Rag.Push(new Vector3(2.2f, 0.6f, 0f) * 220f);
                    pushed = true;
                }
                _ragStep.Invoke(sim.Rag, null);
                _ctrlStep.Invoke(sim.Ctrl, null);
                _poseStep.Invoke(sim.Pose, null);
                Physics.Simulate(Dt);
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("── " + label + " ──");
            int n = sim.Rag.Parts.Count;
            for (int i = 0; i < n; i++)
            {
                ClumsyPart a = sim.Rag.Parts[i];
                if (a.Shape == null)
                    continue;
                for (int j = i + 1; j < n; j++)
                {
                    ClumsyPart b = sim.Rag.Parts[j];
                    if (b.Shape == null)
                        continue;
                    // 关节相连的一对由 ConfigurableJoint.enableCollision 管（默认 false），不用看
                    bool connected = a.Spec.ParentKey == b.Spec.Key || b.Spec.ParentKey == a.Spec.Key;
                    Vector3 dir;
                    float dist;
                    if (Physics.ComputePenetration(a.Shape, a.Shape.transform.position, a.Shape.transform.rotation,
                                                   b.Shape, b.Shape.transform.position, b.Shape.transform.rotation,
                                                   out dir, out dist) && dist > 0.005f)
                        sb.AppendLine(string.Format("   {0,-11} × {1,-11} 穿透 {2,6:F1} mm{3}",
                            a.Spec.Key, b.Spec.Key, dist * 1000f, connected ? "  （关节相连，本来就不碰）" : "  ← 不相连，若开自碰撞就会互推"));
                }
            }
            return sb.ToString();
        }

        public static string CarryReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 环境交互端到端：找目标 → 够过去 → 抓住 → 拖着走 → 松手 ==");
            try
            {
                RagdollRecipe recipe = new RagdollRecipe();
                Sim sim = BuildArmedSim(recipe, true, true);

                ClumsyPart hand = sim.Rag.Find("handl");
                if (hand == null)
                {
                    sb.AppendLine("找不到 handl");
                    TearDown(sim);
                    return sb.ToString();
                }

                ArmIKChain chain = sim.ArmIK.Get("l");
                Vector3 shoulder = chain.ShoulderWorld;

                // ⚠️ **箱子必须放在一个台子上，不能直接摆在地上或半空中。**
                // 两个实测教训：
                //   ① 摆半空中：它有 Rigidbody，会直接摔到地上再滚走（实测 0.6s 内从 y=0.72
                //      掉到 0.15 并滚出 0.3m）—— 于是「跑掉了」，不是「抓不到」。
                //   ② 摆在地上：**这只浣熊的手根本够不到地面**。肩高 0.93m，而臂长只有 0.476m，
                //      所以可达空间是一个以肩为心、半径 0.476m 的球：y 大致在 0.45~1.40m 之间，
                //      地面（y≈0）永远在外面。这是模型尺寸的硬事实，不是 bug。
                //      真要摸地面，得让 IK 连带弯腰（本工程没做，见交接文档 §八）。
                Vector3 want = shoulder + new Vector3(-0.16f, -0.24f, 0.30f);

                GameObject pedestal = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pedestal.name = "台子";
                pedestal.transform.SetParent(sim.World.transform, true);
                // 台面要够宽：第一版用 0.4×0.4，箱子被伸过来的手一碰就滑下台滚走了
                // （实测跑到 1.6m 外）——那时读数看起来像「抓不到」，其实是「东西跑了」。
                pedestal.transform.localScale = new Vector3(0.6f, 0.5f, 0.6f);
                pedestal.transform.position = new Vector3(want.x, want.y - 0.15f - 0.25f, want.z);


                GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "箱子";
                box.transform.SetParent(sim.World.transform, true);
                box.transform.localScale = Vector3.one * 0.3f;
                box.transform.position = want;
                Rigidbody boxRb = box.AddComponent<Rigidbody>();
                boxRb.mass = 2f;
                Grippable grip = box.AddComponent<Grippable>();
                grip.UseCustomMotions = true;
                grip.Motions = JointMotionsConfig.Make(ConfigurableJointMotion.Limited, 38f);
                Physics.SyncTransforms();

                sb.AppendLine("箱子在 " + box.transform.position.ToString("F3")
                    + "，左肩在 " + shoulder.ToString("F3")
                    + "，相距 " + Vector3.Distance(shoulder, box.transform.position).ToString("F3") + "m"
                    + "（左臂可达 " + chain.MaxReach.ToString("F3") + "m）");

                sim.LeftGrip.ScriptedInput = true;
                sim.LeftGrip.GripHeld = true;

                // ---- 阶段 1：站着伸手 ----
                sb.AppendLine("-- 阶段 1：站着伸手（3s，只按抓取键）--");
                string last = "";
                int n1 = Mathf.RoundToInt(3f / Dt);
                for (int i = 0; i < n1; i++)
                {
                    StepSim(sim, 1, Vector2.zero);
                    if (sim.Interact.StateName != last)
                    {
                        last = sim.Interact.StateName;
                        sb.AppendLine("   t=" + (i * Dt).ToString("F2") + "s  " + sim.Interact.LastTransition);
                    }
                }
                sb.AppendLine("   末态 " + sim.Interact.StateName
                    + "   手误差 " + chain.HandErrorRaw.ToString("F4") + "m"
                    + "   IK 权重 " + chain.Weight.ToString("F2"));
                // 手离「箱子表面」多远 —— 抓取球的判据就是它（GripQueryRadius）。
                // 手离「抓取点」的距离会因为 IK 夹紧而失真，要看表面距离才可靠。
                Collider boxCol = box.GetComponent<Collider>();
                float dSurf = boxCol != null
                    ? Vector3.Distance(hand.Body.position, boxCol.ClosestPoint(hand.Body.position)) : -1f;
                sb.AppendLine("   手离箱子表面 " + dSurf.ToString("F4") + "m（抓取球半径 "
                    + recipe.GripQueryRadius.ToString("F3") + "m）   箱子现在 "
                    + box.transform.position.ToString("F3"));
                sb.AppendLine("   " + sim.LeftGrip.Report() + "   骨盆y " + sim.Rag.PelvisPosition.y.ToString("F3"));

                bool grabbed = sim.LeftGrip.HasGrip;
                sb.AppendLine("   抓住 = " + (grabbed
                    ? "是（" + sim.LeftGrip.GrabbedObject.name + "，关节加在手上，物体角运动 Limited 38°）"
                    : "否"));

                // ---- 阶段 2：抓着走 ----
                sb.AppendLine("-- 阶段 2：抓着走（4s，按 W）--");
                Vector3 boxStart = box.transform.position;
                Vector3 charStart = sim.Rag.PelvisPosition;
                for (int i = 0; i < Mathf.RoundToInt(4f / Dt); i++)
                    StepSim(sim, 1, new Vector2(0f, 1f));
                Vector3 dch = sim.Rag.PelvisPosition - charStart;
                dch.y = 0f;
                Vector3 dbx = box.transform.position - boxStart;
                dbx.y = 0f;
                sb.AppendLine("   人走了 " + dch.magnitude.ToString("F2") + "m，箱子跟了 "
                    + dbx.magnitude.ToString("F2") + "m");
                sb.AppendLine("   手与箱子中心距离 "
                    + Vector3.Distance(hand.Body.position, box.transform.position).ToString("F3") + "m"
                    + "   骨盆 y " + sim.Rag.PelvisPosition.y.ToString("F3")
                    + "   直立度 " + sim.Rag.Upright.ToString("F3"));
                sb.AppendLine("   " + sim.LeftGrip.Report());

                // ---- 阶段 3：松手 ----
                sb.AppendLine("-- 阶段 3：松手（1.5s）--");
                sim.LeftGrip.GripHeld = false;
                for (int i = 0; i < Mathf.RoundToInt(1.5f / Dt); i++)
                    StepSim(sim, 1, Vector2.zero);
                sb.AppendLine("   状态 " + sim.Interact.StateName
                    + "   IK 权重 " + sim.ArmIK.WeightOf("l").ToString("F3"));
                sb.AppendLine("   " + sim.LeftGrip.Report());

                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        [MenuItem("笨拙布娃娃/诊断/F 肘解锁三档", false, 25)]
        public static void MenuLockDetail()
        {
            Debug.Log(LockPassengerDetailReport());
        }

        /// <summary>
        /// 肘解锁的三档对照。**这是手部 IK 的前提** ——
        /// 肘锁死时写 targetRotation 不生效，两骨 IK 无从下手（等于一条不能折的棍）。
        /// 读数（2026-10-01，本机）：全锁 8.00s / 只放小臂+手 8.00s / 放全部 0.40s。
        /// 也就是说「只放手臂」是白拿的：不付站立代价，换来一个能折的肘。
        /// </summary>
        public static string LockPassengerDetailReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 肘解锁：三档对照（8s 静止站立）==");
            try
            {
                for (int mode = 0; mode < 3; mode++)
                {
                    RagdollRecipe recipe = new RagdollRecipe();
                    // mode 0 = 全锁（教程 P5 步2 原样）
                    // mode 1 = 只放小臂+手（FreeElbowAngular）：腿仍然锁死
                    // mode 2 = 放全部非旋转点（含膝/脚）
                    recipe.LockPassengerAngular = (mode != 2);
                    recipe.FreeElbowAngular = (mode == 1);

                    Sim sim = BuildSim(recipe, true, true);
                    Trace t = Run(sim, 8f, 0f, Vector3.zero, Vector2.zero, false);
                    string label = mode == 0 ? "A 全锁（教程）" : (mode == 1 ? "B 只放小臂+手" : "C 放全部");
                    sb.AppendLine(Row(label, t, sim));
                    TearDown(sim);
                }
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        [MenuItem("笨拙布娃娃/诊断/G 手臂链标定", false, 26)]
        public static void MenuArmAxis()
        {
            Debug.Log(ArmAxisReport());
        }

        /// <summary>
        /// 手臂链的实测标定：骨长、bind 方向、掌心轴候选。
        /// 对应 EP13 的 "Auto Setup from Tip Transform"（不猜骨长）
        /// 与她对 offset 的警告（"this offset value is for banana man ... otherwise characters
        /// require different offsets"）—— 所以掌心轴必须按模型实测，不能照抄。
        /// </summary>
        public static string ArmAxisReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 手臂链标定（bind pose 实测）==");
            try
            {
                RagdollRecipe recipe = new RagdollRecipe();
                Sim sim = BuildArmedSim(recipe, false, false);
                sb.Append(sim.ArmIK.Report());

                sb.AppendLine("  hand 骨的四根局部轴在 bind 世界里指向哪里（用来判 Recipe.HandPalmAxis）：");
                for (int side = 0; side < 2; side++)
                {
                    ClumsyPart h = sim.Rag.Find(side == 0 ? "handl" : "handr");
                    if (h == null)
                        continue;
                    sb.AppendLine("    " + h.Spec.Key
                        + "  +X " + (h.RestRotation * Vector3.right).ToString("F3")
                        + "   -X " + (h.RestRotation * Vector3.left).ToString("F3"));
                    sb.AppendLine("    " + h.Spec.Key
                        + "  +Z " + (h.RestRotation * Vector3.forward).ToString("F3")
                        + "   -Z " + (h.RestRotation * Vector3.back).ToString("F3"));
                    sb.AppendLine("        手指方向(局部+Y) " + (h.RestRotation * Vector3.up).ToString("F3"));
                }
                sb.AppendLine("  当前 Recipe.HandPalmAxis = " + recipe.HandPalmAxis
                    + "（掌心法线取 hand 骨的哪根轴）");
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        [MenuItem("笨拙布娃娃/诊断/H 两骨 IK", false, 27)]
        public static void MenuArmIK()
        {
            Debug.Log(ArmIKReport());
        }

        /// <summary>
        /// 两骨 IK 的两条验证：
        ///   ① <b>换算式准确度</b> —— 直接把一个世界旋转写进 targetRotation，步进后量骨头实际朝向差多少。
        ///      （交接文档里那条 AngleAxis(−θ, 轴) 就是这么验的，这里验的是它的母式。）
        ///   ② <b>够不够得着</b> —— 把目标放在可达球面附近若干处，量手到目标的距离。
        /// </summary>
        public static string ArmIKReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 手部两骨 IK ==");
            try
            {
                RagdollRecipe recipe = new RagdollRecipe();
                recipe.UseInteraction = false;   // 状态机会把测试用的 IK 目标清掉
                // ① 专用一份配方：DampingRatio = 1（默认 0 是教程原值，角弹簧没阻尼会一直振荡）。
                // ⚠️ **不能把 GravityY 设成 0 来去重力** —— 弹簧刚度是按重量定标的
                //    （k_hips = margin × M·g·h，见 ClumsyRagdoll.Rebuild），g = 0 会把
                //    所有角弹簧一起清成 0，骨头根本不会动（第一版就这么错的：误差 = 100% 目标角）。
                //    正确做法：建完之后再 Physics.gravity = 0（下面循环里每一相都要重设，
                //    因为 BuildSim 会把它重置回配方值）。
                RagdollRecipe probeRecipe = new RagdollRecipe();
                probeRecipe.UseInteraction = false;
                probeRecipe.DampingRatio = 1.0f;
                probeRecipe.DampingRatio = 1.0f;
                Sim sim = BuildArmedSim(recipe, false, false);
                sb.Append(sim.ArmIK.Report());
                ArmIKChain probe = sim.ArmIK.Get("l");
                if (probe == null || !probe.Ready)
                {
                    sb.AppendLine("左臂链不全，跳过验证。");
                    TearDown(sim);
                    return sb.ToString();
                }
                TearDown(sim);

                sb.AppendLine("-- ① WorldToJoint：把世界旋转写进 targetRotation，看骨头实际转到哪（零重力）--");
                float[] angles = { 25f, -40f, 65f, 110f };
                float worst = 0f;
                for (int i = 0; i < angles.Length; i++)
                {
                    Sim s2 = BuildArmedSim(probeRecipe, false, false);   // 相与相之间重建世界
                    Physics.gravity = Vector3.zero;   // 建完之后再清零（见上：GravityY=0 会连弹簧一起清掉）
                    ArmIKChain c2 = s2.ArmIK.Get("l");
                    Quaternion want = Quaternion.AngleAxis(angles[i], Vector3.forward) * c2.BindShoulderRot;

                    Quaternion target;
                    ClumsyArmIK.WorldToJoint(c2.Shoulder, want, out target);
                    c2.Shoulder.Joint.targetRotation = target;
                    for (int k = 0; k < 80; k++)
                        Physics.Simulate(Dt);
                    Physics.SyncTransforms();

                    float err = Quaternion.Angle(c2.Shoulder.Bone.rotation, want);
                    if (err > worst)
                        worst = err;
                    sb.AppendLine("   期望上骨绕世界 Z 转 " + angles[i].ToString("F1") + "°  →  实测误差 "
                        + err.ToString("F3") + "°");
                    TearDown(s2);
                }
                sb.AppendLine("   最大误差 = " + worst.ToString("F3") + "°  "
                    + (worst < 0.05f ? "OK（与隔离实验一致：换算式是精确的）"
                                     : "**偏大，换算或弹簧有问题**"));

                sb.AppendLine("-- ② 够得着吗：把目标放在可达球面附近，量手到目标 --");
                Vector3[] probes =
                {
                    new Vector3(0.00f, 1.00f, 0.30f),    // 正前、齐胸
                    new Vector3(-0.30f, 0.85f, 0.30f),   // 前偏下
                    new Vector3(-0.45f, 0.30f, 0.25f),   // 低处（接近地面）
                    new Vector3(0.00f, 1.60f, 0.10f),    // 头顶上方（一定够不着）
                };
                for (int i = 0; i < probes.Length; i++)
                {
                    Sim s3 = BuildArmedSim(recipe, false, true);
                    s3.ArmIK.SetTarget("l", probes[i], false, Vector3.back, 1f);
                    for (int k = 0; k < 160; k++)
                    {
                        _ragStep.Invoke(s3.Rag, null);
                        _poseStep.Invoke(s3.Pose, null);
                        Physics.Simulate(Dt);
                    }
                    Physics.SyncTransforms();
                    ArmIKChain c3 = s3.ArmIK.Get("l");
                    sb.AppendLine("   目标 " + probes[i].ToString("F3")
                        + "  实际肩 " + c3.ShoulderWorld.ToString("F3")
                        + "  可达比 " + c3.ReachRatio.ToString("F2")
                        + "  手误差 " + c3.HandErrorRaw.ToString("F3") + "m"
                        + "  权重 " + c3.Weight.ToString("F2"));
                    TearDown(s3);
                }
                sb.AppendLine("   判据：可达比 ≤1 的那几行手误差应接近 0；可达比 >1 的那行会被夹到可达面上（误差大是应该的）。");
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        [MenuItem("笨拙布娃娃/诊断/I 环境交互端到端", false, 28)]
        public static void MenuCarry()
        {
            Debug.Log(CarryReport());
        }

        /// <summary>手部相关的全栈搭建：布娃娃 + 两骨 IK + 两只抓取器 + 环境交互状态机。</summary>
        public static Sim BuildArmedSim(RagdollRecipe recipe, bool withController, bool withPose)
        {
            Sim sim = BuildSim(recipe, withController, withPose);

            sim.ArmIK = sim.Model.AddComponent<ClumsyArmIK>();
            sim.ArmIK.Setup(sim.Rag, recipe);
            if (sim.Pose != null)
                sim.Pose.ArmIK = sim.ArmIK;

            sim.LeftGrip = AttachGripper(sim, recipe, sim.Rag.Find("handl"), "l", 0, 0);
            sim.RightGrip = AttachGripper(sim, recipe, sim.Rag.Find("handr"), "r", 1, 1);

            sim.Interact = sim.Model.AddComponent<ClumsyInteraction>();
            sim.Interact.Setup(sim.Rag, recipe, sim.ArmIK, sim.LeftGrip, sim.RightGrip);

            // ★ 2026-10-02 双手搬运（与 ClumsyRagdollGame.Build 同一套接线）
            sim.Carry = sim.Model.AddComponent<ClumsyCarry>();
            sim.Carry.Setup(sim.Rag, recipe, sim.ArmIK, sim.LeftGrip, sim.RightGrip, sim.Interact);
            if (sim.Ctrl != null)
                sim.Ctrl.Carry = sim.Carry;

            // 动画身体 + 笨拙层（与 ClumsyRagdollGame.Build 同一套接线）
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RaccoonSkeleton.ModelPath);
            // 优先用 Blender 手 K 的那套（9 段，含基准帧）；找不到才回落到旧的烘出来的三段。
            RuntimeAnimatorController ac = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                ClumsyAnimationBaker.BlenderControllerPath);
            if (ac == null)
                ac = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                    ClumsyAnimationBaker.ControllerPath);
            sim.Anim = sim.Model.AddComponent<ClumsyAnimationSource>();
            sim.Anim.Setup(sim.Rag, recipe, sim.Ctrl, prefab, ac);
            sim.Wob = sim.Model.AddComponent<ClumsyWobble>();
            sim.Wob.Setup(sim.Rag, recipe);
            if (sim.Pose != null)
            {
                sim.Pose.Animation = sim.Anim;
                sim.Pose.Wobble = sim.Wob;
            }

            Physics.SyncTransforms();
            return sim;
        }

        public static HandGripper AttachGripper(Sim sim, RagdollRecipe recipe, ClumsyPart hand, string suffix,
                                         int index, int button)
        {
            if (hand == null || hand.Bone == null)
                return null;
            HandGripper g = hand.Bone.gameObject.AddComponent<HandGripper>();
            g.Setup(sim.Rag, recipe, sim.ArmIK, suffix, index, button);
            g.ScriptedInput = true;   // 诊断台不读鼠标，GripHeld 由测试代码直接写
            return g;
        }

        /// <summary>连步 N 帧（与 Run 同一套调用顺序：Rag → Ctrl → 交互 → 姿势 → Simulate）。</summary>
        public static void StepSim(Sim sim, int steps, Vector2 move)
        {
            for (int i = 0; i < steps; i++)
                if (_turnRate != 0f && sim.Rag != null && sim.Rag.Root != null)
                    sim.Rag.Root.rotation = Quaternion.Euler(0f, sim.Rag.Root.eulerAngles.y + _turnRate * Dt, 0f);
            {
                if (sim.Ctrl != null)
                {
                    sim.Ctrl.Move = move;
                    sim.Ctrl.Sprint = false;
                }
                if (sim.Rag != null)
                    _ragStep.Invoke(sim.Rag, null);
                if (sim.Ctrl != null)
                    _ctrlStep.Invoke(sim.Ctrl, null);
                if (sim.Carry != null)
                    _carryStep.Invoke(sim.Carry, null);
                if (sim.Interact != null)
                    _interactStep.Invoke(sim.Interact, null);
                if (sim.Balance != null)
                    _balanceStep.Invoke(sim.Balance, null);
                if (sim.Pose != null)
                    _poseStep.Invoke(sim.Pose, null);
                Physics.Simulate(Dt);
            }
            Physics.SyncTransforms();
        }


        // ==================================================================
        // ★ 2026-10-02 · 双手搬运（像《人类一败涂地》那样抱东西）
        //   这一轮加的东西是 `ClumsyCarry`（双手抱住 + 重量反馈）。它要回答的四个问题：
        //     ① 两只手都按住时，**抱得住吗**（状态机能不能进 Holding）；
        //     ② 抱住之后物体**待在携带位上吗**（前后/上下/侧向误差 + 歪角）；
        //     ③ **重量压在身体上了吗** —— 抱着走时的速度、步幅、骨盆高度 vs 空手；
        //     ④ 松手 = 放下 / 走动着松手 = 丢出去。
        //   四档对照（空手 / 2kg / 6kg / 14kg），箱子尺寸固定 0.30m ⇒ 只让**质量**变，
        //   这样量到的差异就只能是重量反馈，不掺「箱子太大够不着」。
        // ==================================================================

        sealed class CarryTrial
        {
            public float Mass;
            public bool Holding;
            public int EnterCount;
            public string Transitions = "";
            public float WalkSeconds;
            public float WalkDistance;
            public float WalkSpeed;
            public float PelvisY;
            public float PelvisMin;
            public float SlotFwd;
            public float SlotUp;
            public float SlotLat;
            public float Tilt;
            public float HandL;
            public float HandR;
            public float ClampCm;
            public float BoxSpeedAfterDrop;
            public bool DroppedBack;
            public float ContactShare;      // 抱着走时两只脚沾地的比例
        }

        /// <summary>跑一次「双手搬运」试验：够过去 → 抱住 → 抱着走 → 松手。</summary>
        static CarryTrial CarryRun(RagdollRecipe rr, float mass, float boxSize, float walkSeconds)
        {
            CarryTrial t = new CarryTrial();
            t.Mass = mass;
            Sim sim = BuildArmedSim(rr, true, true);
            try
            {
                // ★ 先让角色**站定 1s**，再按站定后的骨盆摆箱子。
                //   实测：出生那 0.2s 里骨盆会往后坐 18cm（0.28 → 0.099），
                //   按出生位置摆的箱子于是落在臂长之外（0.52m > 0.478m）⇒ 两只手永远抓不到，
                //   而且**时好时坏**（取决于那一下坐得多远）—— 这种«偶发失败»最难查。
                for (int w = 0; w < Mathf.RoundToInt(1f / Dt); w++)
                    StepSim(sim, 1, Vector2.zero);

                GameObject box = null;
                GameObject ped = null;
                if (mass > 0f)
                {
                    Vector3 pelvis0 = sim.Rag.Hips.Body.position;
                    // ★ 摆位必须是**够得着**的：0.42m 前 / +0.02 高时，箱子两肋到肩的距离实测 0.53m
                    //   > 臂长 0.478m ⇒ 两只手都抓不到，搬运时好时坏（时好时坏才是最难查的）。
                    //   真实流程是「人走到够得着的地方再抓」，诊断台里就把箱子摆在那个距离上。
                    Vector3 want = pelvis0 + new Vector3(0f, 0.06f, 0.28f);

                    ped = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    ped.name = "台子";
                    ped.transform.SetParent(sim.World.transform, true);
                    ped.transform.localScale = new Vector3(0.6f, 0.5f, 0.6f);
                    ped.transform.position = new Vector3(want.x, want.y - boxSize * 0.5f - 0.25f, want.z);

                    box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.name = "箱子";
                    box.transform.SetParent(sim.World.transform, true);
                    box.transform.localScale = Vector3.one * boxSize;
                    box.transform.position = want;
                    Rigidbody brb = box.AddComponent<Rigidbody>();
                    brb.mass = mass;
                    Grippable g = box.AddComponent<Grippable>();
                    g.UseCustomMotions = true;
                    g.Motions = JointMotionsConfig.Make(ConfigurableJointMotion.Limited, 38f);
                    Physics.SyncTransforms();
                }

                // 两只手都按住抓取键 —— 这正是「双手搬运」的输入。
                bool grip = mass > 0f;
                sim.LeftGrip.ScriptedInput = true;
                sim.RightGrip.ScriptedInput = true;
                sim.LeftGrip.GripHeld = grip;
                sim.RightGrip.GripHeld = grip;

                // ---- 阶段 1：伸手抱住（2.5s）----
                string last = "";
                int n1 = Mathf.RoundToInt(2.5f / Dt);
                for (int i = 0; i < n1; i++)
                {
                    StepSim(sim, 1, Vector2.zero);
                    if (sim.Carry != null && sim.Carry.LastTransition != last)
                    {
                        last = sim.Carry.LastTransition;
                        if (last.Length > 0)
                            t.Transitions += "      " + (i * Dt).ToString("F2") + "s  " + last + "\n";
                    }
                }
                t.Holding = sim.Carry != null && sim.Carry.IsHolding;
                t.EnterCount = sim.Carry != null ? sim.Carry.EnterCount : 0;

                // ★ 台子**必须搬走**再走：不然抱着走的时候箱子被台子挡住。
                //   实测（第一版没搬）4s 只走了 0.1m、速度 0.03 m/s ——
                //   读数看着像「抱着东西走不动」，其实是被搬的箱子顶在台子上。
                if (ped != null)
                {
                    UnityEngine.Object.DestroyImmediate(ped);
                    ped = null;
                    Physics.SyncTransforms();
                }

                // ---- 阶段 2：抱着走 4s ----
                Vector3 p0 = sim.Rag.PelvisPosition;
                Vector3 b0 = box != null ? box.transform.position : Vector3.zero;
                float ySum = 0f, yMin = float.MaxValue;
                int samples = 0, contact = 0, frames = 0;
                float eps = 0.012f;
                ClumsyPart fl = sim.Rag.Find("footl");
                ClumsyPart fr = sim.Rag.Find("footr");
                int n2 = Mathf.RoundToInt(walkSeconds / Dt);
                for (int i = 0; i < n2; i++)
                {
                    StepSim(sim, 1, new Vector2(0f, 1f));
                    frames++;
                    float y = sim.Rag.PelvisPosition.y;
                    ySum += y; samples++;
                    if (y < yMin) yMin = y;
                    if (fl != null && fl.Shape != null && fl.Shape.bounds.min.y <= eps) contact++;
                    if (fr != null && fr.Shape != null && fr.Shape.bounds.min.y <= eps) contact++;
                }
                t.WalkSeconds = walkSeconds;
                Vector3 d = sim.Rag.PelvisPosition - p0;
                d.y = 0f;
                t.WalkDistance = d.magnitude;
                t.WalkSpeed = t.WalkDistance / walkSeconds;
                t.PelvisY = samples > 0 ? ySum / samples : 0f;
                t.PelvisMin = yMin;
                t.ContactShare = frames > 0 ? contact * 100f / (frames * 2f) : 0f;

                if (t.Holding && sim.Carry != null)
                {
                    t.SlotFwd = sim.Carry.SlotErrorForward;
                    t.SlotUp = sim.Carry.SlotErrorUp;
                    t.SlotLat = sim.Carry.SlotErrorLateral;
                    t.Tilt = sim.Carry.BoxTiltDegrees;
                    t.HandL = sim.Carry.HandErrorLeft;
                    t.HandR = sim.Carry.HandErrorRight;
                    t.ClampCm = sim.Carry.SlotClampDistance * 100f;
                }

                // ---- 阶段 3：松手 ----
                bool moving = true;
                sim.LeftGrip.GripHeld = false;
                sim.RightGrip.GripHeld = false;
                for (int i = 0; i < Mathf.RoundToInt(1.2f / Dt); i++)
                {
                    StepSim(sim, 1, moving ? new Vector2(0f, 1f) : Vector2.zero);
                    if (i == 1 && box != null)
                    {
                        Vector3 v = box.GetComponent<Rigidbody>().linearVelocity;
                        t.BoxSpeedAfterDrop = new Vector2(v.x, v.z).magnitude;
                    }
                    if (i > 4) moving = false;   // 松手之后就停下，别追着箱子走
                }
                t.DroppedBack = sim.Carry != null && !sim.Carry.IsHolding && sim.Carry.State == ECarryState.Idle;
            }
            finally
            {
                TearDown(sim);
            }
            return t;
        }

        public static string CarryTwoHandedReport()
        {
            return CarryTwoHandedReport(SceneRecipe());
        }

        public static string CarryTwoHandedReport(RagdollRecipe rr)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== ★ 双手搬运（HFF 式抱东西）：够过去 → 抱住 → 抱着走 → 松手 ==");
            sb.AppendLine("配方 = **场景里那份**（SceneRecipe）· UseCarry=" + rr.UseCarry
                + " · 箱子一律 0.30m，只变质量");
            sb.AppendLine("携带位：骨盆前 " + rr.CarrySlotForward.ToString("F2") + "m + 半深×"
                + rr.CarrySlotDepthScale.ToString("F2") + " · 高 +" + rr.CarrySlotUp.ToString("F2")
                + "m · 手离侧面 " + rr.CarryGripStandoff.ToString("F2") + "m");
            sb.AppendLine("抱持：软弹簧 " + rr.CarryHoldSpringPerKg.ToString("F0") + " N/m·kg（阻尼 ×"
                + rr.CarryHoldDamperRatio.ToString("F2") + "）· 手臂加硬 ×" + rr.CarryArmStiffnessScale.ToString("F1")
                + " · 角阻尼 " + rr.CarryAngularDamping.ToString("F0"));
            sb.AppendLine("重量反馈：骨盆下沉 " + rr.CarryLoadSagPerKg.ToString("F3") + " m/kg（封顶 "
                + rr.CarryLoadSagMax.ToString("F2") + "m）· 速度/步幅折减 1/(1+" + rr.CarryLoadSpeedPenalty.ToString("F2")
                + "×载重比)，下限 " + rr.CarryLoadMinSpeedScale.ToString("F2"));
            sb.AppendLine();
            try
            {
                CarryTrial none = CarryRun(rr, 0f, 0.30f, 4f);
                sb.AppendLine("-- 空手基线（不抓东西，只走 4s）--");
                sb.AppendLine("   走 " + none.WalkDistance.ToString("F2") + "m ⇒ " + none.WalkSpeed.ToString("F2")
                    + " m/s   骨盆 y 均 " + none.PelvisY.ToString("F3") + " / 最低 " + none.PelvisMin.ToString("F3")
                    + "   沾地 " + none.ContactShare.ToString("F0") + "%");
                sb.AppendLine();

                float[] masses = { 2f, 6f, 14f };
                for (int i = 0; i < masses.Length; i++)
                {
                    CarryTrial t = CarryRun(rr, masses[i], 0.30f, 4f);
                    sb.AppendLine("-- 抱 " + masses[i].ToString("F0") + " kg --");
                    if (!t.Holding)
                    {
                        sb.AppendLine("   ★ **没抱住**（进入次数 " + t.EnterCount + "）");
                        if (t.Transitions.Length > 0)
                            sb.Append(t.Transitions);
                        continue;
                    }
                    if (t.Transitions.Length > 0)
                        sb.Append(t.Transitions);
                    sb.AppendLine("   抱住了（进入 " + t.EnterCount + " 次）");
                    sb.AppendLine(string.Format("   携带位误差 前 {0,6:F1} / 上 {1,6:F1} / 侧 {2,6:F1} cm   歪角 {3,5:F1}°   手误差 {4:F1}/{5:F1} cm{6}",
                        t.SlotFwd * 100f, t.SlotUp * 100f, t.SlotLat * 100f, t.Tilt,
                        t.HandL * 100f, t.HandR * 100f,
                        t.ClampCm > 0.5f ? "   抱近 " + t.ClampCm.ToString("F1") + "cm" : ""));
                    sb.AppendLine(string.Format("   抱着走 {0:F1}m ⇒ {1:F2} m/s（空手 {2:F2}，**{3:F0}%**）   骨盆 y 均 {4:F3}（空手 {5:F3} ⇒ **下沉 {6:F1} cm**）   沾地 {7:F0}%",
                        t.WalkDistance, t.WalkSpeed, none.WalkSpeed,
                        none.WalkSpeed > 0.01f ? t.WalkSpeed / none.WalkSpeed * 100f : 0f,
                        t.PelvisY, none.PelvisY, (none.PelvisY - t.PelvisY) * 100f, t.ContactShare));
                    sb.AppendLine("   松手：物体水平速度 " + t.BoxSpeedAfterDrop.ToString("F2")
                        + " m/s   回空闲 = " + (t.DroppedBack ? "是" : "**否**"));
                    sb.AppendLine();
                }

                // 补一条「箱子太大」的：0.60m / 14kg 是场景里那三只箱子中最大的那只。
                CarryTrial big = CarryRun(rr, 14f, 0.60f, 2f);
                sb.AppendLine("-- 0.60m / 14kg（场景里最大的那只箱子）--");
                sb.AppendLine("   抱住 = " + (big.Holding ? "是" : "否")
                    + (big.Holding ? "   携带位误差 前 " + (big.SlotFwd * 100f).ToString("F1")
                        + " / 上 " + (big.SlotUp * 100f).ToString("F1")
                        + " cm   歪 " + big.Tilt.ToString("F1") + "°   抱近 " + big.ClampCm.ToString("F1") + "cm"
                        : ""));
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }


        // ==================================================================
        // ★ 2026-10-02 · 搬运的**手感质量**（owner 报的三条：抓不抓得住看不出来 /
        //   抱住之后剧烈抖动 / 手臂·胸口与物体穿模）
        //
        // 量四件事，都是可复跑的数：
        //   ① **抖动**：每个物理步的 |Δv|（= 加速度×dt）的均值 / 最大值。
        //      静止站着应该 ≈ 0；自由落体是 g·dt = 0.196。抖动的本质就是«每步被推一下»。
        //      ⚠️ 必须同时量**不抱东西的对照组** —— 否则分不清哪些抖动是搬运带来的、
        //      哪些是这只浣熊本来就有的（手 IK / 笨拙层 / 托举层）。
        //   ② **箱速**：静止抱着时箱子还在以多快移动（0.5 m/s = 肉眼可见的«抖»）。
        //   ③ **穿模**：物体 × 角色各部件用 `Physics.ComputePenetration` 量**真实交叠深度**。
        //      （抱住之后「物体↔身体」的碰撞是关掉的，所以这量的正是«看起来陷进去多少»。）
        //   ④ **抓取确认**：手掌离物体表面多远（>几厘米就是«看不住抓没抓住»）。
        // ==================================================================

        sealed class CarryQuality
        {
            public string Tag = "";
            public bool Holding;
            public float BoxJerk, BoxJerkMax, BoxSpeed;
            public float HipsJerk, HandJerk;
            public float HandErrL, HandErrR;
            public float PenetrationMax, PenetrationSum;
            public string PenetrationList = "";
        }

        /// <summary>跑一次质量测量。<paramref name="mass"/> ≤ 0 = 不抱东西（对照组）。</summary>
        static CarryQuality CarryQualityRun(RagdollRecipe rr, float mass, float boxSize, string tag,
                                            float settleSeconds)
        {
            CarryQuality q = new CarryQuality();
            q.Tag = tag;
            Sim sim = BuildArmedSim(rr, true, true);
            try
            {
                for (int w = 0; w < Mathf.RoundToInt(1f / Dt); w++)
                    StepSim(sim, 1, Vector2.zero);

                GameObject box = null;
                GameObject ped = null;
                if (mass > 0f)
                {
                    Vector3 pelvis0 = sim.Rag.Hips.Body.position;
                    Vector3 want = pelvis0 + new Vector3(0f, 0.06f, 0.28f);
                    ped = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    ped.transform.SetParent(sim.World.transform, true);
                    ped.transform.localScale = new Vector3(0.6f, 0.5f, 0.6f);
                    ped.transform.position = new Vector3(want.x, want.y - boxSize * 0.5f - 0.25f, want.z);

                    box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.name = "箱子";
                    box.transform.SetParent(sim.World.transform, true);
                    box.transform.localScale = Vector3.one * boxSize;
                    box.transform.position = want;
                    Rigidbody brb0 = box.AddComponent<Rigidbody>();
                    brb0.mass = mass;
                    Grippable g = box.AddComponent<Grippable>();
                    g.UseCustomMotions = true;
                    g.Motions = JointMotionsConfig.Make(ConfigurableJointMotion.Limited, 38f);
                    Physics.SyncTransforms();
                }

                sim.LeftGrip.ScriptedInput = true;
                sim.RightGrip.ScriptedInput = true;
                sim.LeftGrip.GripHeld = mass > 0f;
                sim.RightGrip.GripHeld = mass > 0f;

                for (int i = 0; i < Mathf.RoundToInt(2.5f / Dt); i++)
                    StepSim(sim, 1, Vector2.zero);
                q.Holding = sim.Carry != null && sim.Carry.IsHolding;

                if (ped != null) { UnityEngine.Object.DestroyImmediate(ped); Physics.SyncTransforms(); }
                for (int i = 0; i < Mathf.RoundToInt(settleSeconds / Dt); i++)
                    StepSim(sim, 1, Vector2.zero);

                Rigidbody brb = box != null ? box.GetComponent<Rigidbody>() : null;
                Vector3 bv = brb != null ? brb.linearVelocity : Vector3.zero;
                Vector3 hv = sim.Rag.Hips.Body.linearVelocity;
                Vector3 lv = sim.LeftGrip.HandBody.linearVelocity, rv = sim.RightGrip.HandBody.linearVelocity;
                float sumB = 0f, maxB = 0f, sumH = 0f, sumHand = 0f, sumSpeed = 0f;
                int n = Mathf.RoundToInt(2f / Dt);
                for (int i = 0; i < n; i++)
                {
                    StepSim(sim, 1, Vector2.zero);
                    Vector3 h2 = sim.Rag.Hips.Body.linearVelocity;
                    Vector3 l2 = sim.LeftGrip.HandBody.linearVelocity, r2 = sim.RightGrip.HandBody.linearVelocity;
                    sumH += (h2 - hv).magnitude;
                    sumHand += ((l2 - lv).magnitude + (r2 - rv).magnitude) * 0.5f;
                    hv = h2; lv = l2; rv = r2;
                    if (brb != null)
                    {
                        Vector3 b2 = brb.linearVelocity;
                        float db = (b2 - bv).magnitude;
                        sumB += db;
                        if (db > maxB) maxB = db;
                        sumSpeed += new Vector2(b2.x, b2.z).magnitude;
                        bv = b2;
                    }
                }
                q.BoxJerk = brb != null ? sumB / n : 0f;
                q.BoxJerkMax = maxB;
                q.HipsJerk = sumH / n;
                q.HandJerk = sumHand / n;
                q.BoxSpeed = brb != null ? sumSpeed / n : 0f;
                if (sim.Carry != null)
                {
                    q.HandErrL = sim.Carry.HandErrorLeft;
                    q.HandErrR = sim.Carry.HandErrorRight;
                }

                if (box != null)
                {
                    Collider boxCol = box.GetComponent<Collider>();
                    Vector3 bp = boxCol.transform.position;
                    Quaternion br = boxCol.transform.rotation;
                    var list = new StringBuilder();
                    for (int i = 0; i < sim.Rag.Parts.Count; i++)
                    {
                        Collider pc = sim.Rag.Parts[i].Shape;
                        if (pc == null) continue;
                        Vector3 dir; float dist;
                        if (Physics.ComputePenetration(pc, pc.transform.position, pc.transform.rotation,
                                                       boxCol, bp, br, out dir, out dist) && dist > 0.005f)
                        {
                            q.PenetrationSum += dist;
                            if (dist > q.PenetrationMax) q.PenetrationMax = dist;
                            list.Append(sim.Rag.Parts[i].Spec.Key + " " + (dist * 100f).ToString("F1") + "  ");
                        }
                    }
                    q.PenetrationList = list.ToString().Trim();
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return q;
        }

        public static string CarryQualityReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== ★ 搬运质量：抱住之后静止 2s（6kg / 0.30m 箱子；先 1.5s 稳下来）==");
            sb.AppendLine("抖动 = 每个物理步的 |Δv| 均值（自由落体 = g·dt = 0.196；静止应 ≈ 0）");
            sb.AppendLine("穿模 = 物体与各部件的真实交叠深度（>5mm 才计）");
            sb.AppendLine();
            try
            {
                // 对照组：不抱东西
                CarryQuality ctrl = CarryQualityRun(SceneRecipe(), 0f, 0.30f, "对照·不抱东西", 1.5f);
                sb.AppendLine(string.Format("对照（不抱东西）：骨盆抖动 {0:F4}  手抖动 {1:F4}",
                    ctrl.HipsJerk, ctrl.HandJerk));
                sb.AppendLine();

                // 名字 | 手碰撞忽略 | 角弹簧/kg | 半深比例 | 线弹簧/kg | 线阻尼 | 角阻尼 | 前(m) | 高(m) | 手臂加硬 | 躯干加硬 | 求解迭代
                var cases = new System.Collections.Generic.List<string[]>();
                cases.Add(new string[] { "A 现状(手臂×14 躯干×5)", "true", "15", "1.0", "300", "0.5", "0.5", "0.17", "0.28", "14", "5", "0" });
                cases.Add(new string[] { "B 手臂不硬化(×1)", "true", "15", "1.0", "300", "0.5", "0.5", "0.17", "0.28", "1", "5", "0" });
                cases.Add(new string[] { "C B+躯干也不硬化", "true", "15", "1.0", "300", "0.5", "0.5", "0.17", "0.28", "1", "1", "0" });
                cases.Add(new string[] { "D C+求解迭代×3", "true", "15", "1.0", "300", "0.5", "0.5", "0.17", "0.28", "1", "1", "x3" });
                cases.Add(new string[] { "E C+手臂×3 躯干×2", "true", "15", "1.0", "300", "0.5", "0.5", "0.17", "0.28", "3", "2", "x3" });
                cases.Add(new string[] { "F E+硬线弹簧900/阻尼0.15", "true", "15", "1.0", "900", "0.15", "0.5", "0.17", "0.28", "3", "2", "x3" });

                sb.AppendLine(string.Format("{0,-26} {1,-5} {2,-9} {3,-9} {4,-9} {5,-8} {6,-8} {7,-7} {8}",
                    "配置", "抱住", "箱子抖动", "最大", "骨盆抖动", "手抖动", "箱速", "穿模", "手误差L/R"));
                foreach (string[] c in cases)
                {
                    RagdollRecipe rr = SceneRecipe();
                    rr.CarryIgnoreHandCollision = bool.Parse(c[1]);
                    rr.CarryAngularSpringPerKg = float.Parse(c[2]);
                    rr.CarrySlotDepthScale = float.Parse(c[3]);
                    rr.CarryChestSpringPerKg = float.Parse(c[4]);
                    rr.CarryChestDamperRatio = float.Parse(c[5]);
                    rr.CarryAngularDamperRatio = float.Parse(c[6]);
                    rr.CarrySlotForward = float.Parse(c[7]);
                    rr.CarrySlotUp = float.Parse(c[8]);
                    rr.CarryArmStiffnessScale = float.Parse(c[9]);
                    rr.CarryTorsoStiffnessScale = float.Parse(c[10]);
                    int si = rr.SolverIterations;
                    if (c[11] == "x3") { rr.SolverIterations = si * 3; rr.SolverVelocityIterations = rr.SolverVelocityIterations * 3; }
                    CarryQuality q = CarryQualityRun(rr, 6f, 0.30f, c[0], 1.5f);
                    sb.AppendLine(string.Format("{0,-26} {1,-5} {2,-9} {3,-9} {4,-9} {5,-8} {6,-8} {7,-7} {8}",
                        q.Tag, q.Holding ? "是" : "**否**",
                        q.BoxJerk.ToString("F4"), q.BoxJerkMax.ToString("F4"),
                        q.HipsJerk.ToString("F4"), q.HandJerk.ToString("F4"),
                        q.BoxSpeed.ToString("F3"),
                        (q.PenetrationMax * 100f).ToString("F1") + "cm",
                        (q.HandErrL * 100f).ToString("F0") + "/" + (q.HandErrR * 100f).ToString("F0") + "cm"));
                    if (q.PenetrationList.Length > 0)
                        sb.AppendLine("        穿模清单：" + q.PenetrationList);
                }
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string GaitReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 步态：前进 / 后退 / 原地转身（各 6s，默认配方）==");
            try
            {
                string[] names = { "前进 W", "后退 S", "原地转身（不改输入）" };
                Vector2[] moves = { new Vector2(0f, 1f), new Vector2(0f, -1f), new Vector2(0f, 0f) };
                float[] turns = { 0f, 0f, 120f };
                for (int i = 0; i < 3; i++)
                {
                    _turnRate = turns[i];
                    RagdollRecipe rr = new RagdollRecipe();
                    Sim sim = BuildSim(rr, true, true);
                    Trace tr = Run(sim, 6f, 0f, Vector3.zero, moves[i], false);
                    sb.AppendLine(Row(names[i], tr, sim));
                    if (i == 2)
                    {
                        // 转身踏步：看两条大腿有没有真的在交替外摆/抬腿
                        ClumsyPart ul = sim.Rag.Find("uplegl");
                        ClumsyPart ur = sim.Rag.Find("uplegr");
                        sb.AppendLine(string.Format("      左大腿 targetRotation={0}  右大腿 targetRotation={1}",
                            ul.Joint.targetRotation.eulerAngles.ToString("F1"),
                            ur.Joint.targetRotation.eulerAngles.ToString("F1")));
                    }
                    TearDown(sim);
                }
                _turnRate = 0f;

                // 手臂休息姿势：静止 3s 后两只手应该比肩低（不再是 T-pose）
                sb.AppendLine("-- 手臂休息姿势（静止 3s 后）--");
                RagdollRecipe r2 = new RagdollRecipe();
                Sim s2 = BuildSim(r2, true, true);
                Run(s2, 3f, 0f, Vector3.zero, Vector2.zero, false);
                Physics.SyncTransforms();
                string[] pairs = { "shoulderl", "arml", "forearml", "handl", "shoulderr", "armr", "forearmr", "handr" };
                for (int i = 0; i < pairs.Length; i++)
                {
                    ClumsyPart p = s2.Rag.Find(pairs[i]);
                    if (p == null) continue;
                    Vector3 bone = p.Bone.rotation * Vector3.up;   // 骨头的生长方向
                    sb.AppendLine(string.Format("      {0,-11} 骨头指向={1}  与世界垂直的夹角={2,5:F1}°  手高={3:F3}",
                        pairs[i], bone.ToString("F2"), Vector3.Angle(bone, Vector3.down), p.Bone.position.y));
                }
                TearDown(s2);
            }
            finally
            {
                _turnRate = 0f;
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string TuningReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("############ 调参报告 ############");
            try
            {
                sb.AppendLine("-- 1) 求解器迭代扫描（margin 8 固定，静止 8s）--");
                int[] iters = { 16, 32, 64 };
                int[] vels = { 6, 12, 16 };
                for (int i = 0; i < iters.Length; i++)
                {
                    RagdollRecipe rr = new RagdollRecipe();
                    rr.HipsSpringMargin = 8f;
                    rr.SolverIterations = iters[i];
                    rr.SolverVelocityIterations = vels[i];
                    Sim s1 = BuildSim(rr, true, false);
                    Trace t1 = Run(s1, 8f, 0f, Vector3.zero, Vector2.zero, false);
                    sb.AppendLine(Row(string.Format("求解器 {0}/{1}", iters[i], vels[i]), t1, s1));
                    TearDown(s1);
                }

                sb.AppendLine("-- 2) 弹簧倍数（求解器 64/16，静止 8s）--");
                float[] margins = { 6f, 8f, 11f, 14f };
                for (int i = 0; i < margins.Length; i++)
                {
                    RagdollRecipe rr = new RagdollRecipe();
                    rr.HipsSpringMargin = margins[i];
                    Sim s2 = BuildSim(rr, true, false);
                    Trace t2 = Run(s2, 8f, 0f, Vector3.zero, Vector2.zero, false);
                    sb.AppendLine(Row(string.Format("margin {0:F0} (hips{1:F0} 腿{2:F0} 上身{3:F0})",
                        margins[i], s2.Rag.HipsSpring, s2.Rag.LimbSpring, s2.Rag.UpperSpring), t2, s2));
                    TearDown(s2);
                }

                sb.AppendLine("-- 3) 推倒回弹（求解器 64/16，t=1.5s 推 300 N·s）--");
                float[] pushMargins = { 8f, 11f };
                for (int i = 0; i < pushMargins.Length; i++)
                {
                    RagdollRecipe rr = new RagdollRecipe();
                    rr.HipsSpringMargin = pushMargins[i];
                    Sim s3 = BuildSim(rr, true, false);
                    Trace t3 = Run(s3, 10f, 1.5f, Vector3.forward * 300f, Vector2.zero, false);
                    sb.AppendLine(Row(string.Format("margin {0:F0} 推 300N·s", pushMargins[i]), t3, s3));
                    sb.AppendLine(string.Format("      末 up {0:F3}  末 y {1:F3}（出生 {2:F3}）", t3.FinalUp, t3.FinalY, s3.RestHipY));
                    TearDown(s3);
                }

                sb.AppendLine("-- 4) 走起来（margin 11，按住 W 6s）--");
                bool[] poseOn = { false, true };
                for (int i = 0; i < poseOn.Length; i++)
                {
                    RagdollRecipe rr = new RagdollRecipe();
                    rr.HipsSpringMargin = 11f;
                    rr.UsePoseDriver = poseOn[i];
                    Sim s4 = BuildSim(rr, true, true);
                    Trace t4 = Run(s4, 6f, 0f, Vector3.zero, new Vector2(0f, 1f), false);
                    sb.AppendLine(Row(poseOn[i] ? "姿势驱动 开" : "姿势驱动 关", t4, s4));
                    TearDown(s4);
                }
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string PoseDumpReport()
        {
            return PoseDumpReport(14f);
        }

        public static string PoseDumpReport(float margin)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                RagdollRecipe recipe = new RagdollRecipe();
                recipe.HipsSpringMargin = margin;
                Sim sim = BuildSim(recipe, true, false);
                Run(sim, 6f, 0f, Vector3.zero, Vector2.zero, false);
                Physics.SyncTransforms();

                sb.AppendLine(string.Format("== 站立构型快照（margin {0:F1}，t=6s）==", margin));
                sb.AppendLine(string.Format("骨盆 y {0:F3}（出生 {1:F3}） up {2:F3}  hips弹簧 {3:F0} 四肢弹簧 {4:F0}",
                    sim.Rag.PelvisPosition.y, sim.RestHipY, sim.Rag.Upright, sim.Rag.HipsSpring, sim.Rag.LimbSpring));
                sb.AppendLine(string.Format("      restY  nowY    Δy     偏离出生角  碰撞体最低y  碰撞体最低y-出生"));
                for (int i = 0; i < sim.Rag.Parts.Count; i++)
                {
                    ClumsyPart p = sim.Rag.Parts[i];
                    float dev = Quaternion.Angle(p.RestRotation, p.Body.rotation);
                    float bottom = p.Shape != null ? p.Shape.bounds.min.y : 0f;
                    sb.AppendLine(string.Format("{0,-11} {1,6:F3} {2,6:F3} {3,6:F3}   {4,8:F1}   {5,9:F3}",
                        p.Spec.Key, p.RestPosition.y, p.Bone.position.y, p.Bone.position.y - p.RestPosition.y, dev, bottom));
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string JointAudit()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                RagdollRecipe recipe = new RagdollRecipe();
                Sim sim = BuildSim(recipe, true, false);
                sb.AppendLine("== 关节审计（建完之后原样读回来的值）==");
                sb.AppendLine(string.Format("hips 弹簧 {0:F0} · 四肢弹簧 {1:F0} · 出生骨盆 y {2:F3} · 地面接触点 y {3:F4}",
                    sim.Rag.HipsSpring, sim.Rag.LimbSpring, sim.RestHipY, sim.Rag.GroundY));
                sb.AppendLine("部件        弹簧   maxForce     damper   xMot    angX    父体        anchor");
                for (int i = 0; i < sim.Rag.Parts.Count; i++)
                {
                    ClumsyPart p = sim.Rag.Parts[i];
                    ConfigurableJoint j = p.Joint;
                    if (j == null)
                        continue;
                    JointDrive dx = j.angularXDrive;
                    sb.AppendLine(string.Format("{0,-11} {1,6:F0} {2,10:0.###E+0} {3,8:F1}  {4,-6} {5,-7} {6,-11} {7}",
                        p.Spec.Key, dx.positionSpring, dx.maximumForce, dx.positionDamper,
                        j.xMotion.ToString(), j.angularXMotion.ToString(),
                        j.connectedBody == null ? "(world)" : j.connectedBody.name,
                        j.anchor.ToString("F3")));
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string TraceReport()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                RagdollRecipe recipe = new RagdollRecipe();
                Sim sim = BuildSim(recipe, true, false);
                sb.AppendLine("== 逐帧（0.05s 一采样，共 1s）==");
                sb.AppendLine(string.Format("总质量 {0:F1}  质心高 {1:F3}  出生骨盆 y {2:F3}  hips弹簧 {3:F0}  四肢弹簧 {4:F0}",
                    sim.Rag.TotalMass, sim.Rag.CenterOfMassHeight, sim.RestHipY, sim.Rag.HipsSpring, sim.Rag.LimbSpring));
                sb.AppendLine("    t   骨盆y    up | 左髋角 膝角 | 左脚y  右脚y | 最低碰撞面y");

                ClumsyPart uplegL = sim.Rag.Find("uplegl");
                ClumsyPart legL = sim.Rag.Find("legl");
                ClumsyPart footL = sim.Rag.Find("footl");
                ClumsyPart footR = sim.Rag.Find("footr");

                for (int i = 0; i < 20; i++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        _ragStep.Invoke(sim.Rag, null);
                        if (sim.Ctrl != null) _ctrlStep.Invoke(sim.Ctrl, null);
                        if (sim.Pose != null) _poseStep.Invoke(sim.Pose, null);
                        Physics.Simulate(Dt);
                    }
                    Physics.SyncTransforms();

                    float minBottom = float.PositiveInfinity;
                    for (int k = 0; k < sim.Rag.Parts.Count; k++)
                    {
                        Collider c = sim.Rag.Parts[k].Shape;
                        if (c == null) continue;
                        if (c.bounds.min.y < minBottom) minBottom = c.bounds.min.y;
                    }

                    sb.AppendLine(string.Format("{0,5:F2} {1,7:F3} {2,6:F3} | {3,6:F1} {4,5:F1} | {5,7:F3} {6,7:F3} | {7,8:F4}",
                        (i + 1) * 0.05f,
                        sim.Rag.PelvisPosition.y,
                        sim.Rag.Upright,
                        JointAngle(uplegL),
                        JointAngle(legL),
                        footL != null ? footL.Bone.position.y : 0f,
                        footR != null ? footR.Bone.position.y : 0f,
                        minBottom));
                }
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>当前关节角（相对父刚体，度）。</summary>
        static float JointAngle(ClumsyPart part)
        {
            if (part == null || part.Body == null || part.Joint == null || part.Joint.connectedBody == null)
                return 0f;
            Quaternion local = Quaternion.Inverse(part.Joint.connectedBody.rotation) * part.Body.rotation;
            return Quaternion.Angle(Quaternion.identity, local);
        }

        public static string StandReport()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                RagdollRecipe recipe = new RagdollRecipe();
                Sim sim = BuildSim(recipe, true, true);
                sb.AppendLine("== A 静止站立（无输入，8s）==");
                sb.AppendLine(string.Format("总质量 {0:F1} kg · 质心高 {1:F3} m · 出生骨盆 y {2:F3} · M·g·h = {3:F0} N·m/rad",
                    sim.Rag.TotalMass, sim.Rag.CenterOfMassHeight, sim.RestHipY,
                    sim.Rag.TotalMass * Mathf.Abs(recipe.GravityY) * sim.Rag.CenterOfMassHeight));
                sb.AppendLine(string.Format("hips 弹簧 {0:F0} · 四肢弹簧 {1:F0} · 倍数 {2:F2}",
                    sim.Rag.HipsSpring, sim.Rag.LimbSpring, recipe.HipsSpringMargin));

                Trace t = Run(sim, 8f, 0f, Vector3.zero, Vector2.zero, false);
                sb.AppendLine(Row("静止站立", t, sim));
                TearDown(sim);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string SpringSweep()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== B hips 弹簧倍数扫描（无输入 8s）==");
            float[] margins = { 0.5f, 1f, 2f, 3f, 5f, 8f, 14f };
            try
            {
                for (int i = 0; i < margins.Length; i++)
                {
                    RagdollRecipe recipe = new RagdollRecipe();
                    recipe.HipsSpringMargin = margins[i];
                    Sim sim = BuildSim(recipe, true, true);
                    Trace t = Run(sim, 8f, 0f, Vector3.zero, Vector2.zero, false);
                    sb.AppendLine(Row(string.Format("margin {0,5:F2} (k={1,6:F0})", margins[i], sim.Rag.HipsSpring), t, sim));
                    TearDown(sim);
                }
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string PushReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== C 推倒回弹（t=1.5s 给整具 220 N·s 前推，第 6s 再推一次）==");
            try
            {
                RagdollRecipe recipe = new RagdollRecipe();
                Sim sim = BuildSim(recipe, true, true);
                Trace t = Run(sim, 10f, 1.5f, Vector3.forward * 220f, Vector2.zero, false);
                sb.AppendLine(Row("推一次", t, sim));
                sb.AppendLine(string.Format("  → 末 up {0:F3}（>0.85 = 自己弹回来了）  最长连续站立 {1:F2}s", t.FinalUp, t.Best));
                TearDown(sim);

                Sim sim2 = BuildSim(recipe, true, true);
                // 连续推：每 0.6s 一记，看它能不能一直扛住
                Trace acc = new Trace();
                acc.FirstFall = -1f;
                acc.MinUp = 1f;
                for (int k = 0; k < 12; k++)
                {
                    Trace part = Run(sim2, 0.6f, k == 0 ? 0.3f : 0f, Vector3.forward * 90f, Vector2.zero, false);
                    acc.MinUp = Mathf.Min(acc.MinUp, part.MinUp);
                    acc.FinalUp = part.FinalUp;
                    acc.FinalY = part.FinalY;
                    acc.HipAt1s = part.HipAt1s;
                    if (part.Best > acc.Best) acc.Best = part.Best;
                }
                sb.AppendLine(Row("连续推 12×90N·s", acc, sim2));
                TearDown(sim2);
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string WalkReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== D 走起来（按住 W，8s）==");
            try
            {
                bool[] poseOn = { false, true };
                for (int i = 0; i < poseOn.Length; i++)
                {
                    RagdollRecipe recipe = new RagdollRecipe();
                    recipe.UsePoseDriver = poseOn[i];
                    Sim sim = BuildSim(recipe, true, true);
                    Trace t = Run(sim, 8f, 0f, Vector3.zero, new Vector2(0f, 1f), false);
                    sb.AppendLine(Row(poseOn[i] ? "姿势驱动 开" : "姿势驱动 关（纯弹簧）", t, sim));
                    TearDown(sim);
                }
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        public static string LockPassengerReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== E 非旋转点角运动：锁死 vs 放开（教程 P5 步2 vs 本模型的膝关节）==");
            try
            {
                bool[] locked = { true, false };
                for (int i = 0; i < locked.Length; i++)
                {
                    RagdollRecipe recipe = new RagdollRecipe();
                    recipe.LockPassengerAngular = locked[i];
                    Sim sim = BuildSim(recipe, true, true);
                    Trace t = Run(sim, 8f, 0f, Vector3.zero, Vector2.zero, false);
                    sb.AppendLine(Row(locked[i] ? "锁死（照教程）" : "放开（留膝/肘）", t, sim));
                    TearDown(sim);
                }
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        [MenuItem("笨拙布娃娃/诊断/J 动画与笨拙度", false, 29)]
        public static void MenuAnimation()
        {
            Debug.Log(AnimationReport());
        }

        /// <summary>
        /// 动画片段 + 笨拙层的对照表。三组对比：
        ///   ① 程序化步态 vs 动画片段（同一个 targetRotation，A/B 验证没退化）
        ///   ② 笨拙度 0 / 0.5 / 1.0（★姿态差与晃动偏移应单调上升，站立不应下降）
        /// 判据：连续站立全部应为 8.00s。
        /// </summary>
        public static string AnimationReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 动画片段 + 笨拙层 ==");
            sb.Append(ClumsyAnimationBaker.ReportText());
            // ★ 布娃娃**实际**用的那一个（与 BuildArmedSim 同一套加载顺序）。
            //   上面 Baker.ReportText() 报的是旧的烘出来的三段，容易看岔，所以这里单列。
            RuntimeAnimatorController used = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                ClumsyAnimationBaker.BlenderControllerPath);
            if (used == null)
                used = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                    ClumsyAnimationBaker.ControllerPath);
            sb.AppendLine("-- 布娃娃实际用的 controller --");
            sb.AppendLine("  " + (used != null ? AssetDatabase.GetAssetPath(used) : "**没给**"));
            UnityEditor.Animations.AnimatorController ac2 = used as UnityEditor.Animations.AnimatorController;
            if (ac2 != null)
            {
                for (int li = 0; li < ac2.layers.Length; li++)
                {
                    foreach (var st in ac2.layers[li].stateMachine.states)
                        sb.AppendLine("    状态 " + st.state.name + " -> " +
                                      (st.state.motion != null ? st.state.motion.name : "空"));
                }
                sb.Append("    参数 ");
                for (int pi = 0; pi < ac2.parameters.Length; pi++)
                    sb.Append(ac2.parameters[pi].name + " ");
                sb.AppendLine();
            }
            try
            {
                sb.AppendLine("-- 站着 8s --");
                sb.AppendLine(AnimRow(true, false, 0f));
                sb.AppendLine(AnimRow(true, true, 0.0f));
                sb.AppendLine(AnimRow(true, true, 0.5f));
                sb.AppendLine(AnimRow(true, true, 1.0f));
                sb.AppendLine("-- 走 8s --");
                sb.AppendLine(AnimRow(false, false, 0f));
                sb.AppendLine(AnimRow(false, true, 0.0f));
                sb.AppendLine(AnimRow(false, true, 0.5f));
                sb.AppendLine(AnimRow(false, true, 1.0f));
                sb.AppendLine("判据：连续站立应全部 8.00s；姿态差 / 晃动偏移随笨拙度单调上升。");
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 落地踉跄验证：从不同高度松手，量落那一下触发几次、峰值强度多少、之后稳不稳。
        /// </summary>
        public static string ImpactReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 落地踉跄 ==");
            try
            {
                float[] drops = { 0.0f, 0.3f, 1.0f, 2.0f };
                for (int i = 0; i < drops.Length; i++)
                {
                    RagdollRecipe recipe = new RagdollRecipe();
                    Sim sim = BuildArmedSim(recipe, true, true);
                    if (drops[i] > 0f)
                    {
                        for (int k = 0; k < sim.Rag.Parts.Count; k++)
                        {
                            ClumsyPart p = sim.Rag.Parts[k];
                            if (p.Body == null) continue;
                            p.Body.position = p.RestPosition + Vector3.up * drops[i];
                            p.Body.linearVelocity = Vector3.zero;
                        }
                        sim.Rag.Root.position += Vector3.up * drops[i];
                        Physics.SyncTransforms();
                    }
                    float peakImpact = 0f;
                    for (int k = 0; k < Mathf.RoundToInt(3f / Dt); k++)
                    {
                        StepSim(sim, 1, Vector2.zero);
                        if (sim.Wob != null && sim.Wob.ImpactAmount > peakImpact)
                            peakImpact = sim.Wob.ImpactAmount;
                    }
                    sb.AppendLine("  下落 " + drops[i].ToString("F2") + "m → 踉跄触发 "
                        + (sim.Wob != null ? sim.Wob.ImpactCount.ToString() : "-")
                        + " 次，峰值强度 " + peakImpact.ToString("F2")
                        + "，3s 后 up " + sim.Rag.Upright.ToString("F3")
                        + "  骨盆y " + sim.Rag.PelvisPosition.y.ToString("F3"));
                    TearDown(sim);
                }
                sb.AppendLine("判据：下落越高 → 触发强度越大；三次末 up 都该回到 0.9 以上。");
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        [MenuItem("笨拙布娃娃/诊断/K 落地踉跄", false, 30)]
        public static void MenuImpact() { Debug.Log(ImpactReport()); }

        // ==================================================================
        // 教程 §三 · 撑直方式（方法2 弹簧 / 方法3 力矩）的 A/B
        // ==================================================================

        [MenuItem("笨拙布娃娃/诊断/L 撑直方式", false, 31)]
        public static void MenuBalance() { Debug.Log(BalanceReport()); }

        /// <summary>一条撑直配置的读数（四项判据见 BalanceReport 的注释）。</summary>
        public sealed class BalanceRow
        {
            public string Label;
            public float BaseTorque, TorqueCeiling;
            public float StandBest, FinalUp, JitterRms;                 // ① 静止 8s
            public float RecoverTime, Overshoot; public int Crossings;  // ② 倾 15° 松手
            public float YawError, TurnMoved;                           // ③ 原地转身 6s
            public float PushMinUp, PushFinalUp;                        // ④ 推一把
        }

        static BalanceRow RunBalanceCase(string label, RagdollRecipe recipe)
        {
            BalanceRow row = new BalanceRow();
            row.Label = label;

            // ---- ① 静止 8s：站不站得住 + 站住之后抖不抖 ----
            Sim a = BuildSim(recipe, true, true);
            row.BaseTorque = a.Rag.TotalMass * Mathf.Abs(recipe.GravityY) * a.Rag.CenterOfMassHeight;
            row.TorqueCeiling = a.Balance != null ? a.Balance.TorqueCeiling : 0f;
            int steps = Mathf.RoundToInt(8f / Dt);
            float run = 0f, best = 0f, sq = 0f;
            int ns = 0;
            for (int i = 0; i < steps; i++)
            {
                StepSim(a, 1, Vector2.zero);
                float up = a.Rag.Upright;
                bool standing = up >= 0.85f && a.Rag.PelvisPosition.y >= a.RestHipY * 0.85f;
                run = standing ? run + Dt : 0f;
                if (run > best) best = run;
                if (i * Dt >= 4f && a.Rag.Hips.Body != null)
                {
                    sq += a.Rag.Hips.Body.angularVelocity.sqrMagnitude;
                    ns++;
                }
            }
            row.StandBest = best;
            row.FinalUp = a.Rag.Upright;
            row.JitterRms = ns > 0 ? Mathf.Sqrt(sq / ns) : 0f;
            TearDown(a);

            // ---- ② 倾 15° 松手：「弹簧感」的量——回正快不快、冲过头多少、振几次 ----
            Sim b = BuildSim(recipe, true, true);
            Vector3 tipDir = TiltAndRelease(b, 15f);
            float t = 0f, recover = -1f, overshoot = 0f, prev = 15f;
            int crossings = 0;
            for (int i = 0; i < Mathf.RoundToInt(5f / Dt); i++)
            {
                StepSim(b, 1, Vector2.zero);
                t += Dt;
                float s = SignedTilt(b, tipDir);
                if (recover < 0f && t > 0.25f && Mathf.Abs(s) < 5f)
                    recover = t;
                if (s < overshoot) overshoot = s;
                if (Mathf.Abs(s) > 0.5f && Mathf.Abs(prev) > 0.5f
                    && Mathf.Sign(s) != Mathf.Sign(prev))
                    crossings++;
                prev = s;
            }
            row.RecoverTime = recover;
            row.Overshoot = overshoot;
            row.Crossings = crossings;
            TearDown(b);

            // ---- ③ 原地转身 6s（Root 120°/s）：方法3 必须自带转身力矩 ----
            Sim c = BuildSim(recipe, true, true);
            Vector3 p0 = c.Rag.PelvisPosition;
            _turnRate = 120f;
            for (int i = 0; i < Mathf.RoundToInt(6f / Dt); i++)
                StepSim(c, 1, Vector2.zero);
            _turnRate = 0f;
            Vector3 pf = c.Rag.PelvisForwardWorld; pf.y = 0f;
            Vector3 rf = c.Rag.Root != null ? c.Rag.Root.forward : pf; rf.y = 0f;
            row.YawError = (pf.sqrMagnitude > 1e-6f && rf.sqrMagnitude > 1e-6f)
                ? Vector3.Angle(pf, rf) : 0f;
            Vector3 md = c.Rag.PelvisPosition - p0; md.y = 0f;
            row.TurnMoved = md.magnitude;
            TearDown(c);

            // ---- ④ 推一把（t=1.5s、220 N·s 前推，跑 5s）----
            Sim e = BuildSim(recipe, true, true);
            float minUp = 1f;
            for (int i = 0; i < Mathf.RoundToInt(5f / Dt); i++)
            {
                if (i == Mathf.RoundToInt(1.5f / Dt))
                    e.Rag.Push(Vector3.forward * 220f);
                StepSim(e, 1, Vector2.zero);
                if (e.Rag.Upright < minUp) minUp = e.Rag.Upright;
            }
            row.PushMinUp = minUp;
            row.PushFinalUp = e.Rag.Upright;
            TearDown(e);

            return row;
        }

        /// <summary>
        /// 把整具布娃娃绕**脚底地面点**（骨盆正下方）转 degrees 度，速度清零 —— 像被推歪了松手。
        /// 返回倾斜方向（水平单位向量）：SignedTilt 用它给倾角定正负号。
        /// 轴取「角色右方」→ 前后倒（绕前方轴则是左右倒，两种都行，这里固定一种以便对比）。
        /// </summary>
        static Vector3 TiltAndRelease(Sim sim, float degrees)
        {
            Vector3 pelvis = sim.Rag.PelvisPosition;
            Vector3 axis = (sim.Rag.Hips.Body.rotation * sim.Rag.Hips.LocalRight).normalized;
            Vector3 pivot = new Vector3(pelvis.x, 0.004f, pelvis.z);
            Quaternion rot = Quaternion.AngleAxis(degrees, axis);

            for (int i = 0; i < sim.Rag.Parts.Count; i++)
            {
                ClumsyPart p = sim.Rag.Parts[i];
                if (p.Body == null)
                    continue;
                p.Body.position = pivot + rot * (p.Body.position - pivot);
                p.Body.rotation = rot * p.Body.rotation;
                p.Body.linearVelocity = Vector3.zero;
                p.Body.angularVelocity = Vector3.zero;
            }
            if (sim.Rag.Root != null)
                sim.Rag.Root.position = sim.Rag.Hips.Body.position;
            Physics.SyncTransforms();

            Vector3 up = sim.Rag.PelvisUpWorld;
            Vector3 dir = up - Vector3.Project(up, Vector3.up);
            return dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.forward;
        }

        /// <summary>带符号的倾角（度）：骨盆上方轴在 tipDir 方向上的分量。正 = 还向着最初倒的那边。</summary>
        static float SignedTilt(Sim sim, Vector3 tipDir)
        {
            Vector3 up = sim.Rag.PelvisUpWorld;
            return Mathf.Atan2(Vector3.Dot(up, tipDir), up.y) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 教程 §三「用人工力保持直立」的 A/B。四组判据，每组都对着视频里的一个说法：
        ///   ① 静止 8s（站中位 / 末 up / 抖动 RMS）—— 「能不能站住」；视频说方法2 能站。
        ///   ② 倾 15° 松手（回正时间 / 反向过冲 / 变号次数）—— 「弹簧感」的量化。
        ///      视频 [05:55] 对方法2 的原话是「会产生弹簧感，我不喜欢」。**实测这个抱怨在本工程不成立**：
        ///      弹簧那一路反而是过冲最小、变号 0 次的那一行。。
        ///   ③ 原地转身 6s（朝向误差 / 位移）—— 方法3 需要自己补转身力矩，否则转不了身。
        ///   ④ 推一把 220 N·s（最小 up / 末 up）—— 抗扰动量。
        /// </summary>
        public static string BalanceReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 教程 §三 · 撑直方式 A/B（ref-ActiveRagdolls-视频教程.md §三）==");
            string[] labels =
            {
                "方法2 弹簧 · 二次阻尼 0（基线）",
                "方法2 弹簧 · 二次阻尼 0.05（教程码）",
                "方法3 力矩（按惯量分散）· ζ=0（忠实教程）",
                "方法3 力矩（按惯量分散）· ζ=0.5",
                "方法3 力矩（按惯量分散）· ζ=1.0",
                "方法3 力矩（按惯量分散）· ζ=1.0 + 二次0.05",
                "方法3 力矩（只加骨盆 = 教程原样）· ζ=0",
                "None 对照组（完全不撑）",
            };
            try
            {
                for (int i = 0; i < labels.Length; i++)
                {
                    RagdollRecipe r = new RagdollRecipe();
                    r.CustomTorsoAngularDrag = 0f;   // 除非这一行特别说明，否则先把二次阻尼关掉，一次只动一个变量
                    switch (i)
                    {
                        case 0: break;
                        case 1: r.CustomTorsoAngularDrag = 0.05f; break;
                        case 2: r.Balance = BalanceMode.UprightTorque; break;
                        case 3: r.Balance = BalanceMode.UprightTorque; r.UprightTorqueDampingRatio = 0.5f; break;
                        case 4: r.Balance = BalanceMode.UprightTorque; r.UprightTorqueDampingRatio = 1.0f; break;
                        case 5: r.Balance = BalanceMode.UprightTorque; r.UprightTorqueDampingRatio = 1.0f; r.CustomTorsoAngularDrag = 0.05f; break;
                        case 6:
                            // ★ 反例：完全照教程的 PhysicsModule（AddTorque 只加在 PhysicalTorso = hips 上）。
                            //   实测会自激振荡倒地，原因见 ClumsyBalance.ApplyBodyTorque 的注释。
                            r.Balance = BalanceMode.UprightTorque;
                            r.UprightTorqueOnWholeBody = false;
                            break;
                        case 7: r.Balance = BalanceMode.None; break;
                    }
                    BalanceRow row = RunBalanceCase(labels[i], r);
                    sb.AppendLine(string.Format(
                        "{0,-40} 站中位{1,6:F2}s 末up{2,6:F3} 抖动{3,5:F2}rad/s | 倾15°回正{4,6:F2}s 反向过冲{5,6:F1}° 变号{6,2}次",
                        row.Label, row.StandBest, row.FinalUp, row.JitterRms,
                        row.RecoverTime, row.Overshoot, row.Crossings));
                    sb.AppendLine(string.Format(
                        "{0,-40} 转身误差{1,6:F1}° 位移{2,5:F2}m | 推220N·s 后 minUp{3,6:F3} 末up{4,6:F3} | M·g·h={5:F0} 力矩上限={6:F0} N·m",
                        "", row.YawError, row.TurnMoved, row.PushMinUp, row.PushFinalUp,
                        row.BaseTorque, row.TorqueCeiling));
                }
                sb.AppendLine("读法：站中位 8.00s = 全程没倒（判据与 StandReport 同）；抖动 = 后 4s 骨盆角速度 RMS，越小越不抖；");
                sb.AppendLine("      回正越快、反向过冲越小、变号越少 = 弹簧感越弱（视频 [05:55] 嫌方法2 弹簧感重，这三列就是它的量化）；");
                sb.AppendLine("      转身误差 = 骨盆前方与 Root 前方的夹角（方法3 少了转身力矩这一项就会很大）；None 那一行是「不撑会怎样」的证据。");
                sb.AppendLine("★ 结论（见 HANDOFF-布娃娃物理优化.md）：① None 站不住 → 撑住确实全靠人工力；② 方法3 照教程原样（只加骨盆）完全不能用：");
                sb.AppendLine("  骨盆自身惯量 ~0.08 kg·m² 撑不住 k_eff≈3500 N·m/rad 的显式力矩（ω·dt 超过 2 就发散）；③ 改成按惯量分散到整具身体后方法3 能站（8.00s），");
                sb.AppendLine("  但过冲 / 变号 / 抖动 / 转身精度都不优于现成的方法2 弹簧 → 默认维持方法2，方法3 作为可选路径保留。");
            }
            finally
            {
                _turnRate = 0f;
                Physics.simulationMode = SimulationMode.FixedUpdate;
            }
            return sb.ToString();
        }

        static string AnimRow(bool standing, bool useAnimation, float clumsiness)
        {
            RagdollRecipe recipe = new RagdollRecipe();
            recipe.UseAnimation = useAnimation;
            recipe.Clumsiness = clumsiness;
            Sim sim = BuildArmedSim(recipe, true, true);
            Trace t = Run(sim, 8f, 0f, Vector3.zero,
                standing ? Vector2.zero : new Vector2(0f, 1f), false);
            string label = (standing ? "站 " : "走 ")
                + (useAnimation ? "动画  " : "程序化");
            string row = Row(label, t, sim);
            if (sim.Anim != null && sim.Anim.Bones.Count > 0)
            {
                row += "  姿态差 均" + sim.Anim.PoseGapAverage.ToString("F1") + "°/峰"
                    + sim.Anim.PoseGapPeak.ToString("F1") + "°";
                if (sim.Wob != null)
                    row += "  晃动 均" + sim.Wob.WobbleOffsetAverage.ToString("F1") + "°/峰"
                        + sim.Wob.WobbleOffsetPeak.ToString("F1") + "°  落地" + sim.Wob.ImpactCount;
                row += "  [" + sim.Anim.AnimStateName + "]";
            }
            TearDown(sim);
            return row;
        }

        public static string FullReport()

        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("############ 教程版布娃娃 诊断全量 ############");
            sb.Append(StandReport());
            sb.Append(SpringSweep());
            sb.Append(LockPassengerReport());
            sb.Append(PushReport());
            sb.Append(WalkReport());
            sb.AppendLine("############ 手部与环境交互 ############");
            sb.Append(LockPassengerDetailReport());
            sb.Append(ArmAxisReport());
            sb.Append(ArmIKReport());
            sb.Append(AnimationReport());
            sb.Append(ImpactReport());            sb.Append(BalanceReport());
            return sb.ToString();
        }
    }
}
