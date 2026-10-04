using UnityEngine;

namespace ClumsyRagdoll
{
    /// <summary>
    /// 教程 P6 的三人称相机 + **Blender 那套视口导航**。
    ///
    /// 教程的三层结构：
    ///   <c>Root</c>（Rigidbody + 冻结 Rotation，绕它公转）→ <c>Target</c>（空物体，摆在头顶）
    ///   → <c>Camera</c>（挂在 Target 下，**不要直接挂 Root**）。
    /// 本工程的 Root 由 <see cref="ClumsyRagdoll"/> 建（运动学刚体、只转 yaw、位置跟随骨盆），
    /// 这里只补 Target 与 Camera。
    ///
    /// 教程 P6 步8 的 CamControl：
    ///   mouseX → Root 的 yaw + hips 的 targetRotation；mouseY → stomach 的 targetRotation。
    /// 本工程：mouseX 写 Root.rotation（hips 的角驱动参考系就是 Root，所以不必再写一次）；
    ///         mouseY 走相机自身的 pitch，同时把 <see cref="PitchDegrees"/> 交给
    ///         <see cref="ClumsyPoseDriver"/> 去写 spine 的 targetRotation —— 这正是教程
    ///         「上下看同时带动身体」的意图，且只有**一个**写入者（教程里 CopyMotion 与
    ///         CamControl 会同时写同一个 targetRotation，那是个竞态）。
    ///
    /// ==================================================================
    /// 2026-10-01 追加：**相机导航照抄 Blender 视口**（owner：「套用 blender 的相机逻辑就可以了」）
    ///
    /// 对照表（左边是 Blender 的手册原词，右边是本文件的字段）：
    ///   Orbit（MMB 拖）           → <see cref="OrbitYaw"/> / <see cref="OrbitPitch"/>
    ///   Pan（Shift-MMB 拖）       → <see cref="PivotOffset"/>（枢轴偏移）
    ///   Zoom In/Out（Wheel、Ctrl-MMB 拖）→ <see cref="DistanceScale"/>（指数式，朝枢轴推拉）
    ///   Center View to Mouse（Alt-MMB）  → 把光标下的那个点设成新枢轴
    ///   绕「point of interest」转  → 枢轴 = Target + <see cref="PivotOffset"/>，相机永远看着它
    ///
    /// 三条与 Blender 一致的约定（改之前先想清楚，别一个人一个方向）：
    ///   ① **Turntable**（Blender 默认的 Orbit Method）：地平线永远水平，只有 yaw+pitch 两个自由度。
    ///      本文件用 Euler(OrbitPitch, OrbitYaw, 0) 参数化，天然就是 turnable。
    ///   ② **拖动方向 = 抓住物体拖**（这也是 Blender 的手感）：右拖 → 相机往左绕、物体看着往右转；
    ///      下拖 → 相机抬高、看到顶面。所以两个分量都是「鼠标反着来」。
    ///   ③ **平移/缩放都朝枢轴**：Pan 挪的是枢轴（相机跟着挪，视线方向不变）；
    ///      Zoom 改的是相机到枢轴的距离 —— 与手册那句
    ///      "Moves the view closer to, or further away from, the point of interest" 一致。
    ///
    /// 导航期间鼠标不再驱动角色朝向与躯干俯仰（否则「转相机」会顺手把角色也转走，
    /// 就没法从固定角度看同一个动作了）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClumsyCameraRig : MonoBehaviour
    {
        public ClumsyRagdoll Ragdoll;
        public RagdollRecipe Recipe;

        public Transform Target;
        public Camera Cam;

        public float Yaw;

        public float Pitch = 12f;

        /// <summary>鼠标指针是否锁定。教程 §5.2 项8 自相矛盾（"lock the cursor by doing lockState = None"），这里默认锁。</summary>
        public bool LockCursor = true;

        /// <summary>给姿势驱动的躯干俯仰（度）。</summary>
        public float PitchDegrees
        {
            get { return -Pitch; }
        }

        /// <summary>推拉倍率。乘在 <see cref="RagdollRecipe.CameraDistance"/> 上。</summary>
        public float DistanceScale = 1f;

        // ==================================================================
        // Blender 式导航
        // ==================================================================

        /// <summary>绕枢轴的水平角（度）。中键左右拖动改它。</summary>
        public float OrbitYaw;

        /// <summary>绕枢轴的俯仰（度）。中键上下拖动改它。**单独一个量**，不动 <see cref="Pitch"/>（那个会带动躯干）。</summary>
        public float OrbitPitch;

        /// <summary>枢轴相对 Target 的偏移（**Target 的局部坐标系**里）。
        /// Shift-中键平移改它；Alt-中键「以光标处为中心」也直接写它。
        /// 用局部系是为了让它跟着角色的朝向一起转 —— 角色转身后你看到的还是身上同一个部位。</summary>
        public Vector3 PivotOffset;

        /// <summary>中键绕轨灵敏度的**兜底值** —— 实际读 `Recipe.CameraOrbitSpeed`（搬进 Recipe 才能在 Inspector 里调）。</summary>
        public float OrbitSpeed = 8.0f;

        /// <summary>绕轨俯仰的限幅（度）。85 → 能钻到脚底下往上看，又不会越过极点把画面翻过来。
        /// （Blender 的 Turntable 越过极点会翻面；这里用限幅躲开那个别扭区，覆盖范围是一样的：
        /// yaw 转一圈 + pitch ±85°，除了正南北极，任何方向都够得着。）</summary>
        public float OrbitPitchLimit = 85f;

        /// <summary>Shift-中键平移速度（米 / 鼠标单位，再乘相机到枢轴的距离 —— Blender 也是越远挪得越快）。</summary>
        public float PanSpeed = 0.010f;

        /// <summary>滚轮缩放速度（指数）。0.1 × 2.2 ≈ 每格 ×0.80，与 Blender 滚一格的手感接近。</summary>
        public float WheelZoomSpeed = 2.2f;

        /// <summary>Ctrl-中键缩放速度（指数）。一屏拖动（约 100 单位）≈ 2.7 倍。</summary>
        public float DragZoomSpeed = 0.010f;

        /// <summary>推拉倍率的上下限。0.07 × CameraDistance(3.6) ≈ 0.25 m —— 贴到脸上看手。</summary>
        public float MinDistanceScale = 0.07f;
        public float MaxDistanceScale = 4f;

        public void Setup(ClumsyRagdoll ragdoll, RagdollRecipe recipe)
        {
            Ragdoll = ragdoll;
            Recipe = recipe;
            Build();
        }


        /// <summary>
        /// ★ 2026-10-02：运行期切换「相机跟不跟随角色」（按 `C`）。
        /// 跟随的本质是 Target 认不认 `Ragdoll.Root` 当爹 —— 见 <see cref="Build"/>。
        /// </summary>
        public void SetFollowTarget(bool follow)
        {
            if (Recipe != null)
                Recipe.CameraFollowRagdoll = follow;
            if (Target == null)
                return;
            if (follow && Ragdoll != null && Ragdoll.Root != null)
            {
                Target.SetParent(Ragdoll.Root, false);
                Target.localPosition = new Vector3(0f, Recipe != null ? Recipe.CameraHeight : 0.9f, 0f);
                Target.localRotation = Quaternion.identity;
            }
            else
            {
                Target.SetParent(null, true);   // 留在当前世界位置
            }
        }

        void Start()
        {
            if (Cam == null)
                Build();
        }

        public void Build()
        {
            if (Ragdoll == null || Ragdoll.Root == null)
                return;
            if (Recipe == null)
                Recipe = Ragdoll.Recipe;


            if (Target == null)
            {
                GameObject targetGo = new GameObject("Target");
                // ★ 2026-10-02 owner：「**暂时不要让游戏内的摄像机跟随小浣熊移动**」。
                //   相机跟随不是因为相机自己在跟，而是因为 Target **挂在 Ragdoll.Root 下面**，
                //   而 Root 每帧被 `SyncRootToPelvis` 拖到骨盆位置 ⇒ 相机跟着走。
                //   所以要«不跟随»，只需要**不认这个爹**：Target 留在世界里，之后不动。
                //   （仍然可以中键绕轨 / 滚轮推拉 —— 那套是绕 Target 的，与跟不跟随无关。）
                if (Recipe.CameraFollowRagdoll && Ragdoll.Root != null)
                    targetGo.transform.SetParent(Ragdoll.Root, false);
                Target = targetGo.transform;
            }
            if (Recipe.CameraFollowRagdoll)
            {
                Target.localPosition = new Vector3(0f, Recipe.CameraHeight, 0f);
                Target.localRotation = Quaternion.identity;
            }
            else
            {
                // 不跟随：开局摆在骨盆上方，之后**一直不动**
                Vector3 pelvis = Ragdoll.Hips != null && Ragdoll.Hips.Body != null
                    ? Ragdoll.Hips.Body.position : Vector3.zero;
                Target.position = pelvis + Vector3.up * Recipe.CameraHeight;
                Target.rotation = Quaternion.identity;
            }
            // 转 Root 会带着 hips 一起转（教程 P6 步10 的原话），所以相机挂在 Target 而不是 Root 上。
            if (Cam == null)
            {
                GameObject camGo = new GameObject("相机");
                Cam = camGo.AddComponent<Camera>();
                Cam.fieldOfView = 58f;
                Cam.nearClipPlane = 0.05f;
                Cam.farClipPlane = 400f;
                Cam.clearFlags = CameraClearFlags.Skybox;
                camGo.tag = "MainCamera";
            }
            Cam.transform.SetParent(Target, false);
            ApplyCameraLocal();

            Yaw = Ragdoll.Root != null ? Ragdoll.Root.eulerAngles.y : 0f;
        }

        /// <summary>
        /// 枢轴在世界里的位置 = Target（骨盆上方 CameraHeight）+ 平移偏移。
        /// 相机永远看着它，绕它转、朝它推拉 —— 就是 Blender 那个 "point of interest"。
        /// </summary>
        public Vector3 PivotWorld
        {
            get { return Target != null ? Target.TransformPoint(PivotOffset) : transform.position; }
        }

        /// <summary>相机到枢轴的距离（米）。</summary>
        public float Distance
        {
            get { return Recipe != null ? Recipe.CameraDistance * DistanceScale : 0f; }
        }

        /// <summary>
        /// 相机在 Target 下的位姿 = **绕枢轴的球面轨道**，视线永远指着枢轴。
        ///
        /// 为什么是真绕轨而不是「原地转」：原地转的话俯仰一大角色就跑出画面了
        /// （实测：俯仰 40° 时枢轴已经落到 viewport y = 1.65，屏幕外）。
        ///
        /// 用 <c>orbit * Vector3.up</c> 而不是 <c>Vector3.up</c> 当 LookRotation 的 up 提示：
        /// 俯仰接近 ±90° 时 forward 与世界 up 快平行了，会退化成乱滚；orbit 系的 up 与 forward 永远正交。
        ///
        /// **OrbitYaw = OrbitPitch = 0 且 PivotOffset = 0 时与最初逐位等价**：
        /// 位置 (0,0,−d)、旋转 Euler(Pitch,0,0)，所以跟随相机的默认取景没变（只改了注视点高度，见 Recipe.CameraHeight）。
        /// </summary>
        void ApplyCameraLocal()
        {
            if (Cam == null || Recipe == null)
                return;
            Quaternion orbit = Quaternion.Euler(OrbitPitch, OrbitYaw, 0f);
            Vector3 toCam = orbit * Vector3.back;                 // 枢轴 → 相机的方向（Target 局部系）
            Cam.transform.localPosition = PivotOffset + toCam * Distance;
            Cam.transform.localRotation = Quaternion.LookRotation(-toCam, orbit * Vector3.up)
                                        * Quaternion.Euler(Pitch, 0f, 0f);   // 再叠基础俯仰（鼠标 Y 那个）
        }

        void FixedUpdate()
        {
            if (Ragdoll == null || Ragdoll.Root == null || Recipe == null)
                return;

            if (LockCursor && !IsLandingSelectionActive())
            {
                if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
                    Cursor.lockState = CursorLockMode.Locked;
                if (Input.GetKeyDown(KeyCode.Escape))
                    Cursor.lockState = CursorLockMode.None;
            }

            // ★ 相机不再«独占» yaw：以 `Root` 为准（控制器也会转它），相机只加鼠标增量。
            if (Ragdoll != null && Ragdoll.Root != null)
                Yaw = Ragdoll.Root.eulerAngles.y;

            Navigate();

            // 教程 P6 步8：Quaternion.Euler(0, mouseX, 0) 写到 Root 的旋转上
            // ★ 2026-10-02 转身：**这条死路已经删掉** —— 相机台是**另一个 GameObject**
            //   （`ClumsyRagdollGame` 里 `new GameObject("相机台")`），所以 `GetComponent<ClumsyController>()`
            //   拿到的是 **null**，转身指令永远加不到 Yaw 上 ⇒ 身体不转、只有一条腿在空中抖。
            //   现在改成：**控制器自己把 Root 的 yaw 推上去**（见 ClumsyRagdollControl 的转身段），
            //   相机这边只负责**把鼠标的增量加上去**，两边都做«读当前值 + 加增量»，不会互相覆盖。
            Ragdoll.Root.rotation = Quaternion.Euler(0f, Yaw, 0f);

            ClumsyPoseDriver pose = GetComponent<ClumsyPoseDriver>();
            if (pose != null)
                pose.PitchDegrees = PitchDegrees;
        }

        /// <summary>
        /// Blender 视口导航。**按住中键期间不驱动角色**（朝向与躯干俯仰都交给相机），
        /// 松开中键后者才回到教程 P6 的「鼠标左右转身、上下带躯干」。
        /// </summary>
        void Navigate()
        {
            float mx = Input.GetAxis("Mouse X");
            float my = Input.GetAxis("Mouse Y");
            float scroll = Input.GetAxis("Mouse ScrollWheel");

            // Home = 复位（清掉绕轨/平移/推拉）—— 对应 Blender 的「回到正视图」那种动作
            if (Input.GetKeyDown(KeyCode.Home))
            {
                OrbitYaw = 0f;
                OrbitPitch = 0f;
                PivotOffset = Vector3.zero;
                DistanceScale = 1f;
                ApplyCameraLocal();
                return;
            }

            bool mmb = Input.GetMouseButton(2);
            if (mmb)
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

                if (alt)
                {
                    // Alt-中键：把光标下的那个点设成枢轴（Blender 的 "Center View to Mouse"）。
                    // 想看手就在手上按一下 —— 之后绕轨/推拉都是绕着那只手。
                    RaycastHit hit;
                    if (Physics.Raycast(Cam.ScreenPointToRay(Input.mousePosition), out hit, 200f))
                        PivotOffset = Target.InverseTransformPoint(hit.point);
                }
                else if (ctrl)
                {
                    // 下拖 = 拉近（Blender「Continue」口径：往下/往左是拉近）
                    ZoomBy(Mathf.Exp(my * (Recipe != null ? Recipe.CameraDragZoomSpeed : DragZoomSpeed)));
                }
                else if (shift)
                {
                    // Pan：拖哪边、世界就往哪边走（相机往反方向挪，视线方向不变）。
                    float speed = Distance * (Recipe != null ? Recipe.CameraPanSpeed : PanSpeed);
                    Vector3 delta = -(Cam.transform.right * mx + Cam.transform.up * my) * speed;
                    PivotOffset += Target.InverseTransformDirection(delta);
                }
                else
                {
                    // Orbit（Turntable）：抓 / 拖 —— 右拖相机往左绕，下拖相机抬高看顶面。
                    OrbitYaw += mx * (Recipe != null ? Recipe.CameraOrbitSpeed : OrbitSpeed);
                    OrbitPitch = Mathf.Clamp(OrbitPitch - my * (Recipe != null ? Recipe.CameraOrbitSpeed : OrbitSpeed),
                        -OrbitPitchLimit, OrbitPitchLimit);
                }
            }
            else if (Cursor.lockState == CursorLockMode.Locked && !IsLandingSelectionActive())
            {
                // 教程 P6 步8：mouseX += Input.GetAxis("Mouse X") * rotationSpeed，rotationSpeed = 7
                Yaw += mx * Recipe.RotationSpeed;
                Pitch -= my * Recipe.RotationSpeed;
                Pitch = Mathf.Clamp(Pitch, Recipe.PitchMin, Recipe.PitchMax);
            }

            // 滚轮：上滚拉近（Blender 的 Wheel）
            if (Mathf.Abs(scroll) > 1e-4f)
                ZoomBy(Mathf.Exp(-scroll * (Recipe != null ? Recipe.CameraWheelZoomSpeed : WheelZoomSpeed)));

            ApplyCameraLocal();
        }

        bool IsLandingSelectionActive()
        {
            ClumsyStepActivity activity = Object.FindFirstObjectByType<ClumsyStepActivity>();
            return activity != null && activity.Enabled;
        }

        /// <summary>推拉：<paramref name="factor"/> &gt; 1 拉远、&lt; 1 拉近。指数式，所以远近的手感一致。</summary>
        void ZoomBy(float factor)
        {
            if (factor <= 0f || float.IsNaN(factor))
                return;
            DistanceScale = Mathf.Clamp(DistanceScale * factor, MinDistanceScale, MaxDistanceScale);
        }
    }
}
