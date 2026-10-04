using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    public enum ClumsyMovementMode
    {
        AutomaticGait,
        ManualFootStep,
    }

    /// <summary>
    /// 教程 P3 步8：挂在每一个部件上，任何部位砸到东西都算落地。
    /// 原文：<c>void OnCollisionEnter(Collision c) { playerController.isGrounded = true; }</c>
    /// 这里多接一个 OnCollisionStay —— 教程只写了 Enter，靠控制器在起跳时清标志；
    /// 有 Stay 以后「离地」也能自动纠正，行为与教程一致但不会卡在 true。
    /// </summary>
    public sealed class LimbCollision : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;

        void OnCollisionEnter(Collision collision)
        {
            if (Ragdoll != null)
                Ragdoll.IsGrounded = true;
        }

        void OnCollisionStay(Collision collision)
        {
            if (Ragdoll != null)
                Ragdoll.IsGrounded = true;
        }
    }

    /// <summary>
    /// 教程 P3：给 hips 这个 Rigidbody 加力。
    /// 原文把脚本挂在 hips 上、用 <c>hips.transform.forward</c> 当前方；
    /// 骨骼模型的 forward 不是角色朝向（mixamo 骨骼的局部轴是任意的），
    /// 所以这里改用 P6 的 Root（只转 yaw 的竖直参考系）当前方 —— 与教程「朝向变了也一直往前」的意图一致。
    /// 力的大小也从绝对值（speed = 150）换成加速度域，避免绑死在作者模型的尺寸/质量上（教程 §5.5）。
    /// </summary>
    public sealed class ClumsyController : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;
        public ClumsyMovementMode MovementMode = ClumsyMovementMode.AutomaticGait;
        public bool ManualStepActive { get; set; }

        /// <summary>移动方向参考系。留空则用 Ragdoll.Root。</summary>
        public Transform Facing;

        /// <summary>输入源；为空 = 不收输入（诊断台上用）。</summary>
        public IClumsyInput Source;

        /// <summary>x = 右（A/D），y = 前（W/S）。</summary>
        public Vector2 Move;
        public bool Sprint;
        public bool JumpPressed;

        public float LastJumpTime = -99f;
        /// <summary>★ 2026-10-01：这一物理步里托举层到底有没有生效（诊断台与 HUD 用）。</summary>
        public bool CarryActiveNow { get; private set; }

        /// <summary>★ 2026-10-01：跳 = 把角色扔出去。起跳后还要宽限的物理步数。</summary>
                int _carryOffSteps;

        /// <summary>★ 2026-10-01：起跳输入缓冲还剩几个物理步（见 Recipe.JumpBufferSeconds）。</summary>
        int _jumpBufferSteps;

        /// <summary>★ 2026-10-01：滞空窗口还剩几个物理步（见 Recipe.JumpGravityScale）。</summary>
        int _hangSteps;

        /// <summary>★ 2026-10-01：本跳期间是否真的离过地（用来判断什么时候清 JustJumped）。</summary>
        bool _wasAirborne;

        // ==================================================================
        // ★ 2026-10-01 · 脚种植步态（owner 口径）
        // ==================================================================

        /// <summary>相位 0..1（一个周期 = 左右各一步）。姿势驱动读它来算腿角。</summary>
        public float GaitPhase { get; private set; }

        /// <summary>正在摆动的那只脚（"footl"/"footr"）。</summary>
        public string SwingFoot { get; private set; }

        /// <summary>正在种植（粘在地上）的那只脚。</summary>
        public string PlantFoot { get; private set; }

        /// <summary>★ 2026-10-01：步态**这一刻**是不是在跑。停走之后姿势驱动必须靠它把腿/踝放回去 ——
        /// 之前只看 `PlantFoot != null`，而停走时它保留着上一次的字符串 ⇒ 停住之后腿还是迈步姿势、
        /// 踝的反馈还在调 ⇒ 实测站着会慢慢漂 1 m。</summary>
        public bool GaitActive { get; private set; }

        // ---- ★ 2026-10-02 转身 ----

        /// <summary>转身指令 −1（左）..+1（右），由 A/D 给。</summary>
        public float TurnDrive { get; private set; }

        /// <summary>交给相机的 yaw 速率指令（度 / 秒）。**yaw 的权威在相机那边**（它每帧写 Root.rotation），
        /// 所以控制器只出指令、由相机去累加到 Yaw 上。</summary>
        public float TurnRateCommand { get; private set; }

        /// <summary>本帧**实际**转过的角速度（度 / 秒，带符号）。转身步态的相位按它推进。</summary>
        public float TurnRateNow { get; private set; }

        /// <summary>腿的**前后摆幅**比例：走路 = 1，原地转身 = `TurnGaitStrideScale`（转身不迈步、只抬脚）。</summary>
        public float GaitStrideScale { get; private set; }

        /// <summary>手动迈步活动层用来指定下一次先摆哪只脚。</summary>
        public void RequestSwingFoot(string foot)
        {
            if (foot == "footr")
                GaitPhase = 0.5f;
            else
                GaitPhase = 0f;
        }

        float _turnPhase;
        float _lastRootYaw;
        bool _hasRootYaw;

        /// <summary>摆动进度 0..1：0 = 刚离地（脚在后），1 = 刚要落地（脚在前）。</summary>
        public float SwingProgress { get; private set; }

        /// <summary>种植脚的水平锚点（世界坐标，y 恒为 0）。诊断台用。</summary>
        public Vector3 PlantAnchor { get; private set; }

        /// <summary>此刻种植脚偏离锚点多远（m）。«粘住了»应当 ≈ 0。诊断台用。</summary>
        public float PlantSlipNow { get; private set; }

        /// <summary>整个走路过程里种植脚的最大滑移（m）。诊断台用。</summary>
        public float PlantSlipMax { get; private set; }

        readonly Vector3[] _anchor = new Vector3[2];
        readonly bool[] _wasPlanted = new bool[2];
        PhysicsMaterial _gripMat;

        /// <summary>★ 2026-10-01：平滑后的前进速度（相位推进用，避免被每步的冲劲带快）。</summary>
        float _vFwdSmooth;

        /// <summary>跳跃键上一帧是否按着 —— 用来把「按住」变成「只触发一次」。</summary>
        bool _jumpHeldLatch;
        public float SpeedMeasured { get; private set; }

        // ==================================================================
        // ★ 2026-10-02 · 双手搬运的**重量反馈**（见 Recipe 的「手部 · 双手搬运」段）
        // ==================================================================

        /// <summary>搬运层（`ClumsyRagdollGame.Build` / 诊断台接线）。null = 没接，不折减。</summary>
        public ClumsyCarry Carry;

        /// <summary>此刻手上搬着的质量（kg，0 = 空手）。诊断用。</summary>
        public float CarryHeldMass { get; private set; }

        /// <summary>本帧用在**速度与步幅**上的折减系数（1 = 空手）。诊断用。</summary>
        public float CarrySpeedScale { get; private set; }

        Vector3 _lastPosition;
        float _speedTimer;

        void Start()
        {
            if (Ragdoll == null)
                Ragdoll = GetComponent<ClumsyRagdoll>();
            if (Recipe == null && Ragdoll != null)
                Recipe = Ragdoll.Recipe;
            if (Facing == null && Ragdoll != null)
                Facing = Ragdoll.Root;
            if (Ragdoll != null && Ragdoll.Hips != null && Ragdoll.Hips.Body != null)
                _lastPosition = Ragdoll.Hips.Body.position;
        }

void FixedUpdate()
        {
            if (Ragdoll == null || Ragdoll.Hips == null || Ragdoll.Hips.Body == null || Recipe == null)
                return;

            if (Source != null)
            {
                Move = Source.Move;
                Sprint = Source.Sprint;
                // ⚠️ 起跳只在「按下的那一帧」。
                // 之前这里直接传的是「按住」状态，而 IsGrounded 会被 LimbCollision.OnCollisionStay
                // 每帧刷回 true —— 于是按住空格 = 脚还没离地就每步补一个 3 m/s 冲量，
                // 叠成好几发，跳得比单发高好几倍。**这就是「跳太高」的真正原因**，不是 JumpSpeed 太大。
                bool jumpHeld = Source.JumpHeld;
                JumpPressed = jumpHeld && !_jumpHeldLatch;
                _jumpHeldLatch = jumpHeld;
            }

            // ★ 2026-10-02 双手搬运的**重量反馈**（owner：「真的被压下去」）：
            //   载重比 = 手上搬着的质量 / 角色总质量 ⇒ 走多慢、步幅多小。
            //   ⚠️ **三处必须按同一个比例缩**：指令速度 / 相位步长 / 腿的摆幅。
            //   只缩其中一两条的话，腿扫的比例和身体走的比例对不上 ⇒ 脚在地上蹭
            //   （后退那一轮踩过同一个坑，见交接 §十九：「三处不同比例必滑」）。
            //   做法与后退一致：把它并进 `GaitStrideScale`，三处就自动同比例了。
            CarryHeldMass = 0f;
            float carryScale = 1f;
            if (Carry != null && Carry.IsHolding)
            {
                CarryHeldMass = Carry.HeldMass;
                carryScale = Mathf.Clamp(1f / (1f + Carry.LoadRatio * Recipe.CarryLoadSpeedPenalty),
                                         Recipe.CarryLoadMinSpeedScale, 1f);
            }
            CarrySpeedScale = carryScale;

            Rigidbody hips = Ragdoll.Hips.Body;

            Vector3 forward = Vector3.forward;
            Vector3 right = Vector3.right;
            if (Facing != null)
            {
                forward = Facing.forward;
                right = Facing.right;
            }
            forward.y = 0f;
            right.y = 0f;
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.forward;
            forward.Normalize();
            right.Normalize();

            // 教程 P3 是「一直加力」（hips.AddForce(hips.transform.forward * speed)），
            // 没有速度上限 —— 在他那个小模型上靠摩擦自然限速。这里保留同一个力模型，
            // 只把力按「离目标速度还差多远」缩放，否则 62 kg 的浣熊会一路加速到穿地。
            float runScale = Sprint ? Recipe.SprintScale : 1f;
            float topSpeed = Recipe.MaxSpeed * runScale * carryScale;   // ★ 搬运时更慢
            float topStrafe = Recipe.MaxStrafeSpeed * runScale;
            Vector3 wanted = forward * (Move.y * topSpeed) + right * (Move.x * topStrafe);
            float throttle = 0f;
            if (wanted.sqrMagnitude > 1e-6f)
            {
                Vector3 current = Vector3.ProjectOnPlane(hips.linearVelocity, Vector3.up);
                throttle = Mathf.Clamp01(1f - current.magnitude / Mathf.Max(wanted.magnitude, 0.01f));
            }

            // ★ 2026-10-01：走路改成**脚种植式**（owner 口径：一只脚往前迈、粘在地上，
            //   这条腿拉着身体向前移动）。开的时候前进力来自「被拉到种植脚前方」的弹簧，
            //   不再用下面那套髖上加速度力（那套留着给侧移 / 关掉步态时用）。
            // ★ 2026-10-02 **后退**：这里原来取的是 |Move.y|，**符号被丢掉** ⇒ 脚种植步态里那条
            //   «拉力»（③ 目标速度弹簧）永远朝前：按 S 时腿在往后退、身体却被往前拽。
            //   现在把**带符号**的输入一路传下去；`forwardDrive`（幅值）只用来判「有没有输入」。
            float forwardSigned = Mathf.Clamp(Move.y, -1f, 1f);
            float forwardDrive = Mathf.Abs(forwardSigned);

            // ★ 2026-10-02 **转身**：
            //   ① 本帧实际转了多少（Root 的 yaw 由相机写，量它的差分）；
            //   ② A/D 给出的转身指令（`TurnWithAD` 关掉就回到老的横移）；
            //   ③ 交给相机的速率指令 —— **yaw 的权威在相机那边**，控制器只出指令。
            float rootYaw = Ragdoll.Root != null ? Ragdoll.Root.eulerAngles.y : 0f;
            if (_hasRootYaw)
            {
                float raw = Mathf.DeltaAngle(_lastRootYaw, rootYaw) / Mathf.Max(Time.fixedDeltaTime, 1e-4f);
                TurnRateNow = Mathf.Lerp(TurnRateNow, raw, 0.4f);
            }
            _lastRootYaw = rootYaw;
            _hasRootYaw = true;
            TurnDrive = Recipe.TurnWithAD ? Mathf.Clamp(Move.x, -1f, 1f) : 0f;
            TurnRateCommand = TurnDrive * Recipe.TurnKeySpeed;
            // ★ 2026-10-02：**转身不绕相机** —— 控制器自己把 `Root` 的 yaw 推上去。
            //   原来是指令交给相机去累加，但**相机台是另一个 GameObject**（`ClumsyRagdollGame` 里
            //   `new GameObject("相机台")`），`GetComponent<ClumsyController>()` 拿到 null
            //   ⇒ `Root` 的朝向从没变过 ⇒ 身体不转、转身步态的相位也不推进（只有一条腿在空中抖）。
            //   写法用«读当前值 + 加增量»：相机那边（鼠标）也做同样的事，所以两边不会互相覆盖。
            if (Ragdoll.Root != null && Mathf.Abs(TurnRateCommand) > 0.01f)
            {
                float yawNext = Ragdoll.Root.eulerAngles.y + TurnRateCommand * Time.fixedDeltaTime;
                Ragdoll.Root.rotation = Quaternion.Euler(0f, yawNext, 0f);
            }
            // 鼠标转过来的那部分也算«正在转»（这样鼠标转身也走同一套脚种植转身）
            bool turningNow = Mathf.Abs(TurnRateNow) > Recipe.TurnReferenceRate * 0.12f;
            bool manualMode = MovementMode == ClumsyMovementMode.ManualFootStep;
            bool turnOn = !manualMode && Recipe.UseFootGait && (Mathf.Abs(TurnDrive) > 0.05f || turningNow);
            // 手动脚步模式由 ClumsyStepActivity 驱动脚和骨盆；这里保留本控制器的
            // 托举、接地和稳定层，但绝不再进入旧的自动脚步推进。
            bool gaitOn = !manualMode && Recipe.UseFootGait && forwardDrive > 0.05f;
            if (gaitOn)
            {
                // ★ 搬运时步幅也按同一个比例收（姿势驱动读的就是这个量，见 ClumsyPoseDriver 的 gStride）。
                GaitStrideScale = carryScale;
                StepFootGait(hips, forward, forwardSigned);
                // 横移：`TurnWithAD` 开着时 A/D 拿去做转身了，这里就不再横移
                if (!manualMode && !Recipe.TurnWithAD && throttle > 0f && Mathf.Abs(Move.x) > 0.05f)
                {
                    hips.AddForce(right * (Move.x * Recipe.StrafeAccel)
                        * (throttle * Ragdoll.TotalMass * Recipe.MoveForceMassScale), ForceMode.Force);
                }
            }
            else if (turnOn)
            {
                // ★ 原地 / 转着走：走**脚种植转身**（下面那个方法）
                GaitStrideScale = Recipe.TurnGaitStrideScale;
                StepTurnGait(hips);
            }
            else
            {
                GaitStrideScale = 1f;
                GaitActive = false;
                _wasPlanted[0] = false;
                _wasPlanted[1] = false;
                // 停下来时两只脚都回到低摩擦材质（不然上一帧的种植脚还带着高摩擦，站着会歪着漂）
                ClumsyPart fL2 = Ragdoll.Find("footl");
                ClumsyPart fR2 = Ragdoll.Find("footr");
                if (fL2 != null && fL2.Shape != null) fL2.Shape.sharedMaterial = Ragdoll.SlideMaterial;
                if (fR2 != null && fR2.Shape != null) fR2.Shape.sharedMaterial = Ragdoll.SlideMaterial;
                Ragdoll.SetLegSpringScale("footl", 1f);
                Ragdoll.SetLegSpringScale("footr", 1f);
                if (!manualMode && throttle > 0f)
                {
                    Vector3 accel = forward * (Move.y * Recipe.MoveAccel)
                                  + right * ((Recipe.TurnWithAD ? 0f : Move.x) * Recipe.StrafeAccel);
                    hips.AddForce(accel * (throttle * Ragdoll.TotalMass * Recipe.MoveForceMassScale), ForceMode.Force);
                }
                // ★ 没有前进输入又站在地上：把水平速度刹掉（不然会以 0.15 m/s 匀速漂走）
                if (Recipe.IdleHorizontalDamping > 0f && throttle <= 0.01f && Ragdoll.IsGroundedNow)
                {
                    Vector3 hvel = hips.linearVelocity;
                    Vector3 brake = new Vector3(-hvel.x, 0f, -hvel.z) * Recipe.IdleHorizontalDamping;
                    brake = Vector3.ClampMagnitude(brake, 25f);
                    hips.AddForce(brake * Ragdoll.TotalMass, ForceMode.Force);
                }
            }

            // ★ 2026-10-01 · 托举层（见 Recipe 的「托举层」段）：
            //   胯部受一个足够强的力把身体托住 —— 角色**不是靠双脚支撑站立的**，
            //   腿和躯干只负责挂在骨盆下面当面条（要靠连接传动）。
            if (_carryOffSteps > 0)
                _carryOffSteps--;

            bool carryActive = Recipe.CarryEnabled && Ragdoll != null && Ragdoll.Hips != null;
            // ★ 跳 = 把角色扔出去（owner 口径）：**托举只在接地时生效**。
            //   离地 ⇒ 托举与头气球全撤（空中是自由的布娃娃）；落地 ⇒ 接住。
            //   ⚠️ 不要再用 Time.time 计时判挂起 —— 见 Recipe.CarrySuspendOnJump 的注释。
            if (carryActive && Recipe.CarrySuspendOnJump
                && (_carryOffSteps > 0 || !Ragdoll.IsGroundedNow))
                carryActive = false;
            CarryActiveNow = carryActive;

            if (carryActive)
            {
                float mass = Ragdoll.TotalMass;
                float targetY = Ragdoll.Hips.RestPosition.y * Recipe.CarryHeightScale;

                // ★ 找地：脚悬空多少就把目标压低多少 ⇒ 正常移动时至少一只脚连着地面。
                // ★ 2026-10-02：**把«找地»换成几何解**（见 Recipe.CarryPelvisFromStanceLeg）。
                //   原来那条是经验律（`targetY -= 脚离地量 × 4`），它做不到的事是：
                //   **支撑腿摆到前后两端时，骨盆必须降到«这条腿够得着地面»的高度**。
                //   几何上就是：骨盆高 = 踝的出生高度 + √(髋到踝长² − 水平偏移²)。
                //   实测（`GaitDutyByPhaseReport`）原来的骨盆起伏 10.6 cm 是有的，但**时机不对** ——
                //   声明的支撑窗里，支撑脚真踩在地上的只有 52%，40% 的帧两只脚都在 1 cm 以上。
                if (Recipe.CarryPelvisFromStanceLeg && GaitActive && !string.IsNullOrEmpty(PlantFoot))
                {
                    ClumsyPart sf = Ragdoll.Find(PlantFoot);
                    if (sf != null && sf.Body != null)
                    {
                        Vector3 fp = sf.Body.position;
                        float dx = fp.x - hips.position.x;
                        float dz = fp.z - hips.position.z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        float legLen = Ragdoll.Hips.RestPosition.y - sf.RestPosition.y;
                        float reach = Mathf.Sqrt(Mathf.Max(0f, legLen * legLen - d * d));
                        targetY = sf.RestPosition.y + reach;
                        // 夹住：不许比出生高度低超过 CarryPelvisMaxDip（不然会整个人坐下去）
                        targetY = Mathf.Max(targetY, Ragdoll.Hips.RestPosition.y - Recipe.CarryPelvisMaxDip);
                    }
                }
                else if (Recipe.CarryGroundSeek)
                {
                    float footLow = Ragdoll.LowestSupportY;
                    if (Recipe.CarryGroundSeekSupportOnly && GaitActive)
                    {
                        ClumsyPart seekFoot = Ragdoll.Find(PlantFoot);
                        if (seekFoot != null && seekFoot.Shape != null)
                            footLow = seekFoot.Shape.bounds.min.y;
                    }
                    if (!float.IsInfinity(footLow))
                        targetY -= (footLow - Ragdoll.GroundY) * Recipe.CarryGroundSeekGain
                                 + Recipe.CarryGroundPress;
                }
                // ★ 2026-10-02 重量反馈 · 骨盆额外下沉（见 Recipe.CarryLoadSagPerKg）。
                //   托举层本来就会因为多挂了重量而下沉（弹簧静差），这一档是**额外**、
                //   可控的那部分 —— 用来把「被压下去」调得看得见又不过分。
                if (CarryHeldMass > 0f && Recipe.CarryLoadSagPerKg > 0f)
                    targetY -= Mathf.Min(Recipe.CarryLoadSagMax, Recipe.CarryLoadSagPerKg * CarryHeldMass);

                Vector3 p = hips.position;
                Vector3 v = hips.linearVelocity;
                Vector3 a = Vector3.zero;
                a.y = -Recipe.GravityY * Recipe.CarryAntiGravity
                    + (targetY - p.y) * Recipe.CarryStiffness
                    - v.y * Recipe.CarryDamping;
                a.x = -v.x * Recipe.CarryHorizontalDamping;
                a.z = -v.z * Recipe.CarryHorizontalDamping;
                a = Vector3.ClampMagnitude(a, Recipe.CarryMaxAccel);
                hips.AddForce(a * mass, ForceMode.Force);

                // ★ 头的气球（见 Recipe 的「托举层 · 头」段）：
                //   骨架线性运动是 Locked 的，所以作用在头上的力会沿脊椎链传下去 ——
                //   等价于「气球拴在胯上、绳子是脊椎」，把上身一起吊住。
                if (Recipe.HeadCarryEnabled)
                {
                    ClumsyPart headPart = Ragdoll.Find("head");
                    if (headPart != null && headPart.Body != null)
                    {
                        Rigidbody hb = headPart.Body;
                        Vector3 hp = hb.position;
                        Vector3 hv = hb.linearVelocity;
                        // 目标高度：默认**相对骨盆**（头浮在骨盆上方固定距离），
                        // 否则气球会把骨盆一起拽上去、软脊椎又被折起来 ⇒ 整个人被压扁。
                        float targetHeadY = Recipe.HeadCarryRelative
                            ? hips.position.y + (headPart.RestPosition.y - Ragdoll.Hips.RestPosition.y) * Recipe.HeadCarryHeightScale
                            : headPart.RestPosition.y * Recipe.HeadCarryHeightScale;
                        Vector3 ha = Vector3.zero;
                        ha.y = -Recipe.GravityY * Recipe.HeadCarryAntiGravity
                             + (targetHeadY - hp.y) * Recipe.HeadCarryStiffness
                             - hv.y * Recipe.HeadCarryDamping;
                        ha.x = -hv.x * Recipe.HeadCarryHorizontalDamping;
                        ha.z = -hv.z * Recipe.HeadCarryHorizontalDamping;
                        ha = Vector3.ClampMagnitude(ha, Recipe.HeadCarryMaxAccel);
                        hb.AddForce(ha * hb.mass, ForceMode.Force);

                        // ★ 沿脊椎链分布（默认开）：只拉头会把软脊椎折起来，链子一起托才像「拉直的气球绳」。
                        if (Recipe.HeadCarrySpreadOnSpine)
                        {
                            ClumsyPart np = Ragdoll.Find("neck");
                            if (np != null && np.Body != null)
                                np.Body.AddForce(ha * np.Body.mass, ForceMode.Force);
                            ClumsyPart sp = Ragdoll.Find("spine");
                            if (sp != null && sp.Body != null)
                                sp.Body.AddForce(ha * sp.Body.mass, ForceMode.Force);
                        }
                    }
                }
            }

            // ★ 2026-10-02 转身：把身体**真的**转到 `Root` 的朝向上（相机只写了 Root，没写布娃娃）。
            StepYawServo(hips);

            // 用 IsGroundedNow：诊断台里 Physics.Simulate 不派发碰撞消息，IsGrounded 永远是 false
            // —— 那样连跳都跳不起来（旧的 JumpReport 就是量到「从来没起跳」）。
                        // ★ 2026-10-01 起跳输入缓冲：
            //   原判据是**单帧**的 `JumpPressed && IsGroundedNow` —— 跑动中脚有一半时间离地，
            //   玩家在那一刻按空格，这一跳就被静默丢掉了（demo 的脚本输入早就有「还在地上就一直按」
            //   的重试，键盘这条路径一直没补）。缓冲窗口内只要落上地就补跳。
            if (JumpPressed)
                _jumpBufferSteps = Mathf.Max(1, Mathf.RoundToInt(Recipe.JumpBufferSeconds / Time.fixedDeltaTime));
            if (_jumpBufferSteps > 0 && Ragdoll.IsGroundedNow)
            {
                // 教程：AddForce(new Vector3(0, jumpForce, 0)); isGrounded = false;
                // ★ 跳 = 把小浣熊朝**前上方**扔出去（owner：「跳的距离不够远」—— 原来只有竖直分量，
                //   水平距离完全靠起跳那一刻的跑速）
                // ★ 2026-10-02 后退：跳的水平分量也跟输入方向（按 S 起跳不该往前飞）
                float jumpFwd = Recipe.JumpForwardSpeed * (Move.y < -0.05f ? -1f : 1f);
                Vector3 throwImpulse = (Vector3.up * Recipe.JumpSpeed + forward * jumpFwd)
                                     * Ragdoll.TotalMass;
                hips.AddForce(throwImpulse, ForceMode.Impulse);
                Ragdoll.IsGrounded = false;
                LastJumpTime = Time.time;
                // ★ 跳 = 把角色扔出去：离地后托举自动撤（见上面的 carryActive），落地自动接住。
                _carryOffSteps = Mathf.Max(0, Recipe.CarrySuspendSteps);
                _jumpBufferSteps = 0;
                _hangSteps = Mathf.Max(0, Mathf.RoundToInt(Recipe.JumpHangSeconds / Time.fixedDeltaTime));
                Ragdoll.JustJumped = true;   // ★ 落地时踉跄打折 / 空中走跳跃姿势
            }

            // ★ 滞空：起跳后的窗口内把重力按 JumpGravityScale 缩放（<1 = 飘起来、滞空变长）
            if (_hangSteps > 0)
            {
                // ⚠️ 不要用 IsGroundedNow 提前结束窗口：起跳那一两帧脚还沾地（实测窗口被立刻清零，
                //   滞空完全没生效）—— 窗口只按步数过期，沾地的那几帧不施力就行了。
                _hangSteps--;
                if (!Ragdoll.IsGroundedNow)
                {
                    float lift = (1f - Recipe.JumpGravityScale) * Mathf.Abs(Recipe.GravityY) * Ragdoll.TotalMass;
                    if (lift > 0f)
                        hips.AddForce(Vector3.up * lift, ForceMode.Force);
                }
                // ⚠️ 只有«真的在空中待过之后又落回地面»才清标记 —— 起跳那一两帧脚还沾地，
                //   直接按 IsGroundedNow 清会把它立刻清掉（落地打折就永远不生效）。
                if (!Ragdoll.IsGroundedNow)
                    _wasAirborne = true;
                else if (_wasAirborne)
                {
                    Ragdoll.JustJumped = false;
                    _wasAirborne = false;
                }
            }
            else if (Ragdoll.IsGroundedNow && _wasAirborne)
            {
                Ragdoll.JustJumped = false;
                _wasAirborne = false;
            }
            else if (_jumpBufferSteps > 0)
            {
                _jumpBufferSteps--;
            }
            JumpPressed = false;

            // 实测速度（诊断台用）
            _speedTimer += Time.fixedDeltaTime;
            if (_speedTimer >= 0.1f)
            {
                Vector3 p = hips.position;
                SpeedMeasured = Vector3.ProjectOnPlane(p - _lastPosition, Vector3.up).magnitude / _speedTimer;
                _lastPosition = p;
                _speedTimer = 0f;
            }
        }
        /// <summary>
        /// ★ 2026-10-01：**脚种植步态的一步**（owner 口径的四个量都在这里）。
        ///
        /// owner 原话：「一只脚往前迈，粘在地上，这条腿拉着身体向前移动，在这个过程进行到一半的
        /// 时候另一只脚和地面分开，这条腿拉着脚和它自己回正，然后向前迈，重复这个过程来实现向前移动。」
        ///
        /// 一个周期两个单脚支撑窗：左脚 [0,0.5)、右脚 [0.5,1)；两只脚各自在中间那半个周期里摆动
        /// （右 [0.25,0.75)、左 [0.75,1.25)）⇒ 每个摆动窗的**中点**正好是另一只脚支撑期的中点。
        ///
        /// 四个量：
        ///   ① 相位：走得快就迈得频；不前进时相位不走（不走就不迈步）。
        ///   ② **粘住**：种植脚换成高摩擦材质 + 水平位置弹簧（锁在着地那一点），
        ///      锚点会被「拖出去超过 FootGripAnchorSlip」带着走（不把弹簧勒死）。
        ///   ③ **拉身体**：髖被弹簧拉向「种植脚前方 GaitStepLead」—— 前进力的唯一来源。
        ///   ④ 摆动腿：姿势驱动读 GaitPhase / SwingProgress / SwingFoot 自己算（见 ClumsyPoseDriver）。
        /// </summary>
        void StepFootGait(Rigidbody hips, Vector3 forward, float forwardDrive)
        {
            float dt = Time.fixedDeltaTime;
            float speedScale = Sprint ? Recipe.SprintScale : 1f;
            // ★ 2026-10-02 **后退**：`forwardDrive` 现在是**带符号**的（负 = 后退）。
            //   后退的步幅短（`BackwardStrideScale`，姿势驱动那边用它缩了髋摆幅），
            //   所以**指令速度和相位的步长都要按同一个比例缩** —— 否则腿只扫 0.6 倍、
            //   却要求身体走 1.0 倍 ⇒ 脚在地上蹭。
            // ★ 2026-10-02 搬运：`GaitStrideScale` 也并进来（它已经含搬运的折减）。
            //   于是「指令速度 / 相位步长 / 腿的摆幅」三处按**同一个比例**缩。
            float strideScale = (forwardDrive < -0.05f ? Recipe.BackwardStrideScale : 1f)
                              * Mathf.Max(0.05f, GaitStrideScale);

            // ① 相位：**按走过的距离推进**（不是按固定频率）——
            //   这样「脚落地的位置」和「身体走了多远」永远对得上；停/爬都不会在地上擦。
            //   速度下限取目标速度的 35%：起步时身体还没动，相位不能停（停了就死锁）。
            Vector3 hv0 = hips.linearVelocity;
            float vFwd = Vector3.Dot(new Vector3(hv0.x, 0f, hv0.z), forward);
            // ★ 带符号：负 = 后退（拉力也会反向）
            float targetV = Recipe.GaitSpeed * speedScale * strideScale * Mathf.Clamp(forwardDrive, -1f, 1f);
            // ⚠️ 相位**只跟真实前进速度**走。曾经给过 ±35% 的速度下限，结果相位跑得比身体快一倍
            //   （0.06 s 换一次支撑脚）—— 脚还没来得及«粘住»就被换走了，实测每步滑 15 cm。
            //   0.4 m/s 只是防死锁的底（起步瞬间 v≈0 时相位不能停）。
            // ⚠️ 用**平滑过的**前进速度：髋每步会冲一下（瞬时峰值 ~2× 均值），直接拿瞬时值推进
            //   会让相位跑快一倍（实测支撑相 0.12 s，应该是 0.24 s）—— 脚还没粘住就被换走。
            _vFwdSmooth = Mathf.Lerp(_vFwdSmooth, vFwd, 1f - Mathf.Exp(-dt / 0.15f));
            // ★ 相位按**指令速度**推进：腿按这个速率扫过 → 身体被«支撑腿 + 粘住的脚»带过去。
            //   这正是 owner 要的«这条腿拉着身体向前移动»；实测速度只作为下限保护（打滑时别越扫越离谱）。
            //   实测：按«指令速度»推进会空转 —— 腿按 2.4 m/s 扫，身体只跑到 1.4 ⇒ 看着像原地踏步。
            //   所以相位仍然**跟真实速度**；让身体跑到目标速度是 ③ 拉力的活。
            //   ⚠️ 相位的一个周期 = **两步**（左右各一），所以除的是 2×步长 ——
            //   原来只除了步长，节奏整整快一倍（实测 8–9 步/秒 = 小碎步，不是走）。
            // ★ 空中不推进相位：否则腿会在空中继续«跑步»，看起来像被扔出去失控
            // ★ 2026-10-02：相位的推进速度 —— 见 Recipe.GaitPhaseByCommand。
            //   跟实测速度 = 步态跟着身体走（自限环）；跟指令速度 = 步态是腿给的节奏、身体被拉过去。
            float phaseSpeed = Recipe.GaitPhaseByCommand
                ? Mathf.Max(Mathf.Abs(targetV), 0.4f)
                : Mathf.Max(Mathf.Abs(_vFwdSmooth), 0.4f);   // ★ 后退时 _vFwdSmooth 为负，不能让它掉到地板值
            if (Ragdoll.IsGroundedNow)
                GaitPhase += dt * phaseSpeed / Mathf.Max(0.1f, 2f * Recipe.GaitStepLength * strideScale);
            GaitPhase -= Mathf.Floor(GaitPhase);

            GaitActive = true;
            bool rightSwings = GaitPhase >= 0.25f && GaitPhase < 0.75f;
            SwingFoot = rightSwings ? "footr" : "footl";
            PlantFoot = rightSwings ? "footl" : "footr";
            SwingProgress = Mathf.Clamp01(rightSwings
                ? (GaitPhase - 0.25f) * 2f
                : ((GaitPhase < 0.25f ? GaitPhase + 1f : GaitPhase) - 0.75f) * 2f);

            int pi = PlantFoot == "footl" ? 0 : 1;
            int si = 1 - pi;
            ClumsyPart plant = Ragdoll.Find(PlantFoot);
            ClumsyPart swing = Ragdoll.Find(SwingFoot);

            // ★ 支撑腿撑住：支撑相加硬（否则拉动的只是«挂在髖下面的面条»，
            //   实测脚会一直拖在髖后面 35 cm、以身体速度在地上滑）；摆动腿恢复原档。
            Ragdoll.SetLegSpringScale(PlantFoot, Recipe.GaitSupportStiffnessScale);
            if (Recipe.GaitSupportHipStiffnessScale > 0f)
                Ragdoll.SetSpringScale("upleg" + (PlantFoot.EndsWith("l") ? "l" : "r"), Recipe.GaitSupportHipStiffnessScale);
            Ragdoll.SetLegSpringScale(SwingFoot, Recipe.GaitSwingStiffnessScale);

            // ② 种植脚：锚点 + 高摩擦 + 水平弹簧（走路与转身共用，见 PlantGrip）
            PlantGrip(plant, pi);

            // ③ 髖：被«种植腿»拉着走 —— **目标速度弹簧**。
            //   ⚠️ 原来是位置弹簧（拉向«种植脚前方 GaitStepLead»），实测走不动（0.24 m/s）：
            //   位置弹簧的稳态误差 = (阻尼/刚度)·v，要在 1–2 m/s 下持续出力就得让髖落后目标 20–40 cm
            //   —— 那不是«脚在身体前面一点»，是把目标点扔得很远。改成速度目标之后，
            //   «走多快»直接写在 MaxSpeed 里；脚是不是被粘住由 ② 管。
            Vector3 want = forward * targetV;
            Vector3 dvh = new Vector3(want.x - hv0.x, 0f, want.z - hv0.z);
            Vector3 pull = dvh * Recipe.GaitPullStiffness;
            pull = Vector3.ClampMagnitude(pull, Recipe.GaitMaxAccel);
            hips.AddForce(pull * Ragdoll.TotalMass, ForceMode.Force);

            // ④ ★ 2026-10-02 **脚底离地伺服**（治 owner 报的「脚底跟地面剐蹭」）。
            //   整套步态只写**角度**，而鞋底的高度是角度的**结果** —— 实测摆动脚在整个摆动期的
            //   离地量是 15.6 → 0.5 → 24.0 cm（**中间最低、两头最高**），与「中段抬起来、
            //   两头贴着地」正好相反：走路 6 s 里 39% 的帧有脚穿进地面、26% 的帧两只脚都在 2 cm 以上。
            //   这条弧线用角度调不出来（隔离实验：髋/膝任意组合下脚的竖直位置都不单调），
            //   所以改成**直接对脚做竖直伺服** —— 支撑脚目标 0、摆动脚目标是一条正弦弧。
            // ★ 2026-10-02：摆动脚的离地目标**分方向** —— 后退时腿更直、伺服的稳态误差把
            //   7 cm 只兑现成 2.5 cm，所以后退单独乘一个比例（见 Recipe.BackwardSwingClearanceScale）。
            float swingClear = Recipe.GaitSwingClearance
                             * (forwardDrive < -0.05f ? Recipe.BackwardSwingClearanceScale : 1f);
            StepFootHeight(plant, false, 0f, 0f);
            StepFootHeight(swing, true, SwingProgress, swingClear);
        }

        /// <summary>
        /// ★ 2026-10-02：**单只脚的离地高度伺服**。
        ///
        /// 目标：支撑脚 = 0（踩在地上）；摆动脚 = `GaitSwingClearance · sin(πu)`（中段抬起、两头贴地）。
        /// 做法：一条一维 PD（位置 + 速度阻尼），用 `ForceMode.Acceleration`
        /// ⇒ 与尺寸/质量无关，且上限只有 `FootHeightMaxAccel`。
        ///
        /// ⚠️ **力必须加在整条腿上，不能只加在脚上**（实测）：鞋盒 37 cm 长、绕踝关节转，
        /// 只推脚的话它**绕着踝打转**（脚尖抬起来、脚跟在原地），`bounds.min.y` 几乎不变 ——
        /// 实测把加速度上限从 40 抬到 300（1.1 kg 的脚 = 330 N）也只把穿地帧从 6% 压到 3%。
        /// 分摊到 `foot + leg + upleg` 才是真的把这条腿整体提起来/按下去。
        /// </summary>
        void StepFootHeight(ClumsyPart foot, bool swinging, float u, float swingClearance)
        {
            if (foot == null || foot.Body == null || foot.Shape == null)
                return;
            if (Recipe.FootHeightGain <= 0f || Recipe.FootHeightMaxAccel <= 0f)
                return;
            // ⚠️ **只在接地时生效**：空中不许把脚往地面按（跳 = 把角色扔出去，
            //   按住 W 起跳时这条伺服会把«支撑脚»拉回地面高度，等于把跳拽下来）。
            if (!Ragdoll.IsGroundedNow)
                return;
            float want = swinging ? swingClearance * Mathf.Sin(Mathf.Clamp01(u) * Mathf.PI) : 0f;
            float low = (foot.Shape.bounds.min.y - Ragdoll.GroundY) - want;
            float vy = foot.Body.linearVelocity.y;
            float a = -low * Recipe.FootHeightGain - vy * Recipe.FootHeightDamping;
            a = Mathf.Clamp(a, -Recipe.FootHeightMaxAccel, Recipe.FootHeightMaxAccel);
            Vector3 f = Vector3.up * a;
            foot.Body.AddForce(f, ForceMode.Acceleration);
            if (!Recipe.FootHeightOnWholeLeg)
                return;
            string suffix = foot.Spec.Key.EndsWith("l") ? "l" : "r";
            ClumsyPart shin = Ragdoll.Find("leg" + suffix);
            ClumsyPart thigh = Ragdoll.Find("upleg" + suffix);
            if (shin != null && shin.Body != null)
                shin.Body.AddForce(f, ForceMode.Acceleration);
            if (thigh != null && thigh.Body != null)
                thigh.Body.AddForce(f, ForceMode.Acceleration);
        }


        /// <summary>
        /// ★ 2026-10-02：**种植脚抓地**（走路与转身共用）。
        /// 锚点 + 高摩擦材质 + 水平位置弹簧 —— 把这只脚«粘»在着地那一点上。
        /// </summary>
        void PlantGrip(ClumsyPart plant, int pi)
        {
            if (plant == null || plant.Body == null || plant.Shape == null)
                return;
            Vector3 pp = plant.Body.position;
            Vector3 flat = new Vector3(pp.x, 0f, pp.z);
            if (!_wasPlanted[pi])
            {
                _anchor[pi] = flat;                  // 刚着地：把这一点记下来
                _wasPlanted[pi] = true;
            }
            else
            {
                Vector3 d = flat - _anchor[pi];
                float far = d.magnitude - Recipe.FootGripAnchorSlip;
                if (far > 0f)
                    _anchor[pi] += d.normalized * far;   // 真被拖出去了：锚点跟着，不要抵死
            }
            PlantAnchor = _anchor[pi];
            PlantSlipNow = Vector3.Distance(flat, _anchor[pi]);
            if (PlantSlipNow > PlantSlipMax)
                PlantSlipMax = PlantSlipNow;

            plant.Shape.sharedMaterial = Grip();
            Vector3 v = plant.Body.linearVelocity;
            Vector3 f = (_anchor[pi] - flat) * Recipe.FootGripStiffness
                      - new Vector3(v.x, 0f, v.z) * Recipe.FootGripDamping;
            f = Vector3.ClampMagnitude(f, Recipe.FootGripMaxAccel);
            plant.Body.AddForce(f * plant.Body.mass, ForceMode.Force);
        }

        /// <summary>
        /// ★ 2026-10-02：**脚种植转身的一步**（左转 / 右转）。
        ///
        /// 与走路**同一个口径**，只是把«推进相位的东西»从**走过的距离**换成**转过的角度**：
        ///   ① 转过 `TurnStepDegrees` 度算一步 ⇒ 相位 `_turnPhase`；
        ///   ② 支撑脚**抓地当支点**（身体绕着它转，不是两只脚一起搓地）；
        ///   ③ 摆动脚抬起来跟着转、落在新角度上（脚底伺服给离地弧线）。
        ///
        /// `SwingFoot` / `PlantFoot` / `SwingProgress` / `GaitActive` 与走路共用同一批字段，
        /// 所以姿势驱动和脚底伺服**一行都不用改**就跟着转。
        /// ⚠️ 腿的前后摆幅由 `GaitStrideScale = TurnGaitStrideScale` 压到很小 ——
        /// 原地转身不该«迈步»，只要抬脚；屈膝不压，抬脚的高度才够。
        /// </summary>
        void StepTurnGait(Rigidbody hips)
        {
            float dt = Time.fixedDeltaTime;
            // ① 相位：按**转过的角度**推进（走得快就换脚快，与走路按距离同一个道理）
            float yawRate = Mathf.Abs(TurnRateNow);
            _turnPhase += dt * yawRate / Mathf.Max(10f, Recipe.TurnStepDegrees);
            _turnPhase -= Mathf.Floor(_turnPhase);

            GaitActive = true;
            bool rightSwings = _turnPhase >= 0.25f && _turnPhase < 0.75f;
            SwingFoot = rightSwings ? "footr" : "footl";
            PlantFoot = rightSwings ? "footl" : "footr";
            SwingProgress = Mathf.Clamp01(rightSwings
                ? (_turnPhase - 0.25f) * 2f
                : ((_turnPhase < 0.25f ? _turnPhase + 1f : _turnPhase) - 0.75f) * 2f);

            int pi = PlantFoot == "footl" ? 0 : 1;
            ClumsyPart plant = Ragdoll.Find(PlantFoot);
            ClumsyPart swing = Ragdoll.Find(SwingFoot);

            Ragdoll.SetLegSpringScale(PlantFoot, Recipe.GaitSupportStiffnessScale);
            Ragdoll.SetLegSpringScale(SwingFoot, Recipe.GaitSwingStiffnessScale);

            // ② 支点脚抓地；③ 摆动脚抬起（目标高度与后退同一个道理，要放大）
            PlantGrip(plant, pi);
            float swingClear = Recipe.GaitSwingClearance * Recipe.TurnSwingClearanceScale;
            StepFootHeight(plant, false, 0f, 0f);
            StepFootHeight(swing, true, SwingProgress, swingClear);
        }

        /// <summary>
        /// ★ 2026-10-02：**yaw 伺服** —— 把身体**真的**转到 `Root` 的朝向上。
        ///
        /// 为什么需要：`Root` 的朝向一直是**相机**在写（`ClumsyRagdollCamera.FixedUpdate`），
        /// 而布娃娃的各个刚体不会因为 Root 转了就跟过去（关节的 targetRotation 是局部的）。
        /// 所以相机转身 ≠ 角色转身 —— 这正是「原地转身=两只脚搓地」的根因。
        /// 这里给髖一个绕世界上轴的力矩伺服：误差大就转、转速够了就阻尼住，死区内不较劲。
        /// </summary>
        void StepYawServo(Rigidbody hips)
        {
            if (Recipe.TurnYawStiffness <= 0f || Ragdoll.Root == null)
                return;
            float target = Ragdoll.Root.eulerAngles.y;
            float cur = hips.rotation.eulerAngles.y;
            float err = Mathf.DeltaAngle(cur, target);
            if (Mathf.Abs(err) < Recipe.TurnDeadZoneDegrees)
                err = 0f;
            float rate = Vector3.Dot(hips.angularVelocity, Vector3.up) * Mathf.Rad2Deg;
            float t = err * Recipe.TurnYawStiffness - rate * Recipe.TurnYawDamping;
            t = Mathf.Clamp(t, -Recipe.TurnMaxTorque, Recipe.TurnMaxTorque);
            if (Mathf.Abs(t) < 0.01f)
                return;
            hips.AddTorque(Vector3.up * t, ForceMode.Force);
        }

        /// <summary>种植脚用的高摩擦材质（懒建）。frictionCombine = Maximum：与地面取大，才真粘。</summary>
        PhysicsMaterial Grip()
        {
            if (_gripMat == null)
            {
                _gripMat = new PhysicsMaterial("grip");
                _gripMat.dynamicFriction = Recipe.FootGripFriction;
                _gripMat.staticFriction = Recipe.FootGripFriction;
                _gripMat.bounciness = Recipe.FootBounciness;
                _gripMat.frictionCombine = PhysicsMaterialCombine.Maximum;
                _gripMat.bounceCombine = PhysicsMaterialCombine.Minimum;
            }
            return _gripMat;
        }

    }

    public interface IClumsyInput
    {
        Vector2 Move { get; }
        bool Sprint { get; }
        /// <summary>跳跃键是否「正被按住」。「按下」的边沿由 ClumsyController 判（见那边的注释）。</summary>
        bool JumpHeld { get; }
    }

    /// <summary>本地键盘输入：WASD + Shift + 空格（教程 P3 的键位）。</summary>
    public sealed class KeyboardInput : IClumsyInput
    {
        public Vector2 Move
        {
            get
            {
                float x = 0f;
                float y = 0f;
                if (Input.GetKey(KeyCode.A)) x -= 1f;
                if (Input.GetKey(KeyCode.D)) x += 1f;
                if (Input.GetKey(KeyCode.S)) y -= 1f;
                if (Input.GetKey(KeyCode.W)) y += 1f;
                Vector2 v = new Vector2(x, y);
                return v.sqrMagnitude > 1f ? v.normalized : v;
            }
        }

        // 教程用 GetKey(LeftShift) 判冲刺
        public bool Sprint { get { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); } }

        // 教程用 Input.GetAxis("Jump") > 0（默认绑空格）。
        // 轴是「按住就 > 0」，所以这里只能返回「按住」状态；
        // 「按下」的边沿必须在控制器里锁存 —— 直接拿按住状态去起跳会叠成火箭。
        public bool JumpHeld { get { return Input.GetKey(KeyCode.Space) || Input.GetAxisRaw("Jump") > 0.5f; } }
    }

    /// <summary>
    /// 教程 P4 / P5 的替代实现。
    ///
    /// 教程的做法是：复制一份带 Animator 的模型 → <c>CopyMotion</c> 每帧把
    /// <c>cj.targetRotation = targetLimb.rotation</c>。本工程的 FBX **一个动画片段都没有**，
    /// 所以姿势改成程序化算出来的 <c>targetRotation</c>：
    ///   机制完全不变（唯一的内容仍然是「每帧写旋转点的 targetRotation」），
    ///   变的只是「姿势从哪来」（程序化的正弦，而不是 Animator）。
    ///
    /// 顺带解掉教程里的两处坑：
    ///   ① 教程 P4 把动画骨骼的**世界**旋转直接塞进 targetRotation（一个局部量），
    ///      §5.3 项6 自己标注「这是教程里最值得自己验证的一处」；这里写的是局部目标，所以
    ///   ② 教程为了修左右镜像加了 <c>mirror</c> + <c>Quaternion.Inverse</c>（P4 步9、P5 步7），
    ///      那是 ① 的副作用；写局部目标后镜像问题根本不存在。
    ///
    /// 教程 P4 步8「只驱动旋转点」照做：Passenger（小臂/手/小腿/脚）不写 targetRotation。
    /// </summary>
    public sealed class ClumsyPoseDriver : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public ClumsyController Controller;
        public RagdollRecipe Recipe;

        /// <summary>
        /// 手部两骨 IK。它**不写关节** —— 只把「这几个关节的 targetRotation 该是多少、权重多少」算出来，
        /// 由本驱动统一写。保持「一个关节一个写入者」（教程 P6 那边 CopyMotion 与 CamControl
        /// 抢写同一个 targetRotation 是个竞态，交接文档 §四 项8 已点过）。
        /// </summary>
        public ClumsyArmIK ArmIK;

        /// <summary>
        /// 动画身体（教程的「双身体架构」）。有它、并且 Recipe.UseAnimation 开着
        /// = 姿势来自 AnimationClip 资产（`Assets/Animations/`）；否则回落到下面的程序化步态。
        /// **两者产出的是同一个东西（targetRotation），所以可以随时 A/B。**
        /// </summary>
        public ClumsyAnimationSource Animation;

        /// <summary>笨拙层（目标侧的欠阻尼跟随 + 落地踉跄）。可以为空 = 不启用。</summary>
        public ClumsyWobble Wobble;

        /// <summary>这一帧是不是走动画姿势源。</summary>
        public bool UseAnimationPose
        {
            get { return Recipe != null && Recipe.UseAnimation && Animation != null && Animation.Ready; }
        }

        /// <summary>由相机脚本写入的躯干俯仰（度，+ 为低头/前倾）。</summary>
        public float PitchDegrees;

        /// <summary>当前步态相位。</summary>
        public float Phase;

        /// <summary>移动强度 0..1（前后输入 + 横移的一半）。</summary>
        public float Drive;

        /// <summary>转身踏步相位。</summary>
        public float TurnPhase;

        /// <summary>平滑后的转身角速度（度/秒，正 = 右转）。</summary>
        public float TurnRate;

        /// <summary>-1..1 的转身强度。</summary>
        public float TurnDrive;

        /// <summary>出生姿势之外的固定偏移（把手臂从 T-pose 放下来等）。</summary>
        readonly Dictionary<string, Quaternion> _base = new Dictionary<string, Quaternion>();
        float _lastYaw;

        /// <summary>★ 2026-10-01：踝的摆平修正量（度），每帧按鞋底实际倾角反馈累加。</summary>
        readonly float[] _ankleCmd = new float[2];

        /// <summary>★ 2026-10-01：本帧摆动腿的外摆量（度）。</summary>
        float _gaitAbduct;

        /// <summary>★ 2026-10-01：空中姿势的混合权重（1 = 完全是跳跃姿势）。</summary>
        float _airBlend;

        /// <summary>★ 2026-10-01：本帧的基础外摆量（度）。</summary>
        float _gaitStanceAbduct;

        /// <summary>★ 2026-10-01：本帧摆动的是不是右腿（决定外摆往哪边加）。</summary>
        bool _abductIsRightSwing;
        bool _hasYaw;

        /// <summary>
        /// 算出手臂「放下来」的固定偏移。
        /// **教程没有这一条** —— 它的模型手臂建模时就垂着；我们这具的绑定姿势是 T-pose，
        /// 而 targetRotation = 0 就是 T-pose，所以不加这个，弹簧会一直把手臂拉成侧平举。
        ///
        /// 轴（角色面朝 +Z、右为 +X、上为 +Y）：左臂在 T-pose 沿 -X 伸出，绕 +Z 转 +θ 会把
        /// (-1,0,0) 转到 (0,-1,0)，即「放下」；右臂沿 +X，符号相反。左右各自 ±，是真正的镜像，
        /// 不是教程 P4 那种「把世界旋转塞进局部字段」而不得不加的 mirror 补丁。
        /// </summary>
public void BuildArmRestPose()
        {
            _base.Clear();
            if (Ragdoll == null || Recipe == null)
                return;

            float down = Recipe.ArmDownDegrees;
            float share = Mathf.Clamp01(Recipe.ShoulderDownShare);
            float shoulder = down * share;
            float upper = down * (1f - share);

            // 「可见地绕角色前方轴 +θ」会把朝左的手臂转向下方（已用隔离实验验过符号）。
            _base["shoulderl"] = JointTarget(+shoulder, ForwardAxis(Ragdoll.Find("shoulderl")));
            _base["arml"] = JointTarget(+upper, ForwardAxis(Ragdoll.Find("arml")));
            _base["shoulderr"] = JointTarget(-shoulder, ForwardAxis(Ragdoll.Find("shoulderr")));
            _base["armr"] = JointTarget(-upper, ForwardAxis(Ragdoll.Find("armr")));

            // 肘：小臂从「向下」转向「向前」= 可见地绕右轴 −θ。
            // （LockPassengerAngular 开着的时候小臂角运动是 Locked 的，这一条不生效。）
            _base["forearml"] = JointTarget(-Recipe.ForeArmBendDegrees, RightAxis(Ragdoll.Find("forearml")));
            _base["forearmr"] = JointTarget(-Recipe.ForeArmBendDegrees, RightAxis(Ragdoll.Find("forearmr")));
        }

        /// <summary>
        /// 把「绕角色右/上/前轴可见地转 θ 度」变成 targetRotation。
        ///
        /// ⚠️ **符号是反的，这是实测的。**
        /// 隔离实验（两根刚体 + 一个 ConfigurableJoint，锚点 0、线性 Locked、角 Free）：
        ///   targetRotation = AngleAxis(+90°, 世界 +Z)  →  刚体实际绕 Z 转了 **−90°**
        ///   targetRotation = AngleAxis(−90°, 世界 +Z)  →  刚体实际绕 Z 转了 **+90°**
        /// 也就是 ConfigurableJoint 把 targetRotation 当成「父体相对于本体」的旋转，
        /// 与「本体相对于父体转 θ」差一个逆。教程 P4 直接把世界旋转塞进这个字段（§5.3 项6
        /// 自标为「最值得自己验证的一处」），这个逆很可能就是它需要 mirror + Quaternion.Inverse
        /// 才勉强能跑的真正原因。
        /// 所以本文件所有姿势角度都统一过这个函数取负。
        /// </summary>
        static Quaternion JointTarget(float degrees, Vector3 localAxis)
        {
            return Quaternion.AngleAxis(-degrees, localAxis);
        }

        static Vector3 ForwardAxis(ClumsyPart part)
        {
            return part == null ? Vector3.forward : Vector3.Cross(part.LocalRight, part.LocalUp);
        }

        static Vector3 RightAxis(ClumsyPart part)
        {
            return part == null ? Vector3.right : part.LocalRight;
        }

        readonly Dictionary<string, Quaternion> _targets = new Dictionary<string, Quaternion>();
        float _blend;   // 0 = 出生姿势，1 = 满幅度步态；避免「突然开始走」

void Start()
        {
            if (Ragdoll == null)
                Ragdoll = GetComponent<ClumsyRagdoll>();
            if (Controller == null)
                Controller = GetComponent<ClumsyController>();
            if (Recipe == null && Ragdoll != null)
                Recipe = Ragdoll.Recipe;
            BuildArmRestPose();
        }

void FixedUpdate()
        {
            if (Ragdoll == null || Recipe == null || !Recipe.UsePoseDriver)
                return;
            if (Ragdoll.Hips == null)
                return;
            if (_base.Count == 0)
                BuildArmRestPose();

            float dt = Time.fixedDeltaTime;

            // 手部 IK 必须在姿势驱动**之前**解：它只负责算出 override，
            // 真正写 targetRotation 的是下面的 Pose()。
            if (ArmIK != null)
                ArmIK.Solve(dt);

            // ---- 动画身体与笨拙层，同样必须在写关节之前 ----
            // 顺序：动画 Step（推进 Animator + 落地检测要用到接地状态）→ 笨拙层 Step（检测落地）
            //       → 下面逐个 Pose() 时才去读动画骨髀的局部旋转。
            if (Animation != null)
                Animation.Step(dt);
            if (Wobble != null)
                Wobble.Step(dt);

            // ---- 转身速率（Root 的 yaw 由相机脚本写）----
            float yaw = Ragdoll.Root != null ? Ragdoll.Root.eulerAngles.y : 0f;
            if (_hasYaw)
            {
                float raw = Mathf.DeltaAngle(_lastYaw, yaw) / Mathf.Max(dt, 1e-4f);
                TurnRate = Mathf.Lerp(TurnRate, raw, 0.35f);
            }
            _lastYaw = yaw;
            _hasYaw = true;
            float turn = Mathf.Clamp(TurnRate / Mathf.Max(Recipe.TurnReferenceRate, 1f), -1f, 1f);
            TurnDrive = turn;

            // ---- 移动输入 ----
            float moveForward = 0f;
            float moveSide = 0f;
            bool sprint = false;
            if (Controller != null)
            {
                moveForward = Controller.Move.y;
                moveSide = Controller.Move.x;
                sprint = Controller.Sprint;
            }
            float rawDrive = Mathf.Clamp01(Mathf.Abs(moveForward) + Mathf.Abs(moveSide) * 0.5f);
            float want = rawDrive > 0.05f ? rawDrive : 0f;
            _blend = Mathf.MoveTowards(_blend, want, dt * 3.5f);
            Drive = _blend;

            bool backward = moveForward < -0.05f;
            float stride = backward ? Recipe.BackwardStrideScale : 1f;
            float gaitSign = backward ? -1f : 1f;
            // ★ 2026-10-02：**原地转身不迈步、只抬脚** —— 前后摆幅按 `GaitStrideScale` 压小
            float gStride = Controller != null && Controller.GaitActive ? Controller.GaitStrideScale : 1f;

            // 相位始终按实时前进。**不要把时间倒放**（dir = −1）——
            // 那是「正走路的录像倒着放」，看起来就像有人从后面拖着角色。
            float freq = Recipe.StrideFrequency * (sprint ? Recipe.SprintScale : 1f);
            Phase += dt * freq;
            if (Phase > 1f)
                Phase -= Mathf.Floor(Phase);

            // ---- 原地转身踏步 ----
            float tAbs = Mathf.Abs(turn);
            if (Recipe.TurnStepping && tAbs > 0.02f)
                TurnPhase += dt * Recipe.TurnStepRate * tAbs;
            if (TurnPhase > 1f)
                TurnPhase -= Mathf.Floor(TurnPhase);

            float twoPi = Mathf.PI * 2f;
            float s = Mathf.Sin(Phase * twoPi);
            float c = Mathf.Cos(Phase * twoPi);
            float ts = Mathf.Sin(TurnPhase * twoPi);
            float amp = _blend;
            float tAmp = Recipe.TurnStepping ? tAbs : 0f;
            float turnSign = turn >= 0f ? 1f : -1f;

            // ---- 腿：步行摆动 + 转身踏步（后一个腿交替外摆、抬腿）----
            // ★ 2026-10-01：`Recipe.UseFootGait` 时改用**脚种植步态**的相位算腿角
            //   （相位在 ClumsyController.StepFootGait 里推进，这里只读）。
            //   形状：摆动腿的「向前量」 f = −cos(uπ)（0 = 刚离地、脚在后；1 = 要落地、脚在前），
            //   支撑腿与之反相 f = +cos(uπ)；膝：摆动腿 sin(uπ) 中段最弯，支撑腿只留一点。
            //   符号是实测的：`Pose("uplegl", −25)` 会让左脚**前移** +0.15 m，右脚镜像。
            float legL, legR, kneeL, kneeR;
            bool gaitLegs = Recipe.UseFootGait && Controller != null && Controller.GaitActive;
            if (gaitLegs)
            {
                float u = Controller.SwingProgress;
                bool rightSwings = Controller.SwingFoot == "footr";
                // ★ 2026-10-01：摆动腿的「向前量」不是正弦，而是**前伸 → 略微回带**。
                //   为什么：支撑相里脚相对髖匀速后移 ⇒ 在世界上站着不动 ✓；
                //   而摆动相如果按正弦，落地那一刻脚相对髖的速度 ≈ 0 ⇒ 世界上还在以身体速度前冲，
                //   一落地就打滑（实测每步滑 10 cm）。前伸过一点再回带，落地瞬间的相对速度 ≈ −v ⇒ 世界速度 ≈ 0。
                float uReach = Mathf.Clamp(Recipe.SwingReachAt, 0.3f, 0.95f);
                float reach = 1f + Recipe.SwingOvershoot;
                float fSwing;
                if (u < uReach)
                    fSwing = Mathf.Lerp(-1f, reach, Mathf.SmoothStep(0f, 1f, u / uReach));
                else
                    fSwing = Mathf.Lerp(reach, 1f, Mathf.SmoothStep(0f, 1f, (u - uReach) / Mathf.Max(0.05f, 1f - uReach)));
                float fSupport = 1f - 2f * u;   // 支撑脚：相对髖匀速后移（世界上踩住不动）
                float fL = rightSwings ? fSupport : fSwing;
                float fR = rightSwings ? fSwing : fSupport;
                // ⚠️ 实测：**两条腿«向前»用的是同一个符号**（Pose(uplegl,−25) 与 Pose(uplegr,−25)
                //   都是把脚往前送 +0.15 m）。原代码里左右取反号是因为它要的是«两条腿反向摆»。
                legL = -Recipe.HipSwingDegrees * amp * stride * gStride * gaitSign * fL;
                legR = -Recipe.HipSwingDegrees * amp * stride * gStride * gaitSign * fR;
                // 屈膝剖面：峰值挪到 early（离开后立刻把脚提起来），落地前再伸直
                float kp = Mathf.Clamp(Recipe.SwingKneePeakAt, 0.15f, 0.85f);
                float kneeShape = u < kp
                    ? Mathf.Sin((u / kp) * (Mathf.PI * 0.5f))
                    : Mathf.Cos(((u - kp) / Mathf.Max(0.05f, 1f - kp)) * (Mathf.PI * 0.5f));
                // ⚠️ **摆动腿的屈膝不乘 `stride`**：`stride` 是«步幅»的比例（后退 = `BackwardStrideScale`），
                //   而屈膝管的是«离地高度» —— 两者绑在一起就会出现「步幅短 ⇒ 抬不起脚 ⇒ 一路拖地」。
                //   实测：后退时整段摆动只有 1.0–2.9 cm（前进中段 6.6 cm），刮蹭帧 55%（前进 21%）。
                float kneeSw = Recipe.KneeBendDegrees * kneeShape * amp;
                // 摆动腿外摆（摆动相中段最大）：直走时两只脚会打架，斜走不会 —— 靠这一下把摆动脚挪开
                _gaitAbduct = Recipe.GaitSwingAbductDegrees * Mathf.Sin(u * Mathf.PI) * Recipe.GaitAbductSign;
                // 基础外摆：两条腿一起往外一点，把两只脚的轨迹拉成两条平行线（按驱动量缩放）
                _gaitStanceAbduct = Recipe.GaitStanceAbductDegrees * amp * Recipe.GaitAbductSign;
                _abductIsRightSwing = rightSwings;
                float kneeSup = Recipe.GaitSupportKneeScale * Recipe.KneeBendDegrees * amp * stride * gStride;
                kneeL = rightSwings ? kneeSup : kneeSw;
                kneeR = rightSwings ? kneeSw : kneeSup;
            }
            else
            {
                legL = -Recipe.HipSwingDegrees * s * amp * stride * gaitSign;
                legR = +Recipe.HipSwingDegrees * s * amp * stride * gaitSign;
                kneeL = Recipe.KneeBendDegrees * Mathf.Max(0f, s * gaitSign) * amp * stride;
                kneeR = Recipe.KneeBendDegrees * Mathf.Max(0f, -s * gaitSign) * amp * stride;
            }
            // ★ 2026-10-02：走步态时抬脚由步态负责，转身踏步不再叠加（否则一次抬两次）
            float turnLiftAmp = gaitLegs ? 0f : tAmp;
            float liftL2 = -Recipe.TurnLiftDegrees * Mathf.Max(0f, ts) * turnLiftAmp;
            float liftR2 = -Recipe.TurnLiftDegrees * Mathf.Max(0f, -ts) * turnLiftAmp;
            float abdL = -Recipe.TurnAbductDegrees * ts * tAmp * turnSign;
            float abdR = +Recipe.TurnAbductDegrees * ts * tAmp * turnSign;
            // 摆动腿那一侧再叠上外摆（左 −sign / 右 +sign，与转身踏步同一镜像约定）
            if (gaitLegs)
            {
                if (_abductIsRightSwing) abdR += +_gaitAbduct;
                else abdL += -_gaitAbduct;
                abdL += -_gaitStanceAbduct;   // 左腿往外 = −sign
                abdR += +_gaitStanceAbduct;   // 右腿往外 = +sign
            }
            // ★ 2026-10-01 **空中姿势**：人跳起来不该还在原地跑步。
            //   空中把腿收一点（髋前摆 + 屈膝）、手臂抬起来，落地再用 0.1 s 融回步态。
            //   实测「看起来像摔倒」的另一半原因就是空中腿还在按步态扫 + 落地踉跄 0.92。
            {
                bool air = Ragdoll != null && !Ragdoll.IsGroundedNow;
                float airWant = air ? 1f : 0f;
                _airBlend = Mathf.MoveTowards(_airBlend, airWant, dt * (air ? 9f : 11f));
            }
            if (_airBlend > 0.001f)
            {
                legL = Mathf.Lerp(legL, -Recipe.AirHipDegrees, _airBlend);
                legR = Mathf.Lerp(legR, -Recipe.AirHipDegrees, _airBlend);
                kneeL = Mathf.Lerp(kneeL, Recipe.AirKneeDegrees, _airBlend);
                kneeR = Mathf.Lerp(kneeR, Recipe.AirKneeDegrees, _airBlend);
            }
            Pose("uplegl", legL + liftL2, 0f, abdL, dt);
            Pose("uplegr", legR + liftR2, 0f, abdR, dt);
            Pose("legl", kneeL + Mathf.Max(0f, ts) * tAmp * 10f, 0f, 0f, dt);
            Pose("legr", kneeR + Mathf.Max(0f, -ts) * tAmp * 10f, 0f, 0f, dt);

            // ★ 2026-10-01 **踝**：把鞋底摆平（治「用脚尖走路」）。
            //   ⚠️ 解析式补偿（踝 = −(髋 + 膝)）实测**不准**：隔离实验里髋转 25° 鞋底才斜 5°，
            //   而且一走起来鞋底斜到 78°（等于踮着脚尖走）。所以改成**反馈**：
            //   每帧量鞋底实际倾角，累加一个修正量喂给踝的 targetRotation。
            if (!gaitLegs)
            {
                // 停走：踝放回中立（不然上一帧的修正量会一直挂在脚上，站着都歪着漂）
                _ankleCmd[0] = 0f;
                _ankleCmd[1] = 0f;
                Pose("footl", 0f, 0f, 0f, dt);
                Pose("footr", 0f, 0f, 0f, dt);
            }
            else if (gaitLegs && Recipe.FootLevelAnkle)
            {
                Vector3 charRight = Ragdoll.Root != null ? Ragdoll.Root.right : Vector3.right;
                for (int ai = 0; ai < 2; ai++)
                {
                    string fk = ai == 0 ? "footl" : "footr";
                    ClumsyPart fp = Ragdoll.Find(fk);
                    if (fp == null || fp.Shape == null)
                        continue;
                    // 鞋底朝上那根轴与世界上方的**有符号**夹角（绕角色右轴）
                    float tilt = Vector3.SignedAngle(fp.Shape.transform.up, Vector3.up, charRight);
                    _ankleCmd[ai] = Mathf.Clamp(_ankleCmd[ai] + Recipe.AnkleLevelGain * tilt, -70f, 70f);
                    bool swinging = Controller.SwingFoot == fk;
                    Pose(fk, _ankleCmd[ai] * (swinging ? Recipe.SwingAnkleScale : 1f), 0f, 0f, dt);
                }
            }

            // ---- 臂：与同侧腿反相 ----
            float armAmp = Recipe.ArmSwingDegrees * amp * stride;
            Pose("shoulderl", 0f, 0f, -6f * tAmp * turnSign, dt);
            Pose("shoulderr", 0f, 0f, -6f * tAmp * turnSign, dt);
            if (gaitLegs)
            {
                // ★ 2026-10-01：手臂跟**同一个步态相位**摆（原来用的是本类自己那套 sin，
                //   与新步态的左右脚不同步 —— 看着像肩膀在乱晃）。左臂与左腿反相。
                float gl = Mathf.Cos(Controller.SwingProgress * Mathf.PI);
                bool rSw = Controller.SwingFoot == "footr";
                // ★ 2026-10-02 后退：手臂跟着腿一起镜像（`gaitSign`），不然后退时手臂是顺拐的
                float ffL = (rSw ? +gl : -gl) * gaitSign;
                float ffR = (rSw ? -gl : +gl) * gaitSign;
                if (_airBlend > 0.001f)
                {
                    Pose("arml", Mathf.Lerp(+armAmp * ffL, -Recipe.AirArmDegrees, _airBlend), 0f, 0f, dt);
                    Pose("armr", Mathf.Lerp(-armAmp * ffR, -Recipe.AirArmDegrees, _airBlend), 0f, 0f, dt);
                }
                else
                {
                    Pose("arml", +armAmp * ffL, 0f, 0f, dt);
                    Pose("armr", -armAmp * ffR, 0f, 0f, dt);
                }
                Pose("forearml", -armAmp * 0.4f * Mathf.Max(0f, -ffL), 0f, 0f, dt);
                Pose("forearmr", +armAmp * 0.4f * Mathf.Max(0f, -ffR), 0f, 0f, dt);
            }
            else
            {
                Pose("arml", +armAmp * s * gaitSign, 0f, 0f, dt);
                Pose("armr", -armAmp * s * gaitSign, 0f, 0f, dt);
                Pose("forearml", -armAmp * 0.4f * Mathf.Max(0f, s * gaitSign), 0f, 0f, dt);
                Pose("forearmr", +armAmp * 0.4f * Mathf.Max(0f, -s * gaitSign), 0f, 0f, dt);
            }

            // ---- 躯干：前进前倾 / 后退后仰 / 转身向内侧倾 ----
            float lean = (backward ? Recipe.BackwardLeanDegrees : Recipe.SpineLeanDegrees) * amp + PitchDegrees;
            float sway = (1f - amp) * 1.5f * c;
            float roll = -5f * tAmp * turnSign;
            Pose("spine", lean, sway, roll, dt);
            Pose("neck", -lean * 0.4f, 0f, roll * 0.3f, dt);
            Pose("head", -lean * 0.3f + (1f - amp) * 1.2f * s, 0f, roll * 0.5f, dt);

            // ---- 手骨：步态从来不写它，所以单独走一遍 ----
            // （教程 P4 步8「只驱动旋转点」把手划在旋转点之外，上面没有 Pose("hand…") 的调用。）
            // 没有 IK 覆盖时它写的是出生姿势的基准，与改之前的行为一致。
            if (ArmIK != null)
            {
                for (int ci = 0; ci < ArmIK.Chains.Count; ci++)
                {
                    string[] ikKeys = ArmIK.Chains[ci].JointKeys;
                    if (ikKeys != null && ikKeys.Length >= 4)
                        PoseIKOnly(ikKeys[3], dt);
                }
            }
        }

        /// <summary>把一个「绕局部右方 / 上方 / 前后」的欧拉目标写到该部件的 targetRotation。</summary>
void Pose(string key, float aboutRight, float aboutUp, float aboutForward, float dt)
        {
            ClumsyPart part = Ragdoll.Find(key);
            if (part == null || part.Joint == null)
                return;
            // 教程 P5 步2：非旋转点的角运动是 Locked 的，写 targetRotation 也不会动 —— 跳过。
            if (part.Joint.angularXMotion == ConfigurableJointMotion.Locked)
                return;

            // ---- 姿势从哪来 ----
            // ① 动画姿势源（教程那套双身体架构）：直接把动画骨髀的**局部**旋转反算成 targetRotation。
            // ② 程序化步态（上一轮那套）：下面那段欧拉目标。
            // 两者产出的是**同一个东西**（targetRotation），所以后面的 IK 混合、笨拙层、落盘全都一样走。
            Quaternion wanted;
            if (UseAnimationPose && Animation.TryGetTargetRotation(part, out wanted))
            {
                ApplyJointTarget(part, key, wanted, dt);
                return;
            }

            // aboutXxx = 「让这个部件可见地绕角色的右/上/前轴转多少度」（正负按 Unity 习惯）。
            // 写进 Joint 之前一律过 JointTarget 取负（见 JointTarget 的注释）。
            wanted = Quaternion.identity;
            if (Mathf.Abs(aboutRight) > 1e-4f)
                wanted = JointTarget(aboutRight, part.LocalRight) * wanted;
            if (Mathf.Abs(aboutUp) > 1e-4f)
                wanted = JointTarget(aboutUp, part.LocalUp) * wanted;
            if (Mathf.Abs(aboutForward) > 1e-4f)
                wanted = JointTarget(aboutForward, ForwardAxis(part)) * wanted;

            Quaternion basis;
            if (_base.TryGetValue(key, out basis))
                wanted = basis * wanted;

            ApplyJointTarget(part, key, wanted, dt);
        }

        /// <summary>
        /// 步态**从不写**的关节（手骨 —— 教程 P4 步8「只驱动旋转点」把手骨划在旋转点之外）。
        /// 基准就是出生姿势，然后叠 IK 覆盖。
        /// </summary>
        void PoseIKOnly(string key, float dt)
        {
            ClumsyPart part = Ragdoll.Find(key);
            if (part == null || part.Joint == null)
                return;
            // 教程 P5 步2：非旋转点的角运动是 Locked 的，写 targetRotation 也不会动 —— 跳过。
            // （FreeElbowAngular 把「小臂 + 手」放开了，所以手骨现在会真的动。）
            if (part.Joint.angularXMotion == ConfigurableJointMotion.Locked)
                return;

            Quaternion basis;
            if (!_base.TryGetValue(key, out basis))
                basis = Quaternion.identity;

            ApplyJointTarget(part, key, basis, dt);
        }

        /// <summary>
        /// 落盘：把「步态想要的 targetRotation」与「IK 想要的 targetRotation」按权重插值，
        /// 再按角速度上限推进，最后写进关节。**本工程唯一写 targetRotation 的地方。**
        ///
        /// 在 targetRotation 空间里直接 Slerp 就够了，不必先转回世界旋转：
        /// targetRotation 与「相对父体的局部旋转」一一对应（差一个固定的逆，
        /// 推导见 <see cref="ClumsyArmIK.WorldToJoint"/>），所以两者的 Slerp 结果仍是合法的 targetRotation。
        /// </summary>
        void ApplyJointTarget(ClumsyPart part, string key, Quaternion wanted, float dt)
        {
            // ★ 笨拙层在 IK **之前**：先让「目标自己」变笨拙，再让 IK 把「够东西」这件事拉回来。
            //    顺序反过来的话，抓东西的手会被晃走 —— 手是精确定位需求最强的那一根。
            //    而且被 IK 接管的关节根本不晃（不然抓东西时手会在物体上抹来抹去）。
            float ikWeight = 0f;
            Quaternion ikTarget = Quaternion.identity;
            bool hasIK = ArmIK != null && ArmIK.TryGetOverride(key, out ikTarget, out ikWeight);

            if (Wobble != null && !hasIK)
                wanted = Wobble.Apply(key, part, wanted, dt);

            if (hasIK)
                wanted = Quaternion.Slerp(wanted, ikTarget, ikWeight);

            Quaternion previous;
            if (!_targets.TryGetValue(key, out previous))
                previous = wanted;

            // IK 生效时放宽角速度上限 —— 否则 420°/s 在 0.37m 的肩半径上只有 ~2.7 m/s 的手速，
            // 手会明显跟不上目标（够不到东西）。
            float slew = Mathf.Lerp(Recipe.PoseSlewDegreesPerSecond, Recipe.ArmIKSlewDegreesPerSecond,
                Mathf.Clamp01(ikWeight));
            // ★ 脚种植步态：腿要真的扫过去，420°/s 会把幅度砍掉一半（实测 ±13° vs 目标 ±26°）
            if (Recipe.UseFootGait && Controller != null && Controller.PlantFoot != null
                && (key.StartsWith("upleg") || key.StartsWith("leg") || key.StartsWith("foot")))
                slew = Mathf.Max(slew, Recipe.GaitPoseSlewDegreesPerSecond);
            Quaternion next = Quaternion.RotateTowards(previous, wanted, slew * dt);
            _targets[key] = next;
            part.Joint.targetRotation = next;
        }
        static JointDrive MakeDrive(JointDrive drive, float spring)
        {
            drive.positionSpring = spring;
            return drive;
        }

        /// <summary>关掉姿势驱动：所有旋转点回到出生姿势（教程 P1+P2 的纯弹簧状态）。</summary>
        public void ResetToBindPose()
        {
            _targets.Clear();
            _blend = 0f;
            for (int i = 0; i < Ragdoll.Parts.Count; i++)
            {
                ClumsyPart p = Ragdoll.Parts[i];
                if (p.Joint != null)
                    p.Joint.targetRotation = Quaternion.identity;
            }
        }
    }
}
