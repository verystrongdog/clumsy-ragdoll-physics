# RaccoonStep 走路系统实施方案

## 目标

在 `StepByStepTest` 场景中，使用 `Assets/Models/Characters/111_raccoon_rigged.fbx` 实现类似《Baby Steps（一​​步一脚印）》的物理走路：角色通过左右脚交替迈步、转移身体重心和处理地面摩擦来前进，并允许失衡、滑倒和摔倒。

## 重要约束

现有 `ClumsyRagdoll` 系统是《人类一败涂地》式松散布娃娃系统，与本方案冲突。新系统必须完全独立：

- 不复用或修改 `ClumsyRagdollGame`。
- 不使用 `ClumsyRagdoll`、`ClumsyController`、`ClumsyBalance`、`ClumsyCarry`、`ClumsyStepActivity` 或 `StepByStepMotor`。
- 使用独立命名空间 `RaccoonStep`。
- 新角色必须使用自己的 Rigidbody、ConfigurableJoint、Collider、平衡和步态脚本。
- 保留旧系统，新增角色可以单独运行、隐藏和删除。
- 角色位移必须由脚步和物理重心转移产生，不能直接传送或直接设置根节点位置。

## 当前项目情况

- 实际场景名称：`StepByStepTest`。
- 浣熊模型：`Assets/Models/Characters/111_raccoon_rigged.fbx`。
- 当前场景根对象包含旧的 `ClumsyRagdollGame`，新角色不要挂在旧角色上。
- 建议新增独立对象 `RaccoonStepCharacter`，与旧系统并列放置。

## 目录和脚本

新增目录：

```text
Assets/Scripts/RaccoonStep/
```

建议脚本：

```text
RaccoonStepCharacter.cs  角色总控和状态机
RaccoonBoneMap.cs        FBX 骨骼引用和映射
RaccoonRagdollBuilder.cs 编辑器工具，生成物理部件和关节
RaccoonPhysicsBody.cs    Rigidbody、Collider 和关节运行时管理
RaccoonStepMotor.cs      左右脚交替迈步
RaccoonFootController.cs 单脚目标、抬脚、摆动和落地
RaccoonBalance.cs        重心、支撑区域、姿态修正和失衡判定
RaccoonInput.cs          键盘、鼠标和手柄输入抽象
RaccoonCamera.cs         第三人称相机
RaccoonDebugHUD.cs       调试信息显示
```

所有脚本使用：

```csharp
namespace RaccoonStep
{
}
```

## 模型导入和骨骼映射

第一步必须扫描 FBX 层级，确认实际骨骼名称，不得猜测名称。至少需要：

```text
Root
Pelvis / Hips
Spine
LeftUpperLeg
LeftLowerLeg
LeftFoot
RightUpperLeg
RightLowerLeg
RightFoot
LeftUpperArm
LeftLowerArm
LeftHand
RightUpperArm
RightLowerArm
RightHand
Head
```

使用 `RaccoonBoneMap` 保存实际 Transform 引用，不要在运行时依赖字符串搜索：

```csharp
public sealed class RaccoonBoneMap : MonoBehaviour
{
    public Transform root;
    public Transform pelvis;
    public Transform spine;
    public Transform leftUpperLeg;
    public Transform leftLowerLeg;
    public Transform leftFoot;
    public Transform rightUpperLeg;
    public Transform rightLowerLeg;
    public Transform rightFoot;
}
```

建议使用 Generic Rig，以保留确定的骨骼结构。关闭 Root Motion，确认模型单位、朝向和脚底高度。视觉模型和物理根节点分离：

```text
RaccoonStepCharacter
├── VisualModel
├── PhysicsRoot
├── GroundProbe
└── CameraTarget
```

## 新布娃娃系统定位

新系统是“主动布娃娃”，不是松散布娃娃：

- 主要身体部件使用 Rigidbody。
- 身体部件通过 ConfigurableJoint 连接。
- Angular Drive 保持骨架基本形状。
- 脚部使用独立的接地检测和位置伺服。
- 躯干保持大致直立，但允许倾斜、滑步和摔倒。
- 第一阶段只做骨盆、躯干、双腿和双脚，手臂和头部后置。

## 核心步态

每次只允许一只脚离地：

```text
Stable
  -> PreparingStep
  -> SwingingFoot
  -> PlacingFoot
  -> TransferringWeight
  -> Stable
```

每一步：

1. 确定支撑脚和摆动脚。
2. 支撑脚锁定接触点。
3. 选择摆动脚落点。
4. 摆动脚沿弧线抬起并移动。
5. 根据地面法线调整脚掌姿态。
6. 确认脚底接地。
7. 将骨盆和身体重心送到新脚上。
8. 新脚成为支撑脚，另一只脚进入下一步。

只有在摆动脚接地、重心接近支撑区域、身体倾斜低于阈值且支撑脚没有明显滑动时，才允许换脚。

## 输入方案

第一版使用便于调试的鼠标方案：

- `Q`：选择左脚。
- `E`：选择右脚。
- 鼠标射线命中地面，显示落脚点。
- 鼠标左键确认迈步。
- `WASD` 控制目标方向。
- `R` 重置角色。
- `Tab` 切换手动步态和自动步态。

输入层使用 `FootCommand` 抽象，以便后续加入手柄：

```csharp
public struct FootCommand
{
    public int footIndex;
    public Vector3 targetPoint;
    public Vector3 targetNormal;
    public bool commit;
}
```

## 平衡系统

`RaccoonBalance` 独立计算：

- 身体总重心。
- 双脚支撑区域。
- 重心投影位置。
- 重心到支撑区域的距离。
- 身体倾斜角。
- 支撑脚滑动速度。

状态：

```text
Stable
PreparingStep
SwingingFoot
PlacingFoot
TransferringWeight
Unstable
Falling
Recovering
```

平衡力矩分为姿态恢复、重心修正和失衡判定三层。第一版摔倒后延迟约 1.5 秒重置，稳定后再实现从地面起身。

## 初始参数建议

所有距离应根据模型尺寸校准，先使用腿长比例：

```text
StepLength       = 腿长 × 0.35 ~ 0.55
StepDuration     = 0.35 ~ 0.60 秒
SwingClearance   = 腿长 × 0.10 ~ 0.20
MaxStepDistance  = 腿长 × 0.60
BalanceThreshold = 身高 × 0.05 ~ 0.10
```

## 动画和物理

Rigidbody 决定身体和脚的位置，动画只辅助目标旋转：

- 摆臂。
- 躯干摆动。
- 头部跟随。
- 膝盖朝向。
- 摔倒和起身。

动画不能直接控制角色位移，避免视觉脚步与碰撞体脱节。

## 场景实施

在 `StepByStepTest` 中新增：

```text
RaccoonStepCharacter
```

旧角色和旧系统保持不动。新浣熊先放在测试平台另一侧，与旧系统并列测试；新系统稳定后再隐藏旧角色。

## MCP 执行顺序

1. 扫描 `111_raccoon_rigged.fbx` 的骨骼层级和尺寸。
2. 创建 `RaccoonBoneMap`。
3. 创建独立物理角色和 `RaccoonRagdollBuilder`。
4. 生成骨盆、躯干、双腿和双脚的 Rigidbody、Collider、ConfigurableJoint。
5. 实现单步：支撑脚锁定、摆动脚弧线、脚底接地。
6. 实现左右脚交替。
7. 实现连续前进和转向。
8. 实现重心和平衡。
9. 实现失衡、摔倒和重置。
10. 检查 Console、场景层级和组件引用。
11. 最后加入手臂、头部、动画和交互。

核心验收顺序：

```text
站稳
-> 左脚迈一步
-> 右脚迈一步
-> 连续前进
-> 转向
-> 斜坡和障碍物
-> 失衡
-> 摔倒并重置
```

## 交给 MCP AI 的任务提示

```text
在当前 Unity 项目中，为场景 StepByStepTest 创建一套完全独立的 RaccoonStep 角色系统。

模型：Assets/Models/Characters/111_raccoon_rigged.fbx

不得复用或修改 ClumsyRagdollGame，也不得使用 ClumsyRagdoll、ClumsyController、ClumsyBalance、ClumsyCarry、ClumsyStepActivity 或 StepByStepMotor。

创建独立命名空间 RaccoonStep 和目录 Assets/Scripts/RaccoonStep/。
先扫描 FBX 骨骼和模型尺寸，不得猜测骨骼名称。创建 RaccoonBoneMap 保存实际骨骼引用。

创建新的主动布娃娃系统：
- Rigidbody + ConfigurableJoint + Collider；
- 第一阶段只包含骨盆、躯干、双腿和双脚；
- 支撑脚保持接触点；
- 摆动脚沿弧线移动到地面落脚点；
- 脚底接地后进行重心转移；
- 使用独立的 RaccoonStepMotor 和 RaccoonBalance；
- 角色位移必须由脚步和物理产生，不能传送根节点；
- 重心离开支撑区域后进入 Falling；
- R 键重置角色。

第一版输入：Q/E 选择左右脚，鼠标射线预览落脚点，鼠标左键确认迈步，WASD 控制方向，Tab 切换手动和自动步态。

保留旧系统和旧角色。新增 RaccoonStepCharacter 与旧系统并列放置在 StepByStepTest 场景中。

实现顺序：扫描骨骼 -> 生成独立物理角色 -> 完成单步 -> 左右脚交替 -> 连续行走 -> 平衡和摔倒 -> 动画表现。

每个阶段完成后检查 Unity Console、场景层级和组件引用，不要擅自修改旧 ClumsyRagdoll 系统。
```
