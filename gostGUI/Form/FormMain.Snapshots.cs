// =====================================================================================
// FormMain.Snapshots.cs —— --ui-snapshot 渲染：DPI 上下文、演示数据种子、快照专用接缝。
// 本文件是 FormMain 的 partial，成员从 FormMain.cs 原样搬来（签名/可见性/行为未改）。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using gostGUI.Native;
using gostGUI.Shared;
using Microsoft.Win32;

namespace gostGUI
{
    public partial class FormMain : Form
    {
        /// <summary>启动时的系统 DPI（线程 DPI 上下文相关；--ui-snapshot 会临时切成 96）。</summary>
        private static int ResolveStartupDpi()
        {
            if (StartupDpiOverride > 0)
            {
                return StartupDpiOverride;
            }

            return HostSystemDpi();
        }

        /// <summary>
        /// 宿主真实系统 DPI（不受 <see cref="StartupDpiOverride"/> 影响）。
        /// R49：只有宿主真的是 192 DPI 时，才允许产出“真实 200% 上下文”的那对快照。
        /// </summary>
        internal static int HostSystemDpi()
        {
            try
            {
                uint dpi = NativeMethods.GetDpiForSystem();
                if (dpi >= 48 && dpi <= 480)
                {
                    return (int)dpi;
                }
            }
            catch (Exception)
            {
                // Win7/8.1 没有 GetDpiForSystem，按 96 DPI 处理。
            }

            return 96;
        }

        // =====================================================================================
        // --ui-snapshot：不显示窗口，把两套主题各渲染成 1024×640 的 PNG
        // =====================================================================================

        /// <summary>--ui-snapshot 的命令行入口：成功返回 0，失败返回 1。</summary>
        internal static int RunUiSnapshotCli(string directory)
        {
            string error;
            bool ok = TryRenderSnapshots(directory, out error);
            try
            {
                if (ok)
                {
                    string full = Path.GetFullPath(directory.Trim());
                    Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-light.png"));
                    Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-dark.png"));
                    Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-150-light.png"));
                    Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-150-dark.png"));

                    // R49：宿主不是 192 DPI 时不会产出这一对（TryRenderSnapshots 已给出 NOTICE）。
                    if (HostSystemDpi() == 192)
                    {
                        Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-200-light.png"));
                        Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-200-dark.png"));
                    }
                }
                else
                {
                    Console.WriteLine("FAIL ui-snapshot：" + error);
                }
            }
            catch (Exception)
            {
                // WinExe 没有控制台时忽略，退出码才是权威结果。
            }

            return ok ? 0 : 1;
        }

        /// <summary>
        /// 渲染 96 DPI（设计基线）、144 DPI（150% 模拟）各一对浅/深 PNG；
        /// 宿主真的是 192 DPI（200%）时再渲染一对真实 192 DPI 上下文快照（R49）。
        /// 绝不 Show()/Visible=true：只 CreateControl + DrawToBitmap。
        /// 96/144 在“DPI 不感知”的 96 DPI 绘制上下文里用布局/自绘/字体比例模拟；
        /// 192 用真实 DPI 上下文，文本度量按目标 DPI 显式钉住。
        /// </summary>
        internal static bool TryRenderSnapshots(string directory, out string error)
        {
            error = null;

            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    error = "需要一个输出目录参数。";
                    return false;
                }

                string trimmed = directory.Trim();
                if (trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    error = "需要一个输出目录参数，不能把另一个 -- 开关当作目录。";
                    return false;
                }

                string fullDirectory = Path.GetFullPath(trimmed);
                Directory.CreateDirectory(fullDirectory);

                if (!TryRenderSnapshotPair(fullDirectory, 96, "form-light.png", "form-dark.png", false, out error))
                {
                    return false;
                }

                if (!TryRenderSnapshotPair(fullDirectory, 144, "form-150-light.png", "form-150-dark.png", false, out error))
                {
                    return false;
                }

                // 开发机的真实缩放是 200%：用真实的 192 DPI 上下文（不模拟）输出一对，
                // 验收真实出货比例。R49：宿主不是 192 DPI 时这对没有意义（真实上下文就是
                // 宿主 DPI），必须跳过并明确通知，而不是悄悄产出一张“假的 200%”。
                if (HostSystemDpi() == 192)
                {
                    if (!TryRenderSnapshotPair(fullDirectory, 192, "form-200-light.png", "form-200-dark.png", true, out error))
                    {
                        return false;
                    }
                }
                else
                {
                    WriteSnapshotNotice(
                        "宿主系统 DPI 为 " + HostSystemDpi() + "（不是 192），已跳过 200% 真实 DPI 快照对。");
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>渲染一对（浅色/深色）指定 DPI 的快照；任何一个文件不合法都返回 false。</summary>
        private static bool TryRenderSnapshotPair(
            string fullDirectory,
            int dpi,
            string lightName,
            string darkName,
            bool nativeDpi,
            out string error)
        {
            error = null;
            string lightPath = Path.Combine(fullDirectory, lightName);
            string darkPath = Path.Combine(fullDirectory, darkName);

            // nativeDpi=true 用真实 DPI 上下文（开发机 192 DPI）；否则在 96 DPI 绘制上下文里
            // 用布局/自绘/字体比例模拟目标 DPI。
            // R48：文本度量必须跟随“绘制上下文”，而不是被模拟的 DPI。模拟对在 96 DPI 上下文里
            // 渲染（字体已按 1.5× 放大），按 144 度量会把文字宽度算大 1.5 倍，布局随之失真；
            // 只有真实 192 DPI 上下文才按目标 DPI 度量（同时避免 96 DPI 的测定缓存污染）。
            int measureDpi = nativeDpi ? dpi : ThemePaint.BaselineDpi;
            int previousMeasureDpi = ThemeText.MeasureDpiOverride;
            ThemeText.MeasureDpiOverride = measureDpi;
            try
            {
                using (nativeDpi ? new SnapshotDpiScope(0) : new SnapshotDpiScope(dpi))
                {
                    FormMain form;
                    try
                    {
                        form = new FormMain(true);
                    }
                    catch (Exception ex)
                    {
                        error = "无法创建窗体（" + dpi + " DPI）：" + ex.GetType().Name + ": " + ex.Message;
                        return false;
                    }

                    using (form)
                    {
                        try
                        {
                            int width = SnapshotPixels(dpi, SnapshotWidth);
                            int height = SnapshotPixels(dpi, SnapshotHeight);
                            form.PrepareSnapshotScene(width, height);
                            form.RenderSnapshot(Palette.Light, lightPath, width, height);
                            form.RenderSnapshot(Palette.Dark, darkPath, width, height);
                        }
                        catch (Exception ex)
                        {
                            error = "渲染快照失败（" + dpi + " DPI）：" + ex.GetType().Name + ": " + ex.Message;
                            return false;
                        }
                    }
                }

                if (!IsNonEmptyPng(lightPath))
                {
                    error = "浅色快照未生成或不是有效 PNG：" + lightPath;
                    return false;
                }

                if (!IsNonEmptyPng(darkPath))
                {
                    error = "深色快照未生成或不是有效 PNG：" + darkPath;
                    return false;
                }

                return true;
            }
            finally
            {
                ThemeText.MeasureDpiOverride = previousMeasureDpi;
            }
        }

        /// <summary>把 96 DPI 基线像素换算成指定 DPI 的位图尺寸。</summary>
        private static int SnapshotPixels(int dpi, int baselinePixels)
        {
            return WindowGeometry.DipToPixels(baselinePixels, dpi);
        }

        /// <summary>快照场景准备：固定客户区尺寸、灌演示数据、建句柄、排一次布局，绝不显示窗口。</summary>
        private void PrepareSnapshotScene(int clientWidth, int clientHeight)
        {
            AutoScaleMode = AutoScaleMode.None;
            // DrawToBitmap 对 Form 会连非客户区（标题栏）一起画；截图只保留客户区，
            // 这样位图就是设计与验收基线，不会因为标题栏挤掉底部一行。
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(clientWidth, clientHeight);
            MinimumSize = Size.Empty;
            _elapsedTimer?.Stop();

            CreateControl();
            ForceHandles(this);
            PerformLayout();
            LayoutLogViews();

            SeedSnapshotDemoItems();

            CreateControl();
            ForceHandles(this);
            PerformLayout();
            LayoutLogViews();
            ApplyTheme();

            // 演示日志：让“输出”槽位不是空白。日志走队列 + UI 定时器，快照没有消息循环，
            // 必须手动排空一次，PNG 里才看得到内容。
            IList<ConfigItem> items = ListItems();
            foreach (ConfigItem item in items)
            {
                AppendSystemLine(item, "系统：演示数据，未启动任何进程。");
            }

            _logViewManager?.DrainForTest();
        }

        /// <summary>快照用的演示配置项：只进列表，不写 config.json，也不启动任何进程。</summary>
        private void SeedSnapshotDemoItems()
        {
            _runStarted.Clear();
            _starting.Clear();
            _stopping.Clear();

            if (_logViewManager != null)
            {
                foreach (ConfigItem existing in _configManager.Items)
                {
                    _logViewManager.RemoveLogView(existing);
                }
            }

            _configList.Items.Clear();

            var demo = new[]
            {
                new ConfigItem
                {
                    Name = "默认节点",
                    // 演示“解析成功”的绿色提示：cmd.exe 在 PATH 里真实存在。
                    Program = "cmd.exe",
                    Args = "-L 127.0.0.1:8080 -F tls://abc.com:4000"
                },
                new ConfigItem
                {
                    Name = "测试节点",
                    Program = "gost.exe",
                    Args = "-L 127.0.0.1:8081 -F kcp://abc.com:4000"
                },
                new ConfigItem
                {
                    Name = "本地调试",
                    Program = "C:\\Tools\\gost.exe",
                    Args = "-L 127.0.0.1:9090"
                }
            };

            foreach (ConfigItem item in demo)
            {
                _logViewManager?.AddLogView(item);
                _configList.Items.Add(item);
            }

            // 第一项显示成“运行中”，验证运行时长列与状态点的高亮。
            _runStarted[demo[0]] = DateTime.UtcNow - TimeSpan.FromMinutes(8) - TimeSpan.FromSeconds(12);

            UpdateCountLabel();
            _configList.SelectedIndex = 0;
            RefreshStatus(demo[0]);
        }

        private void RenderSnapshot(Palette palette, string path, int width, int height)
        {
            if (palette == null)
            {
                throw new ArgumentNullException(nameof(palette));
            }

            _themeManager.Mode = ReferenceEquals(palette, Palette.Dark) ? ThemeMode.Dark : ThemeMode.Light;
            ApplyTheme();
            PerformLayout();
            LayoutLogViews();
            _logViewManager?.DrainForTest();

            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static void ForceHandles(Control control)
        {
            if (control == null)
            {
                return;
            }

            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls)
            {
                ForceHandles(child);
            }
        }

        /// <summary>快照流程里的“跳过/降级”通知：控制台可用就打印，--ui-snapshot 的退出码仍是权威。</summary>
        private static void WriteSnapshotNotice(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            try
            {
                Console.WriteLine("NOTICE ui-snapshot：" + message);
            }
            catch (Exception)
            {
                // WinExe 没有控制台时忽略。
            }
        }

        private static bool IsNonEmptyPng(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                byte[] bytes = File.ReadAllBytes(path);
                byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                if (bytes.Length <= signature.Length)
                {
                    return false;
                }

                for (int i = 0; i < signature.Length; i++)
                {
                    if (bytes[i] != signature[i])
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 快照作用域：把线程切到 96 DPI（DPI 不感知）上下文，让 DrawToBitmap 的字体
        /// 以 96 DPI 光栅化；同时用 StartupDpiOverride/ThemePaint.DpiOverride 模拟目标 DPI
        /// 的布局与自绘尺寸。离开作用域时全部恢复。拿不到 API（老系统）时静默照常渲染。
        /// </summary>
        private sealed class SnapshotDpiScope : IDisposable
        {
            private const int DpiAwarenessContextUnaware = -1;

            private readonly IntPtr _previous;
            private readonly bool _applied;
            private readonly int _requestedDpi;
            private readonly int _previousStartupDpi;
            private readonly int _previousPaintDpi;

            /// <summary>dpi &gt; 0：在 96 DPI 绘制上下文里模拟该 DPI；dpi &lt;= 0：保持真实 DPI 上下文。</summary>
            public SnapshotDpiScope(int dpi)
            {
                _requestedDpi = dpi;
                if (dpi <= 0)
                {
                    return;
                }

                _previousStartupDpi = StartupDpiOverride;
                _previousPaintDpi = ThemePaint.DpiOverride;
                StartupDpiOverride = dpi;
                ThemePaint.DpiOverride = dpi;

                try
                {
                    _previous = NativeMethods.GetThreadDpiAwarenessContext();
                    _applied = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(DpiAwarenessContextUnaware));
                }
                catch (Exception)
                {
                    _applied = false;
                }
            }

            public void Dispose()
            {
                if (_requestedDpi <= 0)
                {
                    return;
                }

                StartupDpiOverride = _previousStartupDpi;
                ThemePaint.DpiOverride = _previousPaintDpi;

                if (!_applied)
                {
                    return;
                }

                try
                {
                    NativeMethods.SetThreadDpiAwarenessContext(_previous);
                }
                catch (Exception)
                {
                    // 恢复失败也没有可补救的动作。
                }
            }
        }
    }
}
