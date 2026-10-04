using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>部件在关节链里的角色。来源：教程（ref-布娃娃教程全集流程.md）P4 步8 / P5 步2。</summary>
    public enum PartRole
    {
        /// <summary>根部件（髋）。来源：教程 P1 步5「hips 留空（None）」+ P2 步4「hips spring = 750」。</summary>
        Pelvis = 0,

        /// <summary>旋转点：教程里保留 CopyMotion 的那些（head / torso / stomach / hips / shoulders / upper legs）。</summary>
        Rotation = 1,

        /// <summary>非旋转点：教程 P4 步8 删掉 CopyMotion、P5 步2 把角运动锁死（lower arm / hand / lower leg / foot）。</summary>
        Passenger = 2
    }

    public enum PartShape
    {
        /// <summary>胶囊：沿「本骨 → 子骨」方向。教程里躯干/四肢用胶囊或球。</summary>
        Capsule = 0,
        /// <summary>球：末端部件（头、手）。来源：教程 P3 步2「把 slide 拖到左右脚的 SphereCollider 上」。</summary>
                Sphere = 1,
        /// <summary>
        /// 盒：★ 2026-10-01 新增，只给脚用。
        /// 理由（实测）：脚骨轴比脚底斜 33°（脚骨世界轴 ≈(-0.31,-0.52,+0.80)），
        /// 所以**任何**沿骨轴摆的胶囊都会有一个端球扎到脚底以下 —— r=0.055 的胶囊
        /// 让碰撞体最低点落在 y=-0.0486，整只小浣熊悬在演出面上方 48.6 mm。
        /// 盒子挂在「摆平的挂点」上（见 <see cref="PartSpec.MountEuler"/>），底面才真的是鞋底。
        /// </summary>
        Box = 2
    }

    [Serializable]
    public sealed class PartSpec
    {
        public string Key;
        public string Bone;
        public string ParentKey;
        public PartRole Role;
        public float Mass;
        public float Radius;
        public PartShape Shape;

        /// <summary>本骨到「子骨」的世界距离；由构建器实测填入（0 = 末端部件，用球）。</summary>
        [NonSerialized] public float Length;
        /// <summary>盒的尺寸（在**碰撞体自身坐标系**里）。只有 <see cref="PartShape.Box"/> 用。</summary>
        public Vector3 Size;

        /// <summary>碰撞体中心（在**碰撞体自身坐标系**里）。零 = 老口径
        /// （胶囊从骨原点沿 +Y 起算、球扎在骨原点上）。</summary>
        public Vector3 Center;

        /// <summary>
        /// 碰撞体挂点的**骨局部**欧拉角。零 = 直接挂在本骨上（老口径）。
        /// 非零时会在本骨下建一个只带旋转的子物体、碰撞体挂在它上面 —— 只给脚用。
        /// 取值 = <c>Quaternion.Inverse(脚骨 bind-pose 世界旋转).eulerAngles</c>，
        /// 所以挂点在 bind pose 下与世界轴对齐（鞋底于是真的水平）。
        /// </summary>
        public Vector3 MountEuler;

        /// <summary>胶囊圆柱段长度。&gt;0 时覆盖 <see cref="Length"/> 的自动值
        /// （把胶囊截短，免得端球伸到鞋底以下 —— 小腿现在就是这样）。</summary>
        public float ColliderLength;


        public PartSpec(string key, string bone, string parentKey, PartRole role, float mass, float radius, PartShape shape)
        {
            Key = key;
            Bone = bone;
            ParentKey = parentKey;
            Role = role;
            Mass = mass;
            Radius = radius;
            Shape = shape;
        }
    }

    /// <summary>
    /// 浣熊骨架的部件表。
    /// 模型：<c>Assets/Models/Characters/111_raccoon_rigged.fbx</c>，24 骨 mixamo 命名。
    /// 层级（实测，bind pose 世界坐标）：
    ///   Hips → Spine → Neck → { Head, Left/RightShoulder → Arm → ForeArm → Hand → 手指 }
    ///   Hips → Left/RightUpLeg → Leg → Foot → ToeBase
    /// 部件划分照教程 P1 步2 的清单（head / torso / stomach / hips / thigh / leg / foot / shoulder / arm / hand）：
    ///   hips=Hips · stomach=Spine · torso=Neck · head=Head · shoulder=Shoulder · arm=Arm · hand=Hand ·
    ///   thigh=UpLeg · leg=Leg · foot=Foot。手指与脚趾不上物理（教程没有对应部件，且它们太小）。
    /// 质量：教程 P7 的质量表是该作者模型的「口味选择」（他自己这么说），**不按比例照抄**——
    ///   这里取 62 kg 总量（模型高 ≈1.8 m，人形尺度），再按教程 P7 的相对关系分配：
    ///   hips 最重、torso 次之、lower arm / hand 最轻（P7：0.1，相对 limb 1 的 1/10）。
    /// </summary>
    public static class RaccoonSkeleton
    {
        public const string ModelPath = "Assets/Models/Characters/111_raccoon_rigged.fbx";
        public const string BonePrefix = "mixamorig:";
        /// <summary>模型朝向（实测：左脚尖世界 z 大于踝 z，故面朝 +Z）。</summary>
        public const float FacingYawOffset = 0f;

        public static List<PartSpec> DefaultParts()
        {
            List<PartSpec> p = new List<PartSpec>(18);

            // 中轴：教程 P1 步5 的 head → torso → stomach → hips
            p.Add(new PartSpec("hips",  "mixamorig:Hips",  null,    PartRole.Pelvis,   12.0f, 0.190f, PartShape.Capsule));
            p.Add(new PartSpec("spine", "mixamorig:Spine", "hips",  PartRole.Rotation, 10.0f, 0.180f, PartShape.Capsule));
            p.Add(new PartSpec("neck",  "mixamorig:Neck",  "spine", PartRole.Rotation,  3.0f, 0.130f, PartShape.Capsule));
            p.Add(new PartSpec("head",  "mixamorig:Head",  "neck",  PartRole.Rotation,  4.0f, 0.200f, PartShape.Sphere));

            AddArm(p, "l", "Left");
            AddArm(p, "r", "Right");
            AddLeg(p, "l", "Left");
            AddLeg(p, "r", "Right");

            return p;
        }

        // 教程 P1 步5：shoulder→torso、arm→对应 shoulder、hand→对应 arm（左右不要连错）
        static void AddArm(List<PartSpec> p, string suffix, string mixamo)
        {
            string shoulder = "shoulder" + suffix;
            string arm = "arm" + suffix;
            string fore = "forearm" + suffix;
            string hand = "hand" + suffix;

            p.Add(new PartSpec(shoulder, "mixamorig:" + mixamo + "Shoulder", "neck",     PartRole.Rotation,  2.2f, 0.106f, PartShape.Capsule));
            p.Add(new PartSpec(arm,      "mixamorig:" + mixamo + "Arm",      shoulder,   PartRole.Rotation,  2.2f, 0.083f, PartShape.Capsule));
            p.Add(new PartSpec(fore,     "mixamorig:" + mixamo + "ForeArm",  arm,        PartRole.Passenger, 1.1f, 0.092f, PartShape.Capsule));
            p.Add(new PartSpec(hand,     "mixamorig:" + mixamo + "Hand",     fore,       PartRole.Passenger, 0.4f, 0.102f, PartShape.Sphere));
        }

        // 教程 P1 步5：thigh→hips、leg→对应 thigh、foot→对应 leg
        static void AddLeg(List<PartSpec> p, string suffix, string mixamo)
        {
            string up = "upleg" + suffix;
            string lo = "leg" + suffix;
            string foot = "foot" + suffix;

            p.Add(new PartSpec(up,   "mixamorig:" + mixamo + "UpLeg", "hips", PartRole.Rotation,  6.0f, 0.100f, PartShape.Capsule));
            // ★ 2026-10-01：小腿胶囊必须截短 —— 沿骨轴 30 cm 的胶囊端球落到 y=-0.0218，
            //   比鞋底（y=0）还低 2.2 cm，是「角色悬空」的第二个来源（第一个是脚胶囊）。
            //   0.2721 = 解出来的圆柱段长度，使端球底面落在 y=+0.006（实测：小腿骨长 0.300，
            //   骨轴 ≈(∓0.058,-0.998,+0.005)，端球半径 0.097）。
            PartSpec shin = new PartSpec(lo, "mixamorig:" + mixamo + "Leg", up, PartRole.Passenger, 3.5f, 0.097f, PartShape.Capsule);
            shin.ColliderLength = 0.2721f;
            p.Add(shin);

            // 脚：★ 盒 + 摆平的挂点（不再用沿骨轴的胶囊）。
            //   挂点欧拉 = Inverse(脚骨 bind-pose 世界旋转)，实测让它与世界轴对齐。
            //   尺寸与中心 = **脚部网格**（主导骨是 Foot/ToeBase 的全部顶点、且世界 y<0.08）的
            //   世界包围盒：0.230 × 0.075 × 0.370 米（鞋长 / 身高 = 24%，与上一轮量到的 23.8% 一致）。
            //   中心 y 取 0.001 + 高/2 ⇒ **盒底面落在 y=+0.001**（不取 0：避免出生瞬间被判穿透）。
            PartSpec f = new PartSpec(foot, "mixamorig:" + mixamo + "Foot", lo, PartRole.Passenger, 1.1f, 0f, PartShape.Box);
            f.MountEuler = (suffix == "l") ? new Vector3(307.324f, 326.834f, 210.689f)
                                           : new Vector3(307.192f,  32.630f, 149.356f);
            f.Size = new Vector3(0.230f, 0.075f, 0.370f);
            f.Center = (suffix == "l") ? new Vector3(-0.0242f, -0.0367f, 0.0666f)
                                       : new Vector3( 0.0195f, -0.0367f, 0.0671f);
            p.Add(f);
        }
    }

    /// <summary>
    /// 教程版布娃娃的全部常数。
    /// 每个字段都注明来源；教程没讲的地方标「未讲」并给出这里用的默认值（教程 §5 自己就是这么标注的）。
    /// </summary>
    [Serializable]
    public sealed class RagdollRecipe
    {
        // ------------------------------------------------------------------
        // P2 自平衡：全部实现就是 ConfigurableJoint 的 Angular Drive 里填 Position Spring
        // （教程 §3.1「没有写任何脚本、没有用 AddForce/AddTorque、没有 PID、没有读角度」）
        // ------------------------------------------------------------------

        [Header("P2 · 关节弹簧（自平衡的唯一来源）")]
        [Tooltip("hips 弹簧刚度相对「整具布娃娃的重力力矩 M·g·h_com」的倍数。\n" +
                 "教程原值 750 是该作者模型的绝对值（§3.5 项3：换模型必然要重调）。\n" +
                 "直立判据（本文档推导）：k_hips > M·g·h_com，否则整体就是个倒立摆。\n" +
                 "★ 2026-10-01 owner 要求「布娃娃更软一些」，8.0 → 5.5。实测扫描（静止 8s + 走 8s + 推 220/400 N·s）：\n" +
                 "  锁死姿态下 margin 只能到 7.0（6.0 时走路 站中位掉到 3.02s）；**解锁非旋转点角运动之后**可以到 5.5。\n" +
                 "  margin 5.5 / 上体 0.20 / 解锁 ⇒ 站 8.00s、走 8.00s，推 220 N·s 后最大倾角 2.9° → **5.5°**（给量 +90%）。\n" +
                 "  再往下 margin 5.0 站立只有 7.16s（塌了）。\n" +
                 "★★ 2026-10-01 再加「托举层」（CarryEnabled）之后：**骨盆弹簧不再承担支撑**，\n" +
                 "  它可以降到 **1.5**（比 1 还低的「倒立摆」区）而仍然站 8.00s / 走 8.00s ——\n" +
                 "  因为撑住身体的是胯部的托举力。实测：0.4 / 0.8 / 1.2 / 1.5 / 2.0 / 3.0 / 5.5 全部 8.00s，\n" +
                 "  越低被推时晃得越狠（推 220 N·s 后最大倾角 2.4° → 6.3°），取 1.5 ≈ 4°。")]
        public float HipsSpringMargin = 1.5f;

        [Tooltip("四肢弹簧 / hips 弹簧。来源：教程 P2 步7 的 180 : 750。")]
        public float LimbSpringRatio = 0.05f;

        [Tooltip("躯干（spine/neck/head）弹簧相对 LimbSpring 的倍率。**教程没有这一条**。\n" +
                 "★ 2026-10-01 新增（owner 要「像面条、像人类一败涂地那样」）。\n" +
                 "  原来 spine/neck/head 和**腿**共用 LimbSpringRatio，于是「软躯干」与「能走路的腿」不可兼得 ——\n" +
                 "  实测把 LimbSpringRatio 压到 0.16~0.20，走路当场崩（站中位 1.80s）。拆出这一档之后两者可以分开。\n" +
                 "  1.0 = 与四肢同硬（= 本次改动之前的行为，向后兼容）。")]
        public float TorsoSpringRatio = 0.40f;

        [Tooltip("上半身（shoulder/arm/forearm/hand）弹簧相对 LimbSpring 的倍率。**教程没有这一条**（它只有一个 180 管子与四肢）。\n" +
                 "加上它的理由：支撑靠腿，软度靠手臂。腿硬才能站住，臂软才有「笨拙」的观感 —— Gang Beasts 就是这个味道。\n" +
                 "★ 2026-10-01 owner 要求「更软」：0.45 → **0.20**（手臂松掉 56%）。\n" +
                 "  ⚠️ 不要拿 `LimbSpringRatio` 去调软 —— 它**同时管着腿和躯干**（spine/neck/head/upleg/leg/foot），\n" +
                 "  实测压到 0.16~0.20 走路当场崩（站中位 1.80s）。**软只能软手臂这一路**。\n" +
                 "★★ 2026-10-01 有了托举层之后不再受限：0.45 → **0.12**。")]
        public float UpperBodySpringRatio = 0.12f;

        [Tooltip("阻尼比 ζ（positionDamper = 2ζ√k·I）。教程全程未提 positionDamper（§3.2 ③ / §5.5），保持默认 0。\n" +
                 "0 = 忠实教程；发抖或爆掉就调到 0.05~0.3。")]
        public float DampingRatio = 0.0f;

        [Tooltip("角驱动力矩上限。教程未提 maximumForce（§5.5），Unity 默认无穷大 → 0 表示沿用默认。")]
        public float MaxDriveForce = 0f;

        [Tooltip("只给 hips 用绝对刚度（>0 时覆盖 HipsSpringMargin）。")]
        public float HipsSpringOverride = 0f;

        [Tooltip("只给四肢用绝对刚度（>0 时覆盖 LimbSpringRatio）。")]
        public float LimbSpringOverride = 0f;

        // ------------------------------------------------------------------
        // 教程 §三 · 撑直（三种做法里本工程实现两种）
        //   来源：ref-ActiveRagdolls-视频教程.md §三（视频 [05:41]–[07:01]）与仓库
        //   `Modules/PhysicsModule.cs`。视频 [06:34] 明确说方法 3 是更好的方向：
        //   「方法 2 里的 joint 力矩输出像一个弹簧，越偏离给得越大；自己写的话，
        //    这条『偏离 → 力矩』的函数可以由我们选，于是可以把弹簧感消掉」。
        // ------------------------------------------------------------------

        [Header("教程 §三 · 撑直（方法2 弹簧 / 方法3 力矩）")]
        [Tooltip("撑直用哪一种做法。来源：视频 §三 + PhysicsModule.BALANCE_MODE。\n" +
                 "StabilizerJoint（方法2）= 本工程 2026-10-01 之前的现状：hips↔Root 的角驱动弹簧。\n" +
                 "UprightTorque（方法3）= 自己施加力矩，函数由我们选（见下面四项）。\n" +
                 "None = 对照组，什么都不做。\n" +
                 "切到 UprightTorque 时 ClumsyRagdoll 会把 hips 的角驱动弹簧置 0（否则两条路在打架）。")]
        public BalanceMode Balance = BalanceMode.StabilizerJoint;

        [Tooltip("方法3 的力矩上限相对「整具身体的重力力矩 M·g·h_com」的倍数。\n" +
                 "换算：τ_max = margin × π × M·g·h_com（推导见 ClumsyBalance.RefreshScales）。\n" +
                 "**与 HipsSpringMargin 同一个口径**，所以两边可以直接对着看：弹簧是 N·m/rad，这里是 N·m 上限。")]
        public float UprightTorqueMargin = 8.0f;
        [Tooltip("力矩加在谁身上：true = 按惯量分发到**所有刚体**（★ 本工程实测必须这样，见 ClumsyBalance.ApplyBodyTorque）；\n" +
                 "false = 照教程原样只加在骨盆一根刚体上（会自激振荡，留作对照）。")]
        public bool UprightTorqueOnWholeBody = true;


        [Tooltip("方法3 的「偏离 → 力矩」函数：shape = (角度/180°)^exponent。\n" +
                 "**教程把手画的 AnimationCurve 放在 Inspector 里，本工程是纯数据配方，所以参数化成两参族。**\n" +
                 "1 = 与弹簧同形（线性）；小于 1 = 近直立就顶满、远处饱和（肌肉的样子）；大于 1 = 近直立很软。")]
        public float UprightCurveExponent = 1.0f;

        [Tooltip("方法3 的阻尼项（对「要倒下去」的那部分角速度施加反向力矩）。\n" +
                 "**教程没有这一项**（他的函数只有形状、没有 D 项）；默认 0 = 忠实教程，\n" +
                 "但「消掉弹簧感」这件事本质上是靠 D 项做到的 —— 诊断台 BalanceReport 会给 0 / 0.5 / 1.0 三档读数。")]
        public float UprightTorqueDampingRatio = 0f;

        [Tooltip("转身力矩上限相对 M·g·h_com 的倍数（同一个 π 换算式）。\n" +
                 "来源：教程同一条 BALANCE_MODE.UPRIGHT_TORQUE 分支里的 rotationTorque = 500（绝对值）。\n" +
                 "**方法2 不需要它** —— 那边是 hips 的 yaw 弹簧顺带做掉的；换到方法3 必须自己补上。")]
        public float TurnTorqueMargin = 1.0f;

        [Tooltip("转身力矩的死区（度）：朝向误差小于它就不给力矩，免得正对面时一直抖着自转。")]
        /// ★ 2026-10-02：转身的 yaw 伺服也用它当死区（小于它就不较劲）。
        public float TurnDeadZoneDegrees = 2.0f;

        [Tooltip("二次角阻尼系数 d（教程 PhysicsModule.ApplyCustomDrag 的 customTorsoAngularDrag = 0.05）。\n" +
                 "扣减量 = |ω|²·d，所以 |ω| 小时几乎不扣、大时扣得狠 —— 压抖不压动作。\n" +
                 "**视频完全没讲这一项**，是从仓库代码里读出来的。0 = 关。")]
        public float CustomTorsoAngularDrag = 0.05f;

        [Tooltip("同一套二次角阻尼要不要也施加到四肢（**教程只对躯干做**）。默认 0 = 关（忠实教程）。")]
        public float CustomLimbAngularDrag = 0f;


        // ------------------------------------------------------------------
        // P1 搭布娃娃
        // ------------------------------------------------------------------

        [Header("P1 · 搭布娃娃")]
        [Tooltip("连线运动锁死。来源：教程 P1 步6「xMotion / yMotion / zMotion 全部设为 Locked」。")]
        public bool LockLinearMotion = true;

        [Tooltip("角运动保持 Free。来源：教程 P1 步6「don't lock the angular motions because we do want our limbs to rotate」。")]
        public bool FreeAngularMotion = true;

        [Tooltip("非旋转点的角运动锁死。来源：教程 P5 步2。\n" +
                 "锁了就变成「硬棍腿」；关掉则膝关节/肘关节仍被弹簧拉向出生姿势（本模型有膝，推荐关掉看效果）。\n" +
                 "★ 2026-10-01 改为 **false**。两条理由（都是实测）：\n" +
                 "  ① 走路「后脚前半截没抬起来、跟地面来回摩擦」—— 锁死时膝盖角跨度只有 2.2°（动画里的屈膝被丢掉 90%），\n" +
                 "     腿是**从髋到脚的硬棍**，摆动腿没法缩短 ⇒ 脚抬不起来；放开后膝角跨度 48.6°、脚抬升 +17%。\n" +
                 "  ② owner 要「更软」—— 放开之后 hips 弹簧倍数才降得下去（锁死时 margin 6.0 走路就崩，放开后 5.5 稳）。\n" +
                 "  站立不受影响：锁死 / 放开都是 8.00s（ClumsyRagdollBench.LockPassengerReport）。")]
        public bool LockPassengerAngular = false;

        [Tooltip("自碰撞层。来源：教程 P1 步7/8。")]
        public string SelfCollisionLayerName = "No Self Collision";

        /// <summary>
        /// ★ 2026-10-01：**手臂链单独一层**（只给上臂/前臂/手用）。
        /// owner 报「倒地/被推/被抓时胳膊折进身体里」——根因是布娃娃自碰撞整个关着。
        /// 但自碰撞开不全（关节附近到处是构造性重叠，见 `PairPenetrationReport` 的审计），
        /// 而**上臂/前臂/手 × 躯干是唯一一组互不重叠的** ⇒ 只给这一组开。
        /// 这一层必须在 Project Settings 里存在（菜单「笨拙布娃娃 / 1. 建立图层 / Tag / 碰撞矩阵」
        /// 会建），运行期加不了图层。
        /// </summary>
        public string LimbCollisionLayerName = "Ragdoll Limbs";

        /// <summary>
        /// ★ 2026-10-02：**把模型材质换成双面**（owner：「右胳膊能看到内壁，随视角变化换地方」）。
        ///
        /// 那是**背面剔除**的典型表现：近侧壁被判成背面剔掉，于是看到远侧壁的内表面。
        /// Unity 内置 Standard 不暴露 `_Cull`，所以模型在跑起来时会换成
        /// `ClumsyRagdoll/DoubleSidedStandard`（等价 surface shader，只多一个 `Cull Off`），
        /// 贴图/颜色/法线/自发光原样搬过去。
        /// 关掉 = 回到内置单面材质。
        /// </summary>
        public bool DoubleSidedModelMaterials = true;    // 绕序已由 FixInvertedWinding 修好之后再开：
        // 手臂那层本来就是**开放面**（没有厚度、不闭合），单面渲染从开口看过去是空的；
        // 两面都画 = 开放面从两边都成型，看上去就闭合了。

        /// <summary>是否让手臂链与身体（躯干/头/腿/地面/道具）碰撞。关掉 = 回到老行为（胳膊可以穿过身体）。</summary>
        public bool LimbCollidesWithBody = true;

                [Tooltip("是否真的关掉同层自碰撞（Layer Collision Matrix）。来源：教程 P1 步8。")]
        public bool DisableSelfCollision = true;

        /// <summary>
        /// ★ 2026-10-01：**起跳输入缓冲**（秒）。
        ///
        /// 为什么需要：起跳的判据是 <c>JumpPressed &amp;&amp; IsGroundedNow</c> —— **单帧**。
        /// 而跑动中脚有一半时间是离地的，玩家正好在那一刻按空格，这一次跳就被**静默丢掉**
        /// （demo 自己那条脚本输入路径早就加过「还在地上就一直按」的重试，键盘这条路径一直没有）。
        /// 缓冲窗口内只要落上地就补上这一跳。
        /// </summary>
        public float JumpBufferSeconds = 0.15f;

        [Tooltip("关节 anchor。来源：教程 P1 步8「调 Anchor 直到落在脖子/肩/胯这种真实旋转点」。\n" +
                 "本工程取 anchor = 0，即「本骨的原点」，对骨骼模型来说就是解剖学关节（肩/肘/髋/膝/踝）本身。")]
        public bool AnchorAtBoneOrigin = true;

        // ------------------------------------------------------------------
        // P7 质量与阻尼终版
        // ------------------------------------------------------------------

        [Header("P7 · 质量与阻尼（表见教程 P7-A）")]
        [Tooltip("四肢默认 drag。来源：教程 P7 表 limb = 0.2。")]
        public float LimbDrag = 0.2f;

        [Tooltip("四肢默认 angularDrag。来源：教程 P7 表 limb = 0.25。")]
        public float LimbAngularDrag = 0.25f;

        [Tooltip("lower arm / hand 的 drag。来源：教程 P7 表 = 0。")]
        public float LightLimbDrag = 0.0f;

        [Tooltip("lower arm / hand 的 angularDrag。来源：教程 P7 表 = 0。")]
        public float LightLimbAngularDrag = 0.0f;

        [Tooltip("lower arm 的 ConfigurableJoint.massScale。来源：教程 P7 步41 = 5（治 torso 抖动）。\n" +
                 "教程作者自述不知道它是什么（§5.4）；官方语义是「缩放本刚体对求解器的逆质量/惯量」，\n" +
                 "效果 ≈ 让求解器认为这条小臂更轻，推给躯干的力更小。0 = 不设（保持 1）。")]
        public float LowerArmMassScale = 5.0f;

        // ------------------------------------------------------------------
        // P3 移动
        // ------------------------------------------------------------------

        [Header("P3 · 移动（力加在 hips 上）")]
        [Tooltip("前进加速度 m/s²。教程给的是绝对值 speed = 150（§5.5：他的模型小、质量 1），\n" +
                 "这里改成加速度域，与质量/尺寸无关。")]
        public float MoveAccel = 4.0f;

        [Tooltip("横移加速度 m/s²。教程 strafeSpeed = 100 比 speed = 150 慢，这里保持同样的 2/3 关系。")]
        public float StrafeAccel = 2.6f;

        [Tooltip("冲刺倍率。来源：教程 P3 步5 = 1.5。")]
        public float SprintScale = 1.5f;

        [Tooltip("起跳速度增量 m/s。教程 jumpForce = 6000（§5.5 未给质量，换算 ≈8 m/s），这里取 5.5。")]
        public float JumpSpeed = 4.0f;

        /// <summary>
        /// ★ 2026-10-01：**跳的向前分量**（m/s）。
        /// owner：「跳的距离不够远」——跳原本只有竖直冲量，水平距离完全靠起跳那一刻的跑速，
        /// 而步态改慢之后（2.18 → 1.37 m/s）跳自然也近了。
        /// 既然口径是«把小浣熊扔出去»，就顺着朝向再给一记水平冲量。
        /// 3.0 m/s + 竖直 5.0 m/s ⇒ 滞空 1.02 s、水平 3.0 m（竖直 1.27 m）。
        /// </summary>
        public float JumpForwardSpeed = 3.0f;

        /// <summary>
        /// ★ 2026-10-01：**滞空**——跳跃期间把重力乘一个系数（<1 = 飘）。
        /// owner：「跳跃的效果还是不好，增加一些滞空时间会不会好一些」。
        /// 0.6 + JumpSpeed 4.0：竖直 1.36 m、滞空 **1.36 s**（原 5.0/1.0 是 1.27 m / 1.02 s）——
        /// 高度不变、滞空 +33%。只作用在「起跳后 JumpHangSeconds 秒内且还没落地」的那段，
        /// 以髖上的一个反重力力实现（与托举层同一手法）。
        /// </summary>
        public float JumpGravityScale = 0.6f;

        [Tooltip("滞空窗口（秒）：起跳后这么久之内才减重力，过了就恢复正常重力（免得一直飘）。")]
        public float JumpHangSeconds = 2.5f;

        [Tooltip("前进速度上限 m/s。教程没有这一条：它的 speed = 150 是常量，小模型上靠摩擦自然限速；62 kg 的浣熊按住 W 会一路加速到 30 m/s 并穿地（诊断台实测 8s 走了 38 m、骨盆 y = −5.1）。这里保留同一个力模型，只把力按离目标速度还差多远缩放。")]
        public float MaxSpeed = 6.0f;

        [Tooltip("横移速度上限 m/s。与 MoveAccel : StrafeAccel 同一个 2/3 关系。")]
        public float MaxStrafeSpeed = 4.0f;

        [Tooltip("移动力的参考质量：Force = 加速度 × 该质量 × 总质量。1 = 用整具布娃娃的总质量。")]
        public float MoveForceMassScale = 1.0f;

        // ------------------------------------------------------------------
        // ★ 2026-10-01 · 托举层（Carry）
        //   owner 口径：「在受到控制的时候角色**不是靠双脚支撑实现站立**的，
        //   在胯部受一个足够强的力，其余的部分依靠连接传动」—— 就是人类一败涂地那一套：
        //   骨盆被一个外力托着（高度 + 阻尼），四肢与躯干只是**挂在上面**的面条。
        //   为什么必须先把这一层做出来：只要身体还得靠腿撑着，腿就软不下去
        //   （实测 LimbSpringRatio 压到 0.16~0.20 走路当场崩，站中位 1.80s）。
        // ------------------------------------------------------------------

        [Header("★ 托举层（Carry）· 胯部受力托住身体，四肢当面条")]
        [Tooltip("开 = 骨盆被竖直方向的力托住（角色不靠脚站）；关 = 教程原样（靠腿站）。")]
        public bool CarryEnabled = true;

        [Tooltip("托举高度 = 出生骨盆高度 × 这个倍率。1 = 托在出生高度；小于 1 = 被压低一点（更塌）。")]
        public float CarryHeightScale = 1.0f;

        [Tooltip("竖直位置弹簧：加速度 = 高度误差 × 该值（1/s²）。ω = √k ⇒ 60 ≈ 7.7 rad/s。")]
        public float CarryStiffness = 150f;

        [Tooltip("竖直阻尼：加速度 = −竖直速度 × 该值。ζ=1 时约为 2ω ⇒ 60 的刚度配 ≈15。")]
        public float CarryDamping = 30f;

        [Tooltip("抵消多少比例的重力（1 = 完全抵消，骨盆不再下坠；小于 1 = 留一点体重压在腿上）。")]
        [Range(0f, 2f)] public float CarryAntiGravity = 1.0f;

        [Tooltip("水平阻尼：加速度 = −水平速度 × 该值。压住骨盆被乱带，但别太高（会变滑）。")]
        public float CarryHorizontalDamping = 0.5f;

        /// <summary>
        /// ★ 2026-10-01：**站着不动的水平刹车**（1/s）。
        /// 托举层的水平阻尼只有 0.5（时间常数 2 s），实测站着发呆会以 **0.15 m/s** 匀速漂走
        /// （8 s 漂 1.2 m）—— 因为脚上的法向力被托举层接管、摩擦力按不住那么小的残余速度。
        /// 走路时走的是步态那条目标速度弹簧，这条只在«没有前进输入»时生效。
        /// </summary>
        public float IdleHorizontalDamping = 6f;

        [Tooltip("托举加速度上限（m/s²），防止起步瞬间把人弹飞。")]
        public float CarryMaxAccel = 40f;

        [Tooltip("★ 找地（owner：「正常移动的时候至少有一只脚要跟地面相连」）。\n" +
                 "  开 = 每一帧量最低那只脚离地多少，把托举目标高度**压低同样的量** ⇒ 脚一离开就被放回地面。\n" +
                 "  关 = 托举只认固定目标高度，脚可能悬空（实测 k=30 时脚悬空 114 mm）。")]
        public bool CarryGroundSeek = true;

        [Tooltip("找地的增益：目标高度压低 = 脚离地量 × 该值（>1 = 回得快、略有超压）。")]
        public float CarryGroundSeekGain = 4.0f;

        [Tooltip("找地的预压量（米）：在「脚刚好贴地」之上再压低这么多，保证**至少一只脚真的压着地面**，\n" +
                 "不会因为采样/求解滞后而浮在几毫米处。")]
        public float CarryGroundPress = 0.005f;

        /// <summary>
        /// ★ 2026-10-02：**「找地」只看支撑脚**（默认开）。
        ///
        /// 原来取的是**两只脚**的最低点，于是摆动脚一沉，「找地」就把**整个人往上顶**
        /// （`targetY -= (负值)×增益`）—— 实测摆动脚最低那一帧，支撑脚被抬到 +0.8 cm；
        /// 支撑脚平均离地 3.4 cm、40% 的帧在 2 cm 以上。只看支撑脚之后这两条都减半。
        /// </summary>
        public bool CarryGroundSeekSupportOnly = true;

        /// <summary>
        /// ★ 2026-10-02：**骨盆高度改用几何解**（而不是那条经验性的「找地」）：
        /// `骨盆高 = 踝的出生高度 + √(髋到踝长² − 支撑脚水平偏移²)`。
        ///
        /// 为什么：owner 说「前进逻辑仍然残留着向前拉动的力，而不是单纯的靠双脚」——
        /// 读数上的样子是**相位说某只脚是支撑脚，可它根本不在下面**：
        /// 实测声明的支撑窗里支撑脚真踩在地上只有 **52%**、**40% 的帧两只脚都在 1 cm 以上**。
        /// 根因是支撑腿摆到前后两端时，腿在几何上**够不着地**（±44° 的髋角把脚抬了 16 cm），
        /// 而原来那条找地律（脚离地量 × 4）压不下骨盆。几何解天然就把骨盆在两端压低 ~5 cm。
        /// </summary>
        public bool CarryPelvisFromStanceLeg = true;

        /// <summary>★ 2026-10-02：几何解的骨盆**最多比出生高度低多少**（m）。
        /// 不夹的话实测出现过 0.201 m（整个人坐到地上）—— 支撑脚被甩到很远时 √ 那一下会把目标拉穿。</summary>
        public float CarryPelvisMaxDip = 0.12f;

        [Tooltip("★ 跳 = 把角色扔出去（owner 口径）。\n" +
                 "  开 = **托举只在接地时生效**：起跳后离开地面 ⇒ 托举与头气球全部撤掉，空中是一具自由的\n" +
                 "       布娃娃（被扔出去）；重新落地 ⇒ 托举接住。\n" +
                 "  关 = 托举一直生效，那样**跳不起来**（对称弹簧会把上升速度按回目标高度）。\n" +
                 "  ⚠️ 2026-10-01 曾经用「起跳后 Time.time < t0+1.2s 都撤着」—— **错的**：\n" +
                 "     诊断台里 Time.time 不推进（永远撤着），Play 里也依赖时序很脆；实测落地后骨盆\n" +
                 "     从 0.62 掉到 0.155 就一直躺着起不来。改成按接地判定之后正常。")]
        public bool CarrySuspendOnJump = true;

        [Tooltip("起跳瞬间额外宽限几个物理步（防止刚起跳那一帧还被判成接地、又把上升速度按回去）。")]
        public int CarrySuspendSteps = 3;

        // ------------------------------------------------------------------
        // ★ 2026-10-01 · 头的托举（「气球」）
        //   owner 口径：「给头也加一个额外漂浮的力，就像在胯上栓一个气球一样通过脊椎骨骼相连」。
        //   骨架的线性运动是 Locked 的 ⇒ 作用在**头**上的力会沿脊椎链传下去，把上身一起吊住，
        //   等价于「气球拴在胯上、绳子是脊椎」。它同时解决实测出来的那道悬崖：
        //   躯干弹簧 0.40→0.30 之间头会整个折下去（头高 1.070 → 0.476）。
        // ------------------------------------------------------------------

        [Header("★ 托举层 · 头（气球）")]
        [Tooltip("开 = 头另受一个漂浮力（需要 CarryEnabled 也开）。")]
        public bool HeadCarryEnabled = true;

        [Tooltip("目标头高 = 出生头高 × 该倍率（只在 HeadCarryRelative = false 时用）。")]
        public float HeadCarryHeightScale = 1.0f;

        [Tooltip("★ 目标高度**相对骨盆**算（推荐开）。\n" +
                 "  开 = 目标头高 = 骨盆当前高度 + 出生时「头比骨盆高多少」× 倍率 ⇒ 头浮在骨盆**上方固定距离**，\n" +
                 "       骨盆不会被气球一起拽上去，链条也不会被压扁。\n" +
                 "  关 = 用世界绝对高度（HeadCarryHeightScale × 出生头高）—— 实测那样会把骨盆从 0.660 拽到 0.816、\n" +
                 "       头反而只有 0.906，整个人被压扁。")]
        public bool HeadCarryRelative = true;

        [Tooltip("竖直弹簧（1/s²）。小 = 飘、慢半拍；大 = 头被顶成一根棍。")]
        public float HeadCarryStiffness = 30f;

        [Tooltip("竖直阻尼。")]
        public float HeadCarryDamping = 6f;

        [Tooltip("抵消多少比例的「头的重力」（范围放到 3，>1 = 顺带把上身也吊起来）。")]
        [Range(0f, 3f)] public float HeadCarryAntiGravity = 1.0f;

        [Tooltip("升力要不要**沿脊椎链分布**（head / neck / spine 各受一份，按各自质量）。\n" +
                 "★ 必须开：只拉头的话脊椎是软面条，气球会把链条**折叠**而不是拉直 ——\n" +
                 "  实测只拉头：骨盆被拽到 0.724、头只有 0.83（出生头高 1.049，整个上身被压扁）；\n" +
                 "  分布之后整条链一起被托住，才像「气球拴在胯上、绳子是脊椎」。")]
        public bool HeadCarrySpreadOnSpine = true;

        [Tooltip("水平阻尼：压住头被带着乱甩。")]
        public float HeadCarryHorizontalDamping = 0.3f;

        [Tooltip("托举加速度上限（m/s²）。")]
        public float HeadCarryMaxAccel = 40f;

        [Tooltip("脚底物理材质：动摩擦。来源：教程 P3 步2 = 0.2。")]
        public float FootDynamicFriction = 0.2f;

        [Tooltip("脚底物理材质：静摩擦。来源：教程 P3 步2 = 0.2（字幕只说「也一样」）。")]
        public float FootStaticFriction = 0.2f;

        [Tooltip("脚底物理材质：弹性。来源：教程 P3 步2 = 1（§5.2 项3 标注可疑，靠 Bounce Combine = Minimum 抵消）。")]
        public float FootBounciness = 1.0f;

        // ------------------------------------------------------------------
        // ★ 2026-10-01 · 脚种植式步态（owner 口径）
        // ------------------------------------------------------------------
        //
        // owner 原话：「现在的前进更像是有人拽着浣熊模型而不是双脚移动…先尝试改成一只脚往前迈，
        // 粘在地上，这条腿拉着身体向前移动，在这个过程进行到一半的时候另一只脚和地面分开，
        // 这条腿拉着脚和它自己回正，然后向前迈，重复这个过程来实现向前移动。」
        //
        // 旧那套的问题（实测读数见 GaitSlipReport）：正弦摆腿 + 髖上「加速度力」推着走 +
        // 脚摩擦 0.2（而且 frictionCombine = Minimum，与地面取小 ⇒ 实际 0.2）⇒
        // **两只脚 99–100% 的时间都沾在地上、每步横滑 20–40 cm** = 擦着地拖走。
        //
        // 新那套（四个量）：
        //   ① 相位：一个周期两个单脚支撑窗（左脚 [0,0.5)、右脚 [0.5,1)），
        //      摆动窗各占中间那半个周期（右 [0.25,0.75)、左 [0.75,1.25)）；
        //   ② **粘住**：种植脚的碰撞体换成高摩擦，再给它一个水平位置弹簧（锁在着地那一点）；
        //   ③ **拉身体**：髖被一个弹簧拉向「种植脚前方 GaitStepLead」——这才是前进的力，
        //      不再是髖上的加速度（那条现在只用于腾空/后退）；
        //   ④ **摆动腿**：髖 −A（后）→ +A（前）、膝 0 → 弯 → 0（抬起来往前迈）。

        /// <summary>是否用「脚种植」步态。关掉 = 回到旧的「正弦摆腿 + 髖加速度力」。</summary>
        public bool UseFootGait = true;

        [Tooltip("种植脚着地时脚底材质的目标摩擦（动态）。会比默认的 0.2 高很多 —— 让它真的粘住。")]
        public float FootGripFriction = 0.9f;

        [Tooltip("种植脚的水平位置弹簧（1/s²）。把脚锁在着地那一点上。")]
        public float FootGripStiffness = 1300f;

        [Tooltip("种植脚水平阻尼（1/s）。压住脚在弹簧上的抖动。")]
        public float FootGripDamping = 65f;

        [Tooltip("种植脚弹簧的力上限（m/s²，乘自身质量）。超过就说明它真被拖了 —— 让锚点跟着走，不要抵死。")]
        public float FootGripMaxAccel = 520f;

        [Tooltip("锚点跟随：脚被拖出去超过这个距离（m）就把锚点拉过来，避免弹簧越勒越紧。")]
        public float FootGripAnchorSlip = 0.05f;

        /// <summary>
        /// ★ 2026-10-01：**步长**（m）—— 一个半周期身体要走的距离，也就是两次着地点之间的距离。
        ///
        /// 相位是**按走过的距离**推进的（`相位 += v·dt / 步长`），不是按固定频率：
        /// 这样「脚落地的位置」和「身体走了多远」永远对得上，不会出现脚在地上擦着找位置。
        /// 几何自洽值：腿长 ≈ 0.50 m，髖摆 ±26° ⇒ 步幅 ≈ 0.44 m ⇒ 步长（单脚）≈ 0.34 m。
        /// </summary>
        public float GaitStepLength = 0.49f;
        // ⚠️ 语义：这个数是**一步**的距离；相位一个周期 = 两步 ⇒ 相位的分母是 2×它。

        [Tooltip("拉髖的弹簧（1/s）。对着目标速度拉 —— 稳态误差 = v/σ，所以不再是个位置目标。")]
        public float GaitPullStiffness = 8f;

        /// <summary>
        /// ★ 2026-10-02：**相位按「指令速度」推进**（而不是按实测速度）。
        ///
        /// owner 的口径是「一只脚往前迈、粘在地上，这条腿拉着身体向前移动」——
        /// 那要求**步态周期是腿自己给的**（像肌肉给的节奏），身体是被支撑腿**拉**过去的。
        /// 按实测速度推进的话是个自限环：身体没动 → 相位不走 → 腿不扫 → 没有力矩 → 身体更不动。
        /// </summary>
        public bool GaitPhaseByCommand = false;

        [Tooltip("拉髖的加速度上限（m/s²）。")]
        public float GaitMaxAccel = 25f;

        /// <summary>
        /// ★ 2026-10-01：**步态目标速度**（m/s）—— 种植腿把身体拉到这个速度。
        /// 为什么不直接用 MaxSpeed：那是旧力模型的«速度上限»（6.0），旧那套力弱、实测只跑到 2.2 m/s，
        /// 而速度目标弹簧是**真的会跑到 6 m/s** 的（实测 5.21 m/s，脚被甩到半空、骨盆在 0.24–0.63 之间弹）。
        /// 1.8 m/s ≈ 旧那套实测的 2.18 m/s 稍慢一点，是«快步走»。相位频率 = v/步长 = 1.8/0.34 ≈ 5.3 半周期/秒。
        /// </summary>
        public float GaitSpeed = 2.0f;

        /// <summary>
        /// ★ 2026-10-01：**支撑腿的角驱动刚度倍数**（相对 LimbSpringRatio 那一档）。
        /// 面条档（四肢弹簧 31 N·m/rad）下髋/膝的目标角拉不动腿：实测左脚一直挂在髖后面 35 cm、
        /// 以身体速度在地上滑。支撑相加硬腿才会真的扫过，脚才可能粘住；摆动腿仍用原档。
        /// 8 是扫出来的（4 → 脚还跟不上；16 → 支撑腿太硬、站着时膝像锁死，落地会弹）。
        /// </summary>
        public float GaitSupportStiffnessScale = 8f;

        /// <summary>★ 2026-10-02：**支撑腿«髋»单独的刚度倍数**（0 = 与上面那档相同）。
        /// 前进力要靠支撑髋的角力矩去推地面，所以髋要硬；但小腿/脚留在面条档，观感才不变。</summary>
        public float GaitSupportHipStiffnessScale = 0f;

        /// <summary>★ 2026-10-01：**摆动腿**的刚度倍数。摆动腿是«迈出去»那条 —— 它要是面条，
        /// 抬腿/前迈的目标角全被拖没（实测看不出腿在动）。比支撑腿软一点，但远高于面条档。</summary>
        public float GaitSwingStiffnessScale = 6f;

        [Tooltip("**摆动脚**的目标离地高度（m）——离地弧线的峰值（中段最高、两头贴地）。")]
        public float GaitSwingClearance = 0.07f;

        /// <summary>
        /// ★ 2026-10-02：**脚底离地伺服的刚度**（1/s²，加在脚自己身上的一条竖直加速度 PD）。
        ///
        /// 为什么要有这个伺服：整套步态只写**角度**，鞋底的高度是角度的**结果** ——
        /// 实测摆动脚在整个摆动期的离地量是 15.6 → 0.5 → 24.0 cm（**中间最低、两头最高**），
        /// 与「中段抬起来、两头贴着地」正好相反；走路 6 s 里 **39% 的帧有脚穿进地面**、
        /// 26% 的帧两只脚都在 2 cm 以上。角度调不出这条弧线（隔离实验：髋/膝任意组合下
        /// 脚的竖直位置都不单调），所以改成直接对脚做竖直伺服。
        /// 实测：1 cm 误差 → 4 m/s²（400）。
        /// </summary>
        public float FootHeightGain = 1500f;

        /// <summary>★ 2026-10-02：脚底伺服的竖直速度阻尼（1/s）。临界阻尼 ≈ 2·√(刚度) = 40。</summary>
        public float FootHeightDamping = 60f;

        /// <summary>★ 2026-10-02：脚底伺服的加速度上限（m/s²）。40 ≈ 4 g，够把脚从地里拔出来，又不至于「磁铁脚」。</summary>
        /// <summary>★ 2026-10-02：脚底伺服的加速度上限（m/s²）。40 ≈ 4 g，够把脚从地里拔出来，又不至于「磁铁脚」。</summary>
        public float FootHeightMaxAccel = 150f;

        /// <summary>
        /// ★ 2026-10-02：把脚底伺服的力**分摊到整条腿**（脚 + 小腿 + 大腿），而不是只推脚。
        /// 关掉 = 只推脚 —— 实测那样几乎没用：鞋盒 37 cm 长、绕踝关节转，
        /// 推脚只是让它**绕踝打转**（脚尖抬、脚跟在原地），`bounds.min.y` 不变。
        /// </summary>
        public bool FootHeightOnWholeLeg = true;

        /// <summary>
        /// ★ 2026-10-01：**把脚摆平**（治「用脚尖走路」）。
        /// 实测：姿势驱动**从来没写过 `Pose("foot…")`** —— 脚骨接管的完全是小腿的旋转，
        /// 于是小腿一斜，鞋尖就朝下，看上去是用脚尖在走。
        /// 开：踝角 = −(大腿角 + 膝角)（三者同一个«绕角色右轴»的符号系），支撑相鞋底水平。
        /// </summary>
        public bool FootLevelAnkle = true;

        /// <summary>★ 2026-10-01：踝摆平的反馈增益（每帧把鞋底倾角按这个系数加进修正量；
        /// 1.0 ≈ 几帧收敛）。解析式补偿实测不准（髋 25° 鞋底才斜 5°），只有反馈压得住。</summary>
        public float AnkleLevelGain = 2.5f;

        /// <summary>摆动腿的踝摆平比例（1 = 与支撑腿一样平；小一点让抬起来那只脚稍微垂着）。</summary>
        public float SwingAnkleScale = 1.0f;

        /// <summary>
        /// ★ 2026-10-01：**摆动腿前伸的比例节点**（0..1）与**过冲量**。
        /// 脚在 u=SwingReachAt 处伸到最远（1+SwingOvershoot 倍步长），然后回带到 1.0 落地 ——
        /// 回带那一段相对髖是往后走的，于是落地瞬间脚在世界里接近静止（= 不打滑）。
        /// 实测：正弦剖面落地时脚仍以身体速度前冲，每步滑 10 cm；加回带后掉到 1 cm 量级。
        /// </summary>
        public float SwingReachAt = 0.68f;

        [Tooltip("摆动腿过冲量：>0 时脚先伸到 (1+它)×步长，再回带到落点。")]
        public float SwingOvershoot = 0.35f;

        /// <summary>
        /// ★ 2026-10-01：**摆动期屈膝的峰值位置**（u，0..1）。
        /// 0.5 = 正弦（中段最弯）；但真走路是**离地后很快屈膝把脚提起来**（不然刚离地就刮地），
        /// 然后再伸腿去落地。实测 u=0.5 时摆动脚离地只有 1.2 cm（69% 的摆动帧离地不到 2 cm、16% 直接穿地）。
        /// </summary>
        public float SwingKneePeakAt = 0.5f;

        /// <summary>
        /// ★ 2026-10-01：**摆动腿外摆**（度，摆动相中段最大）——治「两只脚打架」。
        /// 实测：直走时两脚的盒碰撞体会相交（250 帧里 3 帧、最深 1.2 cm），
        /// 而 W+A / W+D 时身体往侧面漂、两脚错开，就基本不发生（250 帧 0 帧）—— 正是 owner 看到的现象。
        /// </summary>
        public float GaitSwingAbductDegrees = 5f;

        /// <summary>外摆的符号（±1）。左腿用 −sign、右腿用 +sign（与转身踏步同一套镜像约定）。</summary>
        public float GaitAbductSign = 1f;

        /// <summary>★ 2026-10-01：**走路时的基础外摆**（度，两条腿同时往外）——把两只脚的轨迹拉开两条平行线，
        /// 配合摆动腿外摆一起治「两只脚打架」。按走路驱动量缩放，站着不动不生效。</summary>
        public float GaitStanceAbductDegrees = 3f;

        // ---- ★ 2026-10-01 空中姿势（跳起来那一段）----

        /// <summary>空中：髋往前收多少度（负号方向 = 收腿）。</summary>
        public float AirHipDegrees = 18f;

        /// <summary>空中：屈膝多少度。</summary>
        public float AirKneeDegrees = 34f;

        /// <summary>空中：手臂抬起多少度（越大越像«张开»）。</summary>
        public float AirArmDegrees = 26f;

        [Tooltip("支撑腿的膝弯比例（相对 KneeBendDegrees）。人走路支撑腿几乎直。")]
        public float GaitSupportKneeScale = 0.25f;

        // ------------------------------------------------------------------
        // P4 / P5 姿势驱动（教程用 Animator + CopyMotion；本模型没有动画片段，改成程序化）
        // ------------------------------------------------------------------

        [Header("P4/P5 · 姿势驱动（本模型无动画片段 → 程序化 targetRotation）")]
        [Tooltip("总开关：程序化步态/待机是否写 targetRotation。关掉 = 纯弹簧回出生姿势（教程 P1+P2 的状态）。")]
        public bool UsePoseDriver = true;

        [Tooltip("步频 Hz。教程 P5 把 walk 动画调慢以避免乱甩，这里直接给频率。")]
        public float StrideFrequency = 1.6f;

        [Tooltip("髋摆动幅度（度）。")]
        public float HipSwingDegrees = 44f;

        [Tooltip("膝弯曲幅度（度）。教程 P5 把下腿角运动锁死，本模型有关节，改成「让步态驱动它」。")]
        public float KneeBendDegrees = 46f;

        [Tooltip("臂摆动幅度（度），与同侧腿反相。")]
        public float ArmSwingDegrees = 20f;

        [Tooltip("躯干前倾（度）。")]
        public float SpineLeanDegrees = 6f;

        [Tooltip("姿势驱动的角速度上限（度/秒），防止 targetRotation 突跳。")]
        public float PoseSlewDegreesPerSecond = 420f;

        /// <summary>
        /// ★ 2026-10-01：**脚种植步态时腿的角速度上限**（度/秒）。
        /// 为什么单独一档：420°/s 下腿根本扫不过去 —— 实测左髋的 targetRotation 只有 ±13°（目标 ±26°），
        /// 于是脚永远吊在髖后面 35 cm、以身体速度在地上滑。腿要真的迈出去，角速度必须放宽；
        /// 手臂/躯干继续用 420（笨拙感留在上半身）。
        /// </summary>
        public float GaitPoseSlewDegreesPerSecond = 1100f;

        [Header("P4/P5 · 手臂休息姿势（**教程没有这一条**）")]
        [Tooltip("把手臂从 T-pose 放下来的总角度（度）。90 = 完全竖直。教程从未教过静态手臂姿势，见交接 §八。")]
        public float ArmDownDegrees = 72f;

        [Tooltip("下放角度里由肩骨承担的比例，剩下的归上臂（想让肩少动、臂多动就调小）。")]
        public float ShoulderDownShare = 0.25f;

        [Tooltip("肘部弯曲（度）。注意：LockPassengerAngular 开着的时候小臂是锁死的，这个值不起作用。")]
        public float ForeArmBendDegrees = 20f;

        [Header("P4/P5 · 后退步态")]
        [Tooltip("后退时的步幅倍率（人倒退走是小碎步）。")]
        /// ★ 2026-10-02：**后退的比例**。它同时管三件事，必须一起缩才不会蹭脚：
        /// ① 姿势驱动里的髋摆幅（`stride`）；② `StepFootGait` 的指令速度 `targetV`；
        /// ③ 相位推进用的步长（`2 × GaitStepLength × 它`）。
        /// 三处的比例不一致时，腿扫过的距离和身体要求的距离对不上 ⇒ 种植脚在地上滑。
        public float BackwardStrideScale = 0.6f;

        /// <summary>
        /// ★ 2026-10-02：**后退时摆动脚的离地目标要单独放大**。
        ///
        /// 为什么不能共用 `GaitSwingClearance`：脚底伺服是**比例控制**，腿的关节驱动一直在往下拽它，
        /// 于是有约 **4.5 cm 的稳态误差**。前进时目标 7 cm 实测中段 5–6.6 cm 刚好；
        /// 后退时髋摆幅只有 0.6 倍、腿更直，同样的 7 cm 只兑现 **2.5 cm** ⇒ 摆动脚整段在地上拖
        /// （实测 u 分档全是 0.5–2.8 cm，刮蹭帧 55%）。
        /// 扫描：目标 7 / 15 / 25 cm ⇒ 中段实测 2.5 / 5.5 / 15 cm，刮蹭 55% / 22% / 10%。
        /// 取 2.0（后退目标 14 cm，中段落在 5–8 cm，与前进同量级）。
        /// </summary>
        public float BackwardSwingClearanceScale = 2.0f;

        [Tooltip("后退时的躯干俯仰（度，负 = 后仰）。教程没有这条；不加的话后退看着像被人从后面拖。")]
        public float BackwardLeanDegrees = -5f;

        [Header("P4/P5 · 原地转身踏步")]
        [Tooltip("转身时腿要不要踏步（不加就是整个人整体平移旋转，脚不动）。")]
        public bool TurnStepping = true;

        [Tooltip("转身角速度参考值（度/秒），到这个速度算「满转」。")]
        public float TurnReferenceRate = 140f;

        [Tooltip("转身踏步频率 Hz。")]
        public float TurnStepRate = 2.2f;

        [Tooltip("转身时腿外摆幅度（度）。")]
        public float TurnAbductDegrees = 16f;

        [Tooltip("转身时抬腿幅度（度）。")]
        public float TurnLiftDegrees = 12f;

        // ------------------------------------------------------------------
        // P6 相机与朝向
        // ------------------------------------------------------------------

        [Header("P6 · 三人称相机")]
        [Tooltip("鼠标灵敏度。来源：教程 P6 步9 = 7。")]
        public float RotationSpeed = 7.0f;

        [Tooltip("上下看限幅。教程只说「clamping it between these numbers」，未给数值（§5.2 项11）。")]
        public float PitchMin = -55f;
        public float PitchMax = 70f;

        [Tooltip("stomach 的俯仰偏移。教程说「以后再调」、全集没给最终值（§5.2 项10）。")]
        public float StomachOffset = 0f;

        [Tooltip("相机距离（教程是手动摆的，无数值）。滚轮乘在它上面：0.07×（≈0.25m）~ 4×（≈14.4m）。")]
        public float CameraDistance = 3.6f;

        [Tooltip("相机注视点（Target）在骨盆上方多高 —— 它同时是中键绕轨的球心。\n" +
                 "教程用的是人头身高尺度的值，初版照搬 1.35。**2026-10-01 实测后改成 0.45**：\n" +
                 "这只浣熊站姿骨盆 0.61 / 脊柱 0.72 / 头 1.05（全高 ≈1.2m），1.35 会把注视点放到\n" +
                 "头顶上方 0.77m —— 画面里角色偏低，而且**近距离（<1.5m）时角色整个掉出画面**（正是要看细节的用法）。\n" +
                 "0.45 ≈ 头高，角色居中，贴到 0.25m 也还在画面里。")]
        public float CameraHeight = 0.45f;
        public float CameraLookHeight = 0.95f;

        // ------------------------------------------------------------------
        // ★ 2026-10-02 · 相机导航速度（owner：「手动移动摄像头的速度太慢了」）
        //
        // ⚠️ 这些原来是 `ClumsyRagdollCamera` 组件上的 public 字段，而那个组件是**运行时挂**的
        // ⇒ **场景里根本没序列化、Inspector 里改不到**，只能改代码重编译。
        // 搬进 Recipe 之后就能和别的常数一样在 Inspector 里直接调。
        // ------------------------------------------------------------------

        /// <summary>中键绕轨灵敏度（度 / 鼠标单位）。原值 3.5 —— owner 反馈太慢。</summary>
        public float CameraOrbitSpeed = 8.0f;

        /// <summary>Shift-中键平移速度（米 / 鼠标单位，再乘相机到枢轴的距离）。原值 0.010。</summary>
        public float CameraPanSpeed = 0.025f;

        /// <summary>滚轮缩放速度（指数）。原值 2.2。</summary>
        public float CameraWheelZoomSpeed = 3.2f;

        /// <summary>Ctrl-中键缩放速度（指数）。原值 0.010。</summary>
        public float CameraDragZoomSpeed = 0.022f;

        /// <summary>Q / E 手动绕角色转身速度（度 / 秒）。原来是写死的 60。</summary>
        public float CameraKeyTurnSpeed = 150f;

        // ------------------------------------------------------------------
        // ★ 2026-10-02 · **转身（左转 / 右转）**
        //
        // 之前 A/D 是横移、鼠标只是把 `Root` 的朝向写了 —— **布娃娃本身没有任何 yaw 力矩**，
        // 所以「原地转身」其实是两只脚在地上搓。这一套把它做成与走路同一口径的**脚种植转身**：
        //   ① A/D 给出转身指令；② 髖上一个 yaw 伺服把身体真的转过去；
        //   ③ 相位按**转过的角度**推进（走路是按走过的距离）⇒ 转过 `TurnStepDegrees` 换一次脚；
        //   ④ 支撑脚抓地当支点（身体绕着它转），摆动脚抬起来跟着转、落在新角度上。
        // ------------------------------------------------------------------

        /// <summary>A/D 用来转身（关掉 = 回到老的横移）。</summary>
        public bool TurnWithAD = true;

        /// <summary>键盘转身速度（度 / 秒）。</summary>
        public float TurnKeySpeed = 130f;

        /// <summary>身体 yaw 伺服的刚度（N·m / 度）。身体没跟上 `Root` 朝向时靠它转过去。</summary>
        public float TurnYawStiffness = 2.5f;

        /// <summary>yaw 伺服的角速度阻尼（N·m / (度/秒)）。</summary>
        public float TurnYawDamping = 0.25f;

        /// <summary>yaw 伺服的力矩上限（N·m）。</summary>
        public float TurnMaxTorque = 120f;

        /// <summary>每转多少度算«一步»（决定换脚节奏）。</summary>
        public float TurnStepDegrees = 90f;

        /// <summary>转身时摆动脚的离地目标比例（与后退同一个道理：伺服的稳态误差要靠目标补）。</summary>
        public float TurnSwingClearanceScale = 2.0f;

        /// <summary>原地转身时腿的**前后摆幅**比例 —— 转身不需要迈步，只要抬脚，所以压得很小。</summary>
        public float TurnGaitStrideScale = 0.15f;

        /// <summary>hips 是否接到 Root 刚体上。来源：教程 P6 步3（P1–P5 是 None，P6 起是 Root）。</summary>
        [Header("P6 · hips 的连接（教程 §5.3 项2 明确的口径变更）")]
        public bool ConnectHipsToRoot = true;

        // ------------------------------------------------------------------
        // P7 抓取
        // ------------------------------------------------------------------

        [Header("P7 · 抓取")]
        [Tooltip("FixedJoint.breakForce。教程只说「set this to like a high amount」（§5.2 项13，未给数值）。")]
        public float GrabBreakForce = 120000f;

        [Tooltip("手部触发球的半径倍数（教程说「可以把 radius 调大，更好抓」）。")]
        public float GrabTriggerScale = 3.2f;

        [Tooltip("被抓住的物体要打的 Tag。来源：教程 P7 步46。")]
        public string GrabbableTag = "item";

        // ------------------------------------------------------------------
        // 手部与环境交互
        //   来源：ref-ActiveRagdolls-视频教程.md §五（Sergio 的 GripModule / Gripper / Grippable
        //   与 UpdateArmsIK）+ ref-Unity动画系列-全集.md EP12/EP13（Two Bone IK Constraint、
        //   Multi Rotation Constraint、Environment Interaction State Machine 五态）。
        // ------------------------------------------------------------------

        [Header("手部 · 肘关节解锁（**教程没有这一条**，是手部 IK 的前提）")]
        [Tooltip("把「小臂 + 手」的角运动从 Locked 改成 Free，其他非旋转点（小腿/脚）照教程锁死。\n" +
                 "为什么需要：LockPassengerAngular 开着时肘关节是锁死的，写 targetRotation 不生效，\n" +
                 "两骨 IK 根本无从下手（等于一条不能折的棍）。\n" +
                 "为什么只放手臂：诊断台实测（2026-10-01）——\n" +
                 "  全锁（现状）     站 8.00s\n" +
                 "  只放小臂+手      站 8.00s   ← 与全锁完全一致，白拿一个肘\n" +
                 "  放全部非旋转点   站 0.40s   ← 膝盖一塌腿就折了\n" +
                 "调用 ClumsyRagdollBench.LockPassengerDetailReport() 可复现。")]
        public bool FreeElbowAngular = true;

        [Header("手部 · 两骨 IK（= EP13 的 Two Bone IK Constraint，自己算）")]
        [Tooltip("总开关。关掉 = 手臂完全由步态姿势驱动（改之前的行为）。")]
        public bool UseArmIK = true;

        [Tooltip("IK 权重的渐变速度（1/秒）。对应 Sergio 的 IKTransitionsSpeed = 10（那边是 Lerp 系数）。\n" +
                 "权重 0 = 步态姿势，1 = 完全按 IK 走。")]
        public float ArmIKWeightSpeed = 3.5f;

        [Tooltip("目标点距离的上限，占骨长和 (L1+L2) 的比例。1 = 完全伸直（IK 奇点，会抖），\n" +
                 "所以留一点余量。对应 EP13 里「手够不到就把 target 拉回来」的常识做法。")]
        public float ArmIKMaxReachFraction = 0.97f;

        [Tooltip("目标点距离的下限，占 |L1−L2| 的比例（太近也会奇点）。")]
        public float ArmIKMinReachFraction = 1.15f;

        [Tooltip("肘部 pole 的方向：沿角色「后方」的分量。教程 EP13 的 hint 放在「腰高、手臂后方」；\n" +
                 "Sergio 的 UpdateArmsIK 把 hint 放在 armsMiddleTarget − armsUpVec（肘往外往下）。")]
        public float ElbowPoleBackward = 0.85f;

        [Tooltip("肘部 pole 的方向：向下分量（人伸手时肘朝下后方）。")]
        public float ElbowPoleDown = 0.55f;

        [Tooltip("肘部 pole 的方向：向外分量（左右各按自己的侧向符号）。")]
        public float ElbowPoleOutward = 0.35f;

        [Tooltip("手掌朝向的目标：0 = 不管（保持绑定扭转），1 = 完全让掌心对准目标表面。\n" +
                 "对应 EP13 的 Multi Rotation Constraint（她说「two bone IK 解决不了旋转」）。")]
        public float HandPalmAlignWeight = 1.0f;
        [Tooltip("手拿法线取 hand 骨的哪根局部轴。0 = **自动标定**（推荐）；非 0 则人工指定：\n" +
                 "+1 = 局部 +X，−1 = 局部 −X，+2 = 局部 +Z，−2 = 局部 −Z。\n" +
                 "⚠️ **不能照抄教程的 offset** —— EP13 原话：那个 offset 是给 Bananaman 的，别的角色要另外定。\n" +
                 "本模型实测正好证明了这一点：bind pose 里两只手**都是手掌朝下**，但局部轴不一样 ——\n" +
                 "  左手 局部 −X → 世界 (0.042, −0.922, −0.384)（朝下）\n" +
                 "  右手 局部 +Z → 世界 (0.049, −0.992, 0.118)（朝下）\n" +
                 "所以一个数值根本盖不住两只手。自动标定的判据就是「四根候选轴里世界方向最朝下的那根」\n" +
                 "（T-pose 的手掌朝下）。读数见 ClumsyRagdollBench.ArmAxisReport()。")]
        public int HandPalmAxis = 0;

        [Tooltip("手掌滚转的额外偏置（度），在 HandPalmAxis 之上再拧一点。调不准的时候用它。")]
        public float HandPalmOffsetDegrees = 0f;

        [Tooltip("IK 生效时姿势驱动的角速度上限（度/秒）。比 PoseSlewDegreesPerSecond 宽松，\n" +
                 "否则手够不到东西（420°/s 在 0.37m 的肩半径上只有 ~2.7 m/s 的手速）。")]
        public float ArmIKSlewDegreesPerSecond = 1400f;

        [Header("手部 · 抓取（照 Sergio 的 GripModule，不是教程 P7 的 FixedJoint）")]
        [Tooltip("抓取关节的位置：true = 加在**手上**（Sergio），false = 加在被抓物体上（教程 P7）。\n" +
                 "加在手上才不会污染被抓物体自己的关节链，而且松手时不用去动物体。")]
        public bool GripJointOnHand = true;

        [Tooltip("线性运动：Locked（位置刚性）。两个来源一致。")]
        public bool GripLockLinear = true;

        [Tooltip("被抓物体默认的角运动（物体没挂 Grippable 时用它）：锁死 = 焊在手里（教程 P7 的 FixedJoint）；\n" +
                 "Limited = 能在手里晃（Sergio 的 JointMotionsConfig）。")]
        public ConfigurableJointMotion GripDefaultAngularMotion = ConfigurableJointMotion.Limited;

        [Tooltip("默认角运动为 Limited 时的锥角限制（度）。Sergio 的三个 SoftJointLimit 默认值。")]
        public float GripDefaultAngularLimit = 25f;

        [Tooltip("抓取关节的 breakForce。教程说「set this to like a high amount」（未给数值）。")]
        public float GripBreakForce = 120000f;

        [Tooltip("抓取关节的 breakTorque。0 = 用 GripBreakForce。")]
        public float GripBreakTorque = 0f;

        [Tooltip("抓取检测球的半径 —— 以**手掌**为中心的一个查询球，不是 Collider。\n" +
                 "教程 P7 用的是「手 Collider 勾 Is Trigger」；Sergio 用 OnCollisionEnter。\n" +
                 "这里改成 Physics.OverlapSphere 查询，三个理由：\n" +
                 "  ① 编辑器的诊断台里 Physics.Simulate 不派发 trigger 消息，只有查询能做确定性验证；\n" +
                 "  ② 查询能挑「最近的那个」目标，而回调只能拿到「最后进入的那个」；\n" +
                 "  ③ 不做成 Collider 就没有「自己和自己重叠」的噪音 —— 实测旧的触发球半径 0.208m\n" +
                 "     把手自己的 shoulder/arm/forearm/hand 全罩进去了（CarryReport 的读数）。")]
        public float GripQueryRadius = 0.16f;

        [Tooltip("满足什么条件才算可抓：true = 必须有 Grippable 组件（Sergio）；\n" +
                 "false = Grippable 或 tag == GrabbableTag 都行（教程 P7 的兼容口径）。")]
        public bool GripRequireGrippableComponent = false;

        [Tooltip("挡住自己的部件：不许抓住自己身上的骨头。来源：Sergio 的 canGripYourself（默认 false）。")]
        public bool CanGripYourself = false;

        [Tooltip("只在「手伸出去够到了」时才允许抓（Sergio：Gripper 由手臂 IK 权重开关）。\n" +
                 "关掉 = 只要按键 + 碰到就能抓（教程 P7 的行为）。")]
        public bool GripOnlyWhenReaching = true;

        [Tooltip("上面那个阈值。来源：Sergio 的 leftArmWeightThreshold / rightArmWeightThreshold = 0.5。")]
        public float GripReachWeightThreshold = 0.5f;

        [Header("相机 · ★ 2026-10-02 owner：暂时不要跟随")]
        [Tooltip("★ **相机要不要跟着小浣熊走**。owner 原话：「暂时不要让游戏内的摄像机跟随小浣熊移动」。\\n" +
                 "跟随的本质是相机的 `Target` 认 `Ragdoll.Root` 当爹（Root 每帧被拖到骨盆位置），\\n" +
                 "所以关掉 = Target 不认爹、留在世界里不动。绕轨/推拉（中键/滚轮）照旧可用。\\n" +
                 "运行期按 `C` 可切换。")]
        public bool CameraFollowRagdoll = false;

        [Header("手部 · ★ 按住键就伸手（不必有东西可抓）")]
        [Tooltip("★ 按住抓取键就把这只手伸到身前的默认位，松开就收回去。\\n" +
                 "owner 口径：「点击左键伸出左手，松开左键左手回落」——\\n" +
                 "以前附近没有可抓物体时状态机直接 return，手一动不动，看不出按键有没有生效。")]
        public bool ReachOutOnGrip = true;

        [Tooltip("★ **伸手段把手臂加硬多少倍**。不加硬的话«伸出手»只是把胳膊抬起来一点、软塌塌地垂着\n" +
                 "（实测手停在目标下方 0.33m 处）。与搬运那一档同理：`Recipe.DampingRatio = 0` ⇒ 驱动无阻尼，\n" +
                 "加硬必须**连阻尼一起给**（下面的阻尼比）。")]
        public float ReachOutArmStiffnessScale = 6f;

        [Tooltip("上面那一档的阻尼比。0 = 不加阻尼（会自激抖动，别这么设）。")]
        public float ReachOutArmDampingRatio = 3f;
        [Tooltip("伸手位：肩的**正前方**多远（米）。可达 0.478 ⇒ 0.40 留 16% 余量，保证伸得直、不夹紧。")]
        public float ReachOutForward = 0.40f;

        [Tooltip("伸手位：往身体外侧偏多少（米，左负右正）。0.06 让两只手不会在中间打架。")]
        public float ReachOutLateral = 0.06f;

        [Tooltip("伸手位：相对肩的高度偏移（米）。−0.02 ≈ 与肩同高略低。")]
        public float ReachOutUp = -0.02f;

        [Header("环境交互状态机（= EP13 的 Environment Interaction State Machine）")]
        [Tooltip("总开关。")]
        public bool UseInteraction = true;

        [Tooltip("Search 态的搜索半径（米）。")]
        public float InteractionSearchRadius = 2.6f;

        [Tooltip("Search 态的搜索视角（度，角色正前方为 0）。Sergio 的 armsHorizontalSeparation 那种\n" +
                 "「只够正前方」的口径，这里用角度表达。")]
        public float InteractionSearchAngle = 130f;

        [Tooltip("Approach 态：手目标点朝抓取点移动的速度（米/秒）。")]
        public float InteractionHandApproachSpeed = 2.4f;

        [Tooltip("Approach → Rise 的判据：手离抓取点小于这个距离（米）就算「到位」。")]
        public float InteractionArriveDistance = 0.10f;

        [Tooltip("Rise → Touch 的判据：手离抓取点小于这个距离（米）就允许建立抓取关节。")]
        public float InteractionTouchDistance = 0.045f;

        [Tooltip("Touch / 已抓状态下，目标离这个距离以外就断开（米）。")]
        public float InteractionReleaseDistance = 0.75f;

        [Tooltip("Reset 态判定「手已经收回来、可以回 Search」的权重阈值。")]
        public float InteractionResetWeight = 0.05f;

        [Tooltip("物体被抓住之后，手的目标点要不要继续跟它走（true = 抓着的东西被拽走时手跟着追）。")]
        public bool InteractionTrackGrabbed = true;
        [Tooltip("手骨原点到物体表面的间距，单位是**手碰撞体的半径**（本模型 0.065m）。\n" +
                 "1 = 手掌球体刚好与表面相切（默认）；0 = 手骨原点就放在表面点上（会陷进去，PhysX 会把两者崩开）。")]
        public float InteractionGraspStandoff = 1.0f;

        // ------------------------------------------------------------------
        // ★ 2026-10-02 · 双手搬运（像《人类一败涂地》那样抱东西）
        //   owner 口径：「**实现类似人类一败涂地的搬运物体的互动逻辑**」，
        //   并在选项里选了「**A 双手抱住 + 重量反馈**」+「**真的被压下去**」。
        //
        //   加这一档之前：抓取只有「一只手按抓取键 → 物体被 ConfigurableJoint 线性 Locked
        //   **焊死**在手上」，能拖能晃，但**没有「抱」**，物体的重量也**一点都不压在身体上**。
        //   现在：两只手都按 + 同一个物体都够得到 ⇒ 走 `ClumsyCarry` 这条新路径。
        //   一只手仍然走旧路径（拖箱子 / 抓单杠吊住自己），一行都没改。
        //
        //   全部读数在诊断台 `CarryTwoHandedReport`（空手 / 2kg / 6kg / 14kg 四档对照）。
        // ------------------------------------------------------------------

        [Header("手部 · 双手搬运（HFF 的抱东西）")]
        [Tooltip("总开关。关掉 = 回到「一只手焊死拖着走」的旧行为（两条路径都在，可 A/B）。")]
        public bool UseCarry = true;

        [Tooltip("在身前多大半径里找可搬的物体（米，从**骨盆**量起）。\n" +
                 "来源：本模型实测可达球 —— 肩高 0.93m、臂长 0.476m，胸前够得着的范围就这么大。")]
        public float CarrySearchRadius = 0.95f;

        [Tooltip("只找身前这个张角里的物体（度，角色正前方为 0）。")]
        public float CarrySearchAngle = 110f;

        [Tooltip("挑目标时，质量每 1kg 折算成多少米«距离惩罚»（让轻的先被抱起来）。")]
        public float CarrySearchMassBias = 0.02f;

        [Tooltip("超过这个质量就**不抱**（kg）。0 = 不设上限。\n" +
                 "「抱不动」的正面表达：太重的箱子双手按上去也不会进入搬运。")]
        public float CarryMaxMass = 0f;

        [Tooltip("携带位：骨盆前方多远处（米，**基础值**，还要再加上物体自己的半深）。")]
        public float CarrySlotForward = 0.20f;

        [Tooltip("携带位再往前加「物体半深 × 这个比例」—— 保证大箱子不插进肚子里。")]
        public float CarrySlotDepthScale = 1.0f;

        [Tooltip("携带位相对骨盆的高度（米）。本模型骨盆 ≈ 0.66m、肩 ≈ 0.93m ⇒ 0.12 = 抱在**胸口下沿**。\n" +
                 "★ 这个值是被实测逼出来的：抬到胸口（0.12）之后两手到肩的距离从 0.44m 降到 0.34m\n" +
                 "   （可达 0.478m ⇒ 从 92% 降到 71%），臂是软的，92% 那种«几乎伸直»的目标\n" +
                 "   实测手停在半路差 0.22m、2.5s 够不到、搬运永远进不去。")]
        public float CarrySlotUp = 0.28f;

        [Tooltip("手掌（手骨原点）离物体侧面的间距（米）。手骨的碰撞体是半径 0.065m 的腕球 ⇒ 0.07 刚好相切。")]
        public float CarryGripStandoff = 0.07f;

        [Tooltip("够不着的判据里留多少余量（可达半径的比例）。0.95 = 留 5% 别顶到极限。")]
        public float CarryReachMargin = 0.95f;

        [Tooltip("够不着时把携带位整体往回拉（大箱子自动往怀里抱近一点）。\n" +
                 "关掉的话 IK 会把两只手各自夹到可达球面上**不同的**地方，箱子被拧着。")]
        public bool CarrySlotClampToReach = true;

        [Tooltip("伸手抱的超时（秒）。超时就放弃、回空闲。")]
        public float CarryReachTimeout = 2.5f;

        [Tooltip("手离抓取位多近算«到位了、可以抱住»（米）。判据用的是 **IK 夹紧之后**的手误差。")]
        public float CarryGrabTolerance = 0.09f;

        [Tooltip("上面那条之外的第二道：手到**原始抓取点**最多能差多少（米）。\n" +
                 "大箱子的两肋在臂长之外时，IK 会把手停在可达面上 —— 那时手到原始抓取点就是有距离的，\n" +
                 "但手确实贴到了箱子。这一条用来把«够不着的大箱子»和«几米外的东西»分开。")]
        public float CarryGrabGoalSlack = 0.20f;

        [Tooltip("手↔物体的软弹簧（N/m，**每公斤被搬物体**）。\n" +
                 "按质量缩放的理由：弹簧固定的话，越重的物体在手里越软（静伸长 = mg/k），\n" +
                 "手感会与质量反相关 —— 而这里要的恰恰是「重 = 更沉」。缩放之后静伸长恒定 ≈ 2.3cm。")]
        public float CarryHoldSpringPerKg = 420f;

        [Tooltip("软弹簧的阻尼 = 弹簧 × 这个比例。0.12 ≈ 对 14kg 的物体接近临界阻尼。")]
        public float CarryHoldDamperRatio = 0.12f;

        [Tooltip("软弹簧的出力上限（N）。太小 = 重物直接脱手。")]
        public float CarryHoldMaxForce = 8000f;

        [Tooltip("搬运关节的角阻尼（只阻尼、不复位）：物体绕«两手连线»自转的那一自由度靠它按掉。")]
        public float CarryAngularDamping = 6f;

        [Tooltip("抱住之后**关掉被搬物体与角色身上各部件的碰撞**。\n" +
                 "理由：抱着的东西是**贴着肚子**的（携带位只到骨盆前 0.29m，箱子半深 0.15m），\n" +
                 "不关掉的话躯干碰撞体会把箱子往外顶，两只手的软弹簧顶不住，箱子会一直在怀里抖。\n" +
                 "代价：抱着的东西会穿过身体（《人类一败涂地》也是这样）。")]
        public bool CarryIgnoreBodyCollision = true;

        [Tooltip("★ 搬运时手臂加硬的倍数。本工程的胳膊是«面条»（四肢弹簧 33、上半身 0.12 倍），\n" +
                 "不加硬**抱不住任何东西**（物体直接把两条胳膊压直、掉到大腿旁边）。\n" +
                 "加硬多少 = «抱得动多重»的手感旋钮：调低 → 抱着的东西越抱越低。")]
        public float CarryArmStiffnessScale = 14f;

        [Tooltip("★ 抱住之后**保持«手 ↔ 物体»的碰撞关闭**。\n" +
                 "为什么：胸口模式下两只手只是 IK 搭在箱子侧面（没有关节）。碰撞一恢复，\n" +
                 "手腕那颗半径 6.5cm 的球就会一直砸箱子 —— 箱子被顶走 → 胸口关节拉回来 → 再被顶走，\n" +
                 "**这就是«抱住之后剧烈抖动»的主因**。关掉之后箱子稳在手上一动不动（实测抖动降两个量级）。")]
        public bool CarryIgnoreHandCollision = true;

        [Tooltip("★ **搬运加硬时一起给多少阻尼比**（关键的一条）。\n" +
                 "本工程的 `Recipe.DampingRatio = 0` ⇒ 所有关节驱动的 positionDamper 都是 0。\n" +
                 "全身软而无阻尼是这只浣熊的手感基础，**不能全局改**；\n" +
                 "但加硬 14 倍之后那是«无阻尼的硬弹簧»，会自激 ——\n" +
                 "实测两只手每个物理步 |Δv| 1.16（不抱东西的对照组只有 0.007），肉眼看就是剧烈抖动。\n" +
                 "0 = 关掉（可 A/B），0.7 ≈ 临界阻尼的一半。")]
        public float CarryStiffnessDampingRatio = 3.0f;

        [Tooltip("★ 搬运时**躯干**（spine / neck）加硬的倍数。\n" +
                 "手臂加硬了、躯干还是«面条»的话，重量会把脊椎压弯 ——\n" +
                 "实测只加硬手臂时两只肩的高度差 0.19m，携带位跟着歪、箱子在怀里打滚。")]
        public float CarryTorsoStiffnessScale = 5f;

        [Tooltip("★ **抱在哪儿**：true = 一个「胸口抱住」关节（关节加在物体上、连到骨盆，\n" +
                 "线性 + 角运动各一路弹簧驱动 ⇒ **物体的位姿完全由它决定**）；\n" +
                 "false = **两只手各一个点约束**（更像《人类一败涂地》的«两只手真的抓着»）。\n" +
                 "\n" +
                 "为什么默认走 true：两条手链是**点约束**，只能定住物体的一条轴（两手连线），\n" +
                 "**绕这条轴的自转是自由的**，而重力对它没有力矩（质心在轴上）⇒ 没有任何东西把它摆回来。\n" +
                 "实测：歪角在 **25°~140°** 之间乱跳，而且**同一组参数连跑三次结果都不一样**\n" +
                 "（两条软手链 + 一个自由自转 + 接触 ⇒ 混沌，PhysX 求解本身也不保证逐位可复现）。\n" +
                 "胸口关节把 6 个自由度全部驱动起来，位姿是**定解**。")]
        public bool CarryHoldOnChest = true;

        [Tooltip("胸口关节的线性弹簧（每 kg 物体，N/m）。6kg 箱子 = 5400N/m ⇒ 静差约 1cm。")]
        public float CarryChestSpringPerKg = 900f;

        [Tooltip("上面那个弹簧的阻尼比例（× 弹簧）。")]
        public float CarryChestDamperRatio = 0.15f;

        [Tooltip("胸口关节的角弹簧（每 kg 物体，N·m/rad）。")]
        public float CarryAngularSpringPerKg = 60f;

        [Tooltip("上面那个角弹簧的阻尼比例（× 角弹簧）。")]
        public float CarryAngularDamperRatio = 0.15f;

        [Tooltip("抱持关节的 breakForce（0 = 用 GripBreakForce）。")]
        public float CarryBreakForce = 0f;

        [Tooltip("★ **把被搬物体摆正**的 PD 力矩（每 kg 物体的刚度，N·m/rad）。\n" +
                 "两条手链是**点约束**，只能定住物体的**一条轴**（两手连线）——\n" +
                 "绕这条轴的自转是自由的，重力对它没有力矩（质心在轴上）⇒ **没有任何东西把它摆回来**，\n" +
                 "进入搬运时是什么滚转角就一直是那个角度（实测歪 68°~128°，箱子在怀里躺着，\n" +
                 "两个抓取点跟着转到上下方向、手误差反而涨到 20~50cm）。0 = 关掉（可 A/B）。")]
        public float CarryUprightStiffnessPerKg = 12f;

        [Tooltip("上面那个 PD 的阻尼（每 kg，N·m·s/rad）。")]
        public float CarryUprightDampingPerKg = 2.0f;

        [Tooltip("回正力矩的出力上限（每 kg，N·m）—— 别让它压过手臂本来的力。")]
        public float CarryUprightMaxTorquePerKg = 1.5f;

        [Tooltip("走动着松手 = **丢出去**：沿当时的水平运动方向补多少速度（m/s）。站着松手就只是掉下去。")]
        public float CarryReleaseThrowSpeed = 2.2f;

        [Tooltip("身上水平速度低于它就算«站着»（不丢，只放下）。")]
        public float CarryReleaseThrowMinSpeed = 0.4f;

        [Tooltip("丢出去时再往上抬多少（m/s）。")]
        public float CarryReleaseThrowLift = 0.6f;

        [Tooltip("★ 重量反馈 · 每 kg 被搬物体把骨盆额外压低多少（米/kg）。\n" +
                 "托举层本来就会因为多挂了重量而下沉（弹簧的静差），这一条是**额外**的一档，\n" +
                 "用来把「被压下去」调得看得出来又不过分。")]
        public float CarryLoadSagPerKg = 0.008f;

        [Tooltip("上面那一档的封顶（米）。")]
        public float CarryLoadSagMax = 0.10f;

        [Tooltip("★ 重量反馈 · 速度/步幅折减：scale = 1 / (1 + 载重比 × 本值)。\n" +
                 "载重比 = 被搬质量 / 角色总质量 ⇒ 3kg 的箱子对 30kg 的角色就是 0.1。")]
        public float CarryLoadSpeedPenalty = 2.5f;

        [Tooltip("上面折减的下限（别抱着东西就完全走不动）。")]
        public float CarryLoadMinSpeedScale = 0.30f;

        // ------------------------------------------------------------------
        // 动画（教程那套「先有动画」的门槛）与笨拙层
        //   来源：ref-ActiveRagdolls-视频教程.md §〇.1 / §一 3~5
        //   + ref-Unity动画系列-全集.md EP04（1D Blend Tree）/ EP02（Culling Mode）。
        //   片段本身由 Assets/Editor/ClumsyAnimationBaker.cs 烘到 Assets/Animations/。
        // ------------------------------------------------------------------

        [Header("动画（双身体架构里的「动画身体」）")]
        [Tooltip("用 AnimationClip 资产当姿势源（教程的做法）。关掉则回落到程序化步态 —— 两者可以 A/B。")]
        public bool UseAnimation = true;

        [Tooltip("AnimatorController 找不到时的兵底：继续用程序化步态，不报错停住。")]
        public bool FallBackToProcedural = true;

        [Header("笨拙层 · 主旋钮")]
        [Tooltip("0 = 干净利落（目标姿势原样传给关节）；1 = 醉汉（目标自己就慢半拍 + 回弹）。\n" +
                 "实测：它只动「目标」，不动支撑弹簧 —— 所以站立那 8.00s 不受影响。")]
        [Range(0f, 1f)] public float Clumsiness = 1.0f;

        [Tooltip("Clumsiness = 0 时的跟随频率（Hz）。高 = 跟得又快又紧，几乎看不出来。")]
        public float WobbleFrequencyAtZero = 8.0f;

        [Tooltip("Clumsiness = 1 时的跟随频率（Hz）。低 = 慢半拍。")]
        public float WobbleFrequencyAtFull = 1.0f;

        [Tooltip("Clumsiness = 1 时的阻尼比。1 = 临界阻尼（不过冲）；0.28 = 明显回弹。")]
        public float WobbleDampingAtFull = 0.22f;

        [Tooltip("腿要不要也晃。**默认关** —— 脚一飘就把站立那套搞坏了；开了会把频率×2、阻尼补上去。")]
        public bool ClumsyLegWobble = false;

        [Header("笨拙层 · 落地踉跄")]
        [Tooltip("触发踉跄的最小落地下降速度（m/s）。低于它就算「轻轻放下」。")]
        public float ImpactMinFallSpeed = 1.6f;

        [Tooltip("到这个下降速度算满强度。")]
        public float ImpactFullFallSpeed = 4.0f;

        /// <summary>
        /// ★ 2026-10-01：**自己跳上去的那一下落地，踉跄要打折**（0..1）。
        /// owner：「跳跃现在看起来就像摔倒一样」。量下来真因就在这：落地冲击按落地那一刻的下降速度算，
        /// 跳一次落地 vy ≈ −3.8 m/s ⇒ impact 0.92（膝盖屈 24°、脊柱塌 11°、持续 0.35 s）
        /// —— 那就是«摔了一跤»的样子。踉跄本意是给«意料之外的落差»用的，自己跳的不该算。
        /// </summary>
        public float JumpLandingImpactScale = 0.2f;

        [Tooltip("踉跄衰减时长（秒）。")]
        public float ImpactDuration = 0.35f;

        [Tooltip("落地时膝盖多弯多少度（满强度时）。")]
        public float ImpactKneeDegrees = 26f;

        [Tooltip("落地时腰多塌多少度（满强度时）。颈/头按它的 0.5/0.4 倍反向跟随。")]
        public float ImpactSpineDegrees = 12f;

        // ------------------------------------------------------------------
        // 数值环境
        // ------------------------------------------------------------------
        // 数值环境
        // ------------------------------------------------------------------

        [Header("物理环境")]
        [Tooltip("重力。教程用 Unity 默认 −9.81（工程原来是 −15，那是旧主动方案的调参）。")]
        public float GravityY = -9.81f;

        [Tooltip("求解器迭代次数。**教程没提这个**（Unity 默认 6）。诊断台实测它是最大的单项杠杆：\n" +
                 "同一个配方下 16/6 → 站 0.60s、32/12 → 7.38s、64/16 → 8.00s（全程不例）。\n" +
                 "原因：布娃娃里大量约束是 Locked，迭代次数直接决定它们「有多硬」。")]
        public int SolverIterations = 64;

        [Tooltip("速度迭代次数。诊断台实测与迭代次数一起从 6 提到 16。")]
        public int SolverVelocityIterations = 16;

        [Tooltip("物理步长。教程用 Unity 默认 0.02。")]
        public float FixedDeltaTime = 0.02f;

        public RagdollRecipe Clone()
        {
            return (RagdollRecipe)MemberwiseClone();
        }
    }
}
