using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 一条手臂的两骨 IK 链（左边一条、右边一条）。
    ///
    /// **骨链（本模型 bind pose 实测，2026-10-01）**：
    /// <code>
    ///   neck → shoulder(0.2711m) → arm(0.1017m) → forearm(0.1048m) → hand(0.0611m → 手指)
    /// </code>
    /// 注意这具骨架的比例和常人相反：Mixamo 叫 <c>Shoulder</c> 的那根**长 27cm**（是真正的上臂），
    /// 叫 <c>Arm</c> 的只有 10cm —— **名字和长度是反的**。所以「两骨」不能按骨头名字取，而是：
    /// <list type="bullet">
    ///   <item>上骨 = <b>shoulder</b> 一根，根点 = shoulder 骨原点；</item>
    ///   <item>下骨 = <b>arm + forearm 两根（保持出生局部旋转，当一根刚体）</b>，肘点 = arm 骨原点；</item>
    ///   <item>末端 = hand 骨原点。</item>
    /// </list>
    /// 于是 L1 = |shoulder→arm| = 0.2711m、L2 = |arm→hand| = 0.2065m（57:43，一副正常的臂比例）。
    /// 两个长度在「forearm 保持出生局部旋转」的前提下都是刚体不变量，所以解是精确的
    /// （IK 生效时会同时把 forearm 的 targetRotation 混向 identity，见 <see cref="ClumsyArmIK.Solve"/>）。
    ///
    /// <b>为什么不是另一分组</b>：按 (shoulder+arm | forearm) 得到 L1=0.372 / L2=0.105，
    /// 肘点落在**腕部**，直臂内半径 |L1−L2| = 0.268m（占可达 56%，手几乎收不回身前）。
    /// 第一版就是这么分的，被诊断台 <c>ArmIKReport</c> 的测试①当场暴露出来。
    /// （IK 生效时会同时把 arm 的 targetRotation 混向 identity，见 <see cref="ClumsyArmIK.Solve"/>）。
    ///
    /// **为什么自己算，而不是装 Animation Rigging 包**（`ref-Unity动画系列-全集.md` EP12/EP13 那条路）：
    /// <list type="number">
    ///   <item>那个包建立在 Mecanim 上，角色必须有 Animator；本工程**一个动画片段都没有**，
    ///         也没有 Animator —— `ref-ActiveRagdolls-视频教程.md` §〇.1 把这条路叫「路线 C」；</item>
    ///   <item>本工程的姿势本来就是「程序化写 ConfigurableJoint.targetRotation」，
    ///         IK 只要产出同样的东西就接得上。那份文档 §〇.1 的原话：
    ///         「换掉的是姿势的来源，不是机制」；</item>
    ///   <item>EP13 的 `TwoBoneIKConstraint`（Tip / Mid / Root / Auto Setup from Tip Transform）
    ///         与 `MultiRotationConstraint`（掌心朝向 + 每个角色不同的 offset）两条语义都照实现了 ——
    ///         本文件的「一切长度与轴都从 bind pose 实测」就是 Auto Setup，
    ///         <see cref="ClumsyArmIK.Solve"/> 里的掌心对齐就是 Multi Rotation。</item>
    /// </list>
    ///
    /// ⚠️ **与 Sergio 的一处有意分歧**：他把 IK 解在「动画身体」上（纯数学、无物理），
    /// 物理布娃娃再去追 —— 开环。这里解在**物理身体自己的当前位姿**上 —— 闭环。
    /// 理由：本工程没有动画身体（路线 C），而且闭环天然自纠：物理下滑时手会继续朝目标修，
    /// 开环则会「手臂姿势对了、手却没够到」。代价是输入有一步（20ms）滞后。
    /// </summary>
    public sealed class ArmIKChain
    {
        public string Suffix;
        /// <summary>角色左 = −1（世界 −X）、右 = +1。</summary>
        public int SideSign;

        public ClumsyPart Shoulder, Upper, Fore, Hand;
        public string[] JointKeys;

        /// <summary>|shoulder 骨原点 → arm 骨原点|，bind 实测（上骨）。</summary>
        public float UpperLength;
        /// <summary>|arm 骨原点 → hand 骨原点|，bind 实测（下骨 = arm + forearm 两根当一根）。</summary>
        public float LowerLength;

        public Vector3 BindUpperDir;
        public Vector3 BindLowerDir;
        public Vector3 BindHandDir;
        public Quaternion BindShoulderRot = Quaternion.identity;
        /// <summary>arm 骨的出生世界旋转 —— 下骨的目标方向由它推出。</summary>
        public Quaternion BindUpperRot = Quaternion.identity;
        public Quaternion BindForeRot = Quaternion.identity;
        public Quaternion BindHandRot = Quaternion.identity;

        /// <summary>手掌法线在 hand 骨局部坐标里的方向（由 Recipe.HandPalmAxis 标定）。</summary>
        public Vector3 PalmAxisLocal = Vector3.left;

        // ---- 运行期 ----
        /// <summary>0 = 完全走步态姿势，1 = 完全走 IK。</summary>
        public float Weight;
        public float TargetWeight;
        public bool HasTarget;
        public Vector3 TargetWorld;
        public bool HasPalmNormal;
        /// <summary>目标表面的**外法线**（掌心要朝 −它）。</summary>
        public Vector3 PalmNormalWorld;

        // ---- 上一帧解出来的东西（诊断用）----
        public Vector3 ClampedTargetWorld;
        public Vector3 ElbowWorld;
        public float ReachRatio;
        public Quaternion DesiredShoulderWorld = Quaternion.identity;
        public Quaternion DesiredUpperWorld = Quaternion.identity;
        public Quaternion DesiredHandWorld = Quaternion.identity;


        public Vector3 ShoulderWorld
        {
            get { return Shoulder != null && Shoulder.Body != null ? Shoulder.Body.position : Vector3.zero; }
        }

        public Vector3 ForeWorld
        {
            get { return Fore != null && Fore.Body != null ? Fore.Body.position : Vector3.zero; }
        }

        public Vector3 HandWorld
        {
            get { return Hand != null && Hand.Body != null ? Hand.Body.position : Vector3.zero; }
        }

        public float MaxReach { get { return UpperLength + LowerLength; } }

        /// <summary>手离「被夹紧后的目标点」还有多远。够到了 = 接近 0。</summary>
        public float HandError
        {
            get { return HasTarget ? Vector3.Distance(HandWorld, ClampedTargetWorld) : 0f; }
        }

        /// <summary>手离「原始目标点」还有多远（诊断用：能看出是不是够不着被夹了）。</summary>
        public float HandErrorRaw
        {
            get { return HasTarget ? Vector3.Distance(HandWorld, TargetWorld) : 0f; }
        }

        public bool Ready
        {
            get { return Shoulder != null && Fore != null && Hand != null; }
        }
    }

    /// <summary>
    /// 手部两骨 IK 的解算器。挂在浣熊根物体上（与 <see cref="ClumsyPoseDriver"/> 同一层）。
    ///
    /// 它**不写关节**。它只算出「这几个关节的 targetRotation 应该是多少、混合权重是多少」，
    /// 由 <see cref="ClumsyPoseDriver"/> 统一写 —— 保持「一个关节一个写入者」。
    /// （教程 P6 那边 CopyMotion 与 CamControl 抢写同一个 targetRotation 是个竞态，
    /// 交接文档 §四 项8 已经点过；这里不再制造第二个。）
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClumsyArmIK : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;

        public readonly List<ArmIKChain> Chains = new List<ArmIKChain>();

        readonly Dictionary<string, Quaternion> _targets = new Dictionary<string, Quaternion>();
        readonly Dictionary<string, float> _weights = new Dictionary<string, float>();

        /// <summary>上一帧解算是否有奇点/退化（诊断用）。</summary>
        public bool LastSolveDegenerate { get; private set; }

        // ------------------------------------------------------------------

        public void Setup(ClumsyRagdoll ragdoll, RagdollRecipe recipe)
        {
            Ragdoll = ragdoll;
            Recipe = recipe;
            Chains.Clear();
            if (Ragdoll == null)
                return;

            AddChain("l", -1);
            AddChain("r", +1);
        }

        void AddChain(string suffix, int sideSign)
        {
            ArmIKChain c = new ArmIKChain();
            c.Suffix = suffix;
            c.SideSign = sideSign;
            c.Shoulder = Ragdoll.Find("shoulder" + suffix);
            c.Upper = Ragdoll.Find("arm" + suffix);
            c.Fore = Ragdoll.Find("forearm" + suffix);
            c.Hand = Ragdoll.Find("hand" + suffix);
            c.JointKeys = new[]
            {
                "shoulder" + suffix, "arm" + suffix, "forearm" + suffix, "hand" + suffix
            };
            c.PalmAxisLocal = ResolvePalmAxis(c.Hand, Recipe);
            c.PalmAxisLocal = PalmAxisVector(Recipe != null ? Recipe.HandPalmAxis : -1);

            if (c.Ready && c.Upper != null)
            {
                // Auto Setup from bind pose（= EP13 的 "Auto Setup from Tip Transform" 的等价物：
                // 不猜骨长、不猜轴向，全从绑定时测出来的位置反推）。
                //
                // ⚠️ **哪两根算「两骨」是实测定的，不是按 Mixamo 的骨头名字定的。**
                // 本模型 bind pose 的实测位置（世界 x）：
                //   neck 0 → shoulder −0.2201 → arm −0.4888 → forearm −0.5905 → hand −0.6919
                // 也就是 Shoulder 骨长 27cm、Arm 骨只有 10cm —— **名字和长度是反的**。
                // 按 (shoulder+arm | forearm) 分组：L1=0.372 / L2=0.105，肘点落在**腕部**，
                // 直臂内半径 |L1−L2| = 0.268m（占可达 56%，手几乎收不回身前）。
                // 按 (shoulder | arm+forearm) 分组：L1=0.271 / L2=0.207 —— 57:43，
                // 一副正常的臂比例，内半径只剩 0.065m。取后者。
                Vector3 upper = c.Upper.RestPosition - c.Shoulder.RestPosition;
                Vector3 lower = c.Hand.RestPosition - c.Upper.RestPosition;

                // hand → 手指（hand 骨的第一个子物体）；拿不到就退化成「下骨方向」。
                Vector3 handDir = lower;
                if (c.Hand.Bone != null && c.Hand.Bone.childCount > 0)
                {
                    Vector3 toFinger = c.Hand.Bone.GetChild(0).position - c.Hand.Bone.position;
                    if (toFinger.sqrMagnitude > 1e-8f)
                        handDir = toFinger;
                }

                c.UpperLength = upper.magnitude;
                c.LowerLength = lower.magnitude;
                c.BindUpperDir = upper.sqrMagnitude > 1e-8f ? upper.normalized : Vector3.right * sideSign;
                c.BindLowerDir = lower.sqrMagnitude > 1e-8f ? lower.normalized : c.BindUpperDir;
                c.BindHandDir = handDir.sqrMagnitude > 1e-8f ? handDir.normalized : c.BindLowerDir;
                c.BindShoulderRot = c.Shoulder.RestRotation;
                c.BindUpperRot = c.Upper.RestRotation;
                c.BindForeRot = c.Fore.RestRotation;
                c.BindHandRot = c.Hand.RestRotation;
            }
            else
            {
                Debug.LogWarning("[ClumsyArmIK] 手臂链 " + suffix + " 解析不全，IK 对该侧停用。");
            }

            Chains.Add(c);
        }

        public ArmIKChain Get(string suffix)
        {
            for (int i = 0; i < Chains.Count; i++)
                if (Chains[i].Suffix == suffix)
                    return Chains[i];
            return null;
        }

        /// <summary>给某只手下一个目标。weight 是**目标权重**，实际权重按 ArmIKWeightSpeed 渐变。</summary>
        public void SetTarget(string suffix, Vector3 world, bool hasPalmNormal, Vector3 palmNormal, float targetWeight)
        {
            ArmIKChain c = Get(suffix);
            if (c == null)
                return;
            c.HasTarget = true;
            c.TargetWorld = world;
            c.HasPalmNormal = hasPalmNormal;
            c.PalmNormalWorld = palmNormal;
            c.TargetWeight = Mathf.Clamp01(targetWeight);
        }

        /// <summary>收回目标：权重渐变回 0，回到步态姿势。</summary>
        public void ClearTarget(string suffix)
        {
            ArmIKChain c = Get(suffix);
            if (c == null)
                return;
            c.HasTarget = false;
            c.TargetWeight = 0f;
        }

        public void ClearAllTargets()
        {
            for (int i = 0; i < Chains.Count; i++)
            {
                Chains[i].HasTarget = false;
                Chains[i].TargetWeight = 0f;
            }
        }

        public float WeightOf(string suffix)
        {
            ArmIKChain c = Get(suffix);
            return c != null ? c.Weight : 0f;
        }

        /// <summary>姿势驱动用：这个关节这一帧有没有 IK 覆盖，权重多少。</summary>
        public bool TryGetOverride(string key, out Quaternion target, out float weight)
        {
            if (_weights.TryGetValue(key, out weight) && weight > 1e-4f
                && _targets.TryGetValue(key, out target))
                return true;
            target = Quaternion.identity;
            weight = 0f;
            return false;
        }

        // ------------------------------------------------------------------

        /// <summary>每个物理步在姿势驱动**之前**调用。</summary>
        public void Solve(float dt)
        {
            _targets.Clear();
            _weights.Clear();
            LastSolveDegenerate = false;

            if (Ragdoll == null || Recipe == null || !Recipe.UseArmIK)
                return;

            for (int i = 0; i < Chains.Count; i++)
            {
                ArmIKChain c = Chains[i];

                // ---- 权重渐变（Sergio 的 SmoothIKTransitions / IKTransitionsSpeed）----
                // 直接吸附会在「按下的那一帧」把整条手臂抽过去，看着像瞬移。
                float want = c.HasTarget ? c.TargetWeight : 0f;
                c.Weight = Mathf.MoveTowards(c.Weight, want,
                    Mathf.Max(0.01f, Recipe.ArmIKWeightSpeed) * Mathf.Max(dt, 1e-4f));
                if (c.Weight <= 1e-4f)
                    continue;

                if (!c.Ready)
                    continue;

                SolveChain(c);

                // ---- 世界旋转 → ConfigurableJoint.targetRotation ----
                // 骨链是 shoulder → arm → forearm → hand，两骨 IK 的「肘」落在 **arm 骨原点**上：
                //   · shoulder 骨：向上骨方向摆（根点在 shoulder 骨原点）
                //   · arm 骨：向下骨方向摆（肘就在这里）
                //   · hand 骨：手指方向 + 掌心滚转
                Quaternion qs, qa, qh;
                if (WorldToJoint(c.Shoulder, c.DesiredShoulderWorld, out qs))
                {
                    _targets[c.JointKeys[0]] = qs;
                    _weights[c.JointKeys[0]] = c.Weight;
                }
                if (WorldToJoint(c.Upper, c.DesiredUpperWorld, out qa))
                {
                    _targets[c.JointKeys[1]] = qa;
                    _weights[c.JointKeys[1]] = c.Weight;
                }
                if (WorldToJoint(c.Hand, c.DesiredHandWorld, out qh))
                {
                    _targets[c.JointKeys[3]] = qh;
                    _weights[c.JointKeys[3]] = c.Weight;
                }

                // 下骨的中间骨（forearm）：混向 identity。
                // 两骨 IK 假定「arm → hand 是一根刚体」—— 不锁住它的话，步态的肘部弯曲会把
                // 手末端带偏，L2 那条不变量就不成立，手会系统性够不到。
                _targets[c.JointKeys[2]] = Quaternion.identity;
                _weights[c.JointKeys[2]] = c.Weight;
                _weights[c.JointKeys[1]] = c.Weight;
            }
        }

        void SolveChain(ArmIKChain c)
        {
            Vector3 root = c.ShoulderWorld;
            float L1 = c.UpperLength;
            float L2 = c.LowerLength;

            Vector3 dir = c.TargetWorld - root;
            float d = dir.magnitude;
            if (d < 1e-4f)
            {
                // 目标就在肩点上：方向无定义，用角色前方兜底。
                dir = Ragdoll.Root != null ? Ragdoll.Root.forward : Vector3.forward;
                d = 1e-4f;
                LastSolveDegenerate = true;
            }
            dir /= d;

            // 目标点夹紧（太远 = 伸直奇点，太近 = 折叠奇点）。
            float dMax = c.MaxReach * Mathf.Clamp(Recipe.ArmIKMaxReachFraction, 0.5f, 0.999f);
            float dMin = Mathf.Abs(L1 - L2) * Mathf.Max(1f, Recipe.ArmIKMinReachFraction);
            float dc = Mathf.Clamp(d, Mathf.Min(dMin, dMax * 0.5f), dMax);
            Vector3 T = root + dir * dc;
            c.ClampedTargetWorld = T;
            c.ReachRatio = c.MaxReach > 1e-6f ? d / c.MaxReach : 0f;

            // ---- 肘部 pole（EP13 的 hint / Sergio 的 LeftHandHint）----
            // hint 的方向按角色自身的前右上去定，不写死世界方向 —— 角色转身以后肘仍然朝后下方。
            Vector3 fwd = Ragdoll.Root != null ? Ragdoll.Root.forward : Vector3.forward;
            Vector3 right = Ragdoll.Root != null ? Ragdoll.Root.right : Vector3.right;
            Vector3 pole = (-fwd * Recipe.ElbowPoleBackward)
                         + (Vector3.down * Recipe.ElbowPoleDown)
                         + (right * (c.SideSign * Recipe.ElbowPoleOutward));
            if (pole.sqrMagnitude < 1e-6f)
                pole = Vector3.down;
            pole.Normalize();

            Vector3 axis = Vector3.Cross(dir, pole);
            if (axis.sqrMagnitude < 1e-5f)
            {
                // dir 与 pole 几乎平行：平面无定义，换一根参考轴。
                axis = Vector3.Cross(dir, Vector3.up);
                if (axis.sqrMagnitude < 1e-5f)
                    axis = Vector3.Cross(dir, right);
                LastSolveDegenerate = true;
            }
            axis.Normalize();

            // 余弦定理。绕 axis 转 +A 会把上骨**朝 pole 那一侧**掰（推导见文件头注释）。
            float cosA = (L1 * L1 + dc * dc - L2 * L2) / (2f * L1 * Mathf.Max(dc, 1e-5f));
            cosA = Mathf.Clamp(cosA, -1f, 1f);
            float A = Mathf.Acos(cosA) * Mathf.Rad2Deg;

            Vector3 upperDir = Quaternion.AngleAxis(A, axis) * dir;
            Vector3 elbow = root + upperDir * L1;
            Vector3 lowerDir = T - elbow;
            if (lowerDir.sqrMagnitude < 1e-8f)
            {
                lowerDir = upperDir;
                LastSolveDegenerate = true;
            }
            else
            {
                lowerDir.Normalize();
            }
            c.ElbowWorld = elbow;

            // ---- 世界旋转：只做「摆动」，扭转沿用 bind（最小旋转 = 不会把手臂拧成麻花）----
            c.DesiredShoulderWorld = Quaternion.FromToRotation(c.BindUpperDir, upperDir) * c.BindShoulderRot;
            c.DesiredUpperWorld = Quaternion.FromToRotation(c.BindLowerDir, lowerDir) * c.BindUpperRot;

            // ---- 手：Multi Rotation（EP13 的第二个约束）----
            // 基准：手指继续沿下骨方向，扭转沿用 bind。
            Vector3 handDir = lowerDir;
            Quaternion handWorld = Quaternion.FromToRotation(c.BindHandDir, handDir) * c.BindHandRot;

            float palmW = Mathf.Clamp01(Recipe.HandPalmAlignWeight);
            if (palmW > 1e-4f && c.HasPalmNormal)
            {
                // 掌心要朝**表面**，即朝 −外法线。
                //
                // ⚠️ **必须给全三自由度，不能只绕手指轴滚。**
                // 第一版就是只滚，实测残差 **49.5°**：目标表面外法线与手指方向夹 134°，
                // 也就是它在「垂直于手指轴的平面」外还有 0.70 的分量 —— **滚转根本修不掉它**。
                // EP13 的 Multi Rotation Constraint 勾的是三个轴、不是一根轴，这里照它做。
                Vector3 n = -c.PalmNormalWorld;                       // 掌心该朝的世界方向
                Vector3 f = Vector3.ProjectOnPlane(handDir, n);       // 手指方向（在 ⟂n 的平面里取最接近下骨的）
                if (f.sqrMagnitude > 1e-6f)
                {
                    f.Normalize();
                    // hand 骨局部：+Y = 手指，PalmAxisLocal = 掌心法线（与 +Y 正交）。
                    // 用两个轴约束把局部系对到世界系：X → n，Y → f，Z → cross(n, f)。
                    Vector3 xLocal = c.PalmAxisLocal;
                    Vector3 zLocal = Vector3.Cross(xLocal, Vector3.up);
                    Quaternion localRot = Quaternion.LookRotation(zLocal, Vector3.up);
                    Quaternion worldRot = Quaternion.LookRotation(Vector3.Cross(n, f), f);
                    Quaternion aim = worldRot * Quaternion.Inverse(localRot);
                    handWorld = Quaternion.Slerp(handWorld, aim, palmW);
                }
            }
            if (Mathf.Abs(Recipe.HandPalmOffsetDegrees) > 1e-4f)
            {
                // 偏置绕**实际的手指轴**拧 —— 它就是「掌心滚转」那一个自由度。
                Vector3 fingerAxis = handWorld * Vector3.up;
                handWorld = Quaternion.AngleAxis(Recipe.HandPalmOffsetDegrees, fingerAxis) * handWorld;
            }

            c.DesiredHandWorld = handWorld;
        }

        /// <summary>
        /// 「想让它朝哪」（世界旋转）→ <c>ConfigurableJoint.targetRotation</c>。
        ///
        /// 推导（`ref-ActiveRagdolls-视频教程.md` §四 的 Stevenson 换算式，本工程
        /// <c>CreateJoint</c> 显式写了 <c>axis = right / secondaryAxis = up</c>，
        /// 所以 <c>worldToJointSpace</c> 是单位阵，那一步可以省）：
        /// <code>
        ///   joint.targetRotation = Inverse(desiredLocal) * startLocal
        /// </code>
        /// 其中 <c>desiredLocal</c> = 「本骨相对 connectedBody」想要有的局部旋转，
        /// <c>startLocal</c> = 出生时同一个量（= <see cref="ClumsyPart.RestLocalToBody"/>）。
        ///
        /// 校验：想让骨头绕自己的轴 <c>a</c> 可见地转 θ，即 <c>desiredLocal = start * AngleAxis(θ, a)</c>，
        /// 代进去得 <c>AngleAxis(−θ, a)</c> —— 正好是 <see cref="ClumsyPoseDriver.JointTarget"/>
        /// 一直在用的那条简化式。两条是同一个式子，不是两套。
        /// </summary>
        public static bool WorldToJoint(ClumsyPart part, Quaternion desiredWorld, out Quaternion target)
        {
            target = Quaternion.identity;
            if (part == null || part.Joint == null || part.Bone == null)
                return false;

            Rigidbody cb = part.Joint.connectedBody;
            Quaternion parentWorld;
            if (cb != null)
                parentWorld = cb.rotation;
            else if (part.Bone.parent != null)
                parentWorld = part.Bone.parent.rotation;
            else
                parentWorld = Quaternion.identity;

            Quaternion desiredLocal = Quaternion.Inverse(parentWorld) * desiredWorld;
            target = Quaternion.Inverse(desiredLocal) * part.RestLocalToBody;
            return true;
        }
        /// <summary>
        /// 掌心法线取 hand 骨的哪根局部轴。
        /// 来源：EP13 的 Multi Rotation Constraint 要一个 offset 才能把掌心转到与 Target 的 Z 轴对齐，
        /// 而她明确警告那个 offset 只对 Bananaman 成立（其它角色要另外定）。
        ///
        /// **本模型实测（ClumsyRagdollBench.ArmAxisReport）正好证明了这一点**：
        /// <code>
        ///   handl  局部 −X → 世界 (0.042, −0.922, −0.384)   局部 +Z → (0.074, −0.380, 0.922)
        ///   handr  局部 −X → 世界 (0.019,  0.119,  0.993)   局部 +Z → (0.049, −0.992, 0.118)
        /// </code>
        /// 两只手**都是手掌朝下**，但一根是 −X、另一根是 +Z —— **一个数值盖不住两只手**。
        /// 所以按「四根候选轴里，bind 世界方向最朝下的那根」自动标定：T-pose 的手掌朝下。
        /// </summary>
        static Vector3 ResolvePalmAxis(ClumsyPart hand, RagdollRecipe recipe)
        {
            if (recipe != null && recipe.HandPalmAxis != 0)
                return PalmAxisVector(recipe.HandPalmAxis);
            if (hand == null)
                return Vector3.left;

            Vector3[] candidates = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            Vector3 best = Vector3.left;
            float bestDot = float.NegativeInfinity;
            for (int i = 0; i < candidates.Length; i++)
            {
                float d = Vector3.Dot(hand.RestRotation * candidates[i], Vector3.down);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = candidates[i];
                }
            }
            return best;
        }


        static Vector3 PalmAxisVector(int code)
        {
            switch (code)
            {
                case 1: return Vector3.right;
                case 2: return Vector3.forward;
                case -2: return Vector3.back;
                case -1:
                default: return Vector3.left;
            }
        }

        // ------------------------------------------------------------------

        /// <summary>诊断台用的一行摘要。</summary>
        public string Report()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("== 手部两骨 IK ==");
            for (int i = 0; i < Chains.Count; i++)
            {
                ArmIKChain c = Chains[i];
                sb.AppendLine("[" + c.Suffix + "] L1=" + c.UpperLength.ToString("F4")
                    + " L2=" + c.LowerLength.ToString("F4")
                    + " 可达=" + c.MaxReach.ToString("F4") + "m"
                    + "  权重=" + c.Weight.ToString("F3")
                    + "  目标=" + (c.HasTarget ? "有" : "无"));
                sb.AppendLine("     bind 上骨方向 " + c.BindUpperDir.ToString("F3")
                    + "  下骨 " + c.BindLowerDir.ToString("F3")
                    + "  手指 " + c.BindHandDir.ToString("F3"));
                sb.AppendLine("     掌心局部轴 " + c.PalmAxisLocal.ToString("F2")
                    + "  bind 世界方向 " + (c.BindHandRot * c.PalmAxisLocal).ToString("F3"));
                if (c.HasTarget)
                {
                    sb.AppendLine("     手误差(夹紧后) " + c.HandError.ToString("F4")
                        + "m  原始 " + c.HandErrorRaw.ToString("F4")
                        + "m  够不到比 " + c.ReachRatio.ToString("F3"));
                }
            }
            return sb.ToString();
        }
    }
}
