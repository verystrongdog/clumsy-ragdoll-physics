using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// **动画身体**：教程那套「双身体架构」里负责演动画的那一半。
    ///
    /// 来源：`ref-ActiveRagdolls-视频教程.md` §一 3/4 ——
    /// <i>"用一个身体演动画，复制一份出来，靠施力去追它"</i>。两个身体：
    /// <list type="number">
    ///   <item><b>动画身体</b>：普通的带动画角色，**完全没有物理**（本类创建的那份隐藏副本）；</item>
    ///   <item><b>物理身体</b>：它的复制品，由 rigidbody + joints 组成 —— 也就是本工程的布娃娃。</item>
    /// </list>
    /// 接缝只有一行：把动画骨骼的**局部**旋转抄给物理关节的 <c>targetRotation</c>
    /// （<see cref="ClumsyPoseDriver"/> 里做的 <c>Inverse(desiredLocal) * bindLocal</c>，
    /// 推导见 <see cref="ClumsyArmIK.WorldToJoint"/>）。**换掉的是姿势的来源，不是机制。**
    ///
    /// 三处与教程不同，都是有意为之：
    /// <list type="number">
    ///   <item><b>动画身体只关渲染器，不关 GameObject。</b> 教程 §一 5 专门讲过一个坑：
    ///         把动画模型整份禁用会**停止播动画**，除非把 culling mode 改成 <c>Always Animate</c>。
    ///         这里渲染器关掉（看不见）、<c>AlwaysAnimate</c> 也照设（保险）。</item>
    ///   <item><b>Animator 关掉自动更新，改由我们手动 <c>Update(dt)</c>。</b>
    ///         实测：<c>Animator.Update()</c> 在 <c>enabled = false</c> 时照样工作、不抛异常。
    ///         这样**播放模式与编辑器诊断台的行为完全一致** —— 诊断台里没有 Update 循环，
    ///         不手动步进就没法做确定性验证。</item>
    ///   <item><b>姿势是烘出来的 AnimationClip 资产</b>（<c>Assets/Animations/</c>，
    ///         见 <c>ClumsyAnimationBaker</c>），不是从 Animator 里凭空来的 —— 但播放仍然是 Animator，
    ///         仍然是 <c>speed</c> + <c>grounded</c> 两个参数、Locomotion / InTheAir 两个状态，
    ///         与教程的三段动画门槛一致。</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClumsyAnimationSource : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;
        public ClumsyController Controller;

        [Tooltip("模型预置体（用来生成那份无物理的动画副本）。")]
        public GameObject ModelPrefab;

        [Tooltip("AnimatorController。为空 = 本模块不工作，姿势驱动回落到程序化步态。")]
        public RuntimeAnimatorController ControllerAsset;

        /// <summary>一根骨头的动画侧信息。</summary>
        public sealed class AnimBone
        {
            public string Key;
            public ClumsyPart Part;
            public Transform AnimBoneTransform;
            /// <summary>出生时相对 connectedBody 的局部旋转（物理侧反算 targetRotation 要用）。</summary>
            public Quaternion BindLocalToBody;
        }

        public readonly List<AnimBone> Bones = new List<AnimBone>();

        /// <summary>
        /// ★ **只拄旋转、没有物理体的那几根骨**（现在是两只脚的 <c>ToeBase</c>）。
        ///
        /// 为什么需要单开一张表：布娃娃的部件表（<see cref="RaccoonSkeleton.DefaultParts"/>）
        /// **没有脚趾**（那根骨太短，上物理没意义），所以脚趾不在 <see cref="Bones"/> 里 ——
        /// 而 <see cref="Step"/> 原来只把 <see cref="Bones"/> 里的骨在两边同步。
        /// 后果（2026-10-01 实测到）：**Blender 动画里脚趾是弯的，Unity 里那只鞋的前半截完全不弯** ——
        /// 对一只鞋长 288 mm / 脚趾骨 176 mm 的浣熊来说，就是「鞋的前半部分不会像真鞋那样弯折」。
        ///
        /// 这几根骨在物理侧只是普通 Transform 子节点（没有 Rigidbody/Joint），
        /// 所以不需要（也不可能）写 targetRotation —— **直接抄 localRotation** 就行。
        /// </summary>
        public sealed class ExtraBone
        {
            public string BoneName;
            public Transform AnimBoneTransform;
            public Transform PhysicalTransform;
        }

        /// <summary>没有物理体、只抄旋转的骨（脚趾）。</summary>
        public readonly List<ExtraBone> ExtraBones = new List<ExtraBone>();

        /// <summary>要抄旋转的骨名单（mixamo 命名）。</summary>
        public static readonly string[] ExtraBoneNames =
        {
            "mixamorig:LeftToeBase",
            "mixamorig:RightToeBase",
        };
        readonly Dictionary<string, AnimBone> _byKey = new Dictionary<string, AnimBone>();

        public GameObject AnimBody { get; private set; }
        public Animator Animator { get; private set; }

        /// <summary>这一帧喂给 Animator 的水平速度（m/s）。</summary>
        public float AnimSpeed { get; private set; }
        public bool AnimGrounded { get; private set; } = true;

        /// <summary>状态名（诊断用）。</summary>
        public string AnimStateName { get; private set; } = "-";

        public bool Ready { get { return Animator != null && Bones.Count > 0; } }

        /// <summary>姿态差的全程均值 / 峰值（诊断台报告用）。</summary>
        public float PoseGapAverage { get; private set; }
        public float PoseGapPeak { get; private set; }
        public float PoseGapAccum;
        public int PoseGapFrames;

        Vector3 _prevPelvis;
        bool _hasPrev;

        // ------------------------------------------------------------------

        public void Setup(ClumsyRagdoll ragdoll, RagdollRecipe recipe, ClumsyController controller,
                          GameObject modelPrefab, RuntimeAnimatorController controllerAsset)
        {
            Ragdoll = ragdoll;
            Recipe = recipe;
            Controller = controller;
            ModelPrefab = modelPrefab;
            ControllerAsset = controllerAsset;
            if (Ragdoll != null)
                _prevPelvis = Ragdoll.PelvisPosition;
            Build();
        }

        void Build()
        {
            Bones.Clear();
            _byKey.Clear();
            if (Ragdoll == null || ModelPrefab == null)
                return;

            // ---- 动画身体：预置体的一份**无物理副本** ----
            AnimBody = Instantiate(ModelPrefab);
            AnimBody.name = "浣熊（动画身体）";
            AnimBody.transform.SetParent(transform, false);
            AnimBody.transform.localPosition = Vector3.zero;
            AnimBody.transform.localRotation = Quaternion.identity;
            AnimBody.transform.localScale = Vector3.one;

            // 它不该参与物理：把副本上带过来的物理组件全拆掉。
            Rigidbody[] rbs = AnimBody.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < rbs.Length; i++) Destroy(rbs[i]);
            ConfigurableJoint[] joints = AnimBody.GetComponentsInChildren<ConfigurableJoint>(true);
            for (int i = 0; i < joints.Length; i++) Destroy(joints[i]);
            Collider[] cols = AnimBody.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++) Destroy(cols[i]);

            // 看不见，但层级保持激活（见类注释第 1 条）。
            SkinnedMeshRenderer[] skins = AnimBody.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skins.Length; i++) skins[i].enabled = false;
            MeshRenderer[] meshes = AnimBody.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshes.Length; i++) meshes[i].enabled = false;

            Animator = AnimBody.GetComponent<Animator>();
            if (Animator == null)
                Animator = AnimBody.AddComponent<Animator>();
            Animator.runtimeAnimatorController = ControllerAsset;
            Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            Animator.applyRootMotion = false;
            Animator.Rebind();
            // ★ 关自动更新，改手动步进（见类注释第 2 条）。
            Animator.enabled = false;

            // ---- 骨头表 ----
            for (int i = 0; i < Ragdoll.Parts.Count; i++)
            {
                ClumsyPart part = Ragdoll.Parts[i];
                if (part == null || part.Joint == null)
                    continue;
                // 髋是根：它的自平衡靠竖直参考系 Root 撑，写它的 targetRotation 会和那套打架。
                // （动画片段里也刻意没有髋的曲线。）
                if (part.IsPelvis)
                    continue;

                Transform t = ClumsyRagdoll.FindDeep(AnimBody.transform, part.Spec.Bone);
                if (t == null)
                    continue;

                AnimBone b = new AnimBone();
                b.Key = part.Spec.Key;
                b.Part = part;
                b.AnimBoneTransform = t;
                b.BindLocalToBody = part.RestLocalToBody;
                Bones.Add(b);
                _byKey[b.Key] = b;
            }

            // ---- 没有物理体、只抄旋转的骨（脚趾）----
            // 两边都是同一个预置体的实例，骨骼层级一样，直接 FindDeep 就对得上。
            ExtraBones.Clear();
            for (int i = 0; i < ExtraBoneNames.Length; i++)
            {
                string boneName = ExtraBoneNames[i];
                Transform animT = ClumsyRagdoll.FindDeep(AnimBody.transform, boneName);
                Transform physT = ClumsyRagdoll.FindDeep(Ragdoll.transform, boneName);
                if (animT == null || physT == null)
                {
                    Debug.LogWarning("[ClumsyAnimationSource] 找不到脚趾骨 " + boneName
                        + "（动画侧 " + (animT != null) + " / 物理侧 " + (physT != null)
                        + "）—— 鞋前半截不会弯。");
                    continue;
                }
                ExtraBone eb = new ExtraBone();
                eb.BoneName = boneName;
                eb.AnimBoneTransform = animT;
                eb.PhysicalTransform = physT;
                ExtraBones.Add(eb);
            }

            if (Animator.runtimeAnimatorController == null)
                Debug.LogWarning("[ClumsyAnimationSource] 没给 AnimatorController —— 动画身体不会动，"
                    + "姿势驱动会回落到程序化步态。");
        }

        // ------------------------------------------------------------------

        /// <summary>每个物理步调用一次（在姿势驱动写关节之前）。</summary>
        public void Step(float dt)
        {
            if (Animator == null)
                return;

            // 水平速度自己算，不用 Controller.SpeedMeasured —— 那个 0.1s 才更新一次，动画会一顿一顿。
            if (Ragdoll != null)
            {
                Vector3 p = Ragdoll.PelvisPosition;
                if (_hasPrev)
                {
                    Vector3 d = Vector3.ProjectOnPlane(p - _prevPelvis, Vector3.up);
                    float inst = d.magnitude / Mathf.Max(dt, 1e-4f);
                    AnimSpeed = Mathf.Lerp(AnimSpeed, inst, 0.35f);
                }
                _prevPelvis = p;
                _hasPrev = true;
            }

            // ★ 用 IsGroundedNow 而不是 IsGrounded：诊断台里 Physics.Simulate 不派发碰撞消息，
            //   IsGrounded（由 LimbCollision 写）永远是 false，动画会一直卡在 InTheAir。
            AnimGrounded = Ragdoll != null && Ragdoll.IsGroundedNow;

            Animator.SetFloat("speed", AnimSpeed);
            Animator.SetBool("grounded", AnimGrounded);
            Animator.Update(dt);

            AnimatorStateInfo info = Animator.GetCurrentAnimatorStateInfo(0);
            AnimStateName = info.IsName("InTheAir") ? "InTheAir" : "Locomotion";


            // ★ 脚趾：物理侧没有 Rigidbody/Joint，姿势驱动那一路（targetRotation）根本碰不到它 ——
            //   在这里直接把动画侧的 localRotation 抄过去，那只大鞋的前半截才会跟着弯。
            for (int i = 0; i < ExtraBones.Count; i++)
            {
                ExtraBone eb = ExtraBones[i];
                if (eb.AnimBoneTransform != null && eb.PhysicalTransform != null)
                    eb.PhysicalTransform.localRotation = eb.AnimBoneTransform.localRotation;
            }
            // 运行统计（诊断台报告用；只看最后一帧会低估得很厉害）
            float gap = PoseGapDegrees();
            PoseGapAccum += gap;
            PoseGapFrames++;
            PoseGapAverage = PoseGapFrames > 0 ? PoseGapAccum / PoseGapFrames : 0f;
            if (gap > PoseGapPeak)
                PoseGapPeak = gap;
        }

        /// <summary>
        /// 取出这根骨头这一帧该有的 <c>targetRotation</c>。
        /// 返回 false = 动画侧没有这根骨（比如髋、手），调用者自己回落。
        /// </summary>
        public bool TryGetTargetRotation(ClumsyPart part, out Quaternion targetRotation)
        {
            targetRotation = Quaternion.identity;
            if (part == null || part.Spec == null)
                return false;
            AnimBone b;
            if (!_byKey.TryGetValue(part.Spec.Key, out b) || b.AnimBoneTransform == null)
                return false;
            Quaternion desiredLocal = b.AnimBoneTransform.localRotation;
            targetRotation = Quaternion.Inverse(desiredLocal) * b.BindLocalToBody;
            return true;
        }

        /// <summary>
        /// ★ **「笨拙度」的量化**：动画身体想要那个姿势，物理身体实际到了哪 —— 逐骨平均夹角（度）。
        ///
        /// 这就是教程那套架构里「笨拙」的来源：布娃娃是一具**被弹簧拉向姿势的软雕像**，
        /// 它永远追不上、永远慢半拍。这个数越大越笨拙（太大就成了瘫）。
        /// 逐骨算的是**局部旋转**差（相对各自的父级），所以与身体站在哪里、朝哪边无关。
        /// </summary>
        public float PoseGapDegrees()
        {
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < Bones.Count; i++)
            {
                AnimBone b = Bones[i];
                if (b.Part == null || b.Part.Body == null || b.Part.Joint == null || b.AnimBoneTransform == null)
                    continue;
                Rigidbody cb = b.Part.Joint.connectedBody;
                Quaternion parent = cb != null ? cb.rotation : Quaternion.identity;
                Quaternion actualLocal = Quaternion.Inverse(parent) * b.Part.Body.rotation;
                sum += Quaternion.Angle(actualLocal, b.AnimBoneTransform.localRotation);
                n++;
            }
            return n > 0 ? sum / n : 0f;
        }

        /// <summary>逐骨姿态差的最大值（诊断用：看是哪根骨最跟不上）。</summary>
        public string WorstBones(int count)
        {
            List<AnimBone> sorted = new List<AnimBone>(Bones);
            List<float> gaps = new List<float>();
            for (int i = 0; i < sorted.Count; i++)
            {
                AnimBone b = sorted[i];
                float g = 0f;
                if (b.Part != null && b.Part.Body != null && b.Part.Joint != null)
                {
                    Rigidbody cb = b.Part.Joint.connectedBody;
                    Quaternion parent = cb != null ? cb.rotation : Quaternion.identity;
                    g = Quaternion.Angle(Quaternion.Inverse(parent) * b.Part.Body.rotation,
                        b.AnimBoneTransform.localRotation);
                }
                gaps.Add(g);
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int k = 0; k < count; k++)
            {
                int best = -1;
                for (int i = 0; i < sorted.Count; i++)
                    if (gaps[i] >= 0f && (best < 0 || gaps[i] > gaps[best])) best = i;
                if (best < 0) break;
                sb.Append(sorted[best].Key + " " + gaps[best].ToString("F1") + "°  ");
                gaps[best] = -1f;
            }
            return sb.ToString();
        }

        void OnDestroy()
        {
            if (AnimBody != null)
                Destroy(AnimBody);
        }

        public string Report()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("== 动画身体 ==");
            sb.AppendLine("AnimatorController " + (ControllerAsset != null ? ControllerAsset.name : "**没给**")
                + "   骨骼 " + Bones.Count + " 根   自动更新=关（手动 Update）");
            sb.AppendLine("状态 " + AnimStateName + "   speed=" + AnimSpeed.ToString("F2")
                + " m/s   grounded=" + AnimGrounded);
            sb.AppendLine("★ 姿态差（动画 vs 物理）平均 " + PoseGapDegrees().ToString("F1") + "°"
                + "   最大的几根：" + WorstBones(4));
            return sb.ToString();
        }
    }
}
