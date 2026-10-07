using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 「用人工力保持直立」的做法。
    /// 来源：`ref-ActiveRagdolls-视频教程.md` §三（视频 [05:41]–[07:01] + 仓库
    /// `Assets/Scripts/Active Ragdoll/Modules/PhysicsModule.cs` 的 `BALANCE_MODE` 枚举，共 5 项）。
    ///
    /// 视频把「撑直」拆成三种做法，并给了各自的代价：
    ///   ① FREEZE_ROTATIONS —— 「糟糕的主意（awful idea），看着僵硬、是大 glitch 的来源」。**本工程不实现。**
    ///   ② STABILIZER_JOINT —— 他实际交付的默认值，但「有弹簧感，我不喜欢」。
    ///   ③ UPRIGHT_TORQUE —— 他认为更好的方向：「和方法 2 内部做的事本质相同，但自己写就允许我们
    ///      决定任意时刻施加多大力矩；方法 2 的力矩输出像一个弹簧（越偏离给得越大），
    ///      自己写的话这条『偏离 → 力矩』的函数可以由我们选，于是能在一定程度上把弹簧感消掉」。
    ///   另有 MANUAL_TORQUE（玩家直接给力矩）与 NONE，本工程也不实现（前者是操作方式，不是撑直机制）。
    ///
    /// 本工程 2026-10-01 之前的现状 = **方法 2**：hips 的 ConfigurableJoint 连到 Root（只转 yaw 的
    /// 竖直参考系），角驱动弹簧把它扳回竖直（推导见 `ClumsyRagdoll` 的类注释）。
    /// 本文件加的是**方法 3**，于是两者可以 A/B（`RagdollRecipe.Balance`）。
    /// </summary>
    public enum BalanceMode
    {
        /// <summary>方法 2 · 稳定器关节（本工程的实现 = hips↔Root 的角驱动弹簧）。</summary>
        StabilizerJoint = 0,

        /// <summary>方法 3 · 自己施加力矩（含自定义的「偏离角 → 力矩」函数 + 转身力矩）。</summary>
        UprightTorque = 1,

        /// <summary>什么都不做（对照组：用来证明「站住」确实是靠人工力，不是靠别的）。</summary>
        None = 2,
    }

    /// <summary>
    /// 教程 §三 的「人工力」层。**只碰 hips 这一个刚体**，不写任何 targetRotation
    /// （姿势仍然是 `ClumsyPoseDriver` 一个人的活，保持「一个关节一个写入者」）。
    ///
    /// 三件事，都来自他的 `PhysicsModule`：
    ///   ① `ApplyCustomDrag()` —— **二次角阻尼**（视频没讲，代码里有）。比线性 angularDrag 好在
    ///      「慢动作几乎不减速、只掐掉快抖」，正是压布娃娃抖动想要的性质。
    ///   ② `BALANCE_MODE.UPRIGHT_TORQUE` 的直立力矩 —— 世界空间力矩，大小 = 上限 × 曲线(偏离角/180°)。
    ///   ③ 同一条分支里的转身力矩 —— `AddRelativeTorque(0, dirPercent * rotationTorque, 0)`。
    ///      **它在方法 2 里是被 hips 关节的 yaw 弹簧顺带做掉的**（Root 只转 yaw，弹簧把 hips 的
    ///      yaw 拉过去），所以换到方法 3 之后必须自己补上，否则角色不能转身。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClumsyBalance : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;

        // ---- 诊断读数（`ClumsyRagdollBench.BalanceReport()` 用）----

        /// <summary>当前倾斜角（度）：骨盆「出生朝上」的那根轴与世界竖直的夹角。</summary>
        public float TiltDegrees { get; private set; }

        /// <summary>本步实际施加的直立力矩大小（N·m）。方法 2 / None 下恒为 0。</summary>
        public float UprightTorque { get; private set; }

        /// <summary>本步实际施加的转身力矩大小（N·m，带符号）。</summary>
        public float TurnTorque { get; private set; }

        /// <summary>朝向误差（度，带符号）：骨盆前方 → Root 前方。</summary>
        public float YawErrorDegrees { get; private set; }

        /// <summary>二次角阻尼这一步从骨盆上扣掉的角速度（rad/s）。</summary>
        public float DragRemoved { get; private set; }

        /// <summary>力矩上限（N·m），= UprightTorqueMargin × π × M·g·h_com。诊断台要看它。</summary>
        public float TorqueCeiling { get; private set; }

        // ---- 内部 ----

        float _turnTorqueScale;
        float _uprightTorqueScale;

        /// <summary>阻尼系数 ÷ 惯量。每个刚体用同一个比值（见 ApplyBodyTorque）。</summary>
        float _damperSlope;

        /// <summary>每个刚体最大主惯量（kg·m²）。整具身体的力矩按它分发（见 ApplyBodyTorque）。</summary>
        float[] _inertiaRaw;
        float _inertiaTotal;

        public void Setup(ClumsyRagdoll ragdoll, RagdollRecipe recipe)
        {
            Ragdoll = ragdoll;
            Recipe = recipe;
            RefreshScales();
        }

        void Start()
        {
            if (Ragdoll == null)
                Ragdoll = GetComponent<ClumsyRagdoll>();
            if (Recipe == null && Ragdoll != null)
                Recipe = Ragdoll.Recipe;
            RefreshScales();
        }

        /// <summary>
        /// 把「模型无关」的配方常数换算成本模型的绝对量。
        ///
        /// **为什么必须换算**：教程的 `uprightTorque = 10000` / `rotationTorque = 500` 是**他那具模型的
        /// 绝对值**，和 `HipsSpring = 750` 是同一类东西（`ClumsyRagdollRecipe` 的 `HipsSpringMargin`
        /// 注释里已经为弹簧做过同一件事：750 → margin × M·g·h_com）。
        /// 这里的换算式：
        ///
        ///   τ_max = margin × π × M·g·h_com
        ///
        /// 推导：直立力矩必须压过「整具身体绕脚底接触点转」的重力力矩 M·g·h_com·sinθ ≈ M·g·h_com·θ（弧度）。
        /// 而曲线在原点附近的斜率是 1/180（百分比 = 角度/180），于是
        ///   τ(θ) = τ_max·θ_deg/180 = τ_max·θ_rad/π  →  dτ/dθ = τ_max/π。
        /// 令 dτ/dθ &gt; margin × M·g·h_com 就得到上式。**margin 与弹簧那一路是同一个口径**，
        /// 所以 `HipsSpringMargin = 8` 与 `UprightTorqueMargin = 8` 是可比的（一个是 N·m/rad、一个是 N·m 上限）。
        ///
        /// 阻尼项：与 `RagdollJointConfigurator` 的关节阻尼同一个式子（2ζ√(k·I)），
        /// 只是 k 换成「力矩函数在原点的斜率」k_eff = τ_max/π，I 换成**整具身体**的惯量
        /// —— 因为力矩是分发到整具身体的（见 ApplyBodyTorque），阻尼也必须按同一个口径走。
        /// </summary>
        public void RefreshScales()
        {
            if (Ragdoll == null || Recipe == null || Ragdoll.TotalMass <= 0f)
            {
                _uprightTorqueScale = 0f;
                _turnTorqueScale = 0f;
                _damperSlope = 0f;
                TorqueCeiling = 0f;
                return;
            }

            float baseTorque = Ragdoll.TotalMass * Mathf.Abs(Recipe.GravityY) * Ragdoll.CenterOfMassHeight;
            _uprightTorqueScale = Recipe.UprightTorqueMargin * Mathf.PI * baseTorque;
            _turnTorqueScale = Recipe.TurnTorqueMargin * Mathf.PI * baseTorque;
            TorqueCeiling = _uprightTorqueScale;

            // ---- 惯量表（分发用）----
            _inertiaTotal = 0f;
            _inertiaRaw = null;
            if (Ragdoll.Parts != null && Ragdoll.Parts.Count > 0)
            {
                _inertiaRaw = new float[Ragdoll.Parts.Count];
                for (int i = 0; i < Ragdoll.Parts.Count; i++)
                {
                    ClumsyPart p = Ragdoll.Parts[i];
                    float v = MaxInertia(p);
                    _inertiaRaw[i] = v;
                    _inertiaTotal += v;
                }
            }

            float kEff = _uprightTorqueScale / Mathf.PI;
            _damperSlope = Recipe.UprightTorqueDampingRatio <= 0f || _inertiaTotal <= 1e-9f
                ? 0f
                : 2f * Recipe.UprightTorqueDampingRatio
                  * Mathf.Sqrt(Mathf.Max(kEff / _inertiaTotal, 1e-6f));
        }

        static float MaxInertia(ClumsyPart p)
        {
            if (p == null || p.Body == null)
                return 0f;
            Vector3 it = p.Body.inertiaTensor;
            return Mathf.Max(it.x, Mathf.Max(it.y, it.z));
        }

        void FixedUpdate()
        {
            if (Ragdoll == null || Recipe == null)
                return;
            if (Ragdoll.Hips == null || Ragdoll.Hips.Body == null)
                return;

            Rigidbody hips = Ragdoll.Hips.Body;

            ApplyCustomDrag(hips);

            if (Recipe.Balance != BalanceMode.UprightTorque)
            {
                UprightTorque = 0f;
                TurnTorque = 0f;
                TiltDegrees = Ragdoll.UprightAngleDegrees;
                return;
            }

            ApplyUprightTorque(hips);
            ApplyTurnTorque(hips);
        }

        /// <summary>
        /// 教程 `PhysicsModule.ApplyCustomDrag()`（**视频完全没讲**，是从仓库代码里读出来的）：
        ///
        /// <code>
        /// var angVel = PhysicalTorso.angularVelocity;
        /// angVel -= (Mathf.Pow(angVel.magnitude, 2) * customTorsoAngularDrag) * angVel.normalized;
        /// PhysicalTorso.angularVelocity = angVel;
        /// </code>
        ///
        /// 二次 = 慢速时几乎不扣、高速时扣得狠，正好是「压抖不压动作」。
        /// **一处与教程代码不同的地方**：原式在 |ω| &gt; 1/d 时会把角速度**扣成反向**
        /// （d = 0.05 时是 20 rad/s，本模型 maxAngularVelocity = 25 够得着）。这里把扣减量夹到 |ω|，
        /// 也就是最快扣到 0、绝不反号 —— 反号就是抖动本身。
        /// </summary>
        void ApplyCustomDrag(Rigidbody hips)
        {
            DragRemoved = 0f;
            float d = Recipe.CustomTorsoAngularDrag;
            if (d > 0f)
                DragRemoved = QuadraticDrag(hips, d);

            // 教程只对躯干做；四肢那一份是本工程加的可选项（默认 0 = 关）。
            float dl = Recipe.CustomLimbAngularDrag;
            if (dl <= 0f || Ragdoll.Parts == null)
                return;
            for (int i = 0; i < Ragdoll.Parts.Count; i++)
            {
                ClumsyPart p = Ragdoll.Parts[i];
                if (p == null || p.IsPelvis || p.Body == null)
                    continue;
                QuadraticDrag(p.Body, dl);
            }
        }

        /// <summary>二次角阻尼本体；返回从角速度里扣掉的大小（rad/s）。扣减量夹到 |ω|，见上。</summary>
        static float QuadraticDrag(Rigidbody rb, float d)
        {
            Vector3 w = rb.angularVelocity;
            float m = w.magnitude;
            if (m < 1e-5f)
                return 0f;
            float removed = Mathf.Min(m * m * d, m);
            rb.angularVelocity = w * (1f - removed / m);
            return removed;
        }

        /// <summary>
        /// 教程 `BALANCE_MODE.UPRIGHT_TORQUE`：
        /// <code>
        /// var balancePercent = Vector3.Angle(PhysicalTorso.transform.up, Vector3.up) / 180;
        /// balancePercent = uprightTorqueFunction.Evaluate(balancePercent);
        /// var rot = Quaternion.FromToRotation(PhysicalTorso.transform.up, Vector3.up).normalized;
        /// PhysicalTorso.AddTorque(new Vector3(rot.x, rot.y, rot.z) * uprightTorque * balancePercent);
        /// </code>
        ///
        /// 三处与教程代码的差异，都是有意为之：
        ///   ① 作用对象：他用 `PhysicalTorso.transform.up`，而 `PhysicalTorso` 取的就是
        ///      **HumanBodyBones.Hips**（`ActiveRagdoll.OnValidate`）—— 与本工程的骨盆是同一个东西。
        ///      但「朝上」那根轴不能用 Transform.up：本模型的骨盆骨局部轴不是世界轴，
        ///      所以用出生时标定下来的 `Ragdoll.PelvisUpWorld`（与 `Ragdoll.Upright` 同一个轴）。
        ///   ② 力矩方向：他把 `FromToRotation` 四元数的 (x,y,z) 当向量用（= 轴 × sin(θ/2)，只借个方向）。
        ///      这里直接写 `Cross(up, worldUp)`，同一个方向，但含义是明确的。
        ///   ③ 曲线：他用 Inspector 里手画的 `AnimationCurve`。配方是纯数据类（没有 Inspector），
        ///      所以换成同样能表达「函数由我们选」的两参族：`shape = p^exponent`，p = 角度/180。
        ///      exponent = 1 → 与弹簧同形（线性）；&lt; 1 → 近直立就顶满、远处饱和（肌肉的样子）；
        ///      &gt; 1 → 近直立很软、远了才硬。
        /// </summary>
        void ApplyUprightTorque(Rigidbody hips)
        {
            Vector3 up = Ragdoll.PelvisUpWorld;
            float angle = Vector3.Angle(up, Vector3.up);
            TiltDegrees = angle;

            if (angle < 0.05f)
            {
                UprightTorque = 0f;
                ApplyDampingOnly(hips);
                return;
            }

            Vector3 axis = Vector3.Cross(up, Vector3.up).normalized;   // 把 up 转回世界上方的那根轴
            float percent = Mathf.Clamp01(angle / 180f);
            float shape = Mathf.Pow(percent, Mathf.Max(0.01f, Recipe.UprightCurveExponent));
            Vector3 torque = axis * (_uprightTorqueScale * shape);
            UprightTorque = torque.magnitude;
            ApplyBodyTorque(torque, true);
        }

        void ApplyDampingOnly(Rigidbody hips)
        {
            ApplyBodyTorque(Vector3.zero, true);
        }

        /// <summary>
        /// 把「整具身体的一个力矩」真实施加下去。
        ///
        /// **★ 这里是本工程与教程代码最实质的一处分歧，而且是实测出来的。**
        /// 教程把力矩直接加在 `PhysicalTorso`（= hips 一根刚体）上：
        /// `PhysicalTorso.AddTorque(...)`。在本工程的比例下这么做**会自激振荡**：
        /// 骨盆自身的惯量只有 ~0.08 kg·m²，而撑住整具身体需要的力矩斜率是
        /// k_eff = margin × M·g·h_com ≈ 3500 N·m/rad，于是骨盆的角加速度量级是
        /// ω = √(k_eff/I) ≈ 200 rad/s，而 `Physics.Simulate` 的显式积分在 ω·dt &gt; 2 时发散
        /// （本工程 dt = 0.02 → 上界 ≈ 100 rad/s）。**关节驱动没有这个问题，因为它是求解器隐式解的。**
        ///
        /// 实测（2026-10-01，BalanceReport 的倾 15° 测试）：只加在骨盆上时，
        /// 倾角 15° → 0° → −13° → −42° 逐周期放大，最终倒地（末 up 0.18）。
        ///
        /// 解法就是「按惯量把同一个力矩分发到所有刚体」：此时每个刚体看到的
        /// ω·dt = √(k_eff / I_总) × dt，I_总 ≈ 0.4 kg·m² → ≈ 1.7 &lt; 2，稳定。
        /// 物理上也更对：**「一个作用在整具身体上的力矩」本来就该按惯量分配**，
        /// 加在一根骨头上只是教程那具模型的简化（他的 `PhysicalTorso` 接近整具身体的惯量）。
        /// 关掉这个开关（`Recipe.UprightTorqueOnWholeBody = false`）就回到教程的原样。
        /// </summary>
        void ApplyBodyTorque(Vector3 baseTorque, bool withDamping)
        {
            if (Ragdoll.Parts == null)
                return;

            bool whole = Recipe.UprightTorqueOnWholeBody && _inertiaRaw != null;
            float invTotal = _inertiaTotal > 1e-9f ? 1f / _inertiaTotal : 0f;

            for (int i = 0; i < Ragdoll.Parts.Count; i++)
            {
                ClumsyPart p = Ragdoll.Parts[i];
                if (p == null || p.Body == null)
                    continue;
                if (!whole && !p.IsPelvis)
                    continue;

                float inr = (_inertiaRaw != null && i < _inertiaRaw.Length) ? _inertiaRaw[i] : 0f;
                Vector3 t = whole ? baseTorque * (inr * invTotal) : baseTorque;
                if (withDamping && _damperSlope > 0f)
                    t -= TipVelocity(p.Body) * (_damperSlope * inr);
                p.Body.AddTorque(t, ForceMode.Force);
            }
        }

        /// <summary>「要倒下去」的那部分角速度：去掉绕世界竖直的旋转（转身不算倒）。</summary>
        static Vector3 TipVelocity(Rigidbody b)
        {
            Vector3 w = b.angularVelocity;
            return w - Vector3.Project(w, Vector3.up);
        }

        /// <summary>
        /// 教程同一条分支里的转身力矩（**方法 2 里不需要它**，见类注释）。
        /// <code>PhysicalTorso.AddRelativeTorque(0, directionAnglePercent * rotationTorque, 0);</code>
        /// 他把方向写成**本体局部**的 Y 轴；人直立时局部 Y ≈ 世界 Y，这里直接用世界 Y
        /// （歪着的时候世界 Y 才是真正的「转身轴」）。
        /// </summary>
        void ApplyTurnTorque(Rigidbody hips)
        {
            Vector3 forward = Ragdoll.PelvisForwardWorld;
            Vector3 wanted = Ragdoll.Root != null ? Ragdoll.Root.forward : forward;
            forward.y = 0f;
            wanted.y = 0f;
            if (forward.sqrMagnitude < 1e-8f || wanted.sqrMagnitude < 1e-8f)
            {
                TurnTorque = 0f;
                YawErrorDegrees = 0f;
                return;
            }

            float error = Vector3.SignedAngle(forward, wanted, Vector3.up);
            YawErrorDegrees = error;

            // 「死区」：正对面时那点残差不要一直给力矩（会变成持续自转的抖）。
            if (Mathf.Abs(error) < Recipe.TurnDeadZoneDegrees)
            {
                TurnTorque = 0f;
                return;
            }

            float signed = (error / 180f) * _turnTorqueScale;
            TurnTorque = signed;
            ApplyBodyTorque(Vector3.up * signed, false);;
            TurnTorque = signed;
        }
    }
}
