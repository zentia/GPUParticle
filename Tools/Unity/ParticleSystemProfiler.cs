// =====================================================================
// ParticleSystemProfiler —— 上真机量"Unity 粒子到底吃了多少 CPU"
// ---------------------------------------------------------------------
// 这不是库的一部分（它依赖 UnityEngine.ParticleSystem，所以**不能**放进
// Runtime/CSharp —— 那边要求零 Unity API）。它是决策工具：用来回答"把粒子从
// CPU 搬到 GPU 到底值不值"。
//
// 用法：
//   1. 拷进工程（OSGame: Client_proj/Program/GameScripts/Assets/Scripts/）
//   2. 挂到场景里任意 GameObject（建议挂在一个常驻的 Debug 根节点上）
//   3. **必须 Development Build**（Recorder 在非 dev 包里取不到 marker）
//      —— Build Settings 勾 Development Build + Autoconnect Profiler
//   4. 装到目标机型（要用那台"没有 compute shader"的最低端机）跑最重的场景
//   5. 看 log / persistentDataPath 下的 gpuparticle_profile_*.txt
//
// 它回答三件事：
//   A. 粒子总共吃多少 ms/帧（按 Unity 真实 marker 名抓，不是猜）
//   B. 这些开销里有多少是"转成 Tier-1 后能省掉的"
//   C. 场景里的 System 按"能不能转"分类后，各自的粒子数占比
//
// 判定线（见 doc/UNITY_MEASUREMENT.md）：
//   粒子相关总计 < 1ms  → 别做
//                 > 3ms  → 值得做
// =====================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace ZEngine.GPUParticle.Diagnostics
{
    /// <summary>
    /// 按 Unity C++ 源码里真实的 PROFILER_INFORMATION 名字抓耗时。
    /// 名字来自 Modules/ParticleSystem/**（Unity 2023.1 源码实测），不是猜的。
    /// </summary>
    public class ParticleSystemProfiler : MonoBehaviour
    {
        [Tooltip("多久打一次报告（秒）")]
        public float reportIntervalSeconds = 5.0f;

        [Tooltip("是否在屏幕上显示（真机上看方便，正式出包关掉）")]
        public bool showOverlay = true;

        [Tooltip("是否在结束时把报告写到 persistentDataPath")]
        public bool writeFileOnDisable = true;

        // ---- marker 分组 ----
        // 能省：转成 Tier-1 后这些工作整体消失
        static readonly string[] kRemovable =
        {
            "ParticleSystem.Update",
            "ParticleSystem.Update2",
            "ParticleSystem.Simulate",
            "ParticleSystem.UpdateJob",
            "ParticleSystem.EndUpdateAll",
            "ParticleSystem.WaitForPreviousRenderingToFinish",
            "ParticleSystem.ScheduleGeometryJobs",
            "ParticleSystem.WaitForPreMapping",
            "ParticleSystem.GeometryJob",
            "ParticleSystem.InstancedMeshJob",
            "ParticleSystem.TrailGeometryJob",
            "ParticleSystem.Sort",
        };

        // 省不掉：换 Tier-1 之后照样要提交绘制
        static readonly string[] kRenderOnly =
        {
            "ParticleSystem.DrawSystem",
            "ParticleSystem.Draw",
        };

        // 模块级：看开销具体落在哪个模块上（决定"哪些模块必须自己实现"）
        static readonly string[] kModules =
        {
            "ParticleSystem.ShapeModule",
            "ParticleSystem.VelocityOverLifetimeModule",
            "ParticleSystem.ClampVelocityModule",
            "ParticleSystem.InheritVelocityModule",
            "ParticleSystem.ForceModule",
            "ParticleSystem.ExternalForcesModule",
            "ParticleSystem.NoiseModule",
            "ParticleSystem.ColorOverLifetimeModule",
            "ParticleSystem.ColorByVelocityModule",
            "ParticleSystem.SizeOverLifetimeModule",
            "ParticleSystem.SizeOverVelocityModule",
            "ParticleSystem.RotationOverLifetimeModule",
            "ParticleSystem.RotationByVelocityModule",
            "ParticleSystem.CollisionModule",
            "ParticleSystem.TriggerModule",
            "ParticleSystem.TrailModule",
            "ParticleSystem.TextureSheetModule",
            "ParticleSystem.LightsModule",
            "ParticleSystem.CustomDataModule",
            "ParticleSystem.LifetimeByEmitterSpeedModule",
        };

        sealed class Slot
        {
            // 自己存 marker 名：Recorder 有没有 `name` 这个属性各版本不一致，
            // 别赌它。
            public string Name;
            public Recorder Recorder;
            public long LastNanoseconds;
            public double AccumulatedMs;
        }

        /// <summary>
        /// 用不变文化格式化。
        /// 注意：不能写成 sb.AppendLine(CultureInfo.InvariantCulture, $"...") ——
        /// StringBuilder 那个 (IFormatProvider, 插值串) 重载依赖
        /// AppendInterpolatedStringHandler，是 .NET 6 才有的，Unity 2022.3 的
        /// netstandard2.1 引用程序集里没有 ⇒ 编译不过。走 FormattableString。
        /// </summary>
        static string Inv(FormattableString s) { return s.ToString(CultureInfo.InvariantCulture); }

        readonly List<Slot> m_Removable = new List<Slot>();
        readonly List<Slot> m_RenderOnly = new List<Slot>();
        readonly List<Slot> m_Modules = new List<Slot>();

        float m_WindowStart;
        int m_Frames;
        string m_LastReport = "(还没有报告)";
        bool m_RecordersValid;

        // ---- 分类统计 ----
        int m_SysTotal, m_SysReady, m_SysNeedsWork, m_SysBlocked;
        long m_ParticlesTotal, m_ParticlesReady, m_ParticlesNeedsWork, m_ParticlesBlocked;
        readonly List<string> m_HotSystems = new List<string>();

        void Awake()
        {
            Build(m_Removable, kRemovable);
            Build(m_RenderOnly, kRenderOnly);
            Build(m_Modules, kModules);
            m_WindowStart = Time.realtimeSinceStartup;
        }

        static void Build(List<Slot> slots, string[] names)
        {
            foreach (string n in names)
            {
                Recorder r = Recorder.Get(n);
                r.enabled = true;
                // 先读一次把"启用前"的累计值扣掉
                slots.Add(new Slot { Name = n, Recorder = r, LastNanoseconds = r.elapsedNanoseconds, AccumulatedMs = 0.0 });
            }
        }

        void Update()
        {
            ++m_Frames;
            float now = Time.realtimeSinceStartup;
            if (now - m_WindowStart >= reportIntervalSeconds)
            {
                EmitReport(now - m_WindowStart);
                m_WindowStart = now;
                m_Frames = 0;
            }
        }

        void EmitReport(float windowSeconds)
        {
            double removableMs = Accumulate(m_Removable);
            double renderMs = Accumulate(m_RenderOnly);
            double modulesMs = Accumulate(m_Modules);
            double frames = m_Frames > 0 ? m_Frames : 1;
            double perFrameRemovable = removableMs / frames;
            double perFrameRender = renderMs / frames;
            double perFrameModules = modulesMs / frames;

            SurveySystems();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("================ GPUParticle / ParticleSystem 量测 ================");
            sb.AppendLine(Inv($"窗口 {windowSeconds:F1}s / {m_Frames} 帧 / 平均 {(windowSeconds / frames * 1000.0):F2} ms 每帧"));
            sb.AppendLine();
            sb.AppendLine("-- A. 粒子耗时（真实 marker）--");
            sb.AppendLine(Inv($"  可省（模拟+几何生成+排序）: {perFrameRemovable:F3} ms/帧"));
            sb.AppendLine(Inv($"  省不掉（提交绘制）        : {perFrameRender:F3} ms/帧"));
            sb.AppendLine(Inv($"  模块明细合计              : {perFrameModules:F3} ms/帧"));
            sb.AppendLine(Inv($"  >>> 粒子总计约 {perFrameRemovable + perFrameRender:F3} ms/帧（占 {(perFrameRemovable + perFrameRender) / (windowSeconds / frames * 1000.0) * 100.0:F1}% 帧时间）"));
            if (!m_RecordersValid)
            {
                sb.AppendLine("  [警告] Recorder 全为 0 —— 多半不是 Development Build，marker 没编译进去。");
            }
            sb.AppendLine();
            sb.AppendLine("-- 单项大头（> 0.05 ms/帧）--");
            foreach (Slot s in m_Removable)
            {
                double ms = s.AccumulatedMs / frames;
                if (ms > 0.05) sb.AppendLine(Inv($"  [可省] {s.Name,-48} {ms:F3} ms"));
            }
            foreach (Slot s in m_Modules)
            {
                double ms = s.AccumulatedMs / frames;
                if (ms > 0.05) sb.AppendLine(Inv($"  [模块] {s.Name,-48} {ms:F3} ms"));
            }
            sb.AppendLine();
            sb.AppendLine("-- B/C. 场景里的 System 按可转性分类（按存活粒子数加权）--");
            sb.AppendLine(Inv($"  可直接转 Tier-1 : {m_SysReady,4} 个 / {m_ParticlesReady,8} 粒子 ({Share(m_ParticlesReady):F1}%)"));
            sb.AppendLine(Inv($"  要额外做（trail/noise/旋转/mesh）: {m_SysNeedsWork,4} 个 / {m_ParticlesNeedsWork,8} 粒子 ({Share(m_ParticlesNeedsWork):F1}%)"));
            sb.AppendLine(Inv($"  转不了（碰撞/触发/子发射器）: {m_SysBlocked,4} 个 / {m_ParticlesBlocked,8} 粒子 ({Share(m_ParticlesBlocked):F1}%)"));
            sb.AppendLine(Inv($"  合计            : {m_SysTotal,4} 个 / {m_ParticlesTotal,8} 粒子"));
            if (m_HotSystems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("-- 粒子数 Top10 --");
                foreach (string line in m_HotSystems) sb.AppendLine("  " + line);
            }
            sb.AppendLine("==================================================================");

            m_LastReport = sb.ToString();
            Debug.Log(m_LastReport);
        }

        double Share(long v)
        {
            return m_ParticlesTotal > 0 ? (double)v / m_ParticlesTotal * 100.0 : 0.0;
        }

        static double Accumulate(List<Slot> slots)
        {
            double total = 0.0;
            foreach (Slot s in slots)
            {
                long now = s.Recorder.elapsedNanoseconds;
                long delta = now - s.LastNanoseconds;
                s.LastNanoseconds = now;
                s.AccumulatedMs = delta / 1.0e6;
                total += s.AccumulatedMs;
            }
            return total;
        }

        /// <summary>
        /// 枚举场景里的 ParticleSystem，按"能不能转 Tier-1"分类。
        /// 判据对应 doc/UNITY_MEASUREMENT.md 的可转性表。
        /// </summary>
        void SurveySystems()
        {
            m_SysTotal = m_SysReady = m_SysNeedsWork = m_SysBlocked = 0;
            m_ParticlesTotal = m_ParticlesReady = m_ParticlesNeedsWork = m_ParticlesBlocked = 0;
            m_HotSystems.Clear();
            List<(int count, string name)> hot = new List<(int, string)>();

#if UNITY_2023_1_OR_NEWER
            ParticleSystem[] systems = FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
#else
            ParticleSystem[] systems = FindObjectsOfType<ParticleSystem>();
#endif
            foreach (ParticleSystem ps in systems)
            {
                if (ps == null) continue;
                int count;
                try { count = ps.particleCount; }
                catch (Exception) { continue; }   // 正在销毁

                bool blocked = ps.collision.enabled || ps.trigger.enabled || ps.subEmitters.enabled;
                // 需要额外工作量，但**不是**不可为：
                //   trails  → 闭式解可在 t, t-Δ, t-2Δ 上求值，拖尾是解析的，不用历史缓冲
                //   noise   → 只能近似（VS 里做噪声位移），保真度要美术点头
                //   3D 旋转 → 角速度只依赖归一化年龄时可解析（烘积分曲线）
                //   mesh 粒子 → 走实例化路径，批次有上限
                bool needsWork = ps.trails.enabled || ps.noise.enabled || ps.rotationOverLifetime.enabled;
                ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
                if (r != null && r.renderMode == ParticleSystemRenderMode.Mesh) needsWork = true;

                ++m_SysTotal;
                m_ParticlesTotal += count;
                if (blocked) { ++m_SysBlocked; m_ParticlesBlocked += count; }
                else if (needsWork) { ++m_SysNeedsWork; m_ParticlesNeedsWork += count; }
                else { ++m_SysReady; m_ParticlesReady += count; }

                hot.Add((count, ps.name + (blocked ? " [不可转]" : (needsWork ? " [要额外做]" : " [可直接转]"))));
            }

            hot.Sort((a, b) => b.count.CompareTo(a.count));
            for (int i = 0; i < hot.Count && i < 10; ++i)
            {
                m_HotSystems.Add(Inv($"{hot[i].count,7}  {hot[i].name}"));
            }

            // Recorder 有效性检查：可省那一组全是 0 就说明 marker 没编进来
            m_RecordersValid = false;
            foreach (Slot s in m_Removable)
            {
                if (s.AccumulatedMs > 0.0) { m_RecordersValid = true; break; }
            }
        }

        void OnDisable()
        {
            if (!writeFileOnDisable) return;
            string path = System.IO.Path.Combine(Application.persistentDataPath,
                "gpuparticle_profile_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".txt");
            System.IO.File.WriteAllText(path, m_LastReport);
            Debug.Log($"[ParticleSystemProfiler] 报告已写到 {path}");
        }

        void OnGUI()
        {
            if (!showOverlay) return;
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 12;
            style.normal.textColor = Color.white;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(8, 8, 560, 300), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(16, 16, 544, 284), m_LastReport, style);
        }
    }
}
