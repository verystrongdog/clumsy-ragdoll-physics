using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ClumsyRagdoll.EditorTools
{
    /// <summary>
    /// 把浣熊的步态/待机/空中姿势**烘成真正的 AnimationClip 资产**，再建一个 AnimatorController。
    ///
    /// 依据：`ref-ActiveRagdolls-视频教程.md` §〇.1 —— 那套主动布娃娃架构的**硬门槛是「先有动画」**：
    /// <i>"An animated body. This is just a standard animated character, whose animation will be
    /// physically copied by the ragdoll."</i> 没有动画片段，整套东西无从谈起。
    /// 他给出的门槛不是「一套动画系统」，而是 **3 段循环动画**（Idle / Moving / InTheAir，
    /// 一个 AnimatorController、三个状态、两个参数 `speed` 与 `moving`）。
    ///
    /// **本文件做的就是那 3 段循环动画。** 内容不是从零编的 —— 用的是
    /// `ClumsyPoseDriver` 里**已经被诊断台验过「能站住 8.00s、能走 8.00s」的那组步态公式**，
    /// 参数化成 (相位, 幅度) 再逐帧采样成关键帧。所以：
    /// <list type="bullet">
    ///   <item>烘出来的片段与程序化步态**逐帧一致**（同一组公式、同一套符号约定）；</item>
    ///   <item>但它们是**资产**：可以在 Unity 的 Animation 窗口里手改、可以后续换成
    ///         Mixamo 的片段（EP06 那条重定向路线），而不用动一行代码。</item>
    /// </list>
    ///
    /// ⚠️ **符号约定（与 ClumsyPoseDriver 完全一致，不要改）**：
    /// 帧里存的是 <c>desiredLocal</c> —— 骨头**相对父级**的局部旋转，
    /// 由 <c>desiredLocal = bindLocal * AngleAxis(−θ, 骨内轴)</c> 得到；
    /// 物理侧反算是 <c>targetRotation = Inverse(desiredLocal) * bindLocal</c>。
    /// 两条是同一个式子的正反（推导见 `ClumsyArmIK.WorldToJoint` 与
    /// `ref-ActiveRagdolls-视频教程.md` §四）。
    /// </summary>
    public static class ClumsyAnimationBaker
    {
        public const string ClipDir = "Assets/Animations";
        public const string IdlePath = ClipDir + "/RaccoonIdle.anim";
        public const string WalkPath = ClipDir + "/RaccoonWalk.anim";
        public const string AirPath = ClipDir + "/RaccoonAir.anim";
        public const string ControllerPath = ClipDir + "/RaccoonLocomotion.controller";

        /// <summary>Blender 手 K 的那一套（基准帧 + 8 段动作，共 9 段）。
        /// 来源：HANDOFF-Blender关键帧动画.md —— 片段在
        /// Assets/Models/Characters/111_raccoon_animated.fbx 里，这里只放路径。
        /// 与上面那套烘出来的旧片段（RaccoonIdle/Walk/Air.anim）并存，用作 A/B 基线。</summary>
        public const string BlenderControllerPath = ClipDir + "/RaccoonActions.controller";

        /// <summary>走到「满幅步态」对应的水平速度（m/s）。Blend Tree 的第二个阈值。</summary>
        public const float WalkFullSpeed = 2.0f;
        /// <summary>跑起来对应的速度（m/s）。第三个阈值 —— 复用 Walk 片段、只是放快。</summary>
        public const float RunFullSpeed = 3.8f;

        // 关键帧密度：够密就行（姿势都是正弦，线性插值不会出问题）
        const int IdleKeys = 31;
        const int WalkKeys = 33;
        const int AirKeys = 19;

        // ------------------------------------------------------------------

        /// <summary>一根骨头在烘焙时需要的全部信息。</summary>
        sealed class BoneInfo
        {
            public string Key;          // 部件表的 key（"spine" / "arml" …）
            public string Path;         // 相对模型根的 transform 路径（动画曲线要用）
            public Quaternion BindLocal;
            public Vector3 LocalRight;
            public Vector3 LocalUp;
            public Vector3 Forward { get { return Vector3.Cross(LocalRight, LocalUp); } }
        }

        /// <summary>「让这块骨头可见地绕角色的右/上/前轴转多少度」。</summary>
        struct PoseDeg
        {
            public float Right, Up, Forward;
            public PoseDeg(float r, float u, float f) { Right = r; Up = u; Forward = f; }
        }

        /// <summary>与 ClumsyPoseDriver.JointTarget 同一个式子：写进去之前取负。</summary>
        static Quaternion JointTarget(float degrees, Vector3 localAxis)
        {
            return Quaternion.AngleAxis(-degrees, localAxis);
        }

        // ------------------------------------------------------------------

        static List<BoneInfo> BuildBoneTable()
        {
            List<BoneInfo> table = new List<BoneInfo>();
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RaccoonSkeleton.ModelPath);
            if (prefab == null)
                return table;

            GameObject probe = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
            List<PartSpec> specs = RaccoonSkeleton.DefaultParts();
            Vector3 worldRight = probe.transform.right;
            Vector3 worldUp = Vector3.up;

            for (int i = 0; i < specs.Count; i++)
            {
                PartSpec spec = specs[i];
                // hips 不烘：它是根，物理那侧靠竖直参考系 Root 撑，写它的 targetRotation 会和自平衡打架。
                // 手/脚也不烘：它们的角运动是 Locked（教程 P5 步2），写了也不动，只会把「姿态差」指标污染掉。
                if (spec.Key == "hips") continue;
                if (spec.Key.StartsWith("hand")) continue;
                if (spec.Key.StartsWith("foot")) continue;

                Transform bone = ClumsyRagdoll.FindDeep(probe.transform, spec.Bone);
                if (bone == null)
                    continue;

                BoneInfo b = new BoneInfo();
                b.Key = spec.Key;
                b.Path = RelativePath(probe.transform, bone);
                b.BindLocal = bone.localRotation;
                b.LocalRight = bone.InverseTransformDirection(worldRight);
                b.LocalUp = bone.InverseTransformDirection(worldUp);
                table.Add(b);
            }
            Object.DestroyImmediate(probe);
            return table;
        }

        static string RelativePath(Transform root, Transform t)
        {
            List<string> parts = new List<string>();
            Transform cur = t;
            while (cur != null && cur != root)
            {
                parts.Insert(0, cur.name);
                cur = cur.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        // ==================================================================
        // 姿势函数：三个片段共用一套（这就是「动画内容」）
        // ==================================================================

        /// <summary>
        /// **步态**。与 `ClumsyPoseDriver.FixedUpdate` 里那段程序化步态是同一组公式，
        /// 只是把 (amp / stride / gaitSign / 转身踏步) 收成了两个参数：
        /// <paramref name="amp"/> = 移动强度 0..1、<paramref name="breathe"/> = 待机呼吸的相位。
        /// </summary>
        static void LocomotionPose(RagdollRecipe r, float phase, float amp, float breathe,
                                   Dictionary<string, PoseDeg> p)
        {
            const float TwoPi = Mathf.PI * 2f;
            float s = Mathf.Sin(phase * TwoPi);
            float c = Mathf.Cos(phase * TwoPi);

            // ---- 手臂休息姿势（教程没有这一条；本模型 bind 是 T-pose，不放下就是侧平举）----
            float down = r.ArmDownDegrees;
            float share = Mathf.Clamp01(r.ShoulderDownShare);
            float shoulderDown = down * share;
            float upperDown = down * (1f - share);

            // ---- 腿 ----
            float legSwing = r.HipSwingDegrees * s * amp;
            p["uplegl"] = new PoseDeg(-legSwing, 0f, 0f);
            p["uplegr"] = new PoseDeg(+legSwing, 0f, 0f);

            float kneeL = r.KneeBendDegrees * Mathf.Max(0f, s) * amp;
            float kneeR = r.KneeBendDegrees * Mathf.Max(0f, -s) * amp;
            // 待机时也留一点膝弯 —— 直腿站着像木偶
            float kneeRest = r.KneeBendDegrees * 0.12f * (1f - amp);
            p["legl"] = new PoseDeg(kneeL + kneeRest, 0f, 0f);
            p["legr"] = new PoseDeg(kneeR + kneeRest, 0f, 0f);

            // ---- 臂：与同侧腿反相 ----
            float armAmp = r.ArmSwingDegrees * amp;
            float idleSway = Mathf.Sin((phase + breathe) * TwoPi) * 2.0f * (1f - amp);
            p["shoulderl"] = new PoseDeg(0f, 0f, +shoulderDown);
            p["shoulderr"] = new PoseDeg(0f, 0f, -shoulderDown);
            p["arml"] = new PoseDeg(+armAmp * s + idleSway, 0f, +upperDown);
            p["armr"] = new PoseDeg(-armAmp * s + idleSway, 0f, -upperDown);
            p["forearml"] = new PoseDeg(-r.ForeArmBendDegrees - armAmp * 0.4f * Mathf.Max(0f, s), 0f, 0f);
            p["forearmr"] = new PoseDeg(-r.ForeArmBendDegrees - armAmp * 0.4f * Mathf.Max(0f, -s), 0f, 0f);

            // ---- 躯干 ----
            float lean = r.SpineLeanDegrees * amp;
            // 呼吸：胸腔起伏，幅度小到几乎看不见但能感觉到
            float breath = Mathf.Sin(breathe * TwoPi * 0.5f) * 1.4f * (1f - amp);
            float sway = (1f - amp) * 1.5f * c;
            p["spine"] = new PoseDeg(lean + breath, sway, 0f);
            p["neck"] = new PoseDeg(-lean * 0.4f - breath * 0.5f, 0f, 0f);
            p["head"] = new PoseDeg(-lean * 0.3f + (1f - amp) * 1.2f * s - breath * 0.4f, 0f, 0f);
        }

        /// <summary>空中姿势：腿收起来、手臂张开、背微拱。**教程没有这一条**（他的三段里 InTheAir 是跟着动画走的）。</summary>
        static void AirPose(RagdollRecipe r, float phase, Dictionary<string, PoseDeg> p)
        {
            const float TwoPi = Mathf.PI * 2f;
            float j = Mathf.Sin(phase * TwoPi);          // 一点点摆动，免得像张静止图片

            float down = r.ArmDownDegrees;
            float upperDown = down * (1f - Mathf.Clamp01(r.ShoulderDownShare));

            p["uplegl"] = new PoseDeg(-34f + 3f * j, 0f, 0f);
            p["uplegr"] = new PoseDeg(-20f - 3f * j, 0f, 0f);
            p["legl"] = new PoseDeg(58f, 0f, 0f);
            p["legr"] = new PoseDeg(36f, 0f, 0f);

            p["shoulderl"] = new PoseDeg(0f, 0f, 0f);
            p["shoulderr"] = new PoseDeg(0f, 0f, 0f);
            p["arml"] = new PoseDeg(-18f - 4f * j, 0f, upperDown - 26f);
            p["armr"] = new PoseDeg(-14f + 4f * j, 0f, -(upperDown - 26f));
            p["forearml"] = new PoseDeg(-46f, 0f, 0f);
            p["forearmr"] = new PoseDeg(-38f, 0f, 0f);

            p["spine"] = new PoseDeg(-7f, 0f, 0f);
            p["neck"] = new PoseDeg(3f, 0f, 0f);
            p["head"] = new PoseDeg(4f + 1.5f * j, 0f, 0f);
        }

        // ==================================================================

        static void Bake(string path, string name, int keyCount, float length, bool loop,
                         System.Action<float, Dictionary<string, PoseDeg>> poseAt)
        {
            List<BoneInfo> table = BuildBoneTable();
            if (table.Count == 0)
            {
                Debug.LogError("[ClumsyAnimationBaker] 骨头表是空的 —— 模型路径对不上？" + RaccoonSkeleton.ModelPath);
                return;
            }

            AnimationClip clip = new AnimationClip();
            clip.name = name;
            clip.frameRate = Mathf.Max(1f, Mathf.Round((keyCount - 1) / Mathf.Max(length, 0.01f)));

            Dictionary<string, PoseDeg> pose = new Dictionary<string, PoseDeg>();
            for (int bi = 0; bi < table.Count; bi++)
            {
                BoneInfo b = table[bi];
                AnimationCurve cx = new AnimationCurve();
                AnimationCurve cy = new AnimationCurve();
                AnimationCurve cz = new AnimationCurve();
                AnimationCurve cw = new AnimationCurve();

                for (int k = 0; k < keyCount; k++)
                {
                    float t = length * k / (keyCount - 1);
                    float phase = (float)k / (keyCount - 1);
                    pose.Clear();
                    poseAt(phase, pose);

                    PoseDeg d;
                    if (!pose.TryGetValue(b.Key, out d))
                        d = new PoseDeg(0f, 0f, 0f);

                    Quaternion q = Quaternion.identity;
                    if (Mathf.Abs(d.Right) > 1e-4f) q = JointTarget(d.Right, b.LocalRight) * q;
                    if (Mathf.Abs(d.Up) > 1e-4f) q = JointTarget(d.Up, b.LocalUp) * q;
                    if (Mathf.Abs(d.Forward) > 1e-4f) q = JointTarget(d.Forward, b.Forward) * q;
                    q = b.BindLocal * q;      // desiredLocal

                    cx.AddKey(new Keyframe(t, q.x));
                    cy.AddKey(new Keyframe(t, q.y));
                    cz.AddKey(new Keyframe(t, q.z));
                    cw.AddKey(new Keyframe(t, q.w));
                }

                MakeLinear(cx); MakeLinear(cy); MakeLinear(cz); MakeLinear(cw);
                clip.SetCurve(b.Path, typeof(Transform), "localRotation.x", cx);
                clip.SetCurve(b.Path, typeof(Transform), "localRotation.y", cy);
                clip.SetCurve(b.Path, typeof(Transform), "localRotation.z", cz);
                clip.SetCurve(b.Path, typeof(Transform), "localRotation.w", cw);
            }

            AnimationClipSettings st = AnimationUtility.GetAnimationClipSettings(clip);
            st.loopTime = loop;
            st.loopBlend = false;
            AnimationUtility.SetAnimationClipSettings(clip, st);

            AssetDatabase.CreateAsset(clip, path);
            Debug.Log("[ClumsyAnimationBaker] 烘好 " + path + "：" + table.Count + " 根骨 × "
                + keyCount + " 帧，时长 " + length.ToString("F2") + "s，loop=" + loop);
        }

        /// <summary>线性切线。默认的 auto 切线在正弦上会过冲，宁可线性（帧够密）。</summary>
        static void MakeLinear(AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
        }

        // ==================================================================
        // 菜单
        // ==================================================================

        [MenuItem("笨拙布娃娃/动画/1. 烘焙三段动画片段", false, 40)]
        public static void BakeAll()
        {
            if (!AssetDatabase.IsValidFolder(ClipDir))
                AssetDatabase.CreateFolder("Assets", "Animations");

            RagdollRecipe r = new RagdollRecipe();

            // Idle：3s 循环。amp = 0（站着不动），靠呼吸与轻微摆动。
            Bake(IdlePath, "RaccoonIdle", IdleKeys, 3.0f, true, delegate (float phase, Dictionary<string, PoseDeg> p)
            {
                LocomotionPose(r, phase * 0.5f, 0f, phase, p);
            });

            // Walk：1s = **一个完整步态周期**。amp = 1（满幅）。
            Bake(WalkPath, "RaccoonWalk", WalkKeys, 1.0f, true, delegate (float phase, Dictionary<string, PoseDeg> p)
            {
                LocomotionPose(r, phase, 1f, 0f, p);
            });

            // Air：1.2s 循环的空中姿势。
            Bake(AirPath, "RaccoonAir", AirKeys, 1.2f, true, delegate (float phase, Dictionary<string, PoseDeg> p)
            {
                AirPose(r, phase, p);
            });

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ClumsyAnimationBaker] 三段片段都烘好了。");
        }

        [MenuItem("笨拙布娃娃/动画/2. 建 AnimatorController", false, 41)]
        public static void BuildController()
        {
            AnimationClip idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(IdlePath);
            AnimationClip walk = AssetDatabase.LoadAssetAtPath<AnimationClip>(WalkPath);
            AnimationClip air = AssetDatabase.LoadAssetAtPath<AnimationClip>(AirPath);
            if (idle == null || walk == null || air == null)
            {
                Debug.LogError("[ClumsyAnimationBaker] 先跑「1. 烘焙三段动画片段」。");
                return;
            }

            AnimatorController ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ctrl.AddParameter("speed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("grounded", AnimatorControllerParameterType.Bool);

            AnimatorStateMachine sm = ctrl.layers[0].stateMachine;

            // ---- 移动混合树（1D，按速度）—— 就是 ref-Unity动画系列-全集.md 的 EP04 ----
            BlendTree tree = new BlendTree();
            tree.name = "Locomotion";
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "speed";
            tree.useAutomaticThresholds = false;
            AssetDatabase.AddObjectToAsset(tree, ctrl);

            tree.AddChild(idle, 0f);
            tree.AddChild(walk, WalkFullSpeed);
            // 「跑」复用 Walk 片段、只把播放速度提上去 —— 本工程没有单独的跑步动画，
            // 这样至少速度对得上；以后换成真片段只要替换这一项。
            tree.AddChild(walk, RunFullSpeed);
            ChildMotion[] kids = tree.children;
            if (kids.Length >= 3)
            {
                kids[1].timeScale = 1.15f;
                kids[2].timeScale = 1.7f;
                tree.children = kids;
            }

            AnimatorState loco = sm.AddState("Locomotion");
            loco.motion = tree;
            loco.writeDefaultValues = true;
            sm.defaultState = loco;

            AnimatorState airState = sm.AddState("InTheAir");
            airState.motion = air;
            airState.writeDefaultValues = true;

            AnimatorStateTransition toAir = loco.AddTransition(airState);
            toAir.hasExitTime = false;
            toAir.duration = 0.10f;
            toAir.AddCondition(AnimatorConditionMode.IfNot, 0f, "grounded");

            AnimatorStateTransition toLoco = airState.AddTransition(loco);
            toLoco.hasExitTime = false;
            toLoco.duration = 0.18f;
            toLoco.AddCondition(AnimatorConditionMode.If, 0f, "grounded");

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ClumsyAnimationBaker] Controller 建好：" + ControllerPath
                + "（2 个状态 Locomotion / InTheAir，2 个参数 speed / grounded）");
        }

        [MenuItem("笨拙布娃娃/动画/3. 全部重建（1 + 2）", false, 42)]
        public static void RebuildAll()
        {
            BakeAll();
            BuildController();
        }

        [MenuItem("笨拙布娃娃/动画/报告", false, 43)]
        public static void Report()
        {
            Debug.Log(ReportText());
        }

        public static string ReportText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("== 动画片段 ==");
            string[] paths = { IdlePath, WalkPath, AirPath, ControllerPath };
            for (int i = 0; i < paths.Length; i++)
            {
                Object a = AssetDatabase.LoadAssetAtPath<Object>(paths[i]);
                sb.AppendLine("  " + paths[i] + " -> " + (a == null ? "**缺失**" : "OK"));
                AnimationClip c = a as AnimationClip;
                if (c != null)
                    sb.AppendLine("      时长 " + c.length.ToString("F2") + "s · 帧率 " + c.frameRate
                        + " · 曲线 " + AnimationUtility.GetCurveBindings(c).Length);
            }
            return sb.ToString();
        }
    }
}
