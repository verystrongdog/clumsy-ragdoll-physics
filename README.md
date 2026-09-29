# 笨拙布娃娃物理

给联机游戏用的人形物理原型：人自己不播走路动画，身体是一具软布娃娃，玩家只决定「往哪挪、手往哪伸」。手感靠近《人类一败涂地》那种站不稳、抓得住、也容易脱手的控制。

联机还没接。现在两名玩家在**同一块屏幕**里试，输入先收成同一种指令，以后主机照这套指令驱动角色。

Unity **6000.0.49f1**。渲染用内置管线，打开就能进 Play，不依赖 URP 资源。

## 怎么跑

1. 用 Unity 6000.0.49f1 打开本目录（不是打开某一个子文件夹）。
2. 等脚本编译完。
3. 任意场景直接按 Play。场景是空的也没关系，播放时会自己生成角色和道具。

第一次打开会把产品名写成「笨拙布娃娃物理」。

## 操作

两人共用一个镜头。镜头会拉开，把两个人都放进画面。

| | 玩家一（珊瑚） | 玩家二（青蓝） |
| --- | --- | --- |
| 移动 | WASD | 方向键 |
| 手 | 鼠标指哪，手就往哪软过去 | IJKL 决定方向，U / O 抬高或放低 |
| 抓取 / 攀爬 | 按住鼠标左键 | 按住右 Ctrl |
| 跳 | 空格 | 右 Shift |
| 重生 | R | M |

共用：

- Q / E 旋转镜头，滚轮拉近拉远
- `-` 更僵硬、更好站稳；`=` 更松、更容易倒
- H 收起或打开说明
- F2 两个人一起重生

试这些：撞倒面前的多米诺，拖轻木箱，去推那只深色重箱，抓黄色把手或单杠把自己吊起来，站上跷跷板。抓朋友的胳膊也行。

## 角色怎么动

每个角色大约 1.5 米，短粗，大手。部位是独立刚体，用 Configurable Joint 连住，关节角度不锁死，所以会折、会扭。

每帧做三件事：

1. **意图**。移动、起跳、瞄准点、抓不抓，都写进 `PuppetCommand`。电机不读键盘。
2. **目标姿态**。腿用两段 IK 做交替迈步，脚落地时会粘一下；手臂用两段 IK 伸向瞄准点；躯干顺着骨盆，稍微前倾。
3. **肌肉**。每块骨头用 PD 扭矩去追目标旋转，扭矩有上限，父骨骼会吃到反作用力。追不上、撞到东西，人就倒。倒了以后扶正变弱，再按移动或跳跃才会使劲爬起来。

松软度会同时减弱肌肉、扶正和攀爬时的拉身体。默认在中间，先能走，再往松了调。

手靠近物体时按住抓取会接上一根会断的 Fixed Joint。抓运动学物体（墙、把手、单杠、平台）时，身体会被往手上拉，用来爬。抓普通刚体时不拉身体，靠手臂把东西拖走；太重，关节会断，人会被带倒。地面和界桩标了 `NotGrabbable`，避免手粘在地上。

物理步长 0.01 秒，求解迭代偏高，用来换稳定，不是最终成本。

## 和以后联机的接缝

`Assets/Scripts/Runtime/PuppetCommand.cs` 里的 `PuppetCommand` 就是以后要同步的玩家意图：

- `Move`：镜头右 / 前，长度不超过 1
- `CameraYaw`：发令时**这名玩家自己的**镜头朝向。同屏阶段两个人读同一个共享镜头
- `AimWorldPoint`：世界坐标瞄准点，由发令侧用自己的镜头算好
- `Reach`、`Grab`：按住状态
- `JumpPressed`、`RespawnPressed`：沿触发的一次性事件，本地输入会把它闩到下一次物理步，避免被吃掉

同屏只是把两个本地输入源接到两具 `PuppetMotor` 上。换联网时，不要同步原始按键，同步这个结构；模拟放在主机上，其他客户端收刚体姿态做插值。物理在两台机器上不会逐帧一致，所以不打算做双端各自模拟再硬对齐。

共享镜头只为同屏试玩。联机后每人一台摄像机，`CameraYaw` 和瞄准点仍由各自的客户端填进同一条指令。

## 目录

```
Assets/Scripts/Runtime/PuppetCommand.cs      指令，以及不能抓的标记
Assets/Scripts/Runtime/LocalPuppetInput.cs   两套键盘（和鼠标）
Assets/Scripts/Runtime/RagdollFactory.cs     拼一具人形
Assets/Scripts/Runtime/PuppetMotor.cs        步态、手臂、肌肉、平衡、爬
Assets/Scripts/Runtime/HandGrabber.cs        抓取和脱手
Assets/Scripts/Runtime/IkMath.cs             两段 IK 和 PD
Assets/Scripts/Runtime/SharedScreenCamera.cs 同屏镜头
Assets/Scripts/Runtime/PlaygroundBuilder.cs  测试场
Assets/Scripts/Runtime/GameBootstrap.cs      进 Play 就生成一切
```

## 手感不对时

- 人站不住，先按 `-` 降低松软度。
- 人太像木偶，按 `=`。
- 抽搐或飞出去：再松一点，或看 Console 有没有 NaN 之后的自动重生。掉出场地也会重生。
- 全身洋红：项目被切到了 URP，但这里的材质走内置 Standard。到 Project Settings → Graphics，把 Scriptable Render Pipeline Settings 清空。
- 字是方框：确认 `Assets/Resources/Ui.ttf` 还在。那是 Noto Sans SC 的子集，改名为 Clumsy Ui，授权在 `ThirdParty/NotoSansSC/OFL.txt`。
