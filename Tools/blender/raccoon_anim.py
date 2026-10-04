# -*- coding: utf-8 -*-
"""
浣熊动作动画 —— 用 Blender 关键帧做，导出成 Unity 能吃的 FBX。

跑法（Blender 5.1，Windows 侧）：

    src = open(r"C:\\Users\\9527\\clumsy-ragdoll-physics\\Tools\\blender\\raccoon_anim.py",
               encoding="utf-8").read()
    exec(compile(src, "raccoon_anim.py", "exec"), {"__name__": "__main__", "bpy": bpy})

依据
----
* `ref-ActiveRagdolls-视频教程.md` §〇.1：主动布娃娃的硬门槛是「**先有动画片段**」，
  他仓库里数出来的是 **3 段循环**（Idle / Moving / InTheAir）。
* `ref-Unity动画系列-全集.md` EP01：导出勾 **In Place**（动画不带位移）·
  EP02：Animator / **Culling Mode = Always Animate**（隐藏动画身体后动画要照播）·
  EP04：1D 混合树按 `speed` 混 Idle↔Walk↔Run · EP08：关键帧 / 切线 / 循环。
* 上一轮（`HANDOFF-动画与笨拙效果.md`）的三段片段是**从程序化步态公式烘出来的快照**，
  不是手 K 的。本脚本走的是那份交接 §九.2 点名的另一条路：**在 Blender 里真正做动画**。

坐标约定（实测自 111_raccoon_rigged.fbx 的 bind pose）
------------------------------------------------------
骨架对象：loc=(0,0,0) · rot=(90°,0,0) · scale=0.01 —— FBX 的 Y-up/厘米 → Blender 的 Z-up/米。
**骨骼空间（armature space）**里（数值单位 = 厘米）：

    +X = 角色左手方向（LeftUpLeg 在 x=+7.16）
    +Y = 上（Hips head y=33.12，Head tail y=67.86）
    +Z = 前（LeftFoot tail z=27.71 大于 head z=15.71）

所以「相对 rest 的某根轴转多少度」这句话是有确定含义的，本文件全部用这套轴说话：

    骨朝向为「上」的（Hips/Spine/Neck/Head）：+X 转 = **前倾**
    骨朝向为「下」的（UpLeg/Leg，以及垂下来之后的 Arm/ForeArm）：+X 转 = **向后摆**
    膝：Leg 的 +X 转 = **屈膝**（小腿往后）
    踝：Foot 的 +X 转 = **绷脚尖**（跖屈）；−X = 勾脚尖（背屈）
    侧摆/开合：Z 轴；扭身/骨盆旋转：Y 轴

左右镜像的规则（推导：镜像矩阵 diag(-1,1,1) 把「绕 a 转 θ」映成「绕 Ma 转 −θ」）：
**X 分量保持不变，Y 与 Z 分量取反**，同时 `_l` ↔ `_r` 互换。走路第二半步就是整身镜像。

★ 种地（ground planting）—— 两处自动解算，都是为了不让手填数
----------------------------------------------------------------
FK 摆腿**不会**自己踩在地上。两版都在这上面栽过，记在这里：

* **第一版**：髋的高低是手填的。结果走路最低点沉到 **−6.3 cm**、落地那一拍沉到 **−13.9 cm**
  （角色才 84.5 cm 高）—— 整只脚插进地板。
* **第二版**：改成「把网格最低点解到地面」。脚是不穿了，但**走路时髋高 0.364–0.393，
  比站立的 0.334 还高 3–6 cm** —— 角色踮着脚走。逐脚一量才看清：
  f00 那一拍 **右脚（后腿）脚尖踩在地上，左脚（前腿）整只悬空 7 cm**。
  因为后脚踝跖屈给到 40°，脚尖比前脚脚跟低了 5 cm，种地就种在后脚尖上了。

所以这一版解两件事：

1. **髋找脚**：`plant[0]` 那只脚的最低顶点解到 `clearance`，髋沿竖直方向平移到位。
   竖直起伏于是不再是手填的，而是**腿长配置算出来的**（支撑腿伸得直 → 人就高）。
2. **踝找地**：其余每只脚各解一次**踝关节角度** ——
   该踩地的解到正好贴地，摆动中的只在「穿地」时才抬高。
   脚踝本来就该是本pose里最没把握的那个数，交给解算比手填准。

于是同一个姿势里，髋高由**前腿**定、后腿踝角由地面定，两者不再互相打架。
"""

import bpy
import math
import os
import numpy as np
from mathutils import Vector, Quaternion

# ----------------------------------------------------------------------------
# 常量
# ----------------------------------------------------------------------------

SRC_FBX = r"C:\Users\9527\clumsy-ragdoll-physics\Assets\Models\Characters\111_raccoon_rigged.fbx"
OUT_FBX = r"C:\Users\9527\clumsy-ragdoll-physics\Assets\Models\Characters\111_raccoon_animated.fbx"
# ★ 一段一个 FBX（Mixamo 那样，without skin）放这里
SEPARATE_DIR = r"C:\Users\9527\clumsy-ragdoll-physics\Assets\Animations\FBX"
# ★ 带蒙皮的**修好骨架**的模型 —— 布娃娃与可见身体都用它（与旧文件同名，GUID 不变）
RIGGED_OUT = r"C:\Users\9527\clumsy-ragdoll-physics\Assets\Models\Characters\111_raccoon_rigged.fbx"
os.makedirs(SEPARATE_DIR, exist_ok=True)

FPS = 60                      # ★ 2026-10-01（第五轮）：30 → 60。owner：「提高小浣熊的动画帧率」。
# ⚠️ **所有片段定义（`CLIP_SPECS` 的 length、各 `*_keys()` 里的帧号）仍然按 30 fps 的
#    "创作帧"写**，输出时统一乘 `FRAME_MUL`。这样改帧率只需要动这一个数，不必去改二十来个帧号。
#    改帧率**不改变时长**（90 创作帧 = 3.00 s，60 输出帧/秒下就是 180 帧，还是 3.00 s）。
FRAME_MUL = FPS // 30
ARM_NAME = "Armature"
MESH_NAME = "HEAD"            # 这具模型整个身体是一个 mesh，名字叫 HEAD

AX = {"X": Vector((1.0, 0.0, 0.0)),
      "Y": Vector((0.0, 1.0, 0.0)),
      "Z": Vector((0.0, 0.0, 1.0))}

BONES = {
    "hips": "mixamorig:Hips",
    "spine": "mixamorig:Spine",
    "neck": "mixamorig:Neck",
    "head": "mixamorig:Head",
    "shoulder_l": "mixamorig:LeftShoulder",
    "arm_l": "mixamorig:LeftArm",
    "forearm_l": "mixamorig:LeftForeArm",
    "hand_l": "mixamorig:LeftHand",
    "shoulder_r": "mixamorig:RightShoulder",
    "arm_r": "mixamorig:RightArm",
    "forearm_r": "mixamorig:RightForeArm",
    "hand_r": "mixamorig:RightHand",
    "upleg_l": "mixamorig:LeftUpLeg",
    "leg_l": "mixamorig:LeftLeg",
    "foot_l": "mixamorig:LeftFoot",
    "toe_l": "mixamorig:LeftToeBase",
    "upleg_r": "mixamorig:RightUpLeg",
    "leg_r": "mixamorig:RightLeg",
    "foot_r": "mixamorig:RightFoot",
    "toe_r": "mixamorig:RightToeBase",
}

MIRROR_PAIR = {}
for _k in list(BONES):
    if _k.endswith("_l"):
        MIRROR_PAIR[_k] = _k[:-2] + "_r"
        MIRROR_PAIR[_k[:-2] + "_r"] = _k

# 种地标定：髋骨局部 +Y 挪 1.0 时，网格最低点在世界 Z 上变多少米。启动时实测。
UP_PER_LOCAL = None
# 脚部顶点掩码，三套：foot（整只脚）/ ball（跟→球）/ toe（鞋前半截的鞋底）。启动时实测。
MASKS = None
ANKLE_LOG = []
META_SYNC = []

# ★ 2026-10-01：**踝关节的活动范围**（度，绕 X；+ = 绷脚尖/跖屈，− = 勾脚尖/背屈）。
#
# owner 的话：「脚后跟只能向小腿肚子弯曲，不能向反方向弯曲」。
# 踝是个单向铰链：脚掌朝小腿肚子那面折（跖屈，+）能折很多，反方向（背屈，−）余量很小；
# 超过这个范围就是**关节掰反了**（解算器没有解剖学常识，只要「够得着地」它什么都干得出来 ——
# 实测把它转到 +80° 以上，脚跟被抬到 300 mm、整只鞋像倒过来的钟摆）。
# 于是解出来的角度一律夹在这个区间里；夹住了还够不着地，就让髋去兜底（原来的兜底逻辑）。
ANKLE_PLANTAR_MAX = 35.0      # 跖屈上限（脚跟往小腿肚子那边抬）
ANKLE_DORSI_MAX = 30.0        # 背屈上限（脚尖往上勾）
# 脚趾（鞋前半截）能折多少（度，− = 往上折）。这不是anatomy，是「这双鞋有多软」：
# 手感目标由 owner 定（「前半部分的弯曲程度还不够大」），所以给得比踝宽松得多。
TOE_FOLD_MAX = 80.0

FOOT_GROUPS = {
    "l": ("mixamorig:LeftFoot", "mixamorig:LeftToeBase"),
    "r": ("mixamorig:RightFoot", "mixamorig:RightToeBase"),
}

# ★ 2026-10-01（第二轮）：**脚趾蒙皮修正** —— 见 fix_foot_rig()。
#
# 实测背景（当前这份 FBX 的原始自动权重）：趾骨权重铺得极宽 —— 归一化后
# y=−280 处还有 0.97、−240 处 0.78、−220 处 0.47、一直到 −160 才归零。
# 也就是说**皮肤真正绕的那根轴在 y≈−228，而骨头在 y=−277，差 49 mm**。
# 骨头前面那 49 mm 全是「折起来会往下走」的区域：把趾骨转过 −26°，
# 鞋底在 y=−250…−210 这一带直接沉到 **−14.5 mm**（−40° 时 −20.9 mm）。
# 后果不是「不好看」而是**种地解算把这一坨当成最低点踩住** —— 髋被抬高，
# 另一只脚浮起 8~18 mm（这正是 HANDOFF-鞋前半截弯折.md §三 记的那处「浮脚」）。
TOE_HINGE_BAND = 0.015        # 铰链过渡带半宽（米）：[趾根−15, 趾根+15]，50% 交叉正落在趾根上
TOE_TIP_EPS = 0.002           # 趾骨尾端收到鞋尖时留的余量（米）

# ★ 2026-10-01（第三轮）：**把脚缩小** —— 见 fix_foot_scale()。
#
# owner：「小浣熊的脚部比例相较于人类骨架的比例过于大了……不然动作还是别扭」。
# 实测（占身高）：鞋长 283 mm = **33.5%**（X Bot 236/1809 = 13.0%）· 脚骨 176 mm = **20.9%**
# （X Bot 7.6%）· 踝高 130 mm = **15.4%**（X Bot 4.8%）—— 脚大 2.6~3.2 倍，
# 所以整只鞋扫过的弧度也是同量级的大，种地解算天天在跟它打架。
# owner 定档：**关节 + 鞋几何一起缩到半程**，鞋长 → 200 mm（23.7%）。
FOOT_TARGET_LEN = 0.200       # 鞋（鞋底）长度目标，米
FOOT_BLEND_TOP = 0.170        # 缩放权重 = 1 的 z 上界（实测靴筒顶在 ~180 mm）
FOOT_BLEND_ZERO = 0.200       # 缩放权重 = 0 的 z 下界（膝在 204 mm，别碰到膝）
FOOT_MIN_Y = -0.020           # 只作用于 y < 这个值的顶点 —— 把尾巴排除（尾巴垂到 z≈140）

# ★ 2026-10-01（第三轮之二）：**把脚的分节照 X Bot 的脚来做** —— 见 retarget_foot_joints()。
#
# owner：「踝关节可以再下降一点。把小浣熊的鞋做成类似 x-bot 脚的分节结构，
# 但是不要分开渲染，保持鞋的整体外观。」
#
# X Bot 的脚（实测，世界毫米）：踝高 **87.6** / 身高 1809 = **4.84%**；
# 趾段 `ToeBase` 长 **92.78** / 鞋长 236.0 = **39.3%**。
# 小浣熊缩完鞋之后：踝高 90.8 = 10.7%（**2.2 倍**）、趾段 54.7 / 201.4 = 27.2%（**0.69 倍**）。
# ⇒ 这两条就是"分节不像脚"的全部内容，按下表对齐；**只动骨骼，网格一字不改**（外观不变）。
ANKLE_HEIGHT_FRAC = 0.0485    # 踝高 / 身高（照 X Bot 的 4.84%）
TOE_SEG_FRAC = 0.393          # 趾段 / 鞋长（照 X Bot 的 39.3%）

# ★ 2026-10-01 新增：**「脚骨主导」的那部分脚**（跟 → 球，不含脚趾）。
#
# 为什么必须和「整只脚」分开：这只鞋 288 mm 长，其中**鞋尖只到趾骨根前面 78 mm**
# （趾骨根 y=-277 / 鞋尖 y=-355）。走路蹬地那一拍趾骨会折起来（就是「鞋前半截弯起来」），
# 于是**整只脚的最低点会跑到被折起来的那截上去** —— 踝解算器为了够地会把脚一直转到
# 「站在鞋尖上」（实测：脚跟被抬到 180~300 mm，整只鞋像个倒过来的钟摆）。
#
# 真鞋在这种情况下是绕着**球**滚的：折起来的前半截压根不碰地。
#
# ⚠️ **2026-10-01 更正（第二轮）**：上面这段设想的做法（踝解算改用 `ball` 掩码、
#    鞋前半截走 `solve_toe`）**代码里最后没落地** —— 实测口径是：
#      · `ball` / `toe` 两套掩码由 `build_masks()` 算出来，但**没有任何调用点用它们**
#        （唯一引用者 `solve_toe()` 自己也是死代码，从来没被 `apply_pose` 调过）；
#      · 髋找脚与踝找地实际一律用 **`foot`（整只脚）**掩码；
#      · 趾骨的「只能往上折」也只写在 `clamp_toe()` 里，同样**没人调用**。
#    这不成问题 —— 因为真正的病根是**蒙皮**（折轴比骨头靠后 49 mm），已由 `fix_foot_rig()` 修掉：
#    修好之后鞋前半截折起来是**离开地面**的，整只脚的最低点自然就落在球上，
#    不需要 `ball` 掩码去替它兜。
#    ⇒ 谁要再动这几套掩码，先读 HANDOFF-鞋前半截弯折.md §七，别照着上面这段旧方案改。


# ----------------------------------------------------------------------------
# 姿势语言的底层：把「armature 空间的轴」翻译成「这根骨自己的局部轴」
# ----------------------------------------------------------------------------

def local_axis(arm, bone_name, axis_name):
    """骨骼 rest 旋转的逆，作用在 armature 空间的轴上 → 该骨 basis 里对应的轴。

    pose_bone.rotation_quaternion 是**骨自己的 rest 坐标系**下的基变换；
    想让骨「绕 armature 空间的 X 轴转 θ」，basis 就得绕 (R_rest⁻¹ · X) 转 θ。
    父级被摆过之后，这根轴会跟着父级走 —— 正是 FK 想要的行为。
    """
    b = arm.data.bones[bone_name]
    r = b.matrix_local.to_3x3().normalized()
    return (r.inverted() @ AX[axis_name]).normalized()


def bone_quat(arm, bone_name, specs):
    """specs = [("X", deg), ...]，**按顺序施加**（后面的在外层）。

    例：`[("Z",-68), ("X", 18)]` = 先把手臂从 T-pose 放下来，再绕（rest 的）X 前后摆。
    """
    q = Quaternion((1.0, 0.0, 0.0, 0.0))
    for axis_name, deg in specs:
        if abs(deg) < 1e-9:
            continue
        axis = local_axis(arm, bone_name, axis_name)
        q = Quaternion(axis, math.radians(deg)) @ q
    return q


def mirror(pose):
    """整身镜像：`_l`↔`_r` 互换，Y / Z 分量取反，X 不变。

    `__hips_loc__` / `__fit__` / `__clear__` 不是「轴 + 角度」的列表，单独处理：
    位移镜像时 X 取反；踩地的脚左右对调；clearance 不变。
    """
    out = {}
    for key, val in pose.items():
        if key == "__hips_loc__":
            out[key] = (-val[0], val[1], val[2])
            continue
        if key == "__clear__":
            out[key] = val
            continue
        if key == "__fit__":
            out[key] = tuple(("r" if f == "l" else "l") if f in ("l", "r") else f for f in val)
            continue
        nk = MIRROR_PAIR.get(key, key)
        out[nk] = [(ax, (-d if ax in ("Y", "Z") else d)) for ax, d in val]
    return out


def merge(*poses):
    """后者覆盖前者（同一根骨只留最后一份）。"""
    out = {}
    for p in poses:
        out.update(p)
    return out


# ---- 姿势上的三个「非旋转」标签 ----------------------------------------------

def ground(pose, clearance=0.0, plant=("l", "r")):
    """种地。

    `plant` 里的**第一只**脚负责定髋高（解到正好 `clearance`）；
    其余每只脚（含 plant 里剩下的、以及没列进来的）都解一次踝角 ——
    列进来的解到正好贴地，没列进来的只在穿地时才往上抬。
    `plant=()` = 不踩地（腾空），此时改解「整个网格的最低点 = clearance」。
    """
    p = dict(pose)
    p["__fit__"] = tuple(plant)
    p["__clear__"] = float(clearance)
    return p


def slide(pose, x=0.0, y=0.0, z=0.0):
    """种地之外的额外髋位移（厘米）。x = 左右，y = 上下，z = 前后。"""
    p = dict(pose)
    p["__hips_loc__"] = (x, y, z)
    return p



# ----------------------------------------------------------------------------
# 手臂：三个关节都要驱动（锁骨 / 肩 / 肘）
# ----------------------------------------------------------------------------
#
# ⚠️ mixamo 的命名在这一段特别容易骗人：
#
#     mixamorig:LeftShoulder   = **锁骨**（不是肩关节）
#     mixamorig:LeftArm        = **上臂，也就是肩关节**
#     mixamorig:LeftForeArm    = **肘**
#
# 所以「弯肩」= 转 `LeftArm`，「弯肘」= 转 `LeftForeArm`，「耸肩」= 转 `LeftShoulder`。
# 三个都在这份 helper 里，别再散在各处手写。
#
# 锁骨那根骨朝向 +X，所以：绕 Z 转 = 抬/沉肩（左：+ 抬），绕 Y 转 = 前后缩（+ 后缩）。

# ---- ★ 基准帧：两臂放在身体两侧（量出来的，不是拍的）-------------------------
#
# 骨架的 rest 是 **T-pose**（两只手平举）。这不是「站着」—— 它只是绑定姿势。
# 所有片段都必须自己把手臂放下来；**但「放下来」到底是多少度，只有一处说了算**，
# 就是这个基准帧 `stand_pose()`（导出里也有一段 `RaccoonStand` 专门给它）。
# 别的片段一律在它上面叠偏移，不再各写各的角度。
#
# ARM_DOWN 是**上臂从 T-pose 往下放多少度**。这里量过三档（角色 845 mm 高）：
#
#   ★★ 关键：**T-pose 的手臂是水平的（离竖直 90°）**。所以决定手臂朝哪儿的，
#      是「锁骨 + 大臂」的**合计旋转角 R** —— 两者都绕同一根 armature Z 轴，
#      角度直接相加。实测（腕指 LeftHand 骨，胯外缘 = 躯干半宽 0.181）：
#
#         R    上臂离竖直      腕离胯外缘    观感
#         68    22°(向外)       +10.4cm     「端着手臂」
#         84     6°(向外)        +7.6cm      还端着一点
#         92     2°(向外)        +6.8cm    ★ 自然下垂
#        100    10°(向内)        +5.3cm      开始夹
#        110    20°(向内)        +3.9cm      掐腰
#        122    32°(向内)        +1.0cm      手按在肚子上
#
#   ⚠️ 我一开始把 `ARM_DOWN` 单独当成「放下多少度」来调，一路加到 104，
#      又给锁骨叠了 −18，合计 122 —— 手臂于是**拐进身体里**，看起来像掐腰。
#      T-pose 已经给了 90°，所以 `ARM_DOWN` 只要 80 上下，不是 100。
#
#   ★ 这具模型的肩关节在 x=0.267，而躯干半宽只有 0.19 —— 锁骨长 14.8 cm。
#     所以即使手臂完全竖直，手也在胯外 ~7 cm。**这是骨架的比例，不是姿势的错**；
#     再往里收就变成掐腰了。
#
#   ★ 另一个坑：**这具模型「看起来的大臂」大半是锁骨，不是 `LeftArm`。**
#     实测各骨管的顶点（rest 的 x 分布）：
#         锁骨 LeftShoulder  197 顶点  x 0.132~0.265（跨 13.3cm）
#         上臂 LeftArm        87 顶点  x 0.267~0.321（跨  5.4cm）
#         前臂 LeftForeArm   299 顶点  x 0.322~0.386
#         手   LeftHand      450 顶点  x 0.366~0.408
#     锁骨 14.8cm、上臂只有 5.5cm —— 从脖子到肩关节那一整段（人眼认作「大臂/肩」的
#     那块）全是锁骨在管。只转 `LeftArm` 的话那块肉几乎不动，看起来就是「只动了小臂」。
CLAV_DROP = -12.0      # ★ 锁骨下沉（把肩关节压低一点）
ARM_DOWN = 70.0        # ★ 上臂：合计 R = 12 + 70 = 82 → 离竖直 6°(向外)，前臂离躯干 1.9cm
ARM_ELBOW = 14.0       # 手臂自然微屈
RUN_ARM_DOWN = 72.0    # 跑步：上臂略抬，靠屈肘把前臂端到身前


def left_arm(down=ARM_DOWN, swing=0.0, elbow=ARM_ELBOW, wrist=6.0, yaw=-4.0,
             shrug=0.0, clav_back=0.0):
    """**左臂**的一份姿势。右臂用 `mirror()` 由它生成，参数含义完全一致。

    down       上臂从 T-pose 往下放多少度。0 = 平举（T-pose）· 90 = 垂直向下 ·
               **> 90 = 内收（贴向身体）** · **负值 = 举过头**（Air / Flail 用负值）
    swing      上臂前后摆，**+= 往后**（与腿同号，走路时手臂与腿反相）
    elbow      屈肘（+= 屈）
    wrist      手腕
    yaw        上臂绕竖直方向的微调（沿用原来那个 −4）
    shrug      锁骨抬降，**+= 耸肩抬起**
    clav_back  锁骨前后，**+= 后缩**（推东西时得给负值 = 前伸）
    """
    return {
        "shoulder_l": [("Z", CLAV_DROP + shrug), ("Y", clav_back)],
        "arm_l": [("Z", -down), ("Y", yaw), ("X", swing)],
        "forearm_l": [("Y", -elbow)],
        "hand_l": [("Y", -wrist)],
    }


def arms(left_kw, right_kw=None):
    """左臂一份、右臂一份。`right_kw` 用**左臂的参数含义**给，内部自动镜像。"""
    lk = dict(left_kw)
    rk = dict(left_kw if right_kw is None else right_kw)
    return merge(mirror(left_arm(**rk)), left_arm(**lk))


# ★ 基准帧的手臂参数 —— 唯一的默认来源
STAND_ARMS = dict(down=ARM_DOWN, swing=0.0, elbow=ARM_ELBOW, wrist=6.0,
                  yaw=-4.0, shrug=0.0, clav_back=0.0)


def stand_pose():
    """★ **基准帧**：两臂放在身体两侧的静止站姿。

    这是全部片段的第 0 层。导出里对应 `RaccoonStand` 那一段，
    要改「手臂垂在哪儿」就改这里（或改上面的 ARM_DOWN / CLAV_DROP），
    **不要**在别的片段里散着改。
    """
    return merge(arms(STAND_ARMS), {"spine": [("X", 2.0)], "neck": [("X", -1.0)]})


def base_pose(**arm_kw):
    """基准帧 + 手臂参数覆盖（`arm_kw` 的键与 `left_arm()` 一致）。"""
    return merge(arms(dict(STAND_ARMS, **arm_kw)), {"spine": [("X", 2.0)], "neck": [("X", -1.0)]})



# ----------------------------------------------------------------------------
# 片段内容
# ----------------------------------------------------------------------------

def idle_keys():
    """3.00 s（90 帧）循环：呼吸 + 重心左右移 + 头微动。

    呼吸**要走到肩胛上** —— 吸气时锁骨抬起来（`shrug`），上臂跟着微张。
    只做胸腔数值的话，肩那一圈是死的，一眼就能看出是程序生成的。
    """
    def breath(t, shift):
        return merge(
            base_pose(down=ARM_DOWN - 1.5 * t, swing=-2.0 * t, elbow=ARM_ELBOW + 2.0 * t,
                      wrist=6.0, shrug=3.5 * t, clav_back=-1.5 * t),
            {
                "hips": [("X", 1.0 - 1.5 * t), ("Z", 1.6 * shift)],
                "spine": [("X", 2.0 + 1.6 * t), ("Z", 1.0 * shift)],
                "neck": [("X", -1.0 - 1.5 * t)],
                "head": [("X", 1.0 * t), ("Y", -3.0 * shift), ("Z", -1.5 * shift)],
                "upleg_l": [("X", -1.0 * shift), ("Z", -1.0 * shift)],
                "upleg_r": [("X", 1.0 * shift), ("Z", 1.0 * shift)],
                "leg_l": [("X", 4.0 + 1.0 * shift)],
                "leg_r": [("X", 4.0 - 1.0 * shift)],
                "foot_l": [("X", -3.0)],
                "foot_r": [("X", -3.0)],
                "toe_l": [("X", 0.0)],
                "toe_r": [("X", 0.0)],
            },
        )
    return [
        (0,  ground(slide(breath(0.0, 0.0), x=0.0))),
        (16, ground(slide(breath(1.0, 0.5), x=-0.3))),
        (30, ground(slide(breath(0.35, 1.0), x=-0.5))),
        (45, ground(slide(breath(0.0, 0.0), x=0.0))),
        (61, ground(slide(breath(1.0, -0.5), x=0.3))),
        (75, ground(slide(breath(0.35, -1.0), x=0.5))),
        (90, ground(slide(breath(0.0, 0.0), x=0.0))),
    ]


# ---- 走路：经典 4 姿势（contact / down / passing / up）× 2 半步 ---------------

def walk_contact():
    """左脚前脚跟触地，右脚在后脚尖蹬地。手臂与腿反相：左臂在后、右臂在前。"""
    return merge(
        base_pose(),
        arms({"down": ARM_DOWN, "swing": 20.0, "elbow": 22.0, "wrist": 7.0,
              "shrug": -1.5, "clav_back": 2.5},
             {"down": ARM_DOWN, "swing": -20.0, "elbow": 26.0, "wrist": 5.0,
              "shrug": 3.0, "clav_back": -3.0}),
        {
            "hips": [("X", 2.5), ("Y", -4.0), ("Z", 2.0)],
            # ★ 2026-10-01（第七轮）：**脊椎的反向扭转要"转得出去"** —— owner：「脊椎和肩膀没有
            #   随着走路发生扭转、晃动」。实测肩线在世界水平面上的偏航（峰峰值）只有 **1.0°**，
            #   而 X Bot 是 **11.26°**：骨盆转 ±4° 是有的，但 `spine` 的 Y（+4.5°）**正好把它抵消干净**，
            #   于是肩在绝对空间里几乎静止 ⇒ 看上去"肩膀不跟着走"。
            #   处置：`spine` 的 Y 放到 ≈2.2 倍骨盆角（4.5→9.0 / 2→4.5 / 0 / −3→−5.5）。
            #   探针实测肩线偏航 1.46° → **9.13°**（按身高折算的目标 ≈9.5°）。
            #   顺带：前后晃动（肩中点 y 行程）也随之从 7.4 mm 起来 —— 那个量**本来就**是肩线扭转造成的。
            "spine": [("X", 3.0), ("Y", 9.0)],
            "neck": [("X", -2.0)],
            "head": [("X", -1.0)],
            # ★ 2026-10-01（第六轮）：−24 → −22、foot_l 12 → 8 —— 见下方 walk_down 的长注释。
            #   原值让 "contact→down" 这对姿势**不自洽**：大腿从 −24° 摆向 −12°、中途几乎竖直，
            #   而膝盖只弯了一半 ⇒ 腿的**够地距离在中途最长**，纯姿势解出来的髋高比线性高 **8~10 mm**
            #   （实测 318.0 → 322.9 → 308.2，中点是凸的）⇒ `contact_pass` 只能抬髋兜底，
            #   于是刚落地那几帧身体**先升 6 mm 再落 12 mm**，起伏读起来是个"哆嗦"。
            #   扫过 3×3 网格后取这组：凸起 8.3 → **1.0 mm**，总起伏仍 23 mm。
            "upleg_l": [("X", -22.0)],
            "leg_l": [("X", 6.0)],
            "foot_l": [("X", 8.0)],         # 12 → 8：见上，让落地那一段的髋高单调下降
            "toe_l": [("X", 0.0)],
            "upleg_r": [("X", 20.0)],
            "leg_r": [("X", 16.0)],
            "foot_r": [("X", 4.0)],         # 后脚绷起来，脚尖蹬地（踝角由解算器定）
            # ★ 2026-10-01：后脚的**鞋前半截**要真的折起来（owner：「鞋子的前半部分的弯曲程度还不够大」）。
            #   趾骨转 -X = 前段往上折。修复前趾权重糊成一坨（折轴比骨头靠后 49 mm），
            #   **可见折角只有骨角的 ~0.7 倍，而且鞋腰会被拽到地面以下 14.5 mm**（详见 fix_foot_rig）。
            #   权重改成真铰链之后可见折角 ≈ 骨角，所以这里的角度要**整体回调**（原来 -26/-40）。
            "toe_r": [("X", -22.0)],
        },
    )


def walk_down():
    """重心落到左脚上，膝盖吃掉冲击，全身最低。"""
    return merge(
        base_pose(),
        arms({"down": ARM_DOWN, "swing": 13.0, "elbow": 20.0, "shrug": -1.0, "clav_back": 1.5},
             {"down": ARM_DOWN, "swing": -13.0, "elbow": 24.0, "shrug": 2.0, "clav_back": -2.0}),
        {
            "hips": [("X", 3.5), ("Y", -2.0), ("Z", 3.5)],
            "spine": [("X", 4.0), ("Y", 4.5)],
            "neck": [("X", -2.5)],
            "head": [("X", -1.0)],
            # ★ 2026-10-01（第六轮）：**这一拍要真的"全身最低"** —— owner：「走路的动画不对，
            #   参考 x-bot 的走路动画，模型应该是有起伏的」。
            #   实测原来四个姿势的髋高是 `318.0 / 324.5 / 331.2 / 326.8`（contact/down/passing/up）：
            #   **最低点落在 `contact`**，而 `down` 自己的 docstring 写着"全身最低"却没最低；
            #   起伏只有 **13.2 mm（1.57% 身高）**，X Bot 是 **44.0 mm（2.43%）**。
            #   ⚠️ 光把膝盖弯大反而**抬高**髋 —— `foot_l` 写死 −14，小腿一斜鞋底就不平、脚跟扎地被顶起来
            #   （实测 k=26→42 髋从 324.5 一路**升**到 337.5）。**脚踝必须联立：踝 = −(大腿+膝)**，
            #   鞋底才保持水平，膝盖的屈才真的变成"人沉下去"。
            "upleg_l": [("X", -12.0)],
            "leg_l": [("X", 40.0)],         # 26 → 40：膝盖吃掉冲击，人真正沉下去
            "foot_l": [("X", -28.0)],       # −14 → −28 = −(upleg+leg)：鞋底保持水平
            "toe_l": [("X", 0.0)],
            "upleg_r": [("X", 12.0)],
            "leg_r": [("X", 20.0)],
            "foot_r": [("X", 12.0)],
            # ★ 蹬地最高潮那一拍：鞋前半截折得最狠。修好铰链后就是 -30° 的可见折角。
            "toe_r": [("X", -30.0)],
        },
    )


def walk_passing():
    """左腿竖直支撑，右腿屈膝从旁边过。身体开始上升。"""
    return merge(
        base_pose(),
        arms({"down": ARM_DOWN, "swing": -2.0, "elbow": 16.0, "shrug": 0.5},
             {"down": ARM_DOWN, "swing": 2.0, "elbow": 16.0, "shrug": 0.5}),
        {
            "hips": [("X", 2.0), ("Y", 0.0), ("Z", 1.0)],
            "spine": [("X", 2.5), ("Y", 0.0)],
            "neck": [("X", -1.5)],
            "head": [("X", 0.0)],
            "upleg_l": [("X", 4.0)],
            "leg_l": [("X", 4.0)],
            "foot_l": [("X", -8.0)],
            "toe_l": [("X", 0.0)],
            "upleg_r": [("X", -16.0)],
            "leg_r": [("X", 46.0)],
            "foot_r": [("X", -30.0)],       # 摆动腿：放平（踝角仍会被解算器夹住）
            # 摆动中的鞋前半截也留一点折（整只鞋离地后立刻摊平反而显得是块板）
            "toe_r": [("X", -8.0)],
        },
    )


def walk_up():
    """最高点：左腿蹬直，右腿向前伸出去够下一次触地。"""
    return merge(
        base_pose(),
        arms({"down": ARM_DOWN, "swing": -12.0, "elbow": 20.0, "shrug": 2.0, "clav_back": -1.5},
             {"down": ARM_DOWN, "swing": 12.0, "elbow": 22.0, "shrug": -1.0, "clav_back": 1.5}),
        {
            "hips": [("X", 2.0), ("Y", 2.5), ("Z", -0.5)],
            "spine": [("X", 2.5), ("Y", -5.5)],
            "neck": [("X", -1.5)],
            "head": [("X", 0.0)],
            "upleg_l": [("X", 12.0)],
            "leg_l": [("X", 6.0)],
            "foot_l": [("X", -13.0)],       # 累计 +5°：轻微蹬地，不踮脚尖
            # ★ 支撑脚开始蹬地了：鞋前半截从这一拍起就折起来，到 down 那一拍折到 -30°
            "toe_l": [("X", -16.0)],
            "upleg_r": [("X", -22.0)],
            "leg_r": [("X", 26.0)],
            "foot_r": [("X", -12.0)],
            "toe_r": [("X", -5.0)],
        },
    )


def walk_keys(length=30):
    """一个完整周期 = 两个半步。frame 0 = 左脚触地。

    踩地的脚（plant）逐拍不一样，这正是关键：
    contact / down 两只脚都在地上（前脚掌 + 后脚尖），passing / up 只有支撑脚在。
    后脚踝角由解算器定 —— 手填过一次，填出「前脚悬空 7 cm」那个错。

    ⚠️ 第二半步必须写成 `mirror(ground(...))`，**不能**写成
    `ground(mirror(...), plant)`：后者镜像了姿势却没镜像 plant，
    于是 passing / up 那两拍会把**摆动腿**当成支撑腿去种地 ——
    髋高被抬到 0.39（站立才 0.33），人像在踮脚走，支撑脚反倒悬空 4 cm。
    """
    marks = [0, 4, 8, 11] if length == 30 else [0, 2, 5, 7]
    half = length // 2
    poses = [walk_contact(), walk_down(), walk_passing(), walk_up()]
    plants = [("l", "r"), ("l", "r"), ("l",), ("l",)]
    grounded = [ground(poses[i], 0.0, plants[i]) for i in range(4)]
    ks = [(marks[i], grounded[i]) for i in range(4)]
    ks += [(half + marks[i], mirror(grounded[i])) for i in range(4)]
    ks.append((length, grounded[0]))
    return ks


# ---- 跑步：前倾、大摆、有腾空 ------------------------------------------------

def run_contact():
    return merge(
        base_pose(),
        arms({"down": RUN_ARM_DOWN, "swing": 42.0, "elbow": 78.0, "wrist": 8.0,
              "shrug": -2.0, "clav_back": 4.0},
             {"down": RUN_ARM_DOWN, "swing": -42.0, "elbow": 82.0, "wrist": 8.0,
              "shrug": 6.0, "clav_back": -5.0}),
        {
            "hips": [("X", 10.0), ("Y", -7.0), ("Z", 3.0)],
            "spine": [("X", 6.0), ("Y", 8.0)],
            "neck": [("X", -8.0)],
            "head": [("X", -6.0)],
            "upleg_l": [("X", -34.0)],
            "leg_l": [("X", 18.0)],
            "foot_l": [("X", 16.0)],
            "toe_l": [("X", 0.0)],
            "upleg_r": [("X", 30.0)],
            "leg_r": [("X", 54.0)],
            "foot_r": [("X", -30.0)],
            "toe_r": [("X", -15.0)],
        },
    )


def run_down():
    return merge(
        base_pose(),
        arms({"down": RUN_ARM_DOWN, "swing": 26.0, "elbow": 74.0, "shrug": -1.0, "clav_back": 2.5},
             {"down": RUN_ARM_DOWN, "swing": -26.0, "elbow": 78.0, "shrug": 4.0, "clav_back": -3.0}),
        {
            "hips": [("X", 12.0), ("Y", -3.0), ("Z", 4.5)],
            "spine": [("X", 7.0), ("Y", 3.0)],
            "neck": [("X", -9.0)],
            "head": [("X", -6.0)],
            "upleg_l": [("X", -16.0)],
            "leg_l": [("X", 48.0)],
            "foot_l": [("X", -32.0)],
            "toe_l": [("X", 0.0)],
            "upleg_r": [("X", 22.0)],
            "leg_r": [("X", 44.0)],
            "foot_r": [("X", -22.0)],
            "toe_r": [("X", -16.0)],
        },
    )


def run_push():
    """蹬离地面、身体腾起（跑步与走路最大的区别就在这一拍）。"""
    return merge(
        base_pose(),
        arms({"down": RUN_ARM_DOWN, "swing": 0.0, "elbow": 70.0, "shrug": 2.0},
             {"down": RUN_ARM_DOWN, "swing": 0.0, "elbow": 74.0, "shrug": 2.0}),
        {
            "hips": [("X", 9.0), ("Y", 0.0), ("Z", 0.5)],
            "spine": [("X", 6.0), ("Y", 0.0)],
            "neck": [("X", -7.0)],
            "head": [("X", -5.0)],
            "upleg_l": [("X", 22.0)],
            "leg_l": [("X", 16.0)],
            "foot_l": [("X", 4.0)],
            "toe_l": [("X", -18.0)],
            "upleg_r": [("X", -30.0)],
            "leg_r": [("X", 74.0)],
            "foot_r": [("X", -44.0)],
            "toe_r": [("X", 0.0)],
        },
    )


def run_air():
    """腾空：两脚都离地，后腿还在收。"""
    return merge(
        base_pose(),
        arms({"down": RUN_ARM_DOWN, "swing": -30.0, "elbow": 72.0, "shrug": 3.0, "clav_back": -2.0},
             {"down": RUN_ARM_DOWN, "swing": 30.0, "elbow": 76.0, "shrug": 3.0, "clav_back": -2.0}),
        {
            "hips": [("X", 10.0), ("Y", 3.0), ("Z", -1.0)],
            "spine": [("X", 6.0), ("Y", -3.0)],
            "neck": [("X", -8.0)],
            "head": [("X", -6.0)],
            "upleg_l": [("X", 26.0)],
            "leg_l": [("X", 58.0)],
            "foot_l": [("X", -50.0)],
            "toe_l": [("X", 0.0)],
            "upleg_r": [("X", -38.0)],
            "leg_r": [("X", 42.0)],
            "foot_r": [("X", -20.0)],
            "toe_r": [("X", 0.0)],
        },
    )


def run_keys(length=18):
    marks = [0, 3, 6, 8]
    half = length // 2
    # 前三拍左脚踩地；最后一拍腾空
    plants = [("l",), ("l",), ("l",), ()]
    clr = [0.0, 0.0, 0.0, 0.05]
    poses = [run_contact(), run_down(), run_push(), run_air()]
    grounded = [ground(poses[i], clr[i], plants[i]) for i in range(4)]
    ks = [(marks[i], grounded[i]) for i in range(4)]
    ks += [(half + marks[i], mirror(grounded[i])) for i in range(4)]
    ks.append((length, grounded[0]))
    return ks


# ---- 空中：下落 / 被抛出的样子（循环） ---------------------------------------

def air_keys(length=30):
    def flail(t):
        s = math.sin(t * 2.0 * math.pi)
        c = math.cos(t * 2.0 * math.pi)
        return merge(
            base_pose(),
            # 下落时手臂是**举起来**的：down 取负值。
            # （踩过坑：第一版写成 `("Z", -108)`，那是「往下 108°」= 越过垂直、
            #   手交叉到身前 —— 和「举手」正好相反。）
            arms({"down": -32.0 + 6.0 * s, "swing": -14.0 + 10.0 * c, "elbow": 46.0 + 10.0 * s,
                  "wrist": 10.0, "shrug": 12.0, "clav_back": -5.0 + 2.0 * s},
                 {"down": -32.0 - 6.0 * s, "swing": -14.0 - 10.0 * c, "elbow": 46.0 - 10.0 * s,
                  "wrist": 10.0, "shrug": 12.0, "clav_back": -5.0 - 2.0 * s}),
            {
                "hips": [("X", -6.0 + 2.0 * s), ("Z", 2.0 * c)],
                "spine": [("X", -4.0 + 2.0 * s)],
                "neck": [("X", -4.0)],
                "head": [("X", -6.0 + 3.0 * s)],
                "upleg_l": [("X", -26.0 + 8.0 * s)],
                "leg_l": [("X", 50.0 - 8.0 * c)],
                "foot_l": [("X", -24.0)],
                "toe_l": [("X", -5.0)],
                "upleg_r": [("X", -18.0 - 8.0 * s)],
                "leg_r": [("X", 42.0 + 8.0 * c)],
                "foot_r": [("X", -24.0)],
                "toe_r": [("X", -5.0)],
            },
        )
    n = 6
    # 全身腾空：plant=() 表示不踩地，改解「整个网格最低点 = 22 cm」
    ks = [(int(round(length * i / n)), ground(flail(i / float(n)), 0.22, ())) for i in range(n)]
    ks.append((length, ground(flail(0.0), 0.22, ())))
    return ks


# ---- 挣扎：被人抓住 / 挂在墙上的样子（循环） ---------------------------------

def flail_keys(length=36):
    def struggle(t):
        a = math.sin(t * 2.0 * math.pi)
        b = math.sin(t * 4.0 * math.pi)
        return merge(
            base_pose(),
            # 被人拎起来：手臂高举过头、抓挠（down 取负值 = 举过水平线）
            arms({"down": -58.0 + 10.0 * a, "swing": -10.0 + 22.0 * b, "elbow": 62.0 + 20.0 * a,
                  "wrist": 12.0 + 8.0 * b, "shrug": 18.0, "clav_back": -6.0},
                 {"down": -58.0 - 10.0 * a, "swing": -10.0 - 22.0 * b, "elbow": 62.0 - 20.0 * a,
                  "wrist": 12.0 - 8.0 * b, "shrug": 18.0, "clav_back": -6.0}),
            {
                "hips": [("X", 4.0 + 5.0 * a), ("Y", 6.0 * b), ("Z", 3.0 * a)],
                "spine": [("X", 2.0 - 4.0 * a), ("Y", -5.0 * b)],
                "neck": [("X", -2.0 + 3.0 * a)],
                "head": [("X", -4.0 * a), ("Y", 8.0 * b)],
                "upleg_l": [("X", -14.0 - 16.0 * a)],
                "leg_l": [("X", 44.0 + 20.0 * b)],
                "foot_l": [("X", -16.0 - 8.0 * a)],
                "toe_l": [("X", -6.0)],
                "upleg_r": [("X", -14.0 + 16.0 * a)],
                "leg_r": [("X", 44.0 - 20.0 * b)],
                "foot_r": [("X", -16.0 + 8.0 * a)],
                "toe_r": [("X", -6.0)],
            },
        )
    n = 8
    # 被人拎着：脚不沾地
    ks = [(int(round(length * i / n)),
           slide(ground(struggle(i / float(n)), 0.06, ()), x=1.5 * math.sin(4.0 * math.pi * i / n)))
          for i in range(n)]
    ks.append((length, slide(ground(struggle(0.0), 0.06, ()), x=0.0)))
    return ks


# ---- 一次性动作 --------------------------------------------------------------

def land_keys():
    """落地缓冲 → 站起来。0.60 s（18 帧）。

    蹲下去多少**不是手填的**：写的是腿的角度（大腿前摆 + 屈膝），
    髋的高低由种地算出来 —— 腿弯得越狠，人就自动越低。
    脚踝取 −(大腿 + 膝) 让脚掌保持水平，踩在地上不打滑。
    """
    def absorb(k):
        thigh = -50.0 * k
        knee = 8.0 + 92.0 * k
        ankle = -(thigh + knee)          # 脚掌始终水平
        return merge(
            base_pose(),
            arms({"down": ARM_DOWN - 10.0 * k, "swing": -24.0 * k, "elbow": ARM_ELBOW + 26.0 * k,
                  "wrist": 6.0, "shrug": -5.0 * k, "clav_back": 3.0 * k}),
            {
                "hips": [("X", 6.0 + 14.0 * k), ("Z", 1.5 * k)],
                "spine": [("X", 2.0 + 14.0 * k)],
                "neck": [("X", -2.0 - 6.0 * k)],
                "head": [("X", -2.0 - 8.0 * k)],
                "upleg_l": [("X", thigh)],
                "leg_l": [("X", knee)],
                "foot_l": [("X", ankle)],
                "toe_l": [("X", -3.0)],
                "upleg_r": [("X", thigh)],
                "leg_r": [("X", knee)],
                "foot_r": [("X", ankle)],
                "toe_r": [("X", -3.0)],
            },
        )
    return [
        (0,  slide(ground(absorb(0.0), 0.02))),   # 刚触地：脚还没吃上力
        (3,  ground(absorb(1.0))),                # 蹲到最深
        (7,  ground(absorb(0.62))),
        (11, ground(absorb(0.24))),
        (18, ground(absorb(0.0))),
    ]


def push_keys():
    """双手往前推。0.80 s（24 帧）。"""
    def shove(k):
        """k: 0 = 收手准备，1 = 推到最远。负值 = 往后拉一点蓄力。"""
        return merge(
            base_pose(),
            # 推：**往前伸靠 `swing`，不是靠 `down`**。
            # `down` 走的是**冠状面**（身体左右那一面），把它压到 0 得到的是
            # 「两臂平举张开」—— 那是投降/端盘子，不是推。（这一版之前就是这么错的：
            # 推到最远时上臂离竖直 80°，其中一大半是**侧向**张开。）
            # 正确做法：上臂留在身体旁边（down 略减），绕 X 轴**往前转** ~76°。
            # 锁骨前伸（clav_back 取负 = 肩膀往前送）、略微耸肩。
            arms({"down": ARM_DOWN - 14.0 * k, "swing": -76.0 * k, "elbow": ARM_ELBOW - 8.0 * k,
                  "wrist": 6.0 - 4.0 * k, "shrug": 6.0 * k, "clav_back": -9.0 * k}),
            {
                "hips": [("X", 2.0 + 12.0 * k)],
                "spine": [("X", 2.0 + 8.0 * k)],
                "neck": [("X", -1.0 - 5.0 * k)],
                "head": [("X", -1.0 - 4.0 * k)],
                # 前腿弓、后腿蹬 —— 脚掌保持水平
                "upleg_l": [("X", -2.0 * k)],
                "leg_l": [("X", 5.0 + 20.0 * k)],
                "foot_l": [("X", 2.0 * k - 5.0 - 16.0 * k)],
                "upleg_r": [("X", 3.0 * k)],
                "leg_r": [("X", 5.0 + 4.0 * k)],
                "foot_r": [("X", -3.0 * k - 5.0 - 2.0 * k)],
                "toe_l": [("X", 0.0)],
                "toe_r": [("X", 0.0)],
            },
        )
    return [
        (0,  ground(shove(0.0))),
        (5,  ground(shove(-0.20))),
        (10, ground(shove(1.0))),
        (16, ground(shove(0.70))),
        (24, ground(shove(0.0))),
    ]


def wave_keys():
    """右手举起来挥两下。1.60 s（48 帧）。"""
    def wave(raise_k, swing):
        return merge(
            base_pose(),
            # 右臂**举过水平线**（down 从 84 一路到 −28），锁骨跟着耸起来 —— 举手不耸锁骨是假的。
            arms({"down": ARM_DOWN, "swing": 3.0 * raise_k, "elbow": ARM_ELBOW},
                 {"down": ARM_DOWN - 128.0 * raise_k, "swing": -8.0 * raise_k,
                  "elbow": ARM_ELBOW + 42.0 * raise_k, "wrist": 6.0 + 4.0 * raise_k,
                  "shrug": 14.0 * raise_k, "clav_back": -5.0 * raise_k}),
            {
                "hips": [("Z", 1.2 * raise_k)],
                "spine": [("X", 2.0), ("Y", -3.0 * raise_k)],
                "neck": [("Y", 4.0 * raise_k)],
                "head": [("X", -2.0 * raise_k), ("Y", 6.0 * raise_k), ("Z", -3.0 * raise_k)],
                # 挥动：手腕 + 前臂整体左右摆。
                # 右臂的屈肘是 **+Y**（`left_arm` 给的是 −elbow，`mirror()` 把 Y 取反）——
                # 写成负号就成了反向折肘，这里踩过一次。
                "forearm_r": [("Y", ARM_ELBOW + 42.0 * raise_k), ("Z", 26.0 * swing)],
                "hand_r": [("Y", 6.0 + 4.0 * raise_k), ("Z", 14.0 * swing)],
                "upleg_l": [("X", -2.0 * raise_k)],
                "leg_l": [("X", 4.0)],
                "foot_l": [("X", -2.0 + 2.0 * raise_k)],
                "upleg_r": [("X", 2.0 * raise_k)],
                "leg_r": [("X", 4.0)],
                "foot_r": [("X", -2.0 - 2.0 * raise_k)],
                "toe_l": [("X", 0.0)],
                "toe_r": [("X", 0.0)],
            },
        )
    return [
        (0,  ground(wave(0.0, 0.0))),
        (8,  ground(wave(1.0, 0.0))),
        (16, ground(wave(1.0, 1.0))),
        (24, ground(wave(1.0, -1.0))),
        (32, ground(wave(1.0, 1.0))),
        (40, ground(wave(0.55, 0.0))),
        (48, ground(wave(0.0, 0.0))),
    ]


# ----------------------------------------------------------------------------
# ★ 基准帧
# ----------------------------------------------------------------------------

def stand_keys():
    """只有一帧：两臂放在身体两侧的静止站姿。

    单独立成一段，是为了让「基准姿势」成为一个**能看、能选、能手动改的帧**，
    而不是藏在每个片段里的那几行角度。改 `stand_pose()` 或
    `ARM_DOWN` / `CLAV_DROP`，八段动画会一起跟着变。
    """
    return [(0, ground(stand_pose())), (3, ground(stand_pose()))]


# ----------------------------------------------------------------------------
# 片段表
# ----------------------------------------------------------------------------

CLIP_SPECS = [
    # (name, length_frames, loop, keys_fn, 用途)
    ("RaccoonStand",  3, False, stand_keys, "★ 基准帧：两臂放在身体两侧（静止，用来对照/手改）"),
    ("RaccoonIdle",  90, True,  idle_keys,  "站立待机"),
    ("RaccoonWalk",  30, True,  lambda: walk_keys(30), "慢走（1.00 s 一个完整左右步）"),
    ("RaccoonRun",   18, True,  lambda: run_keys(18),  "跑（0.60 s，含腾空）"),
    ("RaccoonAir",   30, True,  lambda: air_keys(30),  "腾空 / 下落"),
    ("RaccoonFlail", 36, True,  lambda: flail_keys(36),"被抓住时挣扎"),
    ("RaccoonLand",  18, False, land_keys,  "落地缓冲（一次性）"),
    ("RaccoonPush",  24, False, push_keys,  "双手推（一次性）"),
    ("RaccoonWave",  48, False, wave_keys,  "挥手（一次性）"),
]


# ----------------------------------------------------------------------------
# 写关键帧
# ----------------------------------------------------------------------------

def eval_world_z():
    """解算后的网格顶点的世界 Z（米）。"""
    dg = bpy.context.evaluated_depsgraph_get()
    ob = bpy.data.objects[MESH_NAME]
    ev = ob.evaluated_get(dg)
    me = ev.to_mesh()
    n = len(me.vertices)
    co = np.empty(n * 3, dtype=np.float64)
    me.vertices.foreach_get("co", co)
    co = co.reshape(n, 3)
    mw = np.array(ob.matrix_world, dtype=np.float64)
    z = co @ mw[2, :3] + mw[2, 3]
    ev.to_mesh_clear()
    return z


def zmin(which, part="foot"):
    """`which` = "mesh" 或 "l"/"r"；`part` = "foot"（整只脚）/ "ball"（跟→球）/ "toe"（鞋前半截的鞋底）。"""
    z = eval_world_z()
    if which == "mesh":
        return float(z.min())
    m = MASKS[part][which]
    return float(z[m].min()) if m.any() else float(z.min())


def build_masks():
    """脚部顶点掩码（按**权重和**取；鞋底那一圈常被小腿骨主导，只看主导骨会漏）。

    三套，各有各的用处（⚠️ 2026-10-01 更正：**当前只有 `foot` 有调用点**，
    另两套是给一版没落地的方案留的，见文件顶部那段更正说明）：
      `foot` 整只脚（foot+toe 权重和 > 0.5）—— 髋找脚 + 踝找地 + 穿地兜底，**唯一在用的**；
      `ball` **鞋底从脚跟到折痕**那一段（趾权重 < 0.5）—— 设想的「踝解算 + 髋找脚」，**未接线**。
             它就是这只鞋的「支撑面」：脚往前卷的时候，这一段里最低的那个点
             （先是跟、后来是折痕）先着地，所以「转出去够地」自然就转成**绕着折痕滚**；
      `toe`  折痕往前那半截的**鞋底**（趾权重 > 0.5）—— **脚趾解算用**：
             把它折到刚好贴地，于是鞋前半截像真鞋那样平铺在地上。
    """
    ob = bpy.data.objects[MESH_NAME]
    name_of = {g.index: g.name for g in ob.vertex_groups}
    nv = len(ob.data.vertices)
    mw = ob.matrix_world
    foot = {}
    ball = {}
    toe = {}
    for key, groups in FOOT_GROUPS.items():
        wanted = set(groups)
        toe_names = set(g for g in groups if "Toe" in g)
        w = np.zeros(nv, dtype=np.float64)
        wt = np.zeros(nv, dtype=np.float64)
        sole = np.zeros(nv, dtype=bool)
        for i, v in enumerate(ob.data.vertices):
            s = 0.0
            st = 0.0
            for ge in v.groups:
                nm = name_of.get(ge.group)
                if nm in wanted:
                    s += ge.weight
                if nm in toe_names:
                    st += ge.weight
            w[i] = s
            wt[i] = st
            sole[i] = (mw @ v.co).z < 0.006
        foot[key] = w > 0.5
        # ⚠️ `w > 0.5` 这一项不能省 —— 省掉之后**两只脚的鞋底会同时落进两个掩码**，
        #    于是「量左脚」量到的其实是右脚（实测：解右脚踝，左脚的读数跟着变 ✗）。
        ball[key] = sole & (w > 0.5) & (wt < 0.5)
        toe[key] = sole & (w > 0.5) & (wt > 0.5)
    return {"foot": foot, "ball": ball, "toe": toe}


def clear_pose(arm):
    for pb in arm.pose.bones:
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = Quaternion((1.0, 0.0, 0.0, 0.0))
        pb.location = Vector((0.0, 0.0, 0.0))
        pb.scale = Vector((1.0, 1.0, 1.0))


def fix_backpack_rig(arm, mesh_obj, material_keyword="书包", target="mixamorig:Spine", margin=0.03):
    """★ 2026-10-01：把**背包**整体绑到一根骨上 —— owner：「小浣熊背后的背包不要跟着形变」。

    ## 第一版错在哪（owner 第二轮反馈：「背包固定现在只做了底部，背包整体还是会形变」）

    第一版按**材质名**选（名字里含「书包」），只绑到了 **2133 个顶点**。
    真因：**这只背包不是单一材质。** 用 `ob.ray_cast` 从正后方沿 −Y 打射线，
    z 0.32→0.56 逐格量出来的层序（括号内是命中点的世界 y）：

        z 0.44–0.56   身2(+0.084) > 身2(+0.073) > 身2(−0.028) > 身1(−0.034) > …
        z 0.40–0.44   ← 书包 与 身2 的交界（同一格两种都命中）
        z 0.32–0.40   书包(+0.091) / 表(+0.100) > 身2(+0.076) > 身2(−0.025) > 身1(−0.028)

    也就是说：

        **包的下半外皮 = 「书包皮革」；包的上半外皮 = 「身体.002」**（Tripo 自动分块
        把这个上盖分进了「身体」这个名字底下）；扣件 = 「手环手表」；内衬 = 「身体.002」。

    而 `身体.002` 那 1693 个顶点里 **1038 个的权重在 `mixamorig:Neck`** ——
    于是「下一半绑 Spine、上一半绑 Neck」，包被**两根骨撕成两半**：
    下半个硬了、上半个跟着脖子扭 ⇒ owner 看到的正是「只做了底部，整体还是形变」。

    ## 这一版怎么选（**按几何，不按名字**）

      ① 用了「书包」材质的所有面 → 它们的顶点（左右肩带里有一条**与身体共顶点**，不能丢）；
      ② **完全落在①的世界包围盒（外扩 `margin`）里的连通块** → 整块顶点。

    ①保证与身体纠缠的那几块不漏，②保证名字叫错的那些块（上盖 / 内衬 / 扣件）不漏。
    实测选中：书包 2133 + 身体.002 1213（上盖 679 + 内衬 534）+ 手环手表**躯干上的** 1134
    （后扣 167/152/150/150、胸扣 125、侧扣 121/75、胸前带扣 97×2），
    共 ≈ 4480 顶点；而**手腕上**那对手环手表（x ±0.34–0.38）、腕带（`手环`）、
    手（`左边`）、尾巴、身体、腿、头**全部拒之门外**（它们的包围盒都伸出了这个盒子）。

    做法：这些顶点权重整体改成 100% `target`，并从**其它所有 `mixamorig:` 骨组**里移除。
    为什么目标是 Spine：背包就背在上背；绑死之后它仍跟着脊椎**移动**（它是背在身上的），
    但**不再被拉伸/扭曲**。

    返回的 report 里有 `islands`（选中的连通块）与 `skipped`（与包相交但被排除的块）——
    下次再有人说「只做了一半」，先看这两行。
    """
    report = {"material": None, "verts": 0, "bones_before": {}, "core_verts": 0,
              "box": None, "islands": [], "skipped": []}
    me = mesh_obj.data
    slots = [i for i, m in enumerate(me.materials) if m is not None and material_keyword in m.name]
    if not slots:
        return report
    report["material"] = " / ".join(me.materials[i].name for i in slots)

    name_of = {g.index: g.name for g in mesh_obj.vertex_groups}
    mat_name = [m.name if m is not None else "?" for m in me.materials]
    mw = mesh_obj.matrix_world
    nv = len(me.vertices)

    def world(vi):
        return mw @ me.vertices[vi].co

    # ① 核心：用了书包材质的所有面的顶点
    core = set()
    for p in me.polygons:
        if p.material_index in slots:
            core.update(int(v) for v in p.vertices)
    report["core_verts"] = len(core)
    if not core:
        return report
    xs = []; ys = []; zs = []
    for vi in core:
        w = world(vi)
        xs.append(w.x); ys.append(w.y); zs.append(w.z)
    box = (min(xs) - margin, max(xs) + margin,
           min(ys) - margin, max(ys) + margin,
           min(zs) - margin, max(zs) + margin)
    report["box"] = "x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]" % box

    # 连通块（并查集走边）
    parent = list(range(nv))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for e in me.edges:
        ra, rb = find(e.vertices[0]), find(e.vertices[1])
        if ra != rb:
            parent[rb] = ra
    members = {}
    for i in range(nv):
        members.setdefault(find(i), []).append(i)
    isl_mats = {}
    for p in me.polygons:
        r = find(p.vertices[0])
        isl_mats.setdefault(r, set()).add(mat_name[p.material_index])

    vids = set(core)
    for r, mem in sorted(members.items(), key=lambda kv: -len(kv[1])):
        mxs = []; mys = []; mzs = []
        for vi in mem:
            w = world(vi)
            mxs.append(w.x); mys.append(w.y); mzs.append(w.z)
        bb = (min(mxs), max(mxs), min(mys), max(mys), min(mzs), max(mzs))
        inside = (bb[0] >= box[0] and bb[1] <= box[1] and
                  bb[2] >= box[2] and bb[3] <= box[3] and
                  bb[4] >= box[4] and bb[5] <= box[5])
        touches = any(vi in core for vi in mem)
        if inside:
            vids.update(mem)
            report["islands"].append("%d 顶点 %s bbox x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]%s" % (
                len(mem), "+".join(sorted(isl_mats.get(r, ["?"]))),
                bb[0], bb[1], bb[2], bb[3], bb[4], bb[5], "  ← 含书包面" if touches else ""))
        elif touches:
            report["skipped"].append("%d 顶点 %s bbox x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]（伸出盒外，只取其中的书包面顶点）" % (
                len(mem), "+".join(sorted(isl_mats.get(r, ["?"]))),
                bb[0], bb[1], bb[2], bb[3], bb[4], bb[5]))

    vids = sorted(vids)
    report["verts"] = len(vids)
    if not vids:
        return report
    for vi in vids:
        for g in me.vertices[vi].groups:
            if g.weight <= 0.001:
                continue
            n = name_of.get(g.group, "?")
            report["bones_before"][n] = report["bones_before"].get(n, 0) + 1

    if target not in mesh_obj.vertex_groups:
        return report
    mesh_obj.vertex_groups[target].add(vids, 1.0, 'REPLACE')
    for g in mesh_obj.vertex_groups:
        if g.name == target or not g.name.startswith("mixamorig:"):
            continue
        g.remove(vids)

    after = {}
    for vi in vids:
        for g in me.vertices[vi].groups:
            if g.weight > 0.001:
                n = name_of.get(g.group, "?")
                after[n] = after.get(n, 0) + 1
    report["bones_after"] = after
    return report


def calibrate(arm):
    """实测「髋骨局部 +Y 挪 1.0」→「网格最低点抬高多少米」。"""
    global UP_PER_LOCAL, MASKS
    hips = arm.pose.bones[BONES["hips"]]
    clear_pose(arm)
    bpy.context.view_layer.update()
    z0 = eval_world_z().min()
    hips.location = Vector((0.0, 1.0, 0.0))
    bpy.context.view_layer.update()
    z1 = eval_world_z().min()
    clear_pose(arm)
    bpy.context.view_layer.update()
    UP_PER_LOCAL = float(z1 - z0)
    MASKS = build_masks()
    return UP_PER_LOCAL


# ----------------------------------------------------------------------------
# ★ 骨架修正：把肩关节挪回身体边上 + 按距离重分手臂权重
# ----------------------------------------------------------------------------
#
# 这具模型（Tripo 自动绑的）手臂骨头比例是坏的：
#
#     锁骨 LeftShoulder   14.78 cm   x 0.120 → 0.267
#     上臂 LeftArm         5.55 cm   x 0.267 → 0.322
#     前臂 LeftForeArm     5.71 cm   x 0.322 → 0.377
#     手   LeftHand        3.33 cm   x 0.377 → 0.410
#
# 锁骨比整条手臂还长 —— **肩关节（LeftArm 的 head）被放到了 x=0.267，
# 而躯干半宽只有 0.19**。于是：
#
#   1. 视觉上「大臂」那一段（x 0.13~0.27，跨 9 cm）的蒙皮是绑在**锁骨**上的，
#      而 `LeftArm` 只管 3.7 cm —— **只转 `LeftArm` 那块肉几乎不动**，
#      看起来就是「手臂没动」。
#   2. 手臂垂下来时肩关节离身体太远，手够不到身侧。
#
# 修法分两步，两步都**不会改变静止时的网格**（Blender 的骨架在 rest 时形变恒等，
# 挪骨头只改「怎么动」，不改「长什么样」）：
#
#   ① 把肩关节沿锁骨轴挪到 `SHOULDER_JOINT_X = 19.5`（刚好在躯干外沿）。
#      锁骨 14.78 → 7.75 cm，上臂 5.55 → 12.70 cm —— 比例正常了。
#      ⚠️ 只挪 head 不挪 tail，所以骨头的**方向不变** → 局部旋转系不变
#      （`bone_quat` 是从 `matrix_local` 现算轴的，本来就跟着骨架走）。
#   ② 手臂链（锁骨/上臂/前臂/手/手指）的权重按**到骨段的距离**反比重新分配
#      （1/d³ 归一），保留各顶点原有的「手臂链总权重」，这样同一条手臂内部
#      过渡是连续的，也不会碰到躯干/腿的权重。
#
# 实测：改完只转 `LeftArm`，可见的大臂区域确实跟着走（不是只有小臂动）。

SHOULDER_JOINT_X = 19.5      # 肩关节在 armature 空间的 x（厘米）；躯干半宽 ≈ 19

ARM_CHAINS = {
    "Left": ["LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
             "LeftHandIndex2", "LeftHandIndex4"],
    "Right": ["RightShoulder", "RightArm", "RightForeArm", "RightHand",
              "RightHandIndex1", "RightHandIndex4"],
}


def _seg_dist(P, A, B):
    """点到线段的距离（向量化）。"""
    AB = np.array(B) - np.array(A)
    den = float(AB @ AB)
    if den < 1e-12:
        return np.linalg.norm(P - np.array(A), axis=1)
    t = np.clip((P - np.array(A)) @ AB / den, 0.0, 1.0)
    return np.linalg.norm(P - (np.array(A) + t[:, None] * AB), axis=1)


def fix_arm_rig(arm, mesh_obj, joint_x=SHOULDER_JOINT_X, power=3.0):
    """把肩关节挪回身体边上，并按距离重分手臂链的蒙皮权重。返回一份读数。"""
    clear_pose(arm)
    bpy.context.view_layer.update()
    before = eval_world_z()          # 只用来确认 z 没变；完整比对见下

    # ---- ① 挪肩关节（只改锁骨 tail；LeftArm 的 head 连着它，会跟着走）----
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='EDIT')
    for side, sgn in (("Left", 1.0), ("Right", -1.0)):
        eb = arm.data.edit_bones["mixamorig:" + side + "Shoulder"]
        eb.tail = Vector((joint_x * sgn, eb.tail.y, eb.tail.z))
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.update()
    clear_pose(arm)
    bpy.context.view_layer.update()

    # ---- ② 重分手臂链权重 ----
    nv = len(mesh_obj.data.vertices)
    for side, names in ARM_CHAINS.items():
        segs = []
        for n in names:
            b = arm.data.bones["mixamorig:" + n]
            segs.append((np.array(arm.matrix_world @ b.head_local),
                         np.array(arm.matrix_world @ b.tail_local)))
        idxs = {n: mesh_obj.vertex_groups["mixamorig:" + n].index for n in names}
        vgs = {n: mesh_obj.vertex_groups["mixamorig:" + n] for n in names}
        wmap = {n: np.zeros(nv) for n in names}
        for i, v in enumerate(mesh_obj.data.vertices):
            for ge in v.groups:
                for n, gi in idxs.items():
                    if ge.group == gi:
                        wmap[n][i] = ge.weight
        tot = np.zeros(nv)
        for n in names:
            tot += wmap[n]
        sel = np.where(tot > 0.02)[0]
        if len(sel) == 0:
            continue
        W = eval_world_xyz()
        D = np.stack([_seg_dist(W[sel], a, b) for a, b in segs], axis=1)
        Wt = 1.0 / np.power(D + 1e-4, power)
        Wt /= Wt.sum(axis=1, keepdims=True)
        for n in names:
            vgs[n].add(sel.tolist(), 0.0, 'REPLACE')
        for j, n in enumerate(names):
            for k, i in enumerate(sel):
                w = float(Wt[k, j] * tot[i])
                if w > 1e-4:
                    vgs[n].add([int(i)], w, 'REPLACE')

    bpy.context.view_layer.update()
    clear_pose(arm)
    bpy.context.view_layer.update()

    lens = {}
    for n in ("LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand"):
        lens[n] = round(arm.data.bones["mixamorig:" + n].length, 2)
    return lens


def eval_world_xyz():
    """解算后网格顶点的世界坐标 (N,3)。"""
    dg = bpy.context.evaluated_depsgraph_get()
    ob = bpy.data.objects[MESH_NAME]
    ev = ob.evaluated_get(dg)
    me = ev.to_mesh()
    n = len(me.vertices)
    co = np.empty(n * 3, dtype=np.float64)
    me.vertices.foreach_get("co", co)
    co = co.reshape(n, 3)
    mw = np.array(ob.matrix_world, dtype=np.float64)
    out = co @ mw[:3, :3].T + mw[:3, 3]
    ev.to_mesh_clear()
    return out


def _foot_blend(z):
    """腿/靴过渡用的缩放权重：靴筒顶以下 = 1，膝以下（FOOT_BLEND_ZERO）以上 = 0，中间 smoothstep。"""
    if z <= FOOT_BLEND_TOP:
        return 1.0
    if z >= FOOT_BLEND_ZERO:
        return 0.0
    u = (z - FOOT_BLEND_TOP) / (FOOT_BLEND_ZERO - FOOT_BLEND_TOP)
    return 1.0 - u * u * (3.0 - 2.0 * u)


def fix_foot_scale(arm, mesh_obj, target=FOOT_TARGET_LEN):
    """把两只鞋**几何 + 骨骼一起**等比缩到 `target` 长。返回读数。

    为什么缩：鞋长 283 mm = 身高的 **33.5%**，而 X Bot 是 13.0% —— 脚相对身体大 2.6 倍，
    走路时整只鞋扫过的弧度也是同量级的大。owner 定档「关节 + 鞋几何一起缩到半程」，
    所以这里连**骨头的落点**一起改（踝高 130 → ~92 mm、球 −277 → ~−242 mm）。

    ★ 三个约束（都不是想当然，是量出来逼出来的）
    -------------------------------------------------
    1. **等比**（x/y/z 同一个 k）。只缩长度会得到"又短又高的桶"。
    2. **锚点 = 该侧脚踝正下方的地面点**。鞋底因此仍然贴在 z=0，鞋不会漂起来。
    3. **只按 z 做平滑过渡**（`_foot_blend`），不能按"权重属于 Foot/Toe 的顶点"整块缩 ——
       实测靴筒顶在 **z≈180**、膝在 **z=204**，中间那张曲面是**连续的同一张**，
       整块缩会在靴筒和小腿之间**撕开口子**。按 z 过渡则曲面连续，代价是膝下那 30 mm 被拉长。
       另外只作用于 `y < FOOT_MIN_Y`：**尾巴**垂到 z≈140，不排除会被误缩。

    **幂等**：k = target / 当前鞋长；缩到位之后当前鞋长 = target ⇒ k = 1 ⇒ 再跑不动。
    ⚠️ 必须在 `fix_foot_rig()` **之前**跑：那个函数的铰链是"以趾骨根为轴"，
    而趾骨根就是被本函数搬走的。
    """
    mw = mesh_obj.matrix_world
    iw = mw.inverted()
    aw = arm.matrix_world
    awi = aw.inverted()
    report = {"target_len_mm": round(target * 1000.0, 2)}

    # ---- ① 量：每侧鞋底长度 + 该侧的锚点（**踝**正下方） ----
    #   ⚠️ 锚点取 `LeftFoot.head`（踝），**不是** `ToeBase.head`（球）—— 上一版取错了骨头，
    #      整只鞋绕着球缩，踝的位置也就跟着不对。
    info = {}
    for side, sgn in (("Left", 1.0), ("Right", -1.0)):
        ankle_w = aw @ arm.data.bones["mixamorig:" + side + "Foot"].head_local
        ys = []
        for v in mesh_obj.data.vertices:
            p = mw @ v.co
            if p.y < FOOT_MIN_Y and p.z < 0.006 and p.x * sgn > 0.0:
                ys.append(p.y)
        if not ys:
            raise RuntimeError("量不到 %s 的鞋底顶点" % side)
        info[side] = (ankle_w, max(ys) - min(ys), min(ys), max(ys))
        report[side] = {"len_before_mm": round((max(ys) - min(ys)) * 1000.0, 2),
                        "ankle_z_before_mm": round(ankle_w.z * 1000.0, 2)}

    # ---- ② 缩几何 ----
    for side, sgn in (("Left", 1.0), ("Right", -1.0)):
        ankle_w, L, y0, y1 = info[side]
        k = min(1.0, target / L)
        report[side]["k"] = round(k, 6)
        if k >= 1.0 - 1e-9:
            continue
        A = Vector((ankle_w.x, ankle_w.y, 0.0))
        moved = 0
        for v in mesh_obj.data.vertices:
            p = mw @ v.co
            if p.y >= FOOT_MIN_Y or p.x * sgn <= 0.0:
                continue
            s = _foot_blend(p.z)
            if s <= 1e-6:
                continue
            q = A + (p - A) * k
            v.co = iw @ (p + (q - p) * s)
            moved += 1
        report[side]["verts_scaled"] = moved

    bpy.context.view_layer.update()

    # ---- ③ 缩骨骼（同一套锚点与 k；用同一个 z 过渡，保证与几何一致） ----
    #   ★ **先把要动的端点全部读成快照，再统一写**。
    #     `LeftFoot` 与 `LeftLeg` 是**连接**的（`use_connect=True`）⇒ 两者共用"踝"那一个点。
    #     就地边读边改会把踝**缩两次**：先写 Foot.head，随后读 Leg.tail 拿到的是**已经缩过**的值，
    #     再缩一次 —— 实测踝 130 → **63.5 mm**，而正确值是 90.9（=130×0.699）。
    #     （`ToeBase` 是 `use_connect=False`，头和 Foot.tail 是两个独立点，各缩一次正好重合。）
    new_pts = {}
    for side in ("Left", "Right"):
        ankle_w, L, y0, y1 = info[side]
        k = min(1.0, target / L)
        A = Vector((ankle_w.x, ankle_w.y, 0.0))
        for bn in ("mixamorig:" + side + "Foot", "mixamorig:" + side + "ToeBase",
                   "mixamorig:" + side + "Leg"):
            b = arm.data.bones[bn]
            for attr, w in (("head", aw @ b.head_local), ("tail", aw @ b.tail_local)):
                s = _foot_blend(w.z)
                if s <= 1e-6:
                    new_pts[(bn, attr)] = awi @ w
                else:
                    q = A + (w - A) * k
                    new_pts[(bn, attr)] = awi @ (w + (q - w) * s)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='EDIT')
    for (bn, attr), v in new_pts.items():
        setattr(arm.data.edit_bones[bn], attr, v)
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.update()

    # ---- ④ 读数：缩完的骨长与鞋长 ----
    for side in ("Left", "Right"):
        fb = arm.data.bones["mixamorig:" + side + "Foot"]
        tb = arm.data.bones["mixamorig:" + side + "ToeBase"]
        h = aw @ fb.head_local
        ys = []
        for v in mesh_obj.data.vertices:
            p = mw @ v.co
            if p.y < FOOT_MIN_Y and p.z < 0.006 and (p.x > 0.0) == (side == "Left"):
                ys.append(p.y)
        L = max(ys) - min(ys)
        report[side].update({
            "len_after_mm": round(L * 1000.0, 2),
            "ankle_z_after_mm": round(h.z * 1000.0, 2),
            "foot_bone_mm": round((aw @ fb.tail_local - h).length * 1000.0, 2),
            "toe_bone_mm": round(((aw @ tb.tail_local) - (aw @ tb.head_local)).length * 1000.0, 2),
        })
    return report


def retarget_foot_joints(arm, mesh_obj, ankle_frac=ANKLE_HEIGHT_FRAC, toe_frac=TOE_SEG_FRAC):
    """把踝降到人体比例、把"球"摆到 X Bot 那个分节比例上。**只动骨骼，网格一字不改。**

    为什么：owner 要求「鞋做成类似 x-bot 脚的分节结构，但不要分开渲染、保持整体外观」。
    实测两具骨架的脚**拓扑其实一样**（都是 `Foot` → `ToeBase` 两段形变骨），
    差的是**两段的相对长度**：

        X Bot   ：踝高 4.84% 身高 · 趾段 39.3% 鞋长
        浣熊(旧)：踝高 10.7% 身高 · 趾段 27.2% 鞋长

    所以"做成一样的结构"= 把这两个比例对齐，**不需要也不应该去切网格**。

    落点
    ----
    * **踝 z** = `ankle_frac` × 身高，y/x 保持不动（踝的 y 是腿的下落点，动它会改站姿）。
    * **球 y** = 鞋尖 y + `toe_frac` × 鞋长。
      旁证：缩放后靴筒的"折痕"（上表面那个台阶）实测落在 **y≈−225**，
      而按 X Bot 比例算出来是 **y≈−215** —— 两条独立线索对得上，不是硬凑。
    * 球的 x/z 保持不动（它本来就贴在鞋底线上）。

    幂等：全部是**绝对定位**（不是相对上一次的位置挪），跑多少遍都一样。
    ⚠️ 必须在 `fix_foot_rig()` **之前**跑 —— 那个函数按"趾骨根"切铰链，
    趾骨根就是这里搬走的。也必须在 `fix_foot_scale()` **之后**（要量缩完的鞋长）。
    """
    mw = mesh_obj.matrix_world
    aw = arm.matrix_world
    awi = aw.inverted()
    report = {}

    zs = [(mw @ v.co).z for v in mesh_obj.data.vertices]
    height = max(zs) - min(zs)
    report["body_height_mm"] = round(height * 1000.0, 2)

    new_pts = {}
    for side, sgn in (("Left", 1.0), ("Right", -1.0)):
        bf = arm.data.bones["mixamorig:" + side + "Foot"]
        bt = arm.data.bones["mixamorig:" + side + "ToeBase"]
        bl = arm.data.bones["mixamorig:" + side + "Leg"]
        ankle_w = aw @ bf.head_local
        ball_w = aw @ bf.tail_local
        tip_w = aw @ bt.tail_local

        sole = [(mw @ v.co).y for v in mesh_obj.data.vertices
                if (mw @ v.co).y < FOOT_MIN_Y and (mw @ v.co).z < 0.006
                and (mw @ v.co).x * sgn > 0.0]
        if not sole:
            raise RuntimeError("量不到 %s 的鞋底" % side)
        y_toe, y_heel = min(sole), max(sole)
        L = y_heel - y_toe

        new_ankle_z = ankle_frac * height
        new_ball_y = y_toe + toe_frac * L
        report[side] = {
            "ankle_z_mm": [round(ankle_w.z * 1000.0, 2), round(new_ankle_z * 1000.0, 2)],
            "ball_y_mm": [round(ball_w.y * 1000.0, 2), round(new_ball_y * 1000.0, 2)],
            "shoe_len_mm": round(L * 1000.0, 2),
            "toe_seg_frac_before": round((ball_w.y - y_toe) / L, 4),
            "toe_seg_frac_after": round((new_ball_y - y_toe) / L, 4),
            "ankle_frac_of_height": round(new_ankle_z / height, 4),
        }

        A = Vector((ankle_w.x, ankle_w.y, new_ankle_z))
        B = Vector((ball_w.x, new_ball_y, ball_w.z))
        # 端点是共用的（Foot.head == Leg.tail 连接；Foot.tail == ToeBase.head 不连接但重合），
        # 所以**先全读快照再统一写** —— 与 fix_foot_scale 里踩过的那个坑是同一条纪律。
        new_pts[("mixamorig:" + side + "Foot", "head")] = A
        new_pts[("mixamorig:" + side + "Leg", "tail")] = A
        new_pts[("mixamorig:" + side + "Foot", "tail")] = B
        new_pts[("mixamorig:" + side + "ToeBase", "head")] = B

    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='EDIT')
    for (bn, attr), v in new_pts.items():
        setattr(arm.data.edit_bones[bn], attr, awi @ v)
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.update()

    # 读数：改完的实际长度与比例
    for side in ("Left", "Right"):
        bf = arm.data.bones["mixamorig:" + side + "Foot"]
        h = aw @ bf.head_local
        t = aw @ bf.tail_local
        report[side]["foot_bone_mm"] = round((t - h).length * 1000.0, 2)
        report[side]["ankle_frac_actual"] = round(h.z / height, 4)
    return report


def fix_foot_rig(arm, mesh_obj, band=TOE_HINGE_BAND, shorten_toe=True):
    """把脚趾蒙皮改成**以趾骨根为轴的真铰链**，并把趾骨尾端收回鞋里。返回一份读数。

    要做两件事，都是「这具模型的原始自动权重没被人工碰过」留下的：

    ① **趾骨尾端出鞋** —— 原来 `ToeBase` 尾端在 y=−445 mm，而鞋尖只到 −355 mm，
       整根骨头飘在鞋外面 90 mm（而且跟脚骨一样长：176 / 176）。
       只改**尾端**：head（= 旋转枢轴）**不动**，所以 Unity 侧脚掌胶囊的长度
       （`ClumsyRagdoll` 用 `Distance(Foot, 其子物体)` 算）**不受影响**。

    ② **权重糊成一坨** —— 见 TOE_HINGE_BAND 的注释。这里按「沿鞋长 y 的一个铰链」
       重刷 foot/toe 这两组的分配：趾根往前 15 mm 之外 → 全给趾骨（刚性前掌）；
       趾根往后 15 mm 之外 → 全给脚骨（鞋腰不动）；中间 smoothstep 过渡。
       **两组权重之和保持不变** —— 只搬分配，不动这只脚原本「踩在哪条腿的权重上」。

    ③ **踝那一刀**（2026-10-01 第三轮新增，见 ②b）—— `fix_foot_scale()` 把踝搬走之后，
       `Leg` ↔ `Foot` 的蒙皮边界得跟着搬，否则靴筒会在关节上方折。

    ⚠️ 必须在 `calibrate()` **之前**调用：`build_masks()` 的 ball / toe 两套掩码
    是按趾权重切出来的，权重错了掩码就错了。
    """
    mw = mesh_obj.matrix_world
    iw = arm.matrix_world.inverted()
    report = {}

    # ---- 每只脚：枢轴（趾骨根）+ 鞋尖（该侧脚链最靠前的顶点）----
    info = {}
    for side, sgn in (("Left", 1.0), ("Right", -1.0)):
        bone = arm.data.bones["mixamorig:" + side + "ToeBase"]
        head_w = arm.matrix_world @ bone.head_local
        tail_w = arm.matrix_world @ bone.tail_local
        foot_g = mesh_obj.vertex_groups["mixamorig:" + side + "Foot"].index
        toe_g = mesh_obj.vertex_groups["mixamorig:" + side + "ToeBase"].index
        tip_y, tip_i = None, -1
        for v in mesh_obj.data.vertices:
            w = 0.0
            for ge in v.groups:
                if ge.group == foot_g or ge.group == toe_g:
                    w += ge.weight
            if w <= 0.5:
                continue
            y = (mw @ v.co).y
            if tip_y is None or y < tip_y:
                tip_y, tip_i = y, v.index
        info[side] = (bone.name, head_w, tail_w, tip_y, tip_i)

    # ---- ① 收趾骨尾端 ----
    if shorten_toe:
        bpy.context.view_layer.objects.active = arm
        bpy.ops.object.mode_set(mode='EDIT')
        for side in ("Left", "Right"):
            bname, head_w, tail_w, tip_y, _ = info[side]
            d = (tail_w - head_w).normalized()
            if abs(d.y) > 1e-6:
                t = (tip_y + TOE_TIP_EPS - head_w.y) / d.y
                # ⚠️ 原来这里写的是「只许变短（`t < 当前长度`）」—— 那是**错的**：
                #    `retarget_foot_joints()` 会把球往后搬，趾段因此**要变长**，
                #    而 FBX 每次重新导入时趾骨尾端都被导入器按父骨长造出来（叶骨），
                #    于是"只许变短"会让第二遍跑出来的趾骨比第一遍短。改成**绝对定位**：
                #    只要不是退化长度就照写。
                if t > 0.005:
                    arm.data.edit_bones[bname].tail = iw @ (head_w + d * t)
        bpy.ops.object.mode_set(mode='OBJECT')
        bpy.context.view_layer.update()

    # ---- ② 铰链权重 ----
    for side in ("Left", "Right"):
        bname, head_w, tail_w, tip_y, _ = info[side]
        fg = mesh_obj.vertex_groups["mixamorig:" + side + "Foot"]
        tg = mesh_obj.vertex_groups["mixamorig:" + side + "ToeBase"]
        y_p = head_w.y
        lo, hi = y_p - band, y_p + band                  # lo 在前（更负），hi 在后
        moved = dropped = 0
        for v in mesh_obj.data.vertices:
            wf = wt = 0.0
            for ge in v.groups:
                if ge.group == fg.index:
                    wf = ge.weight
                elif ge.group == tg.index:
                    wt = ge.weight
            tot = wf + wt
            if tot <= 1e-6:
                continue
            y = (mw @ v.co).y
            if y <= lo:
                f = 1.0                                   # 趾根之前：整份给趾骨
            elif y >= hi:
                f = 0.0                                   # 趾根之后：整份给脚骨（原来这里还漏着）
            else:
                u = (y - lo) / (hi - lo)
                f = 1.0 - u * u * (3.0 - 2.0 * u)         # smoothstep
            nw_t, nw_f = tot * f, tot * (1.0 - f)
            if abs(nw_t - wt) > 1e-5 or abs(nw_f - wf) > 1e-5:
                moved += 1
            for g, old, new in ((tg, wt, nw_t), (fg, wf, nw_f)):
                if new <= 1e-6:
                    if old > 0.0:
                        g.remove([v.index])
                        dropped += 1
                else:
                    g.add([v.index], new, 'REPLACE')
        report[side] = {
            "pivot_y_mm": round(y_p * 1000.0, 2),
            "shoe_tip_y_mm": round(tip_y * 1000.0, 2),
            "bone_len_mm_before": round((tail_w - head_w).length * 1000.0, 2),
            # ⚠️ `Bone.length` 是**骨架局部**单位（这副骨架 scale=0.01）；要过 matrix_world 才是世界毫米。
            "bone_len_mm_after": round(
                ((arm.matrix_world @ arm.data.bones["mixamorig:" + side + "ToeBase"].tail_local
                  - arm.matrix_world @ arm.data.bones["mixamorig:" + side + "ToeBase"].head_local).length
                 * 1000.0), 2),
            "verts_reweighted": moved, "groups_cleared": dropped,
        }

    # ---- ②b ★ 踝那一刀：`Leg` ↔ `Foot` 的边界跟着**新踝**走 ----
    #   `fix_foot_scale()` 把踝从 130 mm 搬到 ~91 mm，而蒙皮的 Leg/Foot 边界还留在 130 mm
    #   （原始自动权重是照着旧踝分的）—— 差 **39 mm**，靴筒会在关节上方 39 mm 处折。
    #   这里按新踝重分一次，做法与趾铰链同构（只搬分配、两组之和不变）。
    #   ⚠️ 判据用 **z**（踝是水平铰链），不是 y。
    for side in ("Left", "Right"):
        z_ankle = (arm.matrix_world @ arm.data.bones["mixamorig:" + side + "Foot"].head_local).z
        lg = mesh_obj.vertex_groups["mixamorig:" + side + "Leg"]
        fg = mesh_obj.vertex_groups["mixamorig:" + side + "Foot"]
        lo, hi = z_ankle - band, z_ankle + band
        moved = 0
        for v in mesh_obj.data.vertices:
            wl = wf = 0.0
            for ge in v.groups:
                if ge.group == lg.index:
                    wl = ge.weight
                elif ge.group == fg.index:
                    wf = ge.weight
            tot = wl + wf
            if tot <= 1e-6:
                continue
            z = (mw @ v.co).z
            if z <= lo:
                f = 1.0                                   # 踝以下：整份给脚骨
            elif z >= hi:
                f = 0.0                                   # 踝以上：整份给小腿
            else:
                u = (z - lo) / (hi - lo)
                f = 1.0 - u * u * (3.0 - 2.0 * u)
            nw_f, nw_l = tot * f, tot * (1.0 - f)
            if abs(nw_f - wf) > 1e-5 or abs(nw_l - wl) > 1e-5:
                moved += 1
            for g, old, new in ((fg, wf, nw_f), (lg, wl, nw_l)):
                if new <= 1e-6:
                    if old > 0.0:
                        g.remove([v.index])
                else:
                    g.add([v.index], new, 'REPLACE')
        report[side]["ankle_z_mm"] = round(z_ankle * 1000.0, 2)
        report[side]["ankle_reweighted"] = moved

    bpy.context.view_layer.update()
    return report


def _set_ankle(pose, foot, angle):
    """把某个姿势里那只脚的踝关节 X 角改成 `angle`。"""
    key = "foot_" + foot
    specs = [list(s) for s in pose.get(key, [])]
    for s in specs:
        if s[0] == "X":
            s[1] = angle
            break
    else:
        specs.append(["X", angle])
    pose[key] = [(a, d) for a, d in specs]


def clamp_ankle(angle):
    """踝角夹进解剖学范围（owner：「脚后跟只能向小腿肚子弯曲，不能向反方向弯曲」）。"""
    return max(-ANKLE_DORSI_MAX, min(ANKLE_PLANTAR_MAX, angle))


def clamp_toe(angle):
    """脚趾（鞋前半截）只能往上折 —— 鞋头盒不会朝反方向反折。"""
    return max(-TOE_FOLD_MAX, min(0.0, angle))


def signed_angle_about(q, axis):
    """把四元数 `q` 化成「绕 `axis` 转了多少度」（带符号；q ≈ 单位阵时为 0）。"""
    if q.angle < 1e-6:
        return 0.0
    ang = math.degrees(q.angle)
    return ang if q.axis.dot(axis) >= 0.0 else -ang


def _set_toe(pose, foot, angle):
    """把某个姿势里那只脚的**脚趾** X 角改成 `angle`（− = 鞋前半截往上折）。"""
    key = "toe_" + foot
    specs = [list(s) for s in pose.get(key, [])]
    for s in specs:
        if s[0] == "X":
            s[1] = angle
            break
    else:
        specs.append(["X", angle])
    pose[key] = [(a, d) for a, d in specs]


def _toe_angle(pose, foot):
    for a, d in pose.get("toe_" + foot, []):
        if a == "X":
            return d
    return 0.0


def _ankle_angle(pose, foot):
    for a, d in pose.get("foot_" + foot, []):
        if a == "X":
            return d
    return 0.0


def solve_ankle(arm, pose, foot, clearance, rewrite, exact):
    """解这只脚的踝关节。

    ★ 一个反直觉但决定性的事实（第一版解算器就栽在这）：
    **脚绕踝转，只会让最低的那个角更低，不会更高。**
    脚掌放平时最低点最高；往任何一边转，脚跟或脚尖总有一个扎下去。
    所以「抬高脚」不是靠转踝，而是靠**髋**；转踝只能决定**用哪个角去够地**。

    于是分两步：
      1. 先把脚**放平**（扫一遍找「最低点最高」的那个角 F）——这是这只脚能到的最高位置；
      2. 若 F 处仍悬空，再朝**作者原本想要的那一边**转出去，让脚尖/脚跟落到地面。
    """
    orig = _ankle_angle(pose, foot)

    def z_at(a):
        _set_ankle(pose, foot, a)
        rewrite()
        bpy.context.view_layer.update()
        return zmin(foot)             # 整只脚：由「最先够到地的那个点」决定踝角（原版口径）

    def restore():
        _set_ankle(pose, foot, orig)
        rewrite()
        bpy.context.view_layer.update()

    def settle(angle, tag):
        _set_ankle(pose, foot, angle)
        rewrite()
        bpy.context.view_layer.update()
        ANKLE_LOG.append((foot, orig, angle, tag))

    # 1) 扫出「放平」角 F（脚部最低点最高的那一点）
    #    ★ 搜索范围先夹进踝关节的活动范围 —— 解算器没有解剖学常识，
    #      放开 ±60° 它能给你解出「站在鞋尖上、脚跟抬到 300 mm」那种掰反的姿势。
    lo_s = clamp_ankle(orig - 60.0)
    hi_s = clamp_ankle(orig + 60.0)
    best_a, best_z = lo_s, -1e9
    a = lo_s
    while a <= hi_s + 1e-6:
        z = z_at(a)
        if z > best_z:
            best_z, best_a = z, a
        a += 3.0
    if best_z == -1e9:                       # 范围退化成一个点
        best_a = lo_s
    # 在 F 附近细化
    a = clamp_ankle(best_a - 3.0)
    b = clamp_ankle(best_a + 3.0)
    gr = 0.6180339887
    c, d = b - gr * (b - a), a + gr * (b - a)
    for _ in range(18):
        if z_at(c) < z_at(d):
            a = c
        else:
            b = d
        c, d = b - gr * (b - a), a + gr * (b - a)
    flat = clamp_ankle(0.5 * (a + b))
    z_flat = z_at(flat)

    tol = 1e-4

    def outward(direction):
        """从 flat 朝 direction 转出去，找最低点落到 clearance 的角度（夹在关节范围内）。"""
        t_hi = 75.0
        end = clamp_ankle(flat + direction * t_hi)
        if abs(end - flat) < 1e-6 or z_at(end) > clearance:
            return None                      # 关节到头了仍够不着
        t_a, t_b = 0.0, 1.0                  # 归一化参数：flat → end
        for _ in range(20):
            m = 0.5 * (t_a + t_b)
            if z_at(flat + m * (end - flat)) > clearance:
                t_a = m
            else:
                t_b = m
        return clamp_ankle(flat + 0.5 * (t_a + t_b) * (end - flat))

    if not exact:
        # 摆动中的脚：**先看作者给的那个角**穿不穿地；穿了才去挪它。
        # （踩过坑：一开始写成「放平不穿就原样还原」—— 结果脚平着确实不穿，
        #   但作者给的角是绷着的，于是原样还原 = 让它继续插在地里 4 cm。）
        if z_at(orig) >= clearance - tol:
            restore()
            return
        if z_flat < clearance - tol:
            settle(flat, "摆动脚：放平仍穿地")
            return
        # 从 orig 朝 flat 走（这一段单调升），二分到「刚好不穿地」
        t_a, t_b = 0.0, 1.0
        for _ in range(20):
            m = 0.5 * (t_a + t_b)
            if z_at(orig + m * (flat - orig)) < clearance:
                t_a = m
            else:
                t_b = m
        settle(orig + 0.5 * (t_a + t_b) * (flat - orig), "摆动脚：抬到不穿")
        return

    if z_flat < clearance - tol:
        # 放平都够不着地 —— 髋定得太低，踝救不回来
        settle(flat, "放平仍穿地")
        return
    if abs(z_flat - clearance) <= tol:
        settle(flat, "放平即可")
        return

    # 脚悬着：朝作者原本想要的那一边转出去够地
    direction = 1.0 if orig >= flat else -1.0
    ang = outward(direction)
    if ang is None:
        ang = outward(-direction)
    if ang is None:
        settle(flat, "够不着地")
        return
    settle(ang, "转出去够地")


def solve_toe(arm, pose, foot, clearance, rewrite):
    """★ **死代码（2026-10-01 查明）：没有任何调用点。** 保留原样只为留个记录。

    把这只脚的**脚趾**折到「鞋前半截正好落在地面上」。

    为什么要专门解这一步：这只鞋只有**鞋前半截**（趾骨根往前 78 mm）会弯，owner 要的就是
    「前半截像真鞋那样折起来」。折多少才有意义不能凭空写 —— 它取决于脚踝被解到多少度
    （脚踝转 +35°，趾骨就得折 -35° 才让前半截落回地面；写死一个 -55° 只会让鞋尖扎进地里）。
    所以这里按**几何约束**解：折到鞋前半截的鞋底最低点正好贴地为止。

    只在作者已经写了折角（< -5°）时才上：趾骨写 0 的那些拍（站着、脚掌放平）不该被硬折。
    """
    orig = _toe_angle(pose, foot)
    if orig > -5.0:
        return

    def apply(a):
        _set_toe(pose, foot, a)
        rewrite()
        bpy.context.view_layer.update()

    def z_at(a):
        apply(a)
        return zmin(foot, "toe")

    if z_at(orig) >= clearance - 1e-4:
        apply(orig)                      # 作者给的折角已经不穿地，照用
        return

    hi = clamp_toe(orig - 60.0)          # 再往上折一定能把前半截抬起来
    if z_at(hi) < clearance:
        ANKLE_LOG.append((foot, orig, hi, "趾：折到头仍穿地"))
        apply(hi)
        return
    a, b = orig, hi                      # z(a) < clearance <= z(b)
    for _ in range(18):
        m = 0.5 * (a + b)
        if z_at(m) < clearance:
            a = m
        else:
            b = m
    ang = 0.5 * (a + b)
    ANKLE_LOG.append((foot, orig, ang, "趾：折到鞋前半截贴地"))
    apply(ang)



def apply_pose(arm, pose, frame):
    """把一份姿势写成一帧关键帧（含种地）。"""
    hips = arm.pose.bones[BONES["hips"]]

    def rewrite():
        for key, val in pose.items():
            if key.startswith("__"):
                continue
            name = BONES[key]
            pb = arm.pose.bones[name]
            pb.rotation_mode = 'QUATERNION'
            pb.rotation_quaternion = bone_quat(arm, name, val)

    rewrite()
    loc = pose.get("__hips_loc__", (0.0, 0.0, 0.0))
    hips.location = Vector((loc[0], loc[1], loc[2]))
    bpy.context.view_layer.update()

    if "__fit__" in pose:
        clear = pose["__clear__"]
        planted = [f for f in pose["__fit__"] if f in ("l", "r")]
        if planted:
            # 1) 髋找脚：**主脚**（`planted[0]`）的最低顶点解到 clearance。
            #    ⚠️ 实际用的是 **`foot` 掩码（整只脚）**，不是 `ball` —— 2026-10-01 更正：
            #    这里原来写着「用 ball 掩码」，代码从来没这么做过。
            #    `ball` / `toe` 两套掩码目前**只有 `toe` 被死代码 `solve_toe` 引用**，
            #    换句话说 `build_masks()` 里那两套现在是白算的（见 HANDOFF-鞋前半截弯折.md §七）。
            #    ⚠️ 这个「主脚种地、其余靠踝解算」的结构有个已知后果：踝只能让脚**更低**、
            #    不能更高，所以第二只脚一旦本来就比主脚低，就只能把髋整体抬起来兜底
            #    （`contact_pass`），于是主脚浮空 —— 走路 f00~f06 前脚浮 8~10 mm 就是这么来的。
            primary = planted[0]
            for _ in range(4):
                hips.location.y += (clear - zmin(primary)) / UP_PER_LOCAL
                bpy.context.view_layer.update()
            # 2) 踝找地：其余每只脚各解一次踝角
            for f in ("l", "r"):
                if f == primary:
                    continue
                solve_ankle(arm, pose, f, clear, rewrite, exact=(f in planted))
            # 3) 脚趾**不参与种地解算**（趾骨角只由作者在姿势里手写）。
            #    试过让趾也去够地（solve_toe），结果它和踝解算互相追着跑：
            #    趾一折 → 整脚最低点跑到折痕 → 踝往外转更多 → 鞋前半截更低 → 再折……
            #    实测解到踝 +72°、脚跟抬到 300 mm 那种掰反的姿势。所以趾只由人定。
            #    ⚠️ 更正：`clamp_toe()`（「只能往上折」）**从没被调用过** —— 手写的角度是裸的；
            #       而且 2026-10-01 之前趾权重糊成一坨，折起来的后果一是可见折角只有骨角 0.7 倍、
            #       二是鞋腰被拽到地下 14.5 mm（现在 `fix_foot_rig()` 已经修掉后者）。
        else:
            # 腾空：整个网格的最低点解到 clearance
            for _ in range(4):
                hips.location.y += (clear - zmin("mesh")) / UP_PER_LOCAL
                bpy.context.view_layer.update()

    for key in pose:
        if key.startswith("__"):
            continue
        arm.pose.bones[BONES[key]].keyframe_insert("rotation_quaternion", frame=frame)
    hips.keyframe_insert("location", frame=frame)


def action_fcurves(act):
    """取出一条 action 的全部 fcurve。

    Blender 4.4 起 action 改成了 slot/layer/strip/channelbag 结构，`action.fcurves`
    在 5.x 上已经没有了 —— 老写法会 AttributeError。这里两条路都走。
    """
    if hasattr(act, "fcurves") and len(act.fcurves) > 0:
        return list(act.fcurves)
    out = []
    for layer in getattr(act, "layers", []):
        for strip in getattr(layer, "strips", []):
            for cb in getattr(strip, "channelbags", []):
                out.extend(cb.fcurves)
    if not out and hasattr(act, "fcurves"):
        out = list(act.fcurves)
    return out


def new_action(arm, name):
    ad = arm.animation_data or arm.animation_data_create()
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    ad.action = act
    try:
        if hasattr(ad, "action_slot") and ad.action_slot is None and hasattr(act, "slots"):
            ad.action_slot = act.slots.new(id_type='OBJECT', name=arm.name)
    except Exception:
        pass
    return act


def smooth_loop(act):
    """把首尾关键帧的切线接上，循环处不留顿挫。

    做法：把整条曲线当成环，用「首帧后一段」与「尾帧前一段」算环点的斜率，
    再把首尾两个关键帧的 handle 按这个斜率摆好（FREE 类型）。
    """
    for fc in action_fcurves(act):
        kfs = fc.keyframe_points
        n = len(kfs)
        if n < 3:
            continue
        dt_out = kfs[1].co.x - kfs[0].co.x
        dt_in = kfs[-1].co.x - kfs[-2].co.x
        span = dt_in + dt_out
        if span <= 1e-6:
            continue
        slope = (kfs[1].co.y - kfs[-2].co.y) / span
        for kf, hx in ((kfs[0], dt_out), (kfs[-1], dt_in)):
            kf.interpolation = 'BEZIER'
            kf.handle_left_type = 'FREE'
            kf.handle_right_type = 'FREE'
            kf.handle_right = (kf.co.x + hx / 3.0, kf.co.y + slope * hx / 3.0)
            kf.handle_left = (kf.co.x - hx / 3.0, kf.co.y - slope * hx / 3.0)
        fc.update()


def contact_pass(arm, act, length, clearance=0.0, tol=0.002, iters=2):
    """收尾：把**关键帧之间插值出来的**穿地压掉。

    关键姿势本身已经解过了（髋找脚 + 踝找地），但两帧之间的姿势没人管：
    跑步 18 帧只有 4 个姿势，f04/f05 就插出了 2.8 cm 的穿地。
    这一遍只动穿地的那些帧 —— 先转踝（脚绕踝只能往下扎，所以取「放平再转出去」
    那个解），转不动再抬髋兜底。**会在个别帧上补键**，所以脚踝那几条通道
    实际上被局部烘过 —— 这是接触清理的常规代价，写在交接里。
    """
    scn = bpy.context.scene
    touched = 0

    def nudge_solve(pb, bone, foot):
        base = pb.rotation_quaternion.copy()
        axis = local_axis(arm, bone, "X")
        # ★ 先量出这个姿势此刻的踝角（相对 rest，绕 armature X），再把可动的增量夹进关节范围。
        #   不加这一条的话，补帧这一遍会**把已经夹好的关键帧也覆盖掉** ——
        #   实测踝被推到 +72°（脚跟抬到 166 mm、整只鞋像在踮芭蕾），
        #   以及反方向 -41°（脚背朝小腿前面折，正是 owner 说的「向反方向弯曲」）。
        base_deg = signed_angle_about(base, axis)
        d_lo = max(-35.0, -ANKLE_DORSI_MAX - base_deg)
        d_hi = min(35.0, ANKLE_PLANTAR_MAX - base_deg)
        if d_hi < d_lo:
            d_lo = d_hi = 0.0

        def z_at(d):
            pb.rotation_quaternion = Quaternion(axis, math.radians(d)) @ base
            bpy.context.view_layer.update()
            return zmin(foot)             # 整只脚：兜底要保证哪一截都不穿地

        best_d, best_z = 0.0, z_at(0.0)
        d = d_lo
        while d <= d_hi + 1e-6:
            z = z_at(d)
            if z > best_z:
                best_z, best_d = z, d
            d += 4.0
        if best_z < clearance - 1e-6:
            ang = best_d                        # 放平都够不着，顶在这儿
        else:
            ang = best_d
            for direction in (1.0, -1.0):
                far = max(d_lo, min(d_hi, best_d + direction * 35.0))
                if abs(far - best_d) < 1e-6 or z_at(far) > clearance:
                    continue
                a, b = 0.0, 1.0
                for _ in range(18):
                    m = 0.5 * (a + b)
                    if z_at(best_d + m * (far - best_d)) > clearance:
                        a = m
                    else:
                        b = m
                ang = best_d + 0.5 * (a + b) * (far - best_d)
                break
        pb.rotation_quaternion = Quaternion(axis, math.radians(ang)) @ base
        bpy.context.view_layer.update()

    for _ in range(iters):
        for f in range(length + 1):
            scn.frame_set(f)
            bpy.context.view_layer.update()
            if zmin("mesh") >= -tol:
                continue
            did = False
            for foot in ("l", "r"):
                if zmin(foot) >= -tol:
                    continue
                bone = BONES["foot_" + foot]
                pb = arm.pose.bones[bone]
                nudge_solve(pb, bone, foot)
                pb.keyframe_insert("rotation_quaternion", frame=f)
                did = True
            bpy.context.view_layer.update()
            mz = zmin("mesh")
            if mz < -tol:
                hips = arm.pose.bones[BONES["hips"]]
                hips.location.y += (clearance - mz) / UP_PER_LOCAL
                bpy.context.view_layer.update()
                hips.keyframe_insert("location", frame=f)
                did = True
            if did:
                touched += 1
    return touched


def sync_unity_clip_meta(fbx_path, last_frame):
    """FBX 导出后，把 Unity 侧 `.fbx.meta` 里 `clipAnimations.lastFrame` 同步到**输出帧号**。

    ★ 2026-10-01（第五轮）踩到的**静默截断**：`.meta` 里一旦有 `clipAnimations` 块，
    Unity 就**按它截断片段**。帧率 30 → 60 之后片段变成 61 帧，而 `.meta` 还写着
    `lastFrame: 30` ⇒ Unity 交出来的 `AnimationClip.length` **砍半**（1.00 s → **0.50 s**）
    ⇒ **播放速度整整翻一倍**。而 FBX 本身是对的（Unity 里那条 `__preview__` 时长正常）。

    ⚠️ **只替换 `lastFrame` 那一行**，绝不重建整个 `clipAnimations` 块 ——
    块里的 `internalID` / `name` 是 AnimatorController 引用的对象，重建会换 fileID、
    把动画状态机的绑定打断。
    """
    meta = fbx_path + ".meta"
    if not os.path.exists(meta):
        return "没 meta"
    txt = open(meta, encoding="utf-8", errors="replace").read()
    import re as _re
    hits = _re.findall(r"(\n[ \t]*lastFrame:[ \t]*)(\d+)", txt)
    if len(hits) != 1:
        return "lastFrame 出现 %d 次，不敢动" % len(hits)
    old = int(hits[0][1])
    if old == int(last_frame):
        return "已是 %d" % old
    txt = _re.sub(r"(\n[ \t]*lastFrame:[ \t]*)\d+", r"\g<1>%d" % int(last_frame), txt, count=1)
    open(meta, "w", encoding="utf-8").write(txt)
    return "lastFrame %d → %d" % (old, int(last_frame))


def set_linear(act):
    """把每条曲线的关键帧改成**线性** —— 也就是"姿势之间匀速"。

    ★ 2026-10-01（第五轮）实测出来的：owner 说小浣熊的走路"比 X Bot 顿"。
    量**逐帧姿势变化量**（24 根骨的角度和）的波动：

        RaccoonWalk（Bezier + AUTO_CLAMPED 手柄）  min/mean **0.28**  max/mean **1.65**  ⇒ 5.9×
        XBotWalk_Mixamo（Mixamo，逐帧烘）          min/mean  0.753  max/mean  1.319  ⇒ 1.75×

    也就是浣熊一个循环里**将近停顿两次再猛冲**（9.8°/帧 ↔ 57.9°/帧）。
    真因是 `AUTO_CLAMPED` 手柄在**每个姿势上把曲线压平**（缓入缓出）——
    对着 8 个姿势的走路，就是每个姿势都停一下。四种手柄实测对比：

        当前 AUTO_CLAMPED 5.9× ｜ 全 AUTO **6.8×（更差）** ｜ 全键中央差分（Catmull-Rom）4.2×
        **全 VECTOR（= 姿势之间匀速）2.1×** ← 取这个

    改成匀速之后波动就落到 2.1×，与 mocap 那段的 1.75× 同一量级。
    折算过来也就是「把手 K 的关键帧当逐帧烘过的 mocap 那样播」——
    Unity 那边本来也是这么插值的。
    """
    n = 0
    for fc in action_fcurves(act):
        for k in fc.keyframe_points:
            k.interpolation = 'LINEAR'
            n += 1
        fc.update()
    return n


def build_clip(arm, name, length, loop, keys_fn):
    """`length` 与 `keys_fn()` 返回的帧号都是 **30 fps 的"创作帧"**，写进 action 时乘 `FRAME_MUL`。"""
    global ANKLE_LOG
    ANKLE_LOG = []
    clear_pose(arm)
    act = new_action(arm, name)
    m = FRAME_MUL
    for frame, pose in keys_fn():
        apply_pose(arm, pose, frame * m)
    out_len = length * m
    act.frame_start = 0.0
    act.frame_end = float(out_len)
    if hasattr(act, "use_frame_range"):
        act.use_frame_range = True
    if hasattr(act, "use_cyclic"):
        act.use_cyclic = bool(loop)
    # 顺序要紧：**先定插值，再补接触** —— `contact_pass` 算出来的修正量是按
    # **当前插值**判穿地的，插值换了之后修正量就不对了。
    # （原来这里是「先接循环切线、再补接触」，道理一样：反了循环缝上会插出 1 cm 穿地，实测过。）
    set_linear(act)
    fixed = contact_pass(arm, act, out_len)              # ← 在**输出帧率**上逐帧解，FPS 翻倍分辨率也翻倍
    return act, list(ANKLE_LOG), fixed


# ----------------------------------------------------------------------------
# 主流程
# ----------------------------------------------------------------------------

def main():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for c in list(bpy.data.collections):
        bpy.data.collections.remove(c)
    for a in list(bpy.data.actions):
        bpy.data.actions.remove(a)

    bpy.ops.import_scene.fbx(filepath=SRC_FBX)
    scn = bpy.context.scene
    scn.render.fps = FPS
    scn.render.fps_base = 1.0

    arm = bpy.data.objects[ARM_NAME]
    bpy.context.view_layer.objects.active = arm
    clear_pose(arm)
    # ★ 先把这具模型的坏骨架修好（肩关节位置 + 手臂蒙皮权重），再谈动画。
    #   放在 calibrate 之前：种地标定与后续所有姿势都要基于修好的骨架。
    rig_report = fix_arm_rig(arm, bpy.data.objects[MESH_NAME])
    # ★ 脚部：**先缩小，再改蒙皮**（顺序不能反 —— fix_foot_rig 的铰链以趾骨根为轴，
    #   而趾骨根正是 fix_foot_scale 搬走的那个点）。
    #   两者都必须也在 calibrate 之前 —— build_masks 的 ball/toe 掩码是按趾权重切的。
    foot_scale_report = fix_foot_scale(arm, bpy.data.objects[MESH_NAME])
    # ★ 再把踝降下来、球摆到与 X Bot 脚一致的分节比例上（只动骨骼，网格不变）。
    foot_joint_report = retarget_foot_joints(arm, bpy.data.objects[MESH_NAME])
    foot_report = fix_foot_rig(arm, bpy.data.objects[MESH_NAME])
    # ★ 2026-10-01：背包整体绑死到 Spine —— owner：「背包不要跟着形变」。
    #   ⚠️ 不能再按材质名选：包的上半外皮是「身体.002」（见 fix_backpack_rig 文档），
    #   按名字选只会绑到下半 ⇒ owner 第二轮反馈「只做了底部，整体还是形变」。
    backpack_report = fix_backpack_rig(arm, bpy.data.objects[MESH_NAME])
    k = calibrate(arm)

    made = []
    ankle_rows = []
    for name, length, loop, keys_fn, _use in CLIP_SPECS:
        act, log, fixed = build_clip(arm, name, length, loop, keys_fn)
        made.append((name, length, loop, len(action_fcurves(act)), fixed))
        for foot, orig, solved, tag in log:
            ankle_rows.append("%-13s %s 踝 %+7.1f° → %+7.1f°  (%+.1f°) %s" % (
                name, foot, orig, solved, solved - orig, tag))

    # ★ 导出前把 action 摘掉（免得当前姿势被烘进别的地方），导出后**再挂回基准帧**。
    #
    # 踩过的坑：原来这里导出完把姿势 clear 成 rest（T-pose）就结束了 ——
    # 于是每次跑完脚本，Blender 视口里留下的是**平举的 T-pose**，
    # 看上去就像「手臂根本没放下来」。视口必须停在基准帧上。
    if arm.animation_data:
        arm.animation_data.action = None
    clear_pose(arm)

    scn.frame_start = 0
    scn.frame_end = max(c[1] for c in CLIP_SPECS) * FRAME_MUL     # 创作帧 → 输出帧

    # ========================================================================
    # ★ 导出①：一段一个 FBX（Mixamo 那样，without skin）
    # ========================================================================
    # 三个必须同时满足的东西，缺一个 Unity 那边就废掉（全都实测过）：
    #
    #   1. **一次只导一个 action**：要用 NLA —— 把该 action 放进一条同名 NLA track，
    #      再 `bake_anim_use_nla_strips=True`。
    #      ⚠️ 写成 `bake_anim_use_all_actions=False` + 挂活动 action 是**不行的**：
    #      导出来的片段叫 `Scene`、长度等于场景时间轴（3.000s），九份全一样。
    #   2. **路径要带 `Armature/` 那一层**：只选骨架导出时，Unity 会把骨架当成模型根，
    #      片段路径变成 `mixamorig:Hips` —— 绑到 rigged 模型（路径是
    #      `Armature/mixamorig:Hips`）上**完全不动**（实测位移 0.0000 m）。
    #      所以额外丢一个空物体进去当**兄弟**，逼 Blender 保留 Armature 这一层节点。
    #   3. 空物体只是为了让结构不塌，导完就删。
    #
    # 结果：每个文件 ~225 KB（对比带蒙皮的 2.2 MB），路径与整份导出的完全一致。
    helper = bpy.data.objects.new("RigRoot", None)
    bpy.context.collection.objects.link(helper)

    per_file = []
    for name, length, loop, keys_fn, _use in CLIP_SPECS:
        act = bpy.data.actions[name]
        ad = arm.animation_data or arm.animation_data_create()
        ad.action = None
        try:
            ad.action_slot = None
        except Exception:
            pass
        while len(ad.nla_tracks):
            ad.nla_tracks.remove(ad.nla_tracks[0])
        track = ad.nla_tracks.new()
        track.name = name
        strip = track.strips.new(name, int(act.frame_range[0]), act)
        strip.name = name
        bpy.context.view_layer.update()

        bpy.ops.object.select_all(action='DESELECT')
        arm.select_set(True)
        helper.select_set(True)
        bpy.context.view_layer.objects.active = arm
        path = os.path.join(SEPARATE_DIR, name + ".fbx")
        bpy.ops.export_scene.fbx(
            filepath=path,
            use_selection=True,
            object_types={'ARMATURE', 'EMPTY'},   # 不带 mesh，但保留 Armature 这一层
            global_scale=1.0,
            apply_unit_scale=True,
            apply_scale_options='FBX_SCALE_NONE',
            axis_forward='-Z',
            axis_up='Y',
            use_space_transform=True,
            bake_space_transform=False,
            primary_bone_axis='Y',
            secondary_bone_axis='X',
            armature_nodetype='NULL',
            add_leaf_bones=False,
            use_armature_deform_only=False,
            bake_anim=True,
            bake_anim_use_all_actions=False,
            bake_anim_use_nla_strips=True,        # ← 一条 NLA track = 一个片段
            bake_anim_use_all_bones=True,
            bake_anim_force_startend_keying=True,
            bake_anim_step=1.0,
            bake_anim_simplify_factor=0.0,
        )
        # ★ `.meta` 的片段范围要跟着输出帧数走，否则 Unity 会按旧帧数静默截断（见函数注释）
        meta_note = sync_unity_clip_meta(path, length * FRAME_MUL)
        META_SYNC.append("%s: %s" % (name, meta_note))
        per_file.append((name, os.path.getsize(path)))

    # 空物体与 NLA 只是导出用的脚手架，收拾干净
    if arm.animation_data:
        arm.animation_data.action = None
        while len(arm.animation_data.nla_tracks):
            arm.animation_data.nla_tracks.remove(arm.animation_data.nla_tracks[0])
    bpy.data.objects.remove(helper, do_unlink=True)
    clear_pose(arm)
    bpy.context.view_layer.update()

    # ========================================================================
    # 导出②：自带 mesh 的整份（方便在 Unity 里对着模型预览，以及做重定向核对）
    # ========================================================================
    for n in (ARM_NAME, MESH_NAME):
        if n in bpy.data.objects:
            bpy.data.objects[n].select_set(True)
    bpy.context.view_layer.objects.active = arm

    bpy.ops.export_scene.fbx(
        filepath=OUT_FBX,
        use_selection=True,
        object_types={'ARMATURE', 'MESH'},
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_NONE',
        axis_forward='-Z',
        axis_up='Y',
        use_space_transform=True,
        bake_space_transform=False,
        mesh_smooth_type='OFF',
        use_mesh_modifiers=True,
        use_tspace=False,
        primary_bone_axis='Y',
        secondary_bone_axis='X',
        armature_nodetype='NULL',
        add_leaf_bones=False,
        use_armature_deform_only=False,
        bake_anim=True,
        bake_anim_use_all_actions=True,     # 这份整份导，方便一次导入全部片段
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_bones=True,
        bake_anim_force_startend_keying=True,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,      # 不精简：精简发生在导入阶段（EP01）
        path_mode='AUTO',
        embed_textures=False,
    )

    # ========================================================================
    # 导出③：★ 带蒙皮的**修好骨架**的模型（覆盖 111_raccoon_rigged.fbx）
    # ========================================================================
    # 这一份才是 Play 里那只浣熊。上面 ① 只是片段、② 只是预览版 ——
    # **物理布娃娃和可见蒙皮都建在这个文件上**，骨架不修好，光修片段没用：
    # 可见的「大臂」那段肉仍然归锁骨管，看起来就是「大臂没动」。
    #
    # 只覆盖 .fbx，**不动 .fbx.meta** —— GUID 不变，所以
    # `RaccoonSkeleton.ModelPath` 和场景里 3 处 ModelPrefab 引用全都还指着它。
    # （原有文件没进 git，所以跑本脚本前务必先备份，见交接文档。）
    bpy.ops.object.select_all(action='DESELECT')
    for n in (ARM_NAME, MESH_NAME):
        if n in bpy.data.objects:
            bpy.data.objects[n].select_set(True)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.export_scene.fbx(
        filepath=RIGGED_OUT,
        use_selection=True,
        object_types={'ARMATURE', 'MESH'},
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_NONE',
        axis_forward='-Z',
        axis_up='Y',
        use_space_transform=True,
        bake_space_transform=False,
        mesh_smooth_type='OFF',
        use_mesh_modifiers=True,
        use_tspace=False,
        primary_bone_axis='Y',
        secondary_bone_axis='X',
        armature_nodetype='NULL',
        add_leaf_bones=False,
        use_armature_deform_only=False,
        bake_anim=False,                     # ← 只要模型，不要动画
    )

    # ---- 导出完：把视口停在基准帧上，并存一份 .blend ----
    # 这两件事是给人看的：跑完脚本之后打开 Blender，看到的应该是
    # 「两臂放在身体两侧」的基准帧，而不是 rest 的 T-pose。
    stand = bpy.data.actions.get("RaccoonStand")
    if stand is not None and arm.animation_data:
        arm.animation_data.action = stand
        try:
            if hasattr(arm.animation_data, "action_slot") and arm.animation_data.action_slot is None \
                    and hasattr(stand, "slots") and len(stand.slots):
                arm.animation_data.action_slot = stand.slots[0]
        except Exception:
            pass
        scn.frame_set(0)
        bpy.context.view_layer.update()

    # ⚠️ 必须落在 **Assets/ 之外**（Tools/blender/），否则 Unity 会把 .blend 当模型导入。
    #    OUT_FBX 在 Assets/Models/Characters/ 下，所以是往上三层。
    blend_path = os.path.abspath(os.path.join(os.path.dirname(OUT_FBX),
                                              "..", "..", "..", "Tools", "blender", "raccoon_anim.blend"))
    saved = ""
    try:
        os.makedirs(os.path.dirname(blend_path), exist_ok=True)
        bpy.ops.wm.save_as_mainfile(filepath=blend_path, copy=False)
        saved = blend_path
    except Exception as e:
        saved = "保存失败: %s" % e

    return {
        "calib_up_per_local_m": round(k, 8),
        "arm_bone_lengths_cm": rig_report,
        "foot_rig": foot_report,
        "foot_scale": foot_scale_report,
        "backpack": backpack_report,
        "foot_joints": foot_joint_report,
        "clips": "; ".join("%s f0-%d loop=%s curves=%d 接触补帧=%d" % m for m in made),
        "ankle_solves": len(ankle_rows),
        "ankle_log": "\n".join(ankle_rows),
        "out": OUT_FBX,
        "out_size": os.path.getsize(OUT_FBX),
        "separate_dir": SEPARATE_DIR,
        "rigged_out": RIGGED_OUT,
        "rigged_size": os.path.getsize(RIGGED_OUT),
        "separate": "; ".join("%s %dKB" % (n, kb // 1024) for n, kb in per_file),
        "meta_sync": "; ".join(META_SYNC),
        "viewport_left_at": "RaccoonStand f0（两臂在身侧）",
        "blend_saved": saved,
    }


result = main()
