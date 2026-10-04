# Unity/PhysX 摔倒稳定性检索记录

日期：2026-10-04

## 主要来源

- Unity 6 Manual: [Joint and Ragdoll stability](https://docs.unity3d.com/6000.0/Documentation/Manual/RagdollStability.html)
- Unity 6 Scripting API: [Physics.defaultContactOffset](https://docs.unity3d.com/ScriptReference/Physics-defaultContactOffset.html)
- Unity 6 Scripting API: [Physics.IgnoreCollision](https://docs.unity3d.com/ScriptReference/Physics.IgnoreCollision.html)

## 官方结论

1. 不要直接修改与 Joint 相连的运动学 Rigidbody 的 Transform。这样会跳过 PhysX 对内部速度的计算，可能导致关节抖动和异常拉扯。应使用 Rigidbody.MovePosition/MoveRotation，或使用 Joint Drive 驱动目标姿态。
2. 首先避免 Rigidbody 与地面或彼此初始重叠。`maxDepenetrationVelocity` 只能让脱穿透过程更慢，不能替代正确的碰撞体位置。
3. 关节抖动时，可以把 Solver Iterations 提高到约 10～20；这属于约束稳定性辅助措施。
4. 避免连接刚体之间存在超过约十倍的质量差异，否则容易出现不稳定。
5. Joint Projection 可用于关节严重拉伸、约束无法满足时的兜底修正，但不应作为持续维持姿态的主要手段。
6. 避免不同于 1 的 Rigidbody/Joint 层级缩放，并避免在动态物理阶段持续通过 Transform 传送角色。

## 对当前项目的对应判断

- 上半身重心反馈原先直接写入代理 Transform；这与 Unity 官方关于 Joint Rigidbody 的警告相符，可能造成摔倒起步时抖动。
- 当前代理碰撞体是骨骼线段胶囊体，只是近似覆盖渲染网格，不是几何完美匹配。
- 内部代理碰撞已忽略，这可以减少肢体互相顶开，但不能解决代理与地面的初始重叠。
- 髋部和手部的质量比例偏大，后续需要重新检查质量分布。

## 本轮实施

将站立阶段的上半身反馈改为在 FixedUpdate 中调用 Rigidbody.MovePosition/MoveRotation，避免直接写入连接 Joint 的代理 Transform。摔倒阶段保持现有混合物理流程，不加入地面抬升或最终姿态锁定。

第二轮将代理刚体的最小质量设为 0.5，避免手部等小刚体与髋部形成过大的质量差；将代理刚体的 Solver Iterations 和 Solver Velocity Iterations 初始设为 12，并将最大脱穿透速度限制为 0.5。碰撞体尺寸和摔倒姿态逻辑本轮未改动。

第三轮取消受控摔倒阶段对角色根节点的整体 Transform 旋转和位移。改为缓存所有代理刚体的摔倒起始世界姿态，在 FixedUpdate 中逐个通过 MovePosition/MoveRotation 推进受控摔倒，角色根节点保持稳定，以避免通过父 Transform 间接瞬移关节刚体，并避免第三人称相机跟随根节点摔倒。

第四轮实测发现脚骨骼高度约为 0.075、脚趾约为 0.006，而原脚部胶囊半径为 0.075，导致站立时碰撞体最低点已经低于平台顶面。现将脚部实际碰撞半径调整为 0.045，并增加 0.055 的初始向上偏移；手部最小半径调整为 0.045。运行时检查显示站立状态脚部代理最低点约为 y=0.051，高于平台顶面 y=0，避免初始穿透。

在后续覆盖测试中，将脚部覆盖半径扩大到 0.06、向上偏移扩大到 0.075，手部覆盖半径扩大到 0.055；该调整保持脚部碰撞体的最低点高于平台，同时增加对鞋底、手掌和手指边缘的覆盖。

## 后续建议

1. 验证运动学到动态切换时是否仍存在地面重叠。
2. 按躯干部位重新拟合碰撞体尺寸和中心偏移。
3. 再单独测试质量比例、Solver Iterations、Contact Offset 和 Projection，避免一次修改多个变量。
