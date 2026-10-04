using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ClumsyRagdoll.EditorTools
{
    /// <summary>
    /// **把动画效果渲染出来给人看。**
    ///
    /// 两件事：
    /// <list type="number">
    ///   <item><b>帧条 PNG</b>：把一整套布娃娃（物理身体，不是动画身体）从侧面渲成一格一格的
    ///         精灵表，写到 <c>Assets/Screenshots/</c>。一眼就能看出走着有没有动、腿摆得对不对。</item>
    ///   <item><b>骨骼 ASCII 侧视条</b>：把 18 根骨的世界坐标投影到 (z, y) 平面画成字符画，
    ///         直接打在 Console 里 —— 这是给「看不见图」的场合用的（诊断台、agent 会话）。</item>
    /// </list>
    ///
    /// 为什么需要它：`HANDOFF-动画与笨拙效果.md` §九 项1 写着「笨拙的最终判据还是人眼」——
    /// 这个工具就是给人眼准备的那一份材料。
    /// </summary>
    public static class ClumsyShowcase
    {
        const int FrameW = 288;
        const int FrameH = 288;
        const string OutDir = "Assets/Screenshots";

        /// <summary>ASCII 画哪些骨（key 与 RaccoonSkeleton 的部件表一致）。</summary>
        static readonly string[] AsciiKeys =
        {
            "hips", "spine", "neck", "head",
            "shoulderl", "arml", "forearml", "handl",
            "shoulderr", "armr", "forearmr", "handr",
            "uplegl", "legl", "footl",
            "uplegr", "legr", "footr"
        };

        /// <summary>骨架连线（父 → 子），画字符画用。</summary>
        static readonly string[,] AsciiBones =
        {
            { "hips", "spine" }, { "spine", "neck" }, { "neck", "head" },
            { "neck", "shoulderl" }, { "shoulderl", "arml" }, { "arml", "forearml" }, { "forearml", "handl" },
            { "neck", "shoulderr" }, { "shoulderr", "armr" }, { "armr", "forearmr" }, { "forearmr", "handr" },
            { "hips", "uplegl" }, { "uplegl", "legl" }, { "legl", "footl" },
            { "hips", "uplegr" }, { "uplegr", "legr" }, { "legr", "footr" }
        };

        // ==================================================================
        // 构建一个可渲染的世界
        // ==================================================================

        sealed class Stage
        {
            public ClumsyRagdollBench.Sim Sim;
            public Camera Cam;
            public GameObject Rig;
            public Vector3 BaseCamPos;
            public float StartZ;
            public List<Renderer> Hidden;
            /// <summary>诊断台的 BuildSim 会把 Physics.simulationMode 设成 Script ——
            /// 它是个**会写进 ProjectSettings 的持久设置**，跑完不还回去的话，
            /// 播放模式的物理会整个冻住（实测就是这么发现的：角色站着不动、速度恒为 0）。</summary>
            public SimulationMode SavedSimMode;
            public Vector3 SavedGravity;
        }

        static Stage BuildStage(RagdollRecipe recipe, bool walking)
        {
            Stage st = new Stage();
            st.SavedSimMode = Physics.simulationMode;
            st.SavedGravity = Physics.gravity;
            st.Sim = ClumsyRagdollBench.BuildArmedSim(recipe, true, true);
            st.Sim.Ctrl.Move = walking ? new Vector2(0f, 1f) : Vector2.zero;

            // 地面换成一块干净的大板 —— 诊断台那块 60x60 的灰板在暗背景里够用
            GameObject ground = GameObject.Find("地面");
            if (ground != null)
            {
                Renderer r = ground.GetComponent<Renderer>();
                if (r != null) r.material.color = new Color(0.30f, 0.32f, 0.35f);
            }

            // 灯：诊断台的世界里没有灯，自己加一盏
            GameObject lgo = new GameObject("展示灯");
            lgo.transform.SetParent(st.Sim.World.transform, true);
            Light l = lgo.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 0.72f;
            l.color = new Color(1f, 0.97f, 0.93f);
            l.shadows = LightShadows.None;
            lgo.transform.rotation = Quaternion.Euler(42f, -38f, 0f);

            // 相机：从角色右侧看过去（角色面朝 +Z，于是屏幕右 = 前进方向）
            GameObject camGo = new GameObject("展示相机");
            camGo.transform.SetParent(st.Sim.World.transform, true);
            st.Cam = camGo.AddComponent<Camera>();
            st.Cam.fieldOfView = 26f;
            st.Cam.nearClipPlane = 0.05f;
            st.Cam.farClipPlane = 60f;
            st.Cam.clearFlags = CameraClearFlags.SolidColor;
            st.Cam.backgroundColor = new Color(0.13f, 0.15f, 0.19f);
            st.Cam.allowHDR = false;
            st.Cam.allowMSAA = true;

            st.StartZ = st.Sim.Rag.PelvisPosition.z;
            // 角色约 1.05m 高。2.40m 距离 + 26° FOV → 可视高度 2*2.40*tan(13°) ≈ 1.11m，刚好装下。
            // 带一点 3/4 夹角（相机 z 偏 0.7）让画面有纵深，但仍然是侧视。
            st.BaseCamPos = new Vector3(2.30f, 0.60f, st.StartZ + 0.70f);
            AimCam(st, st.StartZ);

            // ★ 只留诊断台世界。场景里自己的「地面 / 参照柱 / 道具（矮桌+箱子）」都在原点附近，
            //   不藏起来会一起进画面 —— 字符亮度图下面那一大团亮块就是这么来的。
            st.Hidden = HideOthers(st.Sim);
            // 诊断台世界里的那块 60×60 地面也关掉：相机是平视的，地面会占掉画面下半幅，
            // 帧条与字符剪影都会被它糊成一片亮（实测就是这样：下半幅全 @）。
            // 要的是「暗背景 + 角色剪影」这种转盘式渲染。
            Transform benchGround = st.Sim.World.transform.Find("地面");
            if (benchGround != null)
            {
                Renderer br = benchGround.GetComponent<Renderer>();
                if (br != null && br.enabled) { br.enabled = false; st.Hidden.Add(br); }
            }
            return st;
        }

        static List<Renderer> HideOthers(ClumsyRagdollBench.Sim sim)
        {
            List<Renderer> hidden = new List<Renderer>();
            UnityEngine.SceneManagement.Scene scene =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null) continue;
                if (sim != null && sim.World != null && root == sim.World) continue;
                Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
                for (int k = 0; k < rs.Length; k++)
                    if (rs[k] != null && rs[k].enabled) { rs[k].enabled = false; hidden.Add(rs[k]); }
            }
            return hidden;
        }

        static void AimCam(Stage st, float pelvisZ)
        {
            // 跟着角色走：只在 Z 上平移，视线始终盯在骨盆高度
            // 盯在骨盆高度（约 0.60m），相机保持同一高度 → 平视，地平线落在画面中间
            Vector3 look = new Vector3(0f, 0.58f, pelvisZ);
            st.Cam.transform.position = new Vector3(st.BaseCamPos.x, st.BaseCamPos.y, pelvisZ + 0.70f);
            st.Cam.transform.rotation = Quaternion.LookRotation((look - st.Cam.transform.position).normalized, Vector3.up);
        }

        static void Dispose(Stage st)
        {
            if (st == null) return;
            if (st.Hidden != null)
                for (int i = 0; i < st.Hidden.Count; i++)
                    if (st.Hidden[i] != null) st.Hidden[i].enabled = true;
            if (st.Sim != null) ClumsyRagdollBench.TearDown(st.Sim);
            // ★ 一定要还回去（见 Stage.SavedSimMode 的注释）
            Physics.simulationMode = st.SavedSimMode;
            Physics.gravity = st.SavedGravity;
        }

        // ==================================================================
        // 渲染
        // ==================================================================

        static Texture2D Grab(Camera cam, RenderTexture rt, Texture2D buf)
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            buf.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            buf.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            return buf;
        }

        static string SaveSheet(Texture2D[] frames, int cols, string fileName)
        {
            if (!AssetDatabase.IsValidFolder(OutDir))
                AssetDatabase.CreateFolder("Assets", "Screenshots");

            int rows = Mathf.CeilToInt(frames.Length / (float)cols);
            Texture2D sheet = new Texture2D(cols * FrameW, rows * FrameH, TextureFormat.RGB24, false);
            Color32[] clear = new Color32[cols * FrameW * rows * FrameH];
            for (int i = 0; i < clear.Length; i++)
                clear[i] = new Color32(20, 22, 28, 255);
            sheet.SetPixels32(clear);

            for (int i = 0; i < frames.Length; i++)
            {
                int col = i % cols;
                int row = i / cols;
                // RenderTexture 与 SetPixels 都是自下而上，所以第一帧要贴在最上面一行
                int cx = col * FrameW;
                int cy = (rows - 1 - row) * FrameH;
                sheet.SetPixels(cx, cy, FrameW, FrameH, frames[i].GetPixels());
            }
            sheet.Apply();

            byte[] png = sheet.EncodeToPNG();
            string full = "C:/Users/9527/clumsy-ragdoll-physics/" + OutDir + "/" + fileName;
            System.IO.File.WriteAllBytes(full, png);
            Object.DestroyImmediate(sheet);
            return OutDir + "/" + fileName + "（" + (png.Length / 1024) + " KB，" + cols + "×" + rows + " 格）";
        }

        /// <summary>跑一段，每 stride 帧抓一张。</summary>
        static Texture2D[] Capture(Stage st, int warmupSteps, int frames, int stride)
        {
            RenderTexture rt = new RenderTexture(FrameW, FrameH, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            Texture2D buf = new Texture2D(FrameW, FrameH, TextureFormat.RGB24, false);
            Texture2D[] shots = new Texture2D[frames];

            for (int i = 0; i < warmupSteps; i++)
                ClumsyRagdollBench.StepSim(st.Sim, 1, st.Sim.Ctrl.Move);

            for (int i = 0; i < frames; i++)
            {
                for (int k = 0; k < stride; k++)
                    ClumsyRagdollBench.StepSim(st.Sim, 1, st.Sim.Ctrl.Move);

                AimCam(st, st.Sim.Rag.PelvisPosition.z);
                Texture2D shot = new Texture2D(FrameW, FrameH, TextureFormat.RGB24, false);
                Grab(st.Cam, rt, shot);
                shots[i] = shot;
            }

            rt.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(buf);
            return shots;
        }

        // ==================================================================
        // 入口
        // ==================================================================

        [MenuItem("笨拙布娃娃/展示/0. 打开场景并进 Play（自动演示）", false, 50)]
        public static void EnterPlayDemo()
        {
            // 不要弹「是否保存」的对话框 —— 菜单一点就该直接进 Play
            UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/ClumsyRagdoll.unity");
            EditorApplication.isPlaying = true;
        }

        [MenuItem("笨拙布娃娃/展示/1. 走路帧条 PNG", false, 60)]
        public static void RenderWalkSheet()
        {
            Debug.Log(RenderWalk());
        }

        [MenuItem("笨拙布娃娃/展示/2. 待机帧条 PNG", false, 61)]
        public static void RenderIdleSheet()
        {
            Debug.Log(RenderIdle());
        }

        [MenuItem("笨拙布娃娃/展示/3. 对比帧条（笨拙 0 vs 1）", false, 62)]
        public static void RenderCompareSheet()
        {
            Debug.Log(RenderCompare());
        }

        [MenuItem("笨拙布娃娃/展示/4. 骨骼 ASCII 侧视条", false, 63)]
        public static void PrintAscii()
        {
            Debug.Log(AsciiStrip());
        }

        [MenuItem("笨拙布娃娃/展示/5. 全部渲染 + ASCII", false, 64)]
        public static void RenderEverything()
        {
            RenderWalkSheet();
            RenderIdleSheet();
            RenderCompareSheet();
            PrintAscii();
        }

        // ==================================================================
        // 实现
        // ==================================================================

        public static string RenderWalk()
        {
            RagdollRecipe recipe = new RagdollRecipe();
            recipe.UseAnimation = true;
            Stage st = BuildStage(recipe, true);
            try
            {
                // 12 帧 × 0.06s = 0.72s ≈ 大半个步态周期（够看出腿在摆）
                Texture2D[] shots = Capture(st, 40, 12, 3);
                string info = SaveSheet(shots, 6, "anim-walk.png");
                for (int i = 0; i < shots.Length; i++) Object.DestroyImmediate(shots[i]);
                return "走路帧条 → " + info;
            }
            finally
            {
                Dispose(st);
            }
        }

        public static string RenderIdle()
        {
            RagdollRecipe recipe = new RagdollRecipe();
            recipe.UseAnimation = true;
            Stage st = BuildStage(recipe, false);
            try
            {
                Texture2D[] shots = Capture(st, 40, 8, 8);
                string info = SaveSheet(shots, 4, "anim-idle.png");
                for (int i = 0; i < shots.Length; i++) Object.DestroyImmediate(shots[i]);
                return "待机帧条 → " + info;
            }
            finally
            {
                Dispose(st);
            }
        }

        /// <summary>同一个物理时刻，笨拙度 0 与 1 各来一排 —— 上下对比看「慢半拍」。</summary>
        public static string RenderCompare()
        {
            Texture2D[] all = new Texture2D[12];
            string info;
            RagdollRecipe clean = new RagdollRecipe();
            clean.UseAnimation = true;
            clean.Clumsiness = 0f;
            Stage a = BuildStage(clean, true);
            Texture2D[] top;
            try
            {
                top = Capture(a, 60, 6, 3);
                for (int i = 0; i < 6; i++) all[i] = top[i];
            }
            finally { Dispose(a); }

            RagdollRecipe drunk = new RagdollRecipe();
            drunk.UseAnimation = true;
            drunk.Clumsiness = 1f;
            Stage b = BuildStage(drunk, true);
            try
            {
                Texture2D[] bottom = Capture(b, 60, 6, 3);
                for (int i = 0; i < 6; i++) all[6 + i] = bottom[i];
            }
            finally { Dispose(b); }

            info = SaveSheet(all, 6, "anim-clumsy-compare.png");
            for (int i = 0; i < all.Length; i++) Object.DestroyImmediate(all[i]);
            return "对比帧条 → " + info + "（上排 = 笨拙度 0，下排 = 1.0）";
        }

        // ==================================================================
        // ASCII 骨骼侧视
        // ==================================================================

        public static string AsciiStrip()
        {
            RagdollRecipe recipe = new RagdollRecipe();
            recipe.UseAnimation = true;
            Stage st = BuildStage(recipe, true);
            try
            {
                ClumsyRagdollBench.StepSim(st.Sim, 60, st.Sim.Ctrl.Move);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("== 骨骼侧视（走路，每格 6 帧 = 0.12s；左 = 身后，右 = 前进方向）==");
                string[] frames = new string[5];
                for (int f = 0; f < frames.Length; f++)
                {
                    frames[f] = AsciiFrame(st.Sim, 20, 13);
                    ClumsyRagdollBench.StepSim(st.Sim, 6, st.Sim.Ctrl.Move);
                }
                // 横向并排打印
                string[][] lines = new string[frames.Length][];
                for (int f = 0; f < frames.Length; f++)
                    lines[f] = frames[f].Split('\n');
                int rows = lines[0].Length;
                for (int r = 0; r < rows; r++)
                {
                    sb.Append("  ");
                    for (int f = 0; f < frames.Length; f++)
                        sb.Append(lines[f][r].TrimEnd()).Append("  |");
                    sb.AppendLine();
                }
                sb.AppendLine("  图例：O=髋  o=头  左右臂/腿用不同字符，· = 骨段");
                return sb.ToString();
            }
            finally { Dispose(st); }
        }

        static string AsciiFrame(ClumsyRagdollBench.Sim sim, int cols, int rows)
        {
            ClumsyPart[] parts = new ClumsyPart[AsciiKeys.Length];
            for (int i = 0; i < AsciiKeys.Length; i++)
                parts[i] = sim.Rag.Find(AsciiKeys[i]);

            // 先算投影包围盒（用 z-y 平面）
            float minZ = float.MaxValue, maxZ = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null || parts[i].Body == null) continue;
                Vector3 p = parts[i].Body.position;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            if (minZ > maxZ) return "";

            float padZ = Mathf.Max(0.08f, (maxZ - minZ) * 0.22f);
            float padY = Mathf.Max(0.10f, (maxY - minY) * 0.18f);
            minZ -= padZ; maxZ += padZ; minY -= padY; maxY += padY;

            char[,] grid = new char[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    grid[r, c] = ' ';

            System.Func<Vector3, int> colOf = p => Mathf.Clamp(Mathf.RoundToInt((p.z - minZ) / (maxZ - minZ) * (cols - 1)), 0, cols - 1);
            System.Func<Vector3, int> rowOf = p => Mathf.Clamp(Mathf.RoundToInt((maxY - p.y) / (maxY - minY) * (rows - 1)), 0, rows - 1);

            // 画骨段
            for (int b = 0; b < AsciiBones.GetLength(0); b++)
            {
                ClumsyPart pa = sim.Rag.Find(AsciiBones[b, 0]);
                ClumsyPart pb = sim.Rag.Find(AsciiBones[b, 1]);
                if (pa == null || pb == null || pa.Body == null || pb.Body == null) continue;
                Vector3 A = pa.Body.position, B = pb.Body.position;
                int steps = 24;
                for (int s = 0; s <= steps; s++)
                {
                    Vector3 p = Vector3.Lerp(A, B, s / (float)steps);
                    int rr = rowOf(p), cc = colOf(p);
                    if (grid[rr, cc] == ' ') grid[rr, cc] = '.';
                }
            }

            // 画关节点（后画的盖前面的）
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null || parts[i].Body == null) continue;
                Vector3 p = parts[i].Body.position;
                int rr = rowOf(p), cc = colOf(p);
                grid[rr, cc] = AsciiKeys[i] == "hips" ? 'O'
                    : AsciiKeys[i] == "head" ? 'o'
                    : AsciiKeys[i].EndsWith("l") ? '<' : '>';
            }

            StringBuilder sb = new StringBuilder();
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                    sb.Append(grid[r, c]);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // ==================================================================
        // 把**真实渲染**降采样成字符亮度图 —— 给「拿不到图」的场合看
        // ==================================================================

        [MenuItem("笨拙布娃娃/展示/6. 渲染成字符亮度图（在 Console 里看）", false, 65)]
        public static void PrintRenderAscii()
        {
            Debug.Log(RenderAscii());
        }

        /// <summary>
        /// 把相机渲出来的画面降采样成字符亮度图，几帧并排打在 Console 里。
        /// 同时给出**逐帧像素差** —— 那就是「它真的在动」的硬证据。
        /// </summary>
        public static string RenderAscii()
        {
            const int W = 96, H = 96;
            const int Cols = 30, Rows = 16;
            string ramp = " .:-=+*#%@";
            RagdollRecipe recipe = new RagdollRecipe();
            recipe.UseAnimation = true;
            Stage st = BuildStage(recipe, true);
            StringBuilder sb = new StringBuilder();
            // 字符图看的是**角色剪影**，地面会把整幅图填成一片亮 —— 临时把所有地面关掉。
            // ⚠️ 必须遍历全部：场景里本身有一块 240×240 的「地面」（根物体），
            //    诊断台世界里还有一块 60×60 的同名地面 —— 只 Find 一个会关错那一块。
            List<Renderer> grounds = new List<Renderer>();
            GameObject[] all = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i].name != "地面") continue;
                Renderer r = all[i].GetComponent<Renderer>();
                if (r != null && r.enabled) { grounds.Add(r); r.enabled = false; }
            }
            RenderTexture rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            Texture2D buf = new Texture2D(W, H, TextureFormat.RGB24, false);
            try
            {
                const int Frames = 6;
                string[][] art = new string[Frames][];
                Color[][] px = new Color[Frames][];
                for (int f = 0; f < Frames; f++)
                {
                    for (int k = 0; k < 4; k++)
                        ClumsyRagdollBench.StepSim(st.Sim, 1, st.Sim.Ctrl.Move);
                    AimCam(st, st.Sim.Rag.PelvisPosition.z);
                    Grab(st.Cam, rt, buf);
                    px[f] = buf.GetPixels();
                    art[f] = Downsample(px[f], W, H, Cols, Rows, ramp);
                }
                sb.AppendLine("== 渲染成字符亮度图（走路，每格 8 帧 = 0.16s）==");
                sb.AppendLine("   左 = 身后，右 = 前进方向；暗背景 = 空格，越亮字符越密");
                for (int r = 0; r < Rows; r++)
                {
                    sb.Append("  ");
                    for (int f = 0; f < Frames; f++)
                        sb.Append(art[f][r]).Append("| ");
                    sb.AppendLine();
                }
                sb.Append("  ★ 角色占画面（亮度高于背景的像素比例）： ");
                for (int f = 0; f < Frames; f++)
                {
                    int lit = 0;
                    for (int i = 0; i < px[f].Length; i++)
                        if (px[f][i].grayscale > 0.20f) lit++;
                    sb.Append((lit * 100f / px[f].Length).ToString("F1")).Append("% ");
                }
                sb.AppendLine();
                sb.Append("  ★ 逐帧像素差异（越大 = 动得越多）： ");
                for (int f = 1; f < Frames; f++)
                {
                    double d = 0;
                    for (int i = 0; i < px[f].Length; i++)
                        d += Mathf.Abs(px[f][i].grayscale - px[f - 1][i].grayscale);
                    sb.Append((d / px[f].Length * 100.0).ToString("F2")).Append("% ");
                }
                sb.AppendLine();
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(buf);
                for (int i = 0; i < grounds.Count; i++) grounds[i].enabled = true;
                Dispose(st);
            }
            return sb.ToString();
        }

        static string[] Downsample(Color[] px, int w, int h, int cols, int rows, string ramp)
        {
            string[] outRows = new string[rows];
            for (int r = 0; r < rows; r++)
            {
                StringBuilder line = new StringBuilder(cols);
                for (int c = 0; c < cols; c++)
                {
                    float sum = 0f;
                    int n = 0;
                    int y0 = Mathf.FloorToInt(r * h / (float)rows);
                    int y1 = Mathf.Max(y0 + 1, Mathf.FloorToInt((r + 1) * h / (float)rows));
                    int x0 = Mathf.FloorToInt(c * w / (float)cols);
                    int x1 = Mathf.Max(x0 + 1, Mathf.FloorToInt((c + 1) * w / (float)cols));
                    for (int y = y0; y < y1 && y < h; y++)
                    {
                        int flipped = h - 1 - y;   // GetPixels 是自下而上，打印要自上而下
                        for (int x = x0; x < x1 && x < w; x++)
                        {
                            sum += px[flipped * w + x].grayscale;
                            n++;
                        }
                    }
                    float lum = n > 0 ? sum / n : 0f;
                    // 背景大约 0.15：低于它一律当空
                    // 窗口要拉宽：第一版是 (lum-0.17)/0.55，角色受光面一过 0.72 就全变 @，
                    // 整块身体糊成一个实心亮块，看不出形体。
                    float t01 = Mathf.Clamp01((lum - 0.055f) / 0.85f);
                    int li = Mathf.Clamp(Mathf.RoundToInt(t01 * (ramp.Length - 1)), 0, ramp.Length - 1);
                    line.Append(ramp[li]);
                }
                outRows[r] = line.ToString();
            }
            return outRows;
        }
    }
}
