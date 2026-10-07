using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{

    /// <summary>
    /// 教程版布娃娃本体。
    ///
    /// 这套东西的全部内容就是教程 P1 + P2：
    ///   ① 每个部件一个 Rigidbody + ConfigurableJoint，connectedBody 指向父部件（P1 步5）；
    ///   ② 线性运动 Locked、角运动 Free（P1 步6）；
    ///   ③ Angular X Drive / Angular YZ Drive 的 Position Spring 填一个数（P2 步7）；
    ///      四肢 = 180、hips = 750（作者模型的原值）；
    ///   ④ targetRotation 保持 0 = 建关节时的姿势（P2 步5）。
    ///
    /// **站起来的机制**（本文档从 §3.2 ④ 的「hips 强弹簧」推到底）：
    /// hips 的 connectedBody 指向一个「只转 yaw 的竖直参考系」Root，
    /// 于是 hips 的角弹簧等价于「把骨盆扳回竖直」的恢复力矩。
    /// 整具布娃娃绕地面接触点转动时，重力力矩 M·g·h_com 是**发散**项、hips 弹簧 k 是**恢复**项，
    /// 所以直立的条件是 **k_hips &gt; M·g·h_com**。教程的 750 对他的模型成立
    /// （M≈10kg、h≈0.5m → 49 N·m/rad，750 有 15 倍余量），换模型必须按这条重新定标 ——
    /// 这正是教程 §3.5 项3 说的「180/750 是针对他这个模型质量分布的经验值，换模型必然要重调」。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClumsyRagdoll : MonoBehaviour
    {
        public RagdollRecipe Recipe;

        public readonly List<ClumsyPart> Parts = new List<ClumsyPart>();

        public ClumsyPart Hips;

        /// <summary>竖直参考系（教程 P6 的 Root）。只转 yaw、位置跟随骨盆。</summary>
        public Transform Root;
        public Rigidbody RootBody;

        public float TotalMass;
        public float CenterOfMassHeight;
        public float HipsSpring;
        public float LimbSpring;

        /// <summary>上半身（肩/臂/小臂/手）的弹簧刚度 —— 比四肢再软一截，给「笨拙」留出可见的甩臂。</summary>
        public float UpperSpring
        {
            get { return LimbSpring * (Recipe != null ? Recipe.UpperBodySpringRatio : 1f); }
        }

        /// <summary>
        /// 躯干（脊/颈/头）一档。★ 2026-10-01 与**腿**拆开：
        /// 原来它和 upleg/leg/foot 共用 LimbSpring，而腿要硬才站得住、走得了，
        /// 于是躯干也硬 ⇒ 做不出「面条」。拆开之后躯干可以单独软。
        /// </summary>
        public float TorsoSpring
        {
            get { return LimbSpring * (Recipe != null ? Recipe.TorsoSpringRatio : 1f); }
        }
        public float GroundY;

        public PhysicsMaterial SlideMaterial;

        readonly RagdollJointConfigurator _jointConfigurator =
            new RagdollJointConfigurator();

        /// <summary>落地判定，由 LimbCollision 写入（教程 P3 步8）。</summary>
        public bool IsGrounded;

        /// <summary>★ 2026-10-01：这一次离地是**玩家自己跳的**（落地时踉跄要打折、空中要走跳跃姿势）。</summary>
        public bool JustJumped;

        /// <summary>连续落地时间。</summary>
        public float GroundedTime;

        /// <summary>骨盆「出生时朝上」的那根轴在局部坐标里的方向，用来算直立度。</summary>
        Vector3 _restUpLocal = Vector3.up;

        public int SelfCollisionLayer { get; private set; }

        // ------------------------------------------------------------------

        public static ClumsyRagdoll Build(GameObject modelRoot, RagdollRecipe recipe)
        {
            if (modelRoot == null)
                throw new ArgumentNullException("modelRoot");
            if (recipe == null)
                recipe = new RagdollRecipe();

            ClumsyRagdoll rag = modelRoot.GetComponent<ClumsyRagdoll>();
            if (rag == null)
                rag = modelRoot.AddComponent<ClumsyRagdoll>();
            rag.Recipe = recipe;
            rag.Rebuild();
            return rag;
        }

        // ------------------------------------------------------------------

public void Rebuild()
        {
            DestroyPhysics();

            List<PartSpec> specs = RaccoonSkeleton.DefaultParts();
            Dictionary<string, PartSpec> byBone = new Dictionary<string, PartSpec>();
            for (int i = 0; i < specs.Count; i++)
                byBone[specs[i].Bone] = specs[i];

            // ---- P1 步7/8：把部件放进「不自碰撞」层 ----
            SelfCollisionLayer = ResolveLayer(Recipe.SelfCollisionLayerName);
            SetLayerRecursively(gameObject, SelfCollisionLayer);
            if (Recipe.DisableSelfCollision)
                Physics.IgnoreLayerCollision(SelfCollisionLayer, SelfCollisionLayer, true);

            // 蒙皮：必须 updateWhenOffscreen = true。默认 false 时 Unity 只看「根骨的 bounds」
            // 决定要不要重算蒙皮 —— 布娃娃的四肢/头会甩出出生姿势的包围盒，那时网格不更新。
            // （实测：只转 head 骨 70° 画面零变化；整具平移 0.6 m 画面正常跟着走。）
            SkinnedMeshRenderer[] skinned = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                skinned[i].updateWhenOffscreen = true;
                skinned[i].quality = SkinQuality.Bone4;
            }

            // ★ 2026-10-02：**先把绕序反了的面翻回来**（治「能看到内壁」的真病灶）。
            FixInvertedWindingOn(skinned);

            // ★ 2026-10-02：模型材质换成**双面**（治「能看到胳膊内壁」）。
            ApplyDoubleSidedMaterials(skinned);

            // ---- 解析骨骼 ----
            List<ClumsyPart> resolved = new List<ClumsyPart>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                PartSpec spec = specs[i];
                Transform bone = FindDeep(transform, spec.Bone);
                if (bone == null)
                {
                    Debug.LogWarning("[ClumsyRagdoll] 找不到骨骼 " + spec.Bone + "，跳过该部件。");
                    continue;
                }
                ClumsyPart part = new ClumsyPart();
                part.Spec = spec;
                part.Bone = bone;
                part.RestPosition = bone.position;
                part.RestRotation = bone.rotation;
                resolved.Add(part);
            }
            if (resolved.Count == 0)
                throw new InvalidOperationException("一个骨骼都没找到，模型对不上（期望 " + RaccoonSkeleton.ModelPath + "）。");

            Dictionary<string, ClumsyPart> byKey = new Dictionary<string, ClumsyPart>();
            for (int i = 0; i < resolved.Count; i++)
                byKey[resolved[i].Spec.Key] = resolved[i];

            ComputeLengths(resolved, byBone);

            // 质量与质心（只用出生位置，与地面无关）
            TotalMass = 0f;
            float moment = 0f;
            for (int i = 0; i < resolved.Count; i++)
            {
                TotalMass += resolved[i].Spec.Mass;
                moment += resolved[i].Spec.Mass * resolved[i].RestPosition.y;
            }
            float comY = TotalMass > 0f ? moment / TotalMass : 0f;

            // ---- P3 步2：脚底物理材质 slide ----
            SlideMaterial = new PhysicsMaterial("slide");
            SlideMaterial.dynamicFriction = Recipe.FootDynamicFriction;
            SlideMaterial.staticFriction = Recipe.FootStaticFriction;
            SlideMaterial.bounciness = Recipe.FootBounciness;
            SlideMaterial.frictionCombine = PhysicsMaterialCombine.Minimum;
            SlideMaterial.bounceCombine = PhysicsMaterialCombine.Minimum;

            // 角色轴局部化到每根骨头：targetRotation 的旋转轴就写在这两个向量上
            Vector3 worldRight = transform.right;
            Vector3 worldUp = Vector3.up;
            for (int i = 0; i < resolved.Count; i++)
            {
                ClumsyPart p = resolved[i];
                p.LocalRight = p.Bone.InverseTransformDirection(worldRight);
                p.LocalUp = p.Bone.InverseTransformDirection(worldUp);
            }

            // ---- P1 步3：每个部件一个 Rigidbody + Collider ----
            for (int i = 0; i < resolved.Count; i++)
                CreateBody(resolved[i], byKey);
            Physics.SyncTransforms();

            // ★ 2026-10-01：手臂链单独一层 —— 让它**与躯干碰**（布娃娃自碰撞仍然关着）。
            //   为什么要单独给手臂开：见 ClumsyRagdollBench.PairPenetrationReport() 的审计
            //   （关节附近到处都是构造性重叠，自碰撞全开会互推爆炸）。
            ApplyLimbLayerCollision(resolved);
            Physics.SyncTransforms();

            // ---- 地面接触点与质心高：碰撞体建好之后用 bounds 精确取 ----
            GroundY = float.PositiveInfinity;
            for (int i = 0; i < resolved.Count; i++)
            {
                Collider c = resolved[i].Shape;
                if (c == null)
                    continue;
                float bottom = c.bounds.min.y;
                if (bottom < GroundY)
                    GroundY = bottom;
            }
            if (float.IsInfinity(GroundY))
                GroundY = 0f;
            CenterOfMassHeight = Mathf.Max(0.05f, comY - GroundY);

            // ---- P2：弹簧定标 k_hips = margin × M·g·h_com，k_limb = k_hips × 180/750 ----
            float g = Mathf.Abs(Recipe.GravityY);
            HipsSpring = Recipe.HipsSpringOverride > 0f
                ? Recipe.HipsSpringOverride
                : Recipe.HipsSpringMargin * TotalMass * g * CenterOfMassHeight;
            LimbSpring = Recipe.LimbSpringOverride > 0f
                ? Recipe.LimbSpringOverride
                : HipsSpring * Recipe.LimbSpringRatio;

            // ---- P6 步1/2：竖直参考系 Root ----
            ClumsyPart pelvis = null;
            for (int i = 0; i < resolved.Count; i++)
                if (resolved[i].IsPelvis)
                    pelvis = resolved[i];
            if (pelvis == null)
                throw new InvalidOperationException("部件表里没有 Pelvis。");
            Hips = pelvis;

            GameObject rootGo = new GameObject(name + " · Root");
            // ⚠️ 挂到模型根下面，**不要留成场景根物体**。
            // 留成场景根的话，诊断台 TearDown 掉 sim.World 之后它会活下来 ——
            // 实测（2026-10-01）：跑了几十次诊断之后场景里堆了 **129 个孤儿 Root**。
            rootGo.transform.SetParent(transform, true);
            rootGo.transform.position = pelvis.RestPosition;
            rootGo.transform.rotation = Quaternion.Euler(0f, pelvis.RestRotation.eulerAngles.y, 0f);
            Root = rootGo.transform;
            RootBody = rootGo.AddComponent<Rigidbody>();
            RootBody.isKinematic = true;
            RootBody.useGravity = false;
            RootBody.constraints = RigidbodyConstraints.FreezeRotation;

            _restUpLocal = Quaternion.Inverse(pelvis.RestRotation) * Vector3.up;

            // ---- P1 步5/6 + P2 步7：关节与角驱动 ----
            for (int i = 0; i < resolved.Count; i++)
            {
                ClumsyPart part = resolved[i];
                ConfigurableJoint joint = CreateJoint(part, byKey);
                part.Joint = joint;
                _jointConfigurator.Configure(
                    part,
                    joint,
                    Recipe,
                    HipsSpring,
                    LimbSpring,
                    UpperSpring,
                    TorsoSpring);
            }

            // ---- 存下「相对于 connectedBody」的出生局部旋转（手部 IK 换算要用，见 ClumsyPart.RestLocalToBody）
            for (int k = 0; k < resolved.Count; k++)
            {
                ClumsyPart p = resolved[k];
                p.RestLocal = p.Bone.localRotation;
                Rigidbody cb = p.Joint != null ? p.Joint.connectedBody : null;
                p.RestLocalToBody = cb != null
                    ? Quaternion.Inverse(cb.rotation) * p.Bone.rotation
                    : p.Bone.rotation;

                // 自查：IK 的世界旋转 → targetRotation 换算假定「关节的 connectedBody 就是 Transform 父亲」。
                // 手臂链上不成立就会静默算错（和 ParentKey 写成自己那个 bug 同一类），所以直接报错。
                if (cb != null && !p.IsPelvis && p.Bone.parent != cb.transform)
                    Debug.LogError("[ClumsyRagdoll] 部件 " + p.Spec.Key
                        + " 的 connectedBody（" + cb.name + "）不是它的 Transform 父亲（"
                        + (p.Bone.parent != null ? p.Bone.parent.name : "null")
                        + "）。手部 IK 的 targetRotation 换算会算错。");
            }

            Parts.Clear();
            Parts.AddRange(resolved);
            Physics.SyncTransforms();
        }

        // ------------------------------------------------------------------
        // 构建细节
        // ------------------------------------------------------------------

        void ComputeLengths(List<ClumsyPart> parts, Dictionary<string, PartSpec> byBone)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                ClumsyPart part = parts[i];
                Transform chain = null;
                float best = float.NegativeInfinity;
                Transform t = part.Bone;
                for (int c = 0; c < t.childCount; c++)
                {
                    Transform child = t.GetChild(c);
                    if (!byBone.ContainsKey(child.name))
                        continue;
                    // 「链上子骨」= 局部 +Y 偏得最远的那个部件子骨。
                    // 骨骼模型里 +Y 就是骨头的生长方向（实测：除 Hips→UpLeg / Neck→Shoulder 这两处
                    // 解剖附着点外，所有子骨 localPosition 都是 (0, length, 0)）。
                    if (child.localPosition.y > best)
                    {
                        best = child.localPosition.y;
                        chain = child;
                    }
                }
                if (chain == null)
                {
                    // 末端部件：胶囊（脚掌）用最近子物体的距离当长度，球（头/手）不需要长度。
                    if (part.Spec.Shape == PartShape.Capsule && t.childCount > 0)
                        part.Length = Vector3.Distance(t.position, t.GetChild(0).position);
                    else
                        part.Length = 0f;
                }
                else
                {
                    part.Length = Vector3.Distance(t.position, chain.position);
                }
            }
        }



        void CreateBody(ClumsyPart part, Dictionary<string, ClumsyPart> byKey)
        {
            PartSpec spec = part.Spec;
            GameObject go = part.Bone.gameObject;

            Rigidbody body = go.GetComponent<Rigidbody>();
            if (body == null)
                body = go.AddComponent<Rigidbody>();
            body.mass = spec.Mass;
            body.useGravity = true;
            body.isKinematic = false;
            // P7 表：limb = 0.2 / 0.25；lower arm & hand = 0 / 0
            bool light = spec.Role == PartRole.Passenger && spec.Key.StartsWith("forearm");
            body.linearDamping = light ? Recipe.LightLimbDrag : Recipe.LimbDrag;
            body.angularDamping = light ? Recipe.LightLimbAngularDrag : Recipe.LimbAngularDrag;
            body.maxAngularVelocity = 25f;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.interpolation = RigidbodyInterpolation.None;
            
            
            body.automaticCenterOfMass = true;
            body.automaticInertiaTensor = true;
            part.Body = body;

            Collider shape = CreateCollider(go, spec, part.Length);
            part.Shape = shape;

            if (spec.Key.StartsWith("foot"))
                shape.sharedMaterial = SlideMaterial;
        }
        /// <summary>
        /// ★ 2026-10-01：把**手臂链**（上臂/前臂/手）放到独立图层，让它与「身体核心」互碰。
        ///
        /// owner 报：「倒地 / 被推 / 被抓时，胳膊折进身体里」——根因是布娃娃**自碰撞整个关着**
        /// （`Physics.IgnoreLayerCollision(No Self Collision, 同层, true)`，教程 P1 步8）。
        ///
        /// 那为什么不直接全开？`ClumsyRagdollBench.PairPenetrationReport()` 量的审计
        /// （站立 1s / 走路 3s / 推倒后 2s）里，关节附近全是**构造性重叠**，而且大多发生在
        /// **不相连**的两骨之间（相连的由 `ConfigurableJoint.enableCollision=false` 管）：
        ///    髋×脊柱 370mm · 脊柱×颈 310 · 颈×头 327 · 脊柱×头 228 · 髋×颈 144 ·
        ///    脊柱×大腿 77 · 脊柱×肩 62/76 · 头×肩 59/71 · 上臂×手 73/80 · 髋×小腿 12
        /// 把这些打开就是互推爆炸。而 **上臂/前臂/手 × 躯干**是审计里唯一「本来就互不重叠」的一组 ⇒ 只给它开。
        ///
        /// 层矩阵（运行时 `Physics.IgnoreLayerCollision`，与原有 DisableSelfCollision 同一手法）：
        ///    手臂 × 手臂 = 关（同一只手内部本来就有构造性重叠：上臂×手 80mm）
        ///    手臂 × 身体 = **开**
        ///    手臂 × Default（地面/道具/箱子）= 开
        /// 肩**不在**这一层：肩×脊柱本身就重叠 62–76 mm，开不得（要治得先重做躯干碰撞体）。
        /// </summary>
        void ApplyLimbLayerCollision(List<ClumsyPart> parts)
        {
            if (!Recipe.LimbCollidesWithBody)
                return;
            int limb = LayerMask.NameToLayer(Recipe.LimbCollisionLayerName);
            if (limb < 0)
            {
                Debug.LogWarning("[ClumsyRagdoll] 图层「" + Recipe.LimbCollisionLayerName
                    + "」不存在 ⇒ 手臂仍然不与身体碰撞。请跑一次菜单「笨拙布娃娃 / 1. 建立图层 / Tag / 碰撞矩阵」。");
                return;
            }

            Physics.IgnoreLayerCollision(limb, limb, true);
            Physics.IgnoreLayerCollision(limb, SelfCollisionLayer, false);
            Physics.IgnoreLayerCollision(limb, 0, false);

            int moved = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                ClumsyPart p = parts[i];
                if (p == null || p.Bone == null || p.Spec == null)
                    continue;
                string k = p.Spec.Key;
                bool isLimb = k.StartsWith("arm") || k.StartsWith("forearm") || k.StartsWith("hand");
                if (!isLimb)
                    continue;
                p.Bone.gameObject.layer = limb;
                if (p.Shape != null && p.Shape.transform != p.Bone)
                    p.Shape.gameObject.layer = limb;
                moved++;
            }
            LimbLayerIndex = limb;
            LimbPartsMoved = moved;
        }

        /// <summary>手臂链所在图层（-1 = 没启用）。诊断台用。</summary>
        public int LimbLayerIndex { get; private set; } = -1;

        /// <summary>被搬进手臂层的部件数（诊断台用）。</summary>
        public int LimbPartsMoved { get; private set; }

        static Collider CreateCollider(GameObject go, PartSpec spec, float length)
        {
            // ★ 2026-10-01：挂点。MountEuler 非零时在本骨下建一个**只带旋转**的子物体，
            //   碰撞体挂在它上面 —— 脚需要这个：脚骨轴比腽底斜 33°，照骨轴摆的盒/胶囊
            //   总有一个角扎到腽底以下。挂点随骨一起动，所以「摆平」只在 bind pose；
            //   脚一转，盒也跟着转（正确行为）。
            GameObject holder = go;
            if (spec.MountEuler != Vector3.zero)
            {
                holder = new GameObject("ColliderMount");
                holder.transform.SetParent(go.transform, false);
                holder.transform.localRotation = Quaternion.Euler(spec.MountEuler);
                holder.layer = go.layer;
            }

            if (spec.Shape == PartShape.Box)
            {
                BoxCollider b = holder.AddComponent<BoxCollider>();
                b.size = spec.Size;
                b.center = spec.Center;
                return b;
            }

            if (spec.Shape == PartShape.Sphere)
            {
                SphereCollider s = holder.AddComponent<SphereCollider>();
                s.radius = spec.Radius;
                s.center = spec.Center;
                return s;
            }

            CapsuleCollider c = holder.AddComponent<CapsuleCollider>();
            c.direction = 1; // 沿骨头局部 +Y
            c.radius = spec.Radius;
            // ColliderLength > 0 时截短（小腿要截，否则端球落到腽底以下）；
            // 否则照老口径：至少半半径的圆柱段。
            float cyl = spec.ColliderLength > 0f ? spec.ColliderLength : Mathf.Max(length, spec.Radius * 0.5f);
            c.height = cyl + spec.Radius * 2f;
            c.center = spec.Center != Vector3.zero ? spec.Center : new Vector3(0f, cyl * 0.5f, 0f);
            return c;
        }


        /// <summary>
        /// ★ 2026-10-01：把「一侧腿」（大腿/小腿/脚）的角驱动弹簧整体乘一个倍数。
        ///
        /// 用途：脚种植步态里**支撑腿要真的撑得住**。实测（GaitSlipReport + 逐帧脚位）：
        /// 四肢弹簧只有 31 N·m/rad（面条档）时，髋/膝的目标角**根本拉不动腿** ——
        /// 左脚一直挂在髖后面 35 cm，以身体的速度在地上滑（每步 2.6 cm）。
        /// 支撑相加硬之后，腿才会真的按目标角扫过，脚才有可能«粘住»。
        /// 摆动腿与手臂继续用原档（面条感不变）。
        /// </summary>
        public void SetLegSpringScale(string footKey, float scale)
        {
            if (string.IsNullOrEmpty(footKey))
                return;
            string suffix = footKey.EndsWith("l") ? "l" : "r";
            SetSpringScale("upleg" + suffix, scale);
            SetSpringScale("leg" + suffix, scale);
            SetSpringScale("foot" + suffix, scale);
        }


        /// <summary>
        /// ★ 2026-10-02 **搬运加硬专用**：显式指定阻尼比的版本。
        ///
        /// 为什么需要它：`Recipe.DampingRatio = 0` ⇒ 所有关节驱动的 `positionDamper` 也是 0
        /// （`SetDriveSpring` 里那个 `if (Recipe.DampingRatio > 0f)` 直接跳过）。
        /// 全身软、无阻尼是这只浣熊的手感基础，**不能全局改**；
        /// 但搬运时手臂要加硬 14 倍（46 N·m/rad），**无阻尼的硬弹簧会自激** ——
        /// 实测两只手每个物理步 |Δv| 高达 1.16（对照组只有 0.007），肉眼看就是«剧烈抖动»。
        /// 所以「加硬」必须**连阻尼一起给**。
        /// </summary>
        public void SetSpringScale(string key, float scale, float dampingRatio)
        {
            ClumsyPart p = Find(key);
            if (p == null || p.Joint == null)
                return;
            SetDriveSpring(p, SpringFor(p) * Mathf.Max(0.01f, scale), dampingRatio);
        }

        /// <summary>把某个部件的角驱动弹簧改成 base×scale。</summary>
        public void SetSpringScale(string key, float scale)
        {
            ClumsyPart p = Find(key);
            if (p == null || p.Joint == null)
                return;
            SetDriveSpring(p, SpringFor(p) * Mathf.Max(0.01f, scale));
        }

        /// <summary>按 k 设某个部件的角驱动（X 与 YZ 两路），阻尼照 Recipe.DampingRatio 定标。</summary>
        void SetDriveSpring(ClumsyPart part, float k)
        {
            SetDriveSpring(part, k, Recipe != null ? Recipe.DampingRatio : 0f);
        }

        // 兼容运行期调参入口；弹簧分档策略由 RagdollJointConfigurator 统一维护。
        float SpringFor(ClumsyPart part)
        {
            return _jointConfigurator.GetSpring(
                part,
                Recipe,
                HipsSpring,
                LimbSpring,
                UpperSpring,
                TorsoSpring);
        }

        /// <summary>★ 2026-10-02 带显式阻尼比的版本（搬运加硬用，见 SetSpringScale 的重载）。</summary>
        void SetDriveSpring(ClumsyPart part, float k, float dampingRatio)
        {
            if (part == null || part.Joint == null)
                return;
            _jointConfigurator.ConfigureSpring(
                part,
                part.Joint,
                Recipe,
                k,
                dampingRatio);
        }

        ConfigurableJoint CreateJoint(ClumsyPart part, Dictionary<string, ClumsyPart> byKey)
        {
            // 教程 P1 步3：必须是 ConfigurableJoint（不是 CharacterJoint）
            ConfigurableJoint joint = part.Bone.gameObject.AddComponent<ConfigurableJoint>();

            Rigidbody connected = null;
            if (part.IsPelvis)
            {
                // 教程 §5.3 项2：P1–P5 的 hips 是 None（世界参考），P6 起接 Root。
                connected = Recipe.ConnectHipsToRoot ? RootBody : null;
            }
            else if (!string.IsNullOrEmpty(part.Spec.ParentKey) && byKey.ContainsKey(part.Spec.ParentKey))
            {
                connected = byKey[part.Spec.ParentKey].Body;
            }

            // 防御：ParentKey 写错时会把父部件解析成自己，Unity 会静默地把关节挂到世界坐标上，
            // 表现是「这一支被钉在地里不动」而其他部件照常掉——很难从读数上看出来。这里直接报错。
            if (connected != null && connected == part.Body)
            {
                Debug.LogError("[ClumsyRagdoll] 部件 " + part.Spec.Key + " 的父部件解析成了它自己（ParentKey 写错了）。");
                connected = null;
            }
            joint.connectedBody = connected;
            joint.autoConfigureConnectedAnchor = true;
            // 教程 P1 步8 的 anchor：默认 (0.5,0.5,0.5) 是给「方块」准备的；骨骼模型上
            // anchor = 0（本骨原点）恰好就是解剖学关节（肩/肘/髋/膝/踝）本身。
            joint.anchor = Recipe.AnchorAtBoneOrigin ? Vector3.zero : new Vector3(0.5f, 0.5f, 0.5f);
            joint.axis = Vector3.right;
            joint.secondaryAxis = Vector3.up;
            joint.rotationDriveMode = RotationDriveMode.XYAndZ;
            joint.enablePreprocessing = true;
            joint.enableCollision = false;
            joint.projectionMode = JointProjectionMode.None;

            // 教程 P1 步6：x/y/z Motion = Locked，角运动 Free。
            ConfigurableJointMotion linear = Recipe.LockLinearMotion ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Free;
            if (part.IsPelvis)
            {
                // 教程 P2 步2 专门把 hips 的线性改回 Free；P6 步3 又改 Locked 并接 Root。
                // 这里默认 Free：Root 是运动学刚体、位置每帧跟随骨盆，
                // 「把 hips 锁到一个正在跟随它的刚体上」是自指的，会把骨盆的平移直接冻住
                // （教程 §5.4 自己也说这段「没有讲清」）。角驱动的参考系仍然来自 Root，自平衡不受影响。
                linear = ConfigurableJointMotion.Free;
            }
            joint.xMotion = linear;
            joint.yMotion = linear;
            joint.zMotion = linear;

            // ---- 手部与环境交互：肘关节解锁 ----
            // 教程 P5 步2 把**所有**非旋转点的角运动锁死，本模型照做了（LockPassengerAngular）。
            // 但肘锁死意味着小臂相对上臂不能转 —— 两骨 IK 就无从下手（等于一条不能折的棍）。
            // 所以这里给手臂链开一个例外：只放「小臂 + 手」，小腿/脚照旧锁死。
            // 诊断台实测（2026-10-01，ClumsyRagdollBench.LockPassengerDetailReport）：
            //   全锁 8.00s / 只放小臂+手 8.00s / 放全部 0.40s —— 只放手臂是白拿，不付代价。
            bool elbowException = Recipe.FreeElbowAngular && part.IsElbowDownstream;
            bool lockAngular = Recipe.FreeAngularMotion == false
                || (Recipe.LockPassengerAngular && part.Spec.Role == PartRole.Passenger && !elbowException);
            ConfigurableJointMotion angular = lockAngular ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Free;
            joint.angularXMotion = angular;
            joint.angularYMotion = angular;
            joint.angularZMotion = angular;

            // 教程 P7 步41：lower arm 的 Joint.massScale = 5（治 torso 抖动）
            if (Recipe.LowerArmMassScale > 0f && part.Spec.Key.StartsWith("forearm"))
                joint.massScale = Recipe.LowerArmMassScale;

            return joint;
        }


        // ------------------------------------------------------------------
        // 运行期
        // ------------------------------------------------------------------

        /// <summary>Root 位置跟随骨盆的时间常数（秒）。0 = 硬跟（教程 P6 的做法）。给一点点阻尼，相机不会跟着布料抖。</summary>
        public float RootFollowSmoothing = 0.05f;

        /// <summary>教程 P6 步1：Root 是相机的载体，且要跟着人走。每 FixedUpdate 在物理步进前调。</summary>
public void SyncRootToPelvis()
        {
            if (Root == null || Hips == null || Hips.Body == null)
                return;
            Vector3 wanted = Hips.Body.position;
            if (RootFollowSmoothing <= 0f)
            {
                Root.position = wanted;
                return;
            }
            float k = 1f - Mathf.Exp(-Time.fixedDeltaTime / RootFollowSmoothing);
            Root.position = Vector3.Lerp(Root.position, wanted, k);
        }

        /// <summary>骨盆的直立度：出生时朝上的那根轴，现在与世界竖直的夹角余弦。</summary>
        public float Upright
        {
            get
            {
                if (Hips == null || Hips.Body == null)
                    return 0f;
                Vector3 up = Hips.Body.rotation * _restUpLocal;
                return Vector3.Dot(up, Vector3.up);
            }
        }

        /// <summary>骨盆「出生朝上」的那根轴，现在的世界方向（Upright 就是它 · 世界上方）。</summary>
        public Vector3 PelvisUpWorld
        {
            get
            {
                if (Hips == null || Hips.Body == null)
                    return Vector3.up;
                return Hips.Body.rotation * _restUpLocal;
            }
        }

        /// <summary>当前倾斜角（度）：骨盆「出生朝上」轴与世界竖直的夹角。教程 §三 的 balancePercent 就是它 /180。</summary>
        public float UprightAngleDegrees
        {
            get { return Vector3.Angle(PelvisUpWorld, Vector3.up); }
        }

        /// <summary>
        /// 骨盆「出生前方」的世界方向（角色朝向）。
        /// 出生时标定的 LocalRight / LocalUp 是局部量，叉乘再转到世界 —— 与 ClumsyPoseDriver.ForwardAxis 同一个式子。
        /// 转身力矩要用它算朝向误差（教程用的是 PhysicalTorso.transform.forward；本模型的骨局部轴不是角色轴）。
        /// </summary>
        public Vector3 PelvisForwardWorld
        {
            get
            {
                if (Hips == null || Hips.Body == null)
                    return Vector3.forward;
                Vector3 fwd = Vector3.Cross(Hips.LocalRight, Hips.LocalUp);
                if (fwd.sqrMagnitude < 1e-8f)
                    return Vector3.forward;
                return Hips.Body.rotation * fwd.normalized;
            }
        }


        /// <summary>
        /// **不依赖碰撞消息**的落地判定。
        ///
        /// 为什么需要：编辑器诊断台里用 Physics.Simulate 手动步进时**不派发碰撞消息**，
        /// 于是 LimbCollision 永远不写 IsGrounded —— 实测：站着不动 8 秒，IsGrounded 一直是 false。
        /// 动画身体靠这个开关切 Locomotion ↔ InTheAir，一个永远 false 的判定会让它
        /// **一直播空中姿势**（本轮实测就是这么发现的：站着时状态是 InTheAir）。
        ///
        /// 播放模式里 IsGrounded（由 LimbCollision 写）是主来源，这里只是兜底。
        /// 判据：脚/小腿的碰撞体底面是否碰到出生时的地面接触高度。
        /// </summary>
        public bool IsGroundedNow
        {
            get
            {
                if (IsGrounded)
                    return true;
                float lowest = float.PositiveInfinity;
                for (int i = 0; i < Parts.Count; i++)
                {
                    ClumsyPart p = Parts[i];
                    if (p.Shape == null || p.Spec == null)
                        continue;
                    string k = p.Spec.Key;
                    if (!k.StartsWith("foot") && !k.StartsWith("leg"))
                        continue;
                    float b = p.Shape.bounds.min.y;
                    if (b < lowest)
                        lowest = b;
                }
                if (float.IsInfinity(lowest))
                    return false;
                return lowest <= GroundY + 0.05f;
            }
        }

        public Vector3 PelvisPosition
        {
            get { return Hips != null && Hips.Body != null ? Hips.Body.position : transform.position; }
        }

        /// <summary>
        /// 脚/小腿碰撞体的世界最低点。★ 2026-10-01：托举层的「找地」用它 ——
        /// 只要这个值高于 <see cref="GroundY"/>，就说明脚离地了，托举要把目标高度压低那么多，
        /// 保证「正常移动时至少一只脚连着地面」。
        /// </summary>
        public float LowestSupportY
        {
            get
            {
                float lowest = float.PositiveInfinity;
                for (int i = 0; i < Parts.Count; i++)
                {
                    ClumsyPart p = Parts[i];
                    if (p.Shape == null || p.Spec == null)
                        continue;
                    string k = p.Spec.Key;
                    if (!k.StartsWith("foot") && !k.StartsWith("leg"))
                        continue;
                    float b = p.Shape.bounds.min.y;
                    if (b < lowest)
                        lowest = b;
                }
                return lowest;
            }
        }

        public ClumsyPart Find(string key)
        {
            for (int i = 0; i < Parts.Count; i++)
                if (Parts[i].Spec.Key == key)
                    return Parts[i];
            return null;
        }

        /// <summary>
        /// ★ 2026-10-02：**把模型的蒙皮材质全部换成双面**（`Recipe.DoubleSidedModelMaterials`）。
        ///
        /// 症状：右胳膊有一部分能看见内壁，而且随视角变化换地方 —— 这是**背面剔除**的典型表现
        /// （近侧壁被判成背面剔掉，看到的是远侧壁的内表面）。
        /// Unity 内置的 Standard 材质不暴露 `_Cull`，所以这里把每个材质**克隆一份**到
        /// `ClumsyRagdoll/DoubleSidedStandard`（等价 surface shader，只多一个 `Cull Off`），
        /// 贴图 / 颜色 / 金属度 / 光滑度 / 法线 / 遮蔽 / 自发光逐项搬过去 —— 观感一致，只是两面都画。
        /// 克隆按源材质缓存，重复 Rebuild 不会一直造新材质。
        /// </summary>
        public const string DoubleSidedShaderName = "ClumsyRagdoll/DoubleSidedStandard";

        static readonly Dictionary<Material, Material> _doubleSidedCache = new Dictionary<Material, Material>();

        void ApplyDoubleSidedMaterials(SkinnedMeshRenderer[] skinned)
        {
            if (Recipe == null || !Recipe.DoubleSidedModelMaterials)
                return;
            Shader ds = Shader.Find(DoubleSidedShaderName);
            if (ds == null)
            {
                Debug.LogWarning("[ClumsyRagdoll] 找不到 shader " + DoubleSidedShaderName
                    + "，材质保持单面（模型的胳膊可能还会看到内壁）。");
                return;
            }
            for (int i = 0; i < skinned.Length; i++)
            {
                Material[] src = skinned[i].sharedMaterials;
                Material[] dst = new Material[src.Length];
                bool changed = false;
                for (int k = 0; k < src.Length; k++)
                {
                    dst[k] = ToDoubleSided(src[k], ds);
                    if (!ReferenceEquals(dst[k], src[k]))
                        changed = true;
                }
                if (changed)
                    skinned[i].sharedMaterials = dst;
            }
        }

        static Material ToDoubleSided(Material src, Shader ds)
        {
            if (src == null || src.shader == ds)
                return src;
            Material cached;
            if (_doubleSidedCache.TryGetValue(src, out cached) && cached != null)
                return cached;
            Material m = new Material(ds);
            m.name = src.name + "（双面）";
            if (src.HasProperty("_MainTex") && m.HasProperty("_MainTex")) m.SetTexture("_MainTex", src.GetTexture("_MainTex"));
            if (src.HasProperty("_Color") && m.HasProperty("_Color")) m.SetColor("_Color", src.GetColor("_Color"));
            if (src.HasProperty("_BumpMap") && m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", src.GetTexture("_BumpMap"));
            if (src.HasProperty("_BumpScale") && m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", src.GetFloat("_BumpScale"));
            if (src.HasProperty("_OcclusionMap") && m.HasProperty("_OcclusionMap")) m.SetTexture("_OcclusionMap", src.GetTexture("_OcclusionMap"));
            if (src.HasProperty("_EmissionMap") && m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", src.GetTexture("_EmissionMap"));
            if (src.HasProperty("_EmissionColor") && m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", src.GetColor("_EmissionColor"));
            if (src.HasProperty("_Metallic") && m.HasProperty("_Metallic")) m.SetFloat("_Metallic", src.GetFloat("_Metallic"));
            if (src.HasProperty("_Glossiness") && m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", src.GetFloat("_Glossiness"));
            _doubleSidedCache[src] = m;
            return m;
        }

        // ==================================================================
        // ★ 2026-10-02 · 修面：把绕序反了的连通块翻回来
        // ==================================================================

        /// <summary>
        /// owner 报「能看到右胳膊的内壁，而且随视角变换换地方」。
        ///
        /// 实测（Play 里走动着、材质换成 `Cull Off` + `VFACE` 染色）：
        /// 有的方位有 **6925 个像素渲染出来是«背面»**，而且是一整块（不是洞、不是轮廓锯齿）——
        /// 那块面的**绕序是反的**：近侧壁被判成背面剔掉，于是看到的是远侧壁的内表面。
        /// 材质反查还查出那块是**背包那一层**（身体.003 3675 + tripo 2251 + 书包皮革 656 + 手环手表 263 像素），
        /// 而不是手臂那层 —— 但观感上就在右肩/右背那一带，所以 owner 说是「右胳膊」。
        ///
        /// **判定**：逐连通块算**相对自身质心的带符号体积**
        /// `V = Σ dot(A−C₀, cross(B−C₀, C−C₀)) / 6`（对平移不变，比相对世界原点可靠得多 ——
        /// 相对原点那次因为角色走远了，符号全乱，白测了一轮）。`V` 为负且量级不为零的块就是反的。
        /// 实测够大的 191 块里 **177 正 / 14 负**。
        ///
        /// **处置**：把那 14 块的**三角形下标顺序倒过来**，并把它们**顶点的法线取反**。
        /// 连通块是按共享顶点划分的，所以一个顶点只属于一块，不会误伤邻块。
        /// 返回**一份新的 mesh**，不改导入资产；`SkinQuality`/`updateWhenOffscreen` 由外层负责。
        /// </summary>
        static void FixInvertedWindingOn(SkinnedMeshRenderer[] skinned)
        {
            for (int i = 0; i < skinned.Length; i++)
            {
                if (skinned[i] == null || skinned[i].sharedMesh == null)
                    continue;
                Mesh fixedMesh = FixInvertedWinding(skinned[i].sharedMesh);
                if (fixedMesh != null && !ReferenceEquals(fixedMesh, skinned[i].sharedMesh))
                    skinned[i].sharedMesh = fixedMesh;
            }
        }

        /// <summary>把绕序反了的连通块翻回来；没找到问题就原样返回（不复制）。</summary>
        public static Mesh FixInvertedWinding(Mesh src)
        {
            if (src == null)
                return null;

            Vector3[] vs = src.vertices;
            Vector3[] ns = src.normals;
            if (vs == null || vs.Length == 0 || ns == null || ns.Length != vs.Length)
                return src;
            int nv = vs.Length;
            int subCount = src.subMeshCount;

            // 全部三角形摊平（同时记住它属于哪个子网格，用来回写）
            int[][] subTris = new int[subCount][];
            int total = 0;
            for (int s = 0; s < subCount; s++)
            {
                subTris[s] = src.GetTriangles(s);
                total += subTris[s].Length;
            }
            int ntri = total / 3;
            if (ntri == 0)
                return src;

            // ---- 并查集：共享顶点的三角形算同一块 ----
            int[] parent = new int[nv];
            for (int i = 0; i < nv; i++)
                parent[i] = i;
            for (int s = 0; s < subCount; s++)
            {
                int[] t = subTris[s];
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int ra = FindRoot(parent, t[i]);
                    int rb = FindRoot(parent, t[i + 1]);
                    int rc = FindRoot(parent, t[i + 2]);
                    if (rb != ra) parent[rb] = ra;
                    if (rc != ra) parent[rc] = ra;
                }
            }

            // ---- 每块的质心 ----
            Vector3[] sum = new Vector3[nv];
            int[] cnt = new int[nv];
            int[] triCount = new int[nv];
            for (int s = 0; s < subCount; s++)
            {
                int[] t = subTris[s];
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    Vector3 c = (vs[t[i]] + vs[t[i + 1]] + vs[t[i + 2]]) / 3f;
                    int r = FindRoot(parent, t[i]);
                    sum[r] += c;
                    cnt[r]++;
                    triCount[r]++;
                }
            }
            Vector3[] cen = new Vector3[nv];
            for (int i = 0; i < nv; i++)
                if (cnt[i] > 0)
                    cen[i] = sum[i] / cnt[i];

            // ---- 每块相对自身质心的带符号体积 ----
            double[] vol = new double[nv];
            for (int s = 0; s < subCount; s++)
            {
                int[] t = subTris[s];
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int r = FindRoot(parent, t[i]);
                    Vector3 c0 = cen[r];
                    Vector3 a = vs[t[i]] - c0;
                    Vector3 b = vs[t[i + 1]] - c0;
                    Vector3 c = vs[t[i + 2]] - c0;
                    vol[r] += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
                }
            }

            // ---- 标记「反了」的块：体积为负、量级不为零、三角形够多 ----
            const double MinVolume = 1e-11;
            const int MinTris = 40;
            bool[] bad = new bool[nv];
            int badCount = 0;
            for (int i = 0; i < nv; i++)
            {
                if (triCount[i] < MinTris)
                    continue;
                if (vol[i] < -MinVolume)
                {
                    bad[i] = true;
                    badCount++;
                }
            }
            if (badCount == 0)
                return src;

            // ---- 造一份新 mesh：坏块的三角形反向 + 坏块顶点的法线取反 ----
            Mesh dst = UnityEngine.Object.Instantiate(src);
            dst.name = src.name + "（已修正绕序）";
            Vector3[] newNs = new Vector3[ns.Length];
            for (int i = 0; i < ns.Length; i++)
            {
                int r = FindRoot(parent, i);
                newNs[i] = (r < nv && bad[r]) ? -ns[i] : ns[i];
            }
            dst.normals = newNs;

            for (int s = 0; s < subCount; s++)
            {
                int[] t = src.GetTriangles(s);
                int[] outT = new int[t.Length];
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int r = FindRoot(parent, t[i]);
                    if (r < nv && bad[r])
                    {
                        // 反向：a b c → a c b
                        outT[i] = t[i];
                        outT[i + 1] = t[i + 2];
                        outT[i + 2] = t[i + 1];
                    }
                    else
                    {
                        outT[i] = t[i];
                        outT[i + 1] = t[i + 1];
                        outT[i + 2] = t[i + 2];
                    }
                }
                dst.SetTriangles(outT, s, false);
            }
            dst.RecalculateBounds();
            Debug.Log("[ClumsyRagdoll] 修正绕序：" + badCount + " 个连通块被翻正（共 " + ntri + " 个三角形）。");
            return dst;
        }

        static int FindRoot(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        /// <summary>按当前 Recipe 重算弹簧并写回所有角驱动（运行期调参用，不动姿势驱动的 targetRotation）。</summary>
        public void ApplySprings()
        {
            if (Recipe == null || TotalMass <= 0f)
                return;
            float g = Mathf.Abs(Recipe.GravityY);
            HipsSpring = Recipe.HipsSpringOverride > 0f
                ? Recipe.HipsSpringOverride
                : Recipe.HipsSpringMargin * TotalMass * g * CenterOfMassHeight;
            LimbSpring = Recipe.LimbSpringOverride > 0f
                ? Recipe.LimbSpringOverride
                : HipsSpring * Recipe.LimbSpringRatio;

            for (int i = 0; i < Parts.Count; i++)
            {
                ClumsyPart part = Parts[i];
                if (part.Joint == null)
                    continue;
                Quaternion keep = part.Joint.targetRotation;
                _jointConfigurator.Configure(
                    part,
                    part.Joint,
                    Recipe,
                    HipsSpring,
                    LimbSpring,
                    UpperSpring,
                    TorsoSpring);
                part.Joint.targetRotation = keep;
            }
        }

        /// <summary>把整具布娃娃竖直平移，让最低的碰撞体底面刚好停在 groundLevel 上方 clearance 处。</summary>
        public void SnapAboveGround(float groundLevel, float clearance)
        {
            float lift = (groundLevel - GroundY) + clearance;
            if (Mathf.Abs(lift) < 1e-5f)
                return;
            for (int i = 0; i < Parts.Count; i++)
            {
                ClumsyPart p = Parts[i];
                p.RestPosition += Vector3.up * lift;
                if (p.Body != null)
                    p.Body.position = p.RestPosition;
            }
            if (Root != null)
                Root.position += Vector3.up * lift;
            GroundY = groundLevel + clearance;
            Physics.SyncTransforms();
        }

        /// <summary>UI 用：质心相对地面接触点的高度。</summary>
        public float ComHeightSafe { get { return CenterOfMassHeight; } }

        public void Respawn()
        {
            for (int i = 0; i < Parts.Count; i++)
            {
                ClumsyPart p = Parts[i];
                if (p.Body == null)
                    continue;
                p.Body.linearVelocity = Vector3.zero;
                p.Body.angularVelocity = Vector3.zero;
                p.Body.position = p.RestPosition;
                p.Body.rotation = p.RestRotation;
                p.Body.Sleep();
            }
            if (Root != null)
                Root.position = Hips != null ? Hips.RestPosition : Root.position;
            IsGrounded = false;
            GroundedTime = 0f;
            Physics.SyncTransforms();
        }

        /// <summary>给整具布娃娃一记冲量（教程 P3 的 AddForce 同族，用于验证「推得倒、能弹回来」）。</summary>
        public void Push(Vector3 impulse)
        {
            int n = 0;
            for (int i = 0; i < Parts.Count; i++)
                if (Parts[i].Body != null)
                    n++;
            if (n == 0)
                return;
            Vector3 share = impulse / n;
            for (int i = 0; i < Parts.Count; i++)
                if (Parts[i].Body != null)
                    Parts[i].Body.AddForce(share, ForceMode.Impulse);
        }

        public void DestroyPhysics()
        {
            for (int i = 0; i < Parts.Count; i++)
            {
                ClumsyPart p = Parts[i];
                if (p.Bone == null)
                    continue;
                ConfigurableJoint j = p.Bone.GetComponent<ConfigurableJoint>();
                if (j != null)
                    DestroyImmediateSafe(j);
                // 碰撞体可能挂在子物体上（见 CreateCollider 的 MountEuler）—— 按存下来的引用删；
                // 挂在子物体上时连挂点一起删（不然重建会叠出第二套碰撞体）。
                if (p.Shape != null)
                {
                    Transform mount = p.Shape.transform;
                    if (mount != null && mount != p.Bone)
                        DestroyImmediateSafe(mount.gameObject);
                    else
                        DestroyImmediateSafe(p.Shape);
                }
                Rigidbody b = p.Bone.GetComponent<Rigidbody>();
                if (b != null)
                    DestroyImmediateSafe(b);
            }
            Parts.Clear();
            Hips = null;

            if (Root != null)
            {
                DestroyImmediateSafe(Root.gameObject);
                Root = null;
                RootBody = null;
            }
        }

        static void DestroyImmediateSafe(UnityEngine.Object o)
        {
            if (o == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(o);
            else
                UnityEngine.Object.DestroyImmediate(o);
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        public static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
        }

        public static int ResolveLayer(string name)
        {
            int layer = LayerMask.NameToLayer(name);
            if (layer >= 0)
                return layer;
            Debug.LogWarning("[ClumsyRagdoll] 图层「" + name + "」不存在，暂用 Default。"
                + "请在编辑期跑一次 菜单「笨拙布娃娃 / 建立图层与碰撞矩阵」。");
            return 0;
        }

        public static Transform FindDeep(Transform root, string boneName)
        {
            if (root.name == boneName)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), boneName);
                if (found != null)
                    return found;
            }
            return null;
        }

void FixedUpdate()
        {
            SyncRootToPelvis();
            if (IsGrounded)
                GroundedTime += Time.fixedDeltaTime;
            else
                GroundedTime = 0f;
        }
    }
}
