# Clumsy Ragdoll Physics

Unity 物理角色项目，核心功能只有三项：

1. 角色交替步行；
2. 角色失去平衡后摔倒；
3. 角色摔倒后恢复到坐姿。

README 面向需要运行项目、查看实现和调用功能的开发者。

## 环境与启动

- Unity：`6000.0.49f1`
- 场景：`Assets/Scenes/StepByStepTest.unity`
- 默认角色：`RaccoonHandsDownTest`

打开仓库根目录后，打开上述场景并点击 Play。场景中的 `StepByStepPrototype` 会自动找到角色，并绑定骨骼、物理代理、行走、平衡和起身组件。

## 三个核心功能

### 1. 行走

行走由 `RaccoonAlternatingLegController` 实现：

1. 鼠标点击位置被投影到 `GroundHeight` 水平面，得到落脚目标；
2. 当前一条腿保持为支撑腿，脚的位置作为世界空间锚点；
3. 另一条腿进入抬脚、摆动和落地阶段；
4. 使用两段 IK 解算大腿和小腿，脚底目标保持在地面接触平面；
5. 脚落地后交换支撑腿，形成左右交替步行。

鼠标左键请求一步，鼠标位置决定落脚方向和目标点。

关键参数：

| 参数 | 作用 |
| --- | --- |
| `StepDuration` | 摆动脚移动到目标的时间 |
| `LandingDuration` | 脚落地过渡时间 |
| `LiftHeight` | 抬脚高度 |
| `NormalStepDistance` | 默认步长 |
| `MaxReachScale` | IK 最大可达腿长比例 |
| `KneeForwardBias` | 膝盖朝前的偏置 |
| `GroundHeight` | 脚底接触平面高度 |

实现文件：

```text
Assets/Scripts/RaccoonStep/RaccoonAlternatingLegController.cs
Assets/Scripts/RaccoonStep/RaccoonMouseInput.cs
```

### 2. 摔倒

`RaccoonCenterOfMassBalance` 根据物理代理刚体计算角色重心、支撑范围和身体倾角。当重心超出支撑范围或身体倾角超过阈值时，角色进入摔倒流程。

主要设置：

- `EnableFallResponse`：启用摔倒响应；
- `EnableAutomaticBalanceFall`：启用自动失衡摔倒；
- `FallingTiltAngle`：倾倒角度阈值；
- `FallBalanceDistance`：重心偏离支撑范围的阈值；
- `FallDuration`：摔倒姿态过渡时间。

`RaccoonPhysicsRig` 负责创建外置刚体、碰撞体和 `ConfigurableJoint`，并在摔倒时处理物理代理和可视骨骼之间的关系。

实现文件：

```text
Assets/Scripts/RaccoonStep/RaccoonCenterOfMassBalance.cs
Assets/Scripts/RaccoonStep/RaccoonPhysicsRig.cs
```

### 3. 起身

`RaccoonSitRecoveryController` 负责从摔倒姿态恢复到坐姿：

1. 暂停会与起身冲突的行走驱动；
2. 读取摔倒后的躯干和髋部姿态；
3. 通过物理代理逐步驱动躯干坐起；
4. 使用支撑和重心修正保持动作稳定；
5. 达到坐姿后保持当前姿态。

当前稳定版本只保证“摔倒后坐起”，不包含坐起后的收腿或站立流程。场景默认使用 `TorsoOnlyRecovery = true`。

主要设置：

- `EnableInput`：允许触发起身；
- `Duration`：坐起动作时长；
- `GroundHeight`：地面高度；
- `TorsoOnlyRecovery`：只执行躯干坐起并保持坐姿；
- `HoldSeatedPoseAfterRelease`：完成后保持坐姿。

实现文件：

```text
Assets/Scripts/RaccoonStep/RaccoonSitRecoveryController.cs
```

## 运行时组件关系

```text
StepByStepPrototype
├── RaccoonBoneMap                  骨骼映射
├── RaccoonPhysicsRig               外置刚体、碰撞体和关节
├── RaccoonAlternatingLegController 交替步行与腿部 IK
├── RaccoonCenterOfMassBalance      重心计算与摔倒检测
└── RaccoonSitRecoveryController    摔倒后坐起
```

`StepByStepPrototype.BindCharacter()` 是运行时绑定入口。它会把所有组件的 `BoneMap`、`PhysicsRig`、`Character` 和相互依赖的控制器引用连接起来。

## 代码调用

可以从场景中的 `StepByStepPrototype` 获取三个功能对应的控制器：

```csharp
using StepByStep;
using UnityEngine;

public class CharacterControllerAccess : MonoBehaviour
{
    public StepByStepPrototype Demo;

    public void WalkTo(Vector3 worldTarget)
    {
        if (Demo != null && Demo.IsBound && Demo.Gait != null)
            Demo.Gait.RequestStep(worldTarget);
    }

    public void ResetCharacter()
    {
        if (Demo == null || !Demo.IsBound)
            return;

        if (Demo.Recovery != null)
            Demo.Recovery.ResetRecovery();
        if (Demo.Gait != null)
            Demo.Gait.ResetToStandingPose();
        if (Demo.PhysicsRig != null)
            Demo.PhysicsRig.ResetToInitialPose();
    }
}
```

主要状态和入口：

```csharp
Demo.Gait.IsReady;                 // 行走控制器是否完成初始化
Demo.Gait.IsStepping;              // 是否正在执行一步
Demo.Gait.StepProgress;            // 当前步进进度 0~1
Demo.Gait.SupportFootWorld;        // 当前支撑脚世界坐标
Demo.Gait.ApplyBalanceCorrection(delta);
Demo.Gait.MaintainSupportFoot();
Demo.Recovery.ResetRecovery();
Demo.PhysicsRig.ResetToInitialPose();
```

## 目录

```text
Assets/Scenes/StepByStepTest.unity
Assets/Scripts/StepByStep/StepByStepPrototype.cs
Assets/Scripts/RaccoonStep/RaccoonAlternatingLegController.cs
Assets/Scripts/RaccoonStep/RaccoonMouseInput.cs
Assets/Scripts/RaccoonStep/RaccoonCenterOfMassBalance.cs
Assets/Scripts/RaccoonStep/RaccoonSitRecoveryController.cs
Assets/Scripts/RaccoonStep/RaccoonPhysicsRig.cs
Assets/Scripts/RaccoonStep/RaccoonBoneMap.cs
```

## 问题定位

- 行走不动：检查 `StepByStepPrototype.IsBound`、`Gait.IsReady`、`GroundHeight` 和 `RaccoonBoneMap.IsValid`。
- 脚底高度异常：检查步行控制器的 `GroundHeight`，以及 `RaccoonPhysicsRig` 的脚部代理碰撞体设置。
- 角色不摔倒：检查 `EnableFallResponse`、`EnableAutomaticBalanceFall` 和倾角/重心阈值。
- 摔倒后不能坐起：检查 `RaccoonSitRecoveryController` 是否引用同一个 `BoneMap` 和 `PhysicsRig`，并确认没有其他脚本同时驱动躯干。
- 连续测试状态污染：调用 `Recovery.ResetRecovery()`，再调用 `PhysicsRig.ResetToInitialPose()`。
