# 今日工作进展（2026-10-03）

## 项目

- Unity 项目：`clumsy-ragdoll-physics`
- 当前场景：`Assets/Scenes/StepByStepTest.unity`

## 今日完成

### 1. R 键完整复位

在 `RaccoonPhysicsRig` 中加入代理刚体初始姿态缓存：

- 缓存所有代理刚体的初始 `localPosition` 和 `localRotation`。
- 新增 `ResetToInitialPose()`。
- 复位时清除刚体速度和角速度。
- 恢复所有代理刚体的位置和旋转。
- 同步视觉骨骼到代理姿态。

在 `RaccoonCenterOfMassBalance.ResetAfterFall()` 中完善复位：

- 清除摔倒状态和摔倒计时。
- 清除手动重心偏移及其计时。
- 清除 WASD 重心输入残留。
- 调用 `StepController.ResetToStandingPose()`。
- 调用 `PhysicsRig.ResetToInitialPose()`。
- 恢复角色稳定状态。

现在 R 键的目标是恢复到开局位置、开局姿态和开局重心状态，而不是只停止摔倒逻辑。

### 2. 修复 WASD 上半身不移动

发现视觉骨骼同步逻辑错误地要求 `PhysicsActive == true`。站立状态下代理刚体是运动学刚体，但 WASD 仍会改变代理姿态，因此视觉骨骼也必须同步。

已移除该限制。现在站立状态和摔倒状态都会同步代理姿态，WASD 可以继续调整上半身重心。

## 验证结果

- Unity 刷新完成。
- 两个修改脚本均通过标准校验。
- 无新增编译错误。
- 当前 Unity 已进入 Play 模式。

校验工具提示的警告属于已有的 Rigidbody 操作/字符串拼接建议，不是编译错误。控制台中的 `QuaternionToEuler: Input quaternion was not normalized` 仍需后续单独定位，但不影响本次脚本编译和 Play 启动。

## 当前阶段判断

第一阶段的核心逻辑已经具备：

- 静止站立状态下用 WASD 调整上半身重心。
- 重心持续超出阈值后开始计时。
- 持续超时后按偏移方向受控摔倒。
- 摔倒后停止继续修正。
- R 键恢复初始状态。

## 下一阶段（二阶段）计划

二阶段将把重心失衡扩展到行动过程中：

1. 行走时实时计算整体重心。
2. 根据单脚支撑和双脚支撑计算不同支撑范围。
3. 处理支撑脚切换和迈步过程中的短暂不稳定。
4. 判断重心投影是否持续超出当前支撑区域。
5. 在行走、急停、转向或落脚失败时触发摔倒。
6. 保留动作惯性，使摔倒来自行动过程，而不是直接播放静态倒地。

二阶段暂不加入复杂地形、完整跑步系统或全身高精度 ragdoll，先验证“行动中的支撑变化导致失衡摔倒”。
