# 一步一脚印布娃娃系统重构方案

## 1. 目标与范围

本方案只针对当前场景 `Assets/Scenes/StepByStepTest.unity` 使用的“一步一脚印”系统，也就是 `RaccoonStep` 系列脚本。

项目中还存在另一套旧的、偏“人类一败涂地”风格的 `ClumsyRagdoll` 系统。本方案不把两套系统合并，也不把 `ClumsyController`、`ClumsyCarry`、`ClumsyRagdoll` 作为当前一步一脚印系统的核心依赖。

当前场景的实际角色对象为 `RaccoonHandsDownTest`，主要组件为：

- `RaccoonBoneMap`
- `RaccoonStepCharacter`
- `RaccoonPhysicsRig`
- `RaccoonFootController`
- `RaccoonBalance`
- `RaccoonMouseInput`
- `RaccoonLegMechanism`
- `RaccoonLegStepController`
- `RaccoonAlternatingLegController`
- `RaccoonCenterOfMassBalance`
- `RaccoonSitRecoveryController`
- `SkeletonDebugVisualizer`

根对象上的 `ClumsyRagdollGame` 在当前场景中主要承担模式入口作用。启用 `UseStandaloneStepByStep` 后，它创建 `StepByStepPrototype`，不会创建旧的 `ClumsyRagdoll` 角色系统。

## 2. 当前系统边界

```text
RaccoonMouseInput
        ↓
RaccoonStepCharacter
        ↓
步态与迈步控制
 ├─ RaccoonFootController
 ├─ RaccoonLegMechanism
 ├─ RaccoonLegStepController
 └─ RaccoonAlternatingLegController
        ↓
平衡与恢复
 ├─ RaccoonBalance
 ├─ RaccoonCenterOfMassBalance
 └─ RaccoonSitRecoveryController
        ↓
RaccoonPhysicsRig
        ↓
代理刚体、ConfigurableJoint、碰撞体
        ↓
视觉骨骼与渲染模型
```

核心原则：

1. 导入的视觉骨骼不是物理刚体本身。
2. `RaccoonPhysicsRig` 管理物理代理和关节生命周期。
3. 步态系统通过物理代理驱动脚步，不直接修改视觉网格。
4. 平衡系统计算力、力矩或目标，不能同时承担代理创建职责。
5. 恢复系统可以临时改变恢复关节，但不应掌管整个物理刚体集合。
6. `StepByStepPrototype` 是组合根，只负责找到组件、注入依赖和设置运行顺序。

## 3. 分阶段重构计划

### 阶段一：建立基线，不改变行为

记录当前可接受行为：

- 场景可以进入 Play Mode；
- 角色可以生成物理代理；
- 角色可以站立和移动；
- 左右脚能够交替迈步；
- 脚接触地面时不会持续漂移；
- 角色坐下或失衡后可以触发恢复；
- 视觉模型与物理代理保持合理同步；
- 退出 Play Mode 后不残留运行时生成对象。

每次拆分后都必须重新检查这些行为。

### 阶段二：拆分 `RaccoonPhysicsRig`

目标文件：

`Assets/Scripts/RaccoonStep/RaccoonPhysicsRig.cs`

该文件目前职责过多，应逐步拆成以下内部服务或独立模块：

```text
RaccoonPhysicsRig
 ├─ RaccoonProxyBuilder
 │   └─ 创建物理代理、Collider、Rigidbody
 ├─ RaccoonProxyRegistry
 │   └─ 按骨骼 ID 查询代理部件和刚体
 ├─ RaccoonJointConfigurator
 │   └─ 配置 ConfigurableJoint 的运动限制和驱动参数
 ├─ RaccoonPhysicsLifecycle
 │   └─ 初始化、启用、禁用、销毁和重建物理代理
 └─ RaccoonRecoveryJoint
     └─ 管理恢复阶段临时使用的脊柱关节
```

第一步优先抽取无状态或低状态的逻辑，不立即移动场景组件。`RaccoonPhysicsRig` 暂时保留为兼容外壳，继续对外提供现有字段和方法。

### 阶段三：拆分平衡系统

目标文件：

- `RaccoonBalance.cs`
- `RaccoonCenterOfMassBalance.cs`

建议职责：

```text
RaccoonBalance
 └─ 基础直立和目标方向控制

RaccoonCenterOfMassEstimator
 └─ 质量、重心、速度和支撑点测量

RaccoonBalanceController
 └─ 根据测量结果计算平衡力和力矩

RaccoonSupportState
 └─ 接地、单脚支撑、双脚支撑和失衡状态
```

测量模块只输出数据，控制模块才施加物理影响，避免同一个脚本既读取状态又修改所有刚体。

### 阶段四：拆分坐下与恢复系统

目标文件：

`RaccoonSitRecoveryController.cs`

建议拆成：

```text
RaccoonPostureStateDetector
 └─ 判断站立、坐下、躺倒、恢复中

RaccoonRecoveryStateMachine
 └─ 管理恢复阶段和状态转换

RaccoonRecoveryPosePlanner
 └─ 计算恢复时的脊柱、骨盆和腿目标

RaccoonRecoveryJoint
 └─ 通过 RaccoonPhysicsRig 创建和释放临时恢复关节
```

恢复系统不直接重新创建整套物理代理，只通过 `RaccoonPhysicsRig` 请求代理或关节操作。

### 阶段五：整理步态和迈步系统

涉及文件：

- `RaccoonFootController.cs`
- `RaccoonLegMechanism.cs`
- `RaccoonLegStepController.cs`
- `RaccoonAlternatingLegController.cs`

目标结构：

```text
RaccoonGaitCoordinator
 ├─ RaccoonStepPlanner
 │   └─ 决定何时迈步、迈哪只脚、落脚点在哪里
 ├─ RaccoonLegController
 │   └─ 管理单条腿的状态和关节目标
 └─ RaccoonFootMotor
     └─ 对脚部物理代理施加移动或姿态驱动
```

步态系统的输出应尽量是数据，例如：

```csharp
public struct RaccoonStepCommand
{
    public bool HasStep;
    public bool LeftFoot;
    public Vector3 TargetPosition;
    public float Progress;
}
```

脚步规划和脚部物理执行分离后，未来可以替换步态策略，而不必修改刚体驱动代码。

### 阶段六：整理视觉同步与调试显示

涉及文件：

- `RaccoonBoneMap.cs`
- `RaccoonStepCharacter.cs`
- `SkeletonDebugVisualizer.cs`

建议职责：

```text
RaccoonBoneMap
 └─ 只负责骨骼引用和骨骼 ID 映射

RaccoonStepCharacter
 └─ 只负责视觉模型与物理代理的生命周期和同步入口

SkeletonDebugVisualizer
 └─ 只负责调试绘制，不参与物理控制
```

调试可视化不能反向驱动角色，也不能创建或修改物理关节。

### 阶段七：收窄组合根

`StepByStepPrototype` 最终只负责：

- 查找 `RaccoonHandsDownTest`；
- 获取各个系统组件；
- 注入依赖；
- 设置更新顺序；
- 提供测试和演示入口。

它不应负责：

- 计算重心；
- 直接修改关节限制；
- 直接给脚部刚体施加具体力；
- 编写恢复算法；
- 管理视觉网格材质。

## 4. 与旧 ClumsyRagdoll 系统的隔离规则

以下脚本属于旧系统或旧系统兼容层，不作为当前一步一脚印重构的目标：

- `ClumsyRagdoll.cs`
- `ClumsyRagdollControl.cs`
- `ClumsyRagdollGame.cs` 中旧系统分支
- `ClumsyCarry.cs`
- `ClumsyInteraction.cs`
- `ClumsyArmIK.cs`
- `RagdollJointConfigurator.cs`

当前一步一脚印系统不应新增对这些类型的依赖。若未来需要共享功能，应提取到与具体系统无关的接口或纯数据结构，而不是让 `RaccoonStep` 直接引用 `ClumsyRagdoll`。

## 5. 兼容与实施策略

1. 不直接删除现有 `MonoBehaviour`。
2. 不改变当前场景中已有组件的序列化字段名称。
3. 先增加新服务，再让旧组件转发调用。
4. 确认新服务稳定后，才删除重复实现。
5. 每次只拆一个职责，避免同时改变步态、质量和恢复逻辑。
6. 所有运行时创建对象必须有明确的 owner，并在销毁时由 owner 清理。
7. 所有物理代理查询尽量通过统一的部件注册表完成，减少 `GetComponent` 和字符串查找散落。

## 6. 验收标准

每个阶段完成后执行：

- Unity 编译无错误；
- Console 无项目脚本错误；
- 场景无 Missing Script；
- Play Mode 可以正常进入和退出；
- 不产生孤儿物理代理、Root 或临时关节；
- 角色仍能站立、移动、迈步和恢复；
- 视觉骨骼与物理代理仍保持同步；
- 当前一步一脚印系统不新增对 `ClumsyRagdoll` 的引用；
- 旧 ClumsyRagdoll 场景仍可以单独编译和运行。

## 7. 当前状态

已完成的基础工作主要位于旧 `ClumsyRagdoll` 系统，不能等同于一步一脚印系统已经完成重构。

当前一步一脚印系统的下一项正确工作是：

> 在保持 `RaccoonPhysicsRig` 对外接口不变的前提下，先抽取代理注册、代理创建和关节配置职责。

