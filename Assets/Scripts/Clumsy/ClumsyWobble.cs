using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// **笨拙动作效果**。两层，都作用在「姿势驱动写出去的目标」上，而不是直接推物理：
    ///
    /// <list type="number">
    ///   <item><b>晃动（wobble）</b>：把动画给的目标姿势过一个**欠阻尼二阶跟随器**，
    ///         再交给关节弹簧去追。于是目标自己就会慢半拍、会过冲、会回弹 ——
    ///         观感是「动作总是比意图慢一点、还晃」。这是 Gang Beasts 那种味道的来源。</item>
    ///   <item><b>落地踉跄（impact）</b>：落地那一下按落地速度注入一次性的屈膝 + 塌腰偏移，
    ///         按 <see cref="RagdollRecipe.ImpactDuration"/> 衰减。</item>
    /// </list>
    ///
    /// <b>为什么不是「直接把布娃娃调软」</b>：布娃娃的弹簧已经在干这件事了
    /// （`PoseGapDegrees()` 量的就是它）。但把弹簧调软会一起把**支撑**调软 ——
    /// 站立那 8.00s 是靠 `k_hips &gt; M·g·h_com` 换来的（见 `HANDOFF-教程版布娃娃.md` §一），
    /// 动不得。这个模块只让**目标**变笨拙，支撑不动，所以站立不受影响。
    ///
    /// <b>主旋钮</b>：<see cref="RagdollRecipe.Clumsiness"/>（0 = 干净利落，1 = 醉汉）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClumsyWobble : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;

        sealed class Node
        {
            public Quaternion Pos = Quaternion.identity;
            public Vector3 Vel;
            public bool Init;
        }

        readonly Dictionary<string, Node> _nodes = new Dictionary<string, Node>();

        /// <summary>落地踉跄的当前强度 0..1（已按时间衰减）。</summary>
        public float ImpactAmount { get; private set; }

        /// <summary>本帧晃动让目标偏离动画姿势多少度（逐骨平均）—— 「笨拙」的第二个量化指标。</summary>
        public float WobbleOffsetDegrees { get; private set; }

        /// <summary>全程均值 / 峰值（诊断台报告用；只看最后一帧会低估得很厉害）。</summary>
        public float WobbleOffsetAverage { get; private set; }
        public float WobbleOffsetPeak { get; private set; }
        float _offsetAccum;
        int _offsetFrames;

        /// <summary>落地冲击计数（诊断用）。</summary>
        public int ImpactCount { get; private set; }

        float _impactRaw;
        float _impactTimer;
        float _prevVy;
        bool _wasGrounded;

        float _offsetSum;
        int _offsetN;

        // ------------------------------------------------------------------

        public void Setup(ClumsyRagdoll ragdoll, RagdollRecipe recipe)
        {
            Ragdoll = ragdoll;
            Recipe = recipe;
            _nodes.Clear();
            if (Ragdoll != null && Ragdoll.Hips != null && Ragdoll.Hips.Body != null)
                _wasGrounded = Ragdoll.IsGrounded;
        }

        public void Reset()
        {
            _nodes.Clear();
            _impactRaw = 0f;
            _impactTimer = 0f;
            ImpactAmount = 0f;
            _offsetAccum = 0f;
            _offsetFrames = 0;
            WobbleOffsetAverage = 0f;
            WobbleOffsetPeak = 0f;
        }

        // ------------------------------------------------------------------

        /// <summary>晃动作用在哪几根骨上。腿默认不晃 —— 脚一飘就把站立那套搞坏了。</summary>
        bool IsWobbleBone(string key)
        {
            if (key == "spine" || key == "neck" || key == "head")
                return true;
            if (key.StartsWith("shoulder") || key.StartsWith("arm") || key.StartsWith("forearm"))
                return true;
            if (Recipe != null && Recipe.ClumsyLegWobble)
                return key.StartsWith("upleg") || key.StartsWith("leg");
            return false;
        }

        /// <summary>每个物理步调用一次（在姿势驱动之前）。</summary>
        public void Step(float dt)
        {
            // ---- 落地检测：拿「接地那一刻的下降速度」当冲击量 ----
            if (Ragdoll != null && Ragdoll.Hips != null && Ragdoll.Hips.Body != null)
            {
                float vy = Ragdoll.Hips.Body.linearVelocity.y;
                // 用 IsGroundedNow：诊断台里 Physics.Simulate 不派发碰撞消息，
                // IsGrounded 永远 false —— 那样落地踉跄一次都不会触发（实测：2m 落下都没反应）。
                bool grounded = Ragdoll.IsGroundedNow;
                if (grounded && !_wasGrounded && Recipe != null)
                {
                    float fall = Mathf.Max(0f, -_prevVy);
                    if (fall > Recipe.ImpactMinFallSpeed)
                    {
                        _impactRaw = Mathf.Clamp01((fall - Recipe.ImpactMinFallSpeed)
                            / Mathf.Max(0.01f, Recipe.ImpactFullFallSpeed - Recipe.ImpactMinFallSpeed));
                        // ★ 2026-10-01：**自己跳上去的那一下不算摔**（owner：「跳跃看起来就像摔倒一样」）。
                        //   跳一次落地 vy≈−3.8 ⇒ 原样是 0.92（膝盖屈 24°、脊柱塌 11°）= 摔了一跤的样子。
                        if (Ragdoll.JustJumped)
                            _impactRaw *= Mathf.Clamp01(Recipe.JumpLandingImpactScale);
                        _impactTimer = Recipe.ImpactDuration;
                        ImpactCount++;
                    }
                }
                _prevVy = vy;
                _wasGrounded = grounded;
            }

            if (_impactTimer > 0f)
            {
                _impactTimer -= dt;
                if (_impactTimer <= 0f)
                {
                    _impactTimer = 0f;
                    _impactRaw = 0f;
                }
            }
            ImpactAmount = _impactTimer > 0f
                ? _impactRaw * Mathf.Clamp01(_impactTimer / Mathf.Max(0.01f, Recipe.ImpactDuration))
                : 0f;

            WobbleOffsetDegrees = _offsetN > 0 ? _offsetSum / _offsetN : 0f;
            _offsetSum = 0f;
            _offsetN = 0;

            _offsetAccum += WobbleOffsetDegrees;
            _offsetFrames++;
            WobbleOffsetAverage = _offsetFrames > 0 ? _offsetAccum / _offsetFrames : 0f;
            if (WobbleOffsetDegrees > WobbleOffsetPeak)
                WobbleOffsetPeak = WobbleOffsetDegrees;
        }

        /// <summary>
        /// 把姿势源给的 <paramref name="targetRotation"/> 过一遍笨拙层，返回新的 targetRotation。
        /// 调用者随后把它交给关节（以及 IK 的权重混合）。
        /// </summary>
        public Quaternion Apply(string key, ClumsyPart part, Quaternion targetRotation, float dt)
        {
            if (Recipe == null || part == null)
                return targetRotation;

            // ---- ① 落地踉跄 ----
            if (ImpactAmount > 1e-3f)
            {
                float extra = 0f;
                if (key.StartsWith("leg")) extra = Recipe.ImpactKneeDegrees * ImpactAmount;
                else if (key == "spine") extra = Recipe.ImpactSpineDegrees * ImpactAmount;
                else if (key == "neck") extra = -Recipe.ImpactSpineDegrees * 0.5f * ImpactAmount;
                else if (key == "head") extra = -Recipe.ImpactSpineDegrees * 0.4f * ImpactAmount;

                if (Mathf.Abs(extra) > 1e-3f)
                {
                    // 左乘 —— 与 ClumsyPoseDriver.Pose 里 JointTarget 的用法同一个约定：
                    // desiredLocal 多乘一个 JT(x, LocalRight) ⟺ targetRotation 左乘 AngleAxis(−x, LocalRight)。
                    targetRotation = Quaternion.AngleAxis(-extra, part.LocalRight) * targetRotation;
                }
            }

            // ---- ② 晃动：欠阻尼二阶跟随 ----
            float amount = Mathf.Clamp01(Recipe.Clumsiness);
            if (amount <= 1e-3f || !IsWobbleBone(key))
                return targetRotation;

            // 主旋钮把「跟随频率」和「阻尼比」一起拉：
            //   amount = 0 → 6Hz / ζ=1.0（跟得又快又稳，几乎看不出来）
            //   amount = 1 → 2.2Hz / ζ=0.28（慢半拍 + 明显回弹）
            float freq = Mathf.Lerp(Recipe.WobbleFrequencyAtZero, Recipe.WobbleFrequencyAtFull, amount);
            float zeta = Mathf.Lerp(1.0f, Recipe.WobbleDampingAtFull, amount);
            if (Recipe.ClumsyLegWobble && (key.StartsWith("upleg") || key.StartsWith("leg")))
            {
                // 腿要跟得紧一些，否则脚会飘
                freq *= 2.0f;
                zeta = Mathf.Min(1.0f, zeta + 0.35f);
            }

            float w = 2f * Mathf.PI * freq;
            float k = w * w;
            float c = 2f * zeta * w;

            Node node;
            if (!_nodes.TryGetValue(key, out node))
            {
                node = new Node();
                _nodes[key] = node;
            }
            if (!node.Init)
            {
                node.Pos = targetRotation;
                node.Init = true;
                return targetRotation;
            }

            // 在「旋转向量空间」里做弹簧：err 是当前位姿到目标位姿的旋转向量（弧度）。
            Vector3 err = ToRotationVector(Quaternion.Inverse(node.Pos) * targetRotation);
            // 显式欧拉；ω·dt 必须远小于 2 才稳 —— 2.2Hz 时 ω·dt = 0.28，安全。
            node.Vel += (err * k - node.Vel * c) * dt;
            node.Pos = Normalize(node.Pos * FromRotationVector(node.Vel * dt));

            _offsetSum += Quaternion.Angle(node.Pos, targetRotation);
            _offsetN++;
            return node.Pos;
        }

        // ------------------------------------------------------------------

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-8f)
                return Quaternion.identity;
            return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        /// <summary>四元数 → 旋转向量（轴 × 弧度）。取最短路径（w ≥ 0）。</summary>
        static Vector3 ToRotationVector(Quaternion q)
        {
            q = Normalize(q);
            if (q.w < 0f)
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            float s = Mathf.Sqrt(Mathf.Max(0f, 1f - q.w * q.w));
            if (s < 1e-6f)
                return Vector3.zero;
            float angle = 2f * Mathf.Acos(Mathf.Clamp(q.w, -1f, 1f));
            return new Vector3(q.x, q.y, q.z) / s * angle;
        }

        static Quaternion FromRotationVector(Vector3 v)
        {
            float a = v.magnitude;
            if (a < 1e-8f)
                return Quaternion.identity;
            return Quaternion.AngleAxis(a * Mathf.Rad2Deg, v / a);
        }

        // ------------------------------------------------------------------

        public string Report()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("== 笨拙层 ==");
            sb.AppendLine("Clumsiness = " + (Recipe != null ? Recipe.Clumsiness.ToString("F2") : "-")
                + "   晃动作用骨 " + _nodes.Count + " 根   当前晃动偏移 "
                + WobbleOffsetDegrees.ToString("F1") + "°");
            sb.AppendLine("落地踉跄 强度 " + ImpactAmount.ToString("F2")
                + "   累计触发 " + ImpactCount + " 次");
            return sb.ToString();
        }
    }
}
