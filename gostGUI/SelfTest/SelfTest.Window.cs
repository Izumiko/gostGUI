// =====================================================================================
// SelfTest.Window.cs —— 窗口几何与 DPI 用例：DIP 窗口状态、布局与热切换。
// 本文件是自检套件的 partial：原单体 SelfTest.cs 已按领域拆分（该文件已不存在），用例名/断言/注册顺序未改。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace gostGUI
{
    internal static partial class SelfTest
    {
        /// <summary>
        /// Task 13 绑定约束：窗口尺寸/位置以 DIP 存储，换缩放比例后不能逐次变大/变小。
        /// 96/120/144 三档都做 DIP → 设备像素 → DIP 往返，误差不得超过 1 DIP。
        /// </summary>
        private static void WindowPersistsInDips()
        {
            int[] dpis = { 96, 120, 144 };
            int[] dipValues = { 0, 1, 7, 63, 100, 480, 560, 640, 800, 1024, 1280, 1536, 1920, 3840 };

            foreach (int dpi in dpis)
            {
                Assert(dpi > 0, "DPI 必须为正数：" + dpi);

                foreach (int value in dipValues)
                {
                    int pixels = WindowGeometry.DipToPixels(value, dpi);
                    int roundTrip = WindowGeometry.PixelsToDip(pixels, dpi);
                    Assert(
                        Math.Abs(roundTrip - value) <= 1,
                        $"DPI {dpi}：{value} DIP → {pixels}px → {roundTrip} DIP，误差超过 1 DIP。");
                }

                // 1280×800 @ (40,30) DIP 的三档捕获必须仍以 DIP 表达同一几何。
                int pxWidth = (int)Math.Round(1280 * dpi / 96.0, MidpointRounding.AwayFromZero);
                int pxHeight = (int)Math.Round(800 * dpi / 96.0, MidpointRounding.AwayFromZero);
                int pxX = (int)Math.Round(40 * dpi / 96.0, MidpointRounding.AwayFromZero);
                int pxY = (int)Math.Round(30 * dpi / 96.0, MidpointRounding.AwayFromZero);

                ConfigWindow captured = WindowGeometry.Capture(
                    new Size(pxWidth, pxHeight),
                    new Point(pxX, pxY),
                    false,
                    dpi);

                Assert(Math.Abs(captured.W - 1280) <= 1, $"DPI {dpi}：捕获宽度 {captured.W} 应约等于 1280 DIP。");
                Assert(Math.Abs(captured.H - 800) <= 1, $"DPI {dpi}：捕获高度 {captured.H} 应约等于 800 DIP。");
                Assert(Math.Abs(captured.X - 40) <= 1, $"DPI {dpi}：捕获 X {captured.X} 应约等于 40 DIP。");
                Assert(Math.Abs(captured.Y - 30) <= 1, $"DPI {dpi}：捕获 Y {captured.Y} 应约等于 30 DIP。");
                AssertEqual(false, captured.Max);
                AssertEqual(dpi, captured.Dpi);

                ConfigWindow maximized = WindowGeometry.Capture(
                    new Size(pxWidth, pxHeight),
                    new Point(pxX, pxY),
                    true,
                    dpi);
                AssertEqual(true, maximized.Max);
                Assert(Math.Abs(maximized.W - 1280) <= 1, "最大化也要保留可恢复的普通尺寸。");

                // 捕获值再走一次 DIP → px → DIP，仍要在误差内。
                int recaptured = WindowGeometry.PixelsToDip(
                    WindowGeometry.DipToPixels(captured.W, dpi),
                    dpi);
                Assert(Math.Abs(recaptured - captured.W) <= 1, $"DPI {dpi}：捕获值二次往返误差超过 1 DIP。");
            }
        }

        /// <summary>负数/零/超大尺寸与越界坐标一律回落到默认窗口状态。</summary>
        private static void WindowInvalidValuesFallBackToDefault()
        {
            ConfigWindow[] invalid =
            {
                new ConfigWindow { W = -1, H = -2, X = 10, Y = 10 },
                new ConfigWindow { W = 0, H = 0, X = 10, Y = 10 },
                new ConfigWindow { W = 639, H = 479, X = 10, Y = 10 },
                new ConfigWindow { W = 640, H = 479, X = 10, Y = 10 },
                new ConfigWindow { W = 20000, H = 20000, X = 10, Y = 10 },
                new ConfigWindow { W = int.MaxValue, H = int.MaxValue, X = int.MaxValue, Y = int.MinValue },
                new ConfigWindow { W = 1024, H = 640, X = int.MaxValue, Y = int.MinValue },
                new ConfigWindow { W = 1024, H = 640, X = -40000, Y = 0 },
                new ConfigWindow { W = 1024, H = 640, X = 0, Y = 40000 }
            };

            foreach (ConfigWindow candidate in invalid)
            {
                ConfigWindow normalized = WindowGeometry.Normalize(candidate);
                AssertEqual(ConfigWindow.DefaultWidth, normalized.W);
                AssertEqual(ConfigWindow.DefaultHeight, normalized.H);
                AssertEqual(ConfigWindow.DefaultX, normalized.X);
                AssertEqual(ConfigWindow.DefaultY, normalized.Y);
                AssertEqual(false, normalized.Max);
            }

            // 合法值（包括多显示器的负坐标）必须原样保留。
            var valid = new ConfigWindow { W = 1280, H = 800, X = -1920, Y = 200, Max = true };
            ConfigWindow kept = WindowGeometry.Normalize(valid);
            AssertEqual(1280, kept.W);
            AssertEqual(800, kept.H);
            AssertEqual(-1920, kept.X);
            AssertEqual(200, kept.Y);
            AssertEqual(true, kept.Max);

            ConfigWindow nullNormalized = WindowGeometry.Normalize(null);
            AssertEqual(ConfigWindow.DefaultWidth, nullNormalized.W);
            AssertEqual(ConfigWindow.DefaultHeight, nullNormalized.H);

            // 损坏的 dpi 按 96 处理。
            var badDpi = new ConfigWindow { W = 1024, H = 640, X = 1, Y = 2, Dpi = 9999 };
            AssertEqual(96, WindowGeometry.Normalize(badDpi).Dpi);
        }

        /// <summary>
        /// 保存的 DIP 必须用**捕获时**的 DPI 换算。混用当前/系统 DPI 会让
        /// 高 DPI 显示器上保存的窗口被误判为越界或恢复到错误像素位置。
        /// </summary>
        private static void WindowMixedDpiUsesCaptureDpi()
        {
            var screens = new List<Rectangle>
            {
                new Rectangle(0, 0, 3072, 1920),      // 192 DPI 主屏
                new Rectangle(3072, 0, 1024, 768)     // 96 DPI 副屏
            };
            var workingAreas = new List<Rectangle>
            {
                new Rectangle(0, 0, 3072, 1824),
                new Rectangle(3072, 0, 1024, 728)
            };

            Point location;
            WindowPlacement placement;

            // 192 DPI 捕获的 900×600 @ (100,50) DIP → 1800×1200 @ (200,100) px，
            // 即使当前启动 DPI 是 96 也必须按 192 换算。
            var capturedAt192 = new ConfigWindow { W = 900, H = 600, X = 100, Y = 50, Dpi = 192 };
            Size size = WindowGeometry.Place(
                capturedAt192, 96, screens, workingAreas, out location, out placement);
            AssertEqual(WindowPlacement.Restored, placement);
            AssertEqual(1800, size.Width);
            AssertEqual(1200, size.Height);
            AssertEqual(200, location.X);
            AssertEqual(100, location.Y);
            Assert(location.X + size.Width <= 3072, "192 DPI 保存的窗口应落在 192 DPI 屏幕内。");

            // 同样 DIP 数值在 96 DPI 捕获 → 900×600 @ (100,50)。
            capturedAt192.Dpi = 96;
            size = WindowGeometry.Place(
                capturedAt192, 96, screens, workingAreas, out location, out placement);
            AssertEqual(WindowPlacement.Restored, placement);
            AssertEqual(900, size.Width);
            AssertEqual(100, location.X);

            // 越界判断也必须用捕获 DPI：X=3100 DIP @192 → 6200 px（越界），
            // @96 → 3100 px（落在副屏上）。
            var far = new ConfigWindow { W = 900, H = 600, X = 3100, Y = 100, Dpi = 192 };
            size = WindowGeometry.Place(far, 96, screens, workingAreas, out location, out placement);
            AssertEqual(WindowPlacement.OffscreenReset, placement);
            AssertEqual(ConfigWindow.DefaultWidth, size.Width);
            AssertEqual(ConfigWindow.DefaultHeight, size.Height);

            far.Dpi = 96;
            size = WindowGeometry.Place(far, 96, screens, workingAreas, out location, out placement);
            AssertEqual(WindowPlacement.Restored, placement);
            AssertEqual(900, size.Width);
            AssertEqual(3100, location.X);
            AssertEqual(100, location.Y);
        }

        /// <summary>
        /// Normalize 的最小下限必须来自与 MinimumSize 相同的设计下限，
        /// 并且能随较小的桌面放宽，不能把应用自己允许的窗口判成损坏。
        /// </summary>
        private static void WindowMinimumSourceRelaxesWithWorkArea()
        {
            AssertEqual(880, WindowGeometry.DesignMinimumWidthDip);
            AssertEqual(560, WindowGeometry.DesignMinimumHeightDip);

            var chrome = new Size(16, 39);
            Size smallFloor = WindowGeometry.MinimumDipFor(new Rectangle(0, 0, 500, 400), 96, chrome);
            AssertEqual(484, smallFloor.Width);   // 500 - 16
            AssertEqual(361, smallFloor.Height);  // 400 - 39

            var smallSaved = new ConfigWindow { W = 484, H = 361, X = 10, Y = 10, Dpi = 96 };
            ConfigWindow relaxed = WindowGeometry.Normalize(smallSaved, smallFloor.Width, smallFloor.Height);
            AssertEqual(484, relaxed.W);
            AssertEqual(361, relaxed.H);

            // 默认（大屏）下限仍是历史 640×480：同样的小窗口按损坏处理。
            ConfigWindow legacy = WindowGeometry.Normalize(smallSaved);
            AssertEqual(ConfigWindow.DefaultWidth, legacy.W);
            AssertEqual(ConfigWindow.DefaultHeight, legacy.H);

            // 大屏上设计下限不会被放宽。
            Size largeFloor = WindowGeometry.MinimumDipFor(
                new Rectangle(0, 0, 3072, 1824), 192, new Size(26, 71));
            AssertEqual(640, largeFloor.Width);
            AssertEqual(480, largeFloor.Height);
        }

        /// <summary>恢复窗口时必须夹到工作区；保存的位置不再与任何屏幕相交时回落居中默认尺寸。</summary>
        private static void WindowPlacementClampsAndFallsBack()
        {
            var screens = new List<Rectangle> { new Rectangle(0, 0, 1920, 1080) };
            var workingAreas = new List<Rectangle> { new Rectangle(0, 0, 1920, 1040) };

            Point location;
            WindowPlacement placement;

            // 尺寸大于工作区：夹到工作区，位置也夹进可见范围。
            var oversized = new ConfigWindow { W = 2000, H = 1500, X = 100, Y = 100 };
            Size clamped = WindowGeometry.Place(
                oversized, 96, screens, workingAreas, out location, out placement);
            AssertEqual(WindowPlacement.Restored, placement);
            Assert(clamped.Width <= 1920, "宽度应夹到工作区：" + clamped.Width);
            Assert(clamped.Height <= 1040, "高度应夹到工作区：" + clamped.Height);
            Assert(location.X >= 0, "X 不应为负：" + location.X);
            Assert(location.Y >= 0, "Y 不应为负：" + location.Y);
            Assert(location.X + clamped.Width <= 1920, "窗口右缘应留在工作区内。");
            Assert(location.Y + clamped.Height <= 1040, "窗口下缘应留在工作区内。");

            // 位置越界：回落居中 + 默认尺寸。
            var offscreen = new ConfigWindow { W = 1200, H = 700, X = 9000, Y = 8000, Max = true };
            Size reset = WindowGeometry.Place(
                offscreen, 96, screens, workingAreas, out location, out placement);
            AssertEqual(WindowPlacement.OffscreenReset, placement);
            AssertEqual(ConfigWindow.DefaultWidth, reset.Width);
            AssertEqual(ConfigWindow.DefaultHeight, reset.Height);
            AssertEqual((1920 - reset.Width) / 2, location.X);
            AssertEqual((1040 - reset.Height) / 2, location.Y);

            // 只保存了尺寸（位置为 -1）：居中，但保留尺寸。
            var noPosition = new ConfigWindow { W = 1200, H = 700, X = -1, Y = -1 };
            Size centered = WindowGeometry.Place(
                noPosition, 96, screens, workingAreas, out location, out placement);
            AssertEqual(WindowPlacement.Centered, placement);
            AssertEqual(1200, centered.Width);
            AssertEqual(700, centered.Height);
            AssertEqual((1920 - centered.Width) / 2, location.X);

            // 多显示器：负坐标位置应保留，不能被当成非法值。
            var leftMonitor = new ConfigWindow { W = 1200, H = 700, X = -1920, Y = 0 };
            var screens2 = new List<Rectangle>
            {
                new Rectangle(-1920, 0, 1920, 1080),
                new Rectangle(0, 0, 1920, 1080)
            };
            var works2 = new List<Rectangle>
            {
                new Rectangle(-1920, 0, 1920, 1040),
                new Rectangle(0, 0, 1920, 1040)
            };
            Size onLeft = WindowGeometry.Place(
                leftMonitor, 96, screens2, works2, out location, out placement);
            AssertEqual(WindowPlacement.Restored, placement);
            AssertEqual(-1920, location.X);
            AssertEqual(0, location.Y);
        }

        /// <summary>
        /// Task 13：隐藏到托盘前把窗口客户区尺寸/位置以 DIP 写入 config.json，
        /// 并能在新实例里读回同一几何（误差 ≤1 DIP）。
        /// </summary>
        private static void FormWindowStatePersistsOnHide()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "窗口用例", Program = "cmd.exe" }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnDedicatedSta(() =>
                {
                    int previousDpi = FormMain.StartupDpiOverride;
                    int previousPaint = ThemePaint.DpiOverride;
                    FormMain.StartupDpiOverride = 96;
                    ThemePaint.DpiOverride = 96;
                    try
                    {
                        using (var form = new FormMain(true, manager))
                        {
                            form.SuppressDialogsForTest = true;
                            form.SetWindowGeometryForTest(new Size(1234, 700), new Point(120, 60));
                            form.TriggerFormClosingForTest();

                            AssertEqual(1234, manager.WindowState.W);
                            AssertEqual(700, manager.WindowState.H);
                            AssertEqual(120, manager.WindowState.X);
                            AssertEqual(60, manager.WindowState.Y);
                            AssertEqual(false, manager.WindowState.Max);
                            AssertEqual(96, manager.WindowState.Dpi);
                            AssertEqual("", form.LastWindowSaveFailureForTest);
                        }
                    }
                    finally
                    {
                        FormMain.StartupDpiOverride = previousDpi;
                        ThemePaint.DpiOverride = previousPaint;
                    }
                });

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "落盘后的配置应可解析：" + reloaded.Error);
                AssertEqual(1234, reloaded.Data.Window.W);
                AssertEqual(700, reloaded.Data.Window.H);
                AssertEqual(120, reloaded.Data.Window.X);
                AssertEqual(60, reloaded.Data.Window.Y);
                AssertEqual(96, reloaded.Data.Window.Dpi);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        private static void FormTrayRoundTripKeepsWindowHandle()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "托盘用例", Program = "cmd.exe" }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                MethodInfo restore = typeof(FormMain).GetMethod(
                    "RestoreAndBringToFront", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(restore != null, "应能调用托盘恢复入口。");
                var manager = new ConfigurationManager(path);
                RunOnDedicatedSta(() =>
                {
                    using (var form = new FormMain(true, manager))
                    {
                        form.SuppressDialogsForTest = true;
                        form.Show();
                        Assert(form.Visible, "自检应从可见的窗体开始。");
                        IntPtr handle = form.Handle;
                        int destroyed = 0;
                        form.HandleDestroyed += (_, __) => destroyed++;

                        for (int i = 0; i < 2; i++)
                        {
                            form.Close();
                            Assert(!form.IsDisposed && !form.Visible, "关闭应隐藏窗口，而不是退出。");
                            Assert(destroyed == 0, "收进托盘时不应销毁窗口句柄。");
                            Assert(form.IsHandleCreated, "窗口隐藏后仍应保留句柄。");
                            AssertEqual(handle, form.Handle);

                            restore.Invoke(form, null);
                            Assert(form.Visible, "从托盘恢复后窗口应可见。");
                            Assert(destroyed == 0, "从托盘恢复时不应销毁窗口句柄。");
                            AssertEqual(handle, form.Handle);
                        }
                    }
                });
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// Task 13：启动时按 DIP 恢复窗口客户区与位置；96/120/144 三档都换算到正确的设备像素。
        /// 位置已不在任何屏幕上时回落居中 + 默认尺寸。
        /// </summary>
        private static void FormWindowStateRestoresFromConfig()
        {
            int[] dpis = { 96, 120, 144 };
            foreach (int dpi in dpis)
            {
                string dir = CreateTempDirectory();
                try
                {
                    string path = Path.Combine(dir, "config.json");
                    Assert(Common.SaveTo(new ConfigData
                    {
                        Window = new ConfigWindow { W = 900, H = 580, X = 20, Y = 10, Dpi = dpi },
                        Items = new List<ConfigItem>
                        {
                            new ConfigItem { Name = "恢复用例", Program = "cmd.exe" }
                        }
                    }, path), "写夹具应成功：" + Common.LastSaveError);

                    var manager = new ConfigurationManager(path);
                    int expectedDpi = dpi;
                    RunOnDedicatedSta(() =>
                    {
                        int previousDpi = FormMain.StartupDpiOverride;
                        int previousPaint = ThemePaint.DpiOverride;
                        FormMain.StartupDpiOverride = expectedDpi;
                        ThemePaint.DpiOverride = expectedDpi;
                        try
                        {
                            using (var form = new FormMain(true, manager))
                            {
                                form.RestoreWindowStateForTest();

                                AssertEqual(
                                    WindowGeometry.DipToPixels(900, expectedDpi),
                                    form.ClientSize.Width);
                                AssertEqual(
                                    WindowGeometry.DipToPixels(580, expectedDpi),
                                    form.ClientSize.Height);
                                AssertEqual(
                                    WindowGeometry.DipToPixels(20, expectedDpi),
                                    form.Location.X);
                                AssertEqual(
                                    WindowGeometry.DipToPixels(10, expectedDpi),
                                    form.Location.Y);
                                AssertEqual(FormStartPosition.Manual, form.StartPosition);
                            }
                        }
                        finally
                        {
                            FormMain.StartupDpiOverride = previousDpi;
                            ThemePaint.DpiOverride = previousPaint;
                        }
                    });
                }
                finally
                {
                    DeleteTempDirectory(dir);
                }
            }

            // 越界位置：居中 + 默认尺寸。
            string offscreenDir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(offscreenDir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Window = new ConfigWindow { W = 900, H = 580, X = 20000, Y = 20000, Dpi = 96 },
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "越界用例", Program = "cmd.exe" }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnDedicatedSta(() =>
                {
                    int previousDpi = FormMain.StartupDpiOverride;
                    int previousPaint = ThemePaint.DpiOverride;
                    FormMain.StartupDpiOverride = 96;
                    ThemePaint.DpiOverride = 96;
                    try
                    {
                        using (var form = new FormMain(true, manager))
                        {
                            form.RestoreWindowStateForTest();
                            AssertEqual(FormStartPosition.CenterScreen, form.StartPosition);
                            AssertEqual(ConfigWindow.DefaultWidth, form.ClientSize.Width);
                            AssertEqual(ConfigWindow.DefaultHeight, form.ClientSize.Height);
                        }
                    }
                    finally
                    {
                        FormMain.StartupDpiOverride = previousDpi;
                        ThemePaint.DpiOverride = previousPaint;
                    }
                });
            }
            finally
            {
                DeleteTempDirectory(offscreenDir);
            }
        }

        /// <summary>
        /// CRITICAL 回归：最小化时 ClientSize 可能是 0×0、Bounds 是任务栏图标，
        /// 捕获必须用 RestoreBounds 减去**按窗口样式算出**的非客户区尺寸，
        /// 而不是 Bounds − ClientSize。最大化同理。
        /// </summary>
        private static void FormWindowStateCapturesMinimizedAndMaximized()
        {
            // 与 review 实测一致的最小化状态：Bounds 314×50、ClientSize 0×0、
            // RestoreBounds 是普通外框 916×639、chrome 16×39（900×600 客户区）。
            var minimized = FormMain.BuildWindowState(
                new Rectangle(0, 0, 314, 50),
                Size.Empty,
                new Rectangle(100, 50, 916, 639),
                new Size(16, 39),
                false,
                true,
                96,
                640,
                480);

            AssertEqual(900, minimized.W);
            AssertEqual(600, minimized.H);
            AssertEqual(100, minimized.X);
            AssertEqual(50, minimized.Y);
            AssertEqual(false, minimized.Max);
            AssertEqual(96, minimized.Dpi);

            // “restored client size is exact”：把捕获值再恢复，客户区必须精确回到 900×600。
            var screens = new List<Rectangle> { new Rectangle(0, 0, 1920, 1080) };
            var works = new List<Rectangle> { new Rectangle(0, 0, 1920, 1040) };
            Point location;
            WindowPlacement placement;
            Size restored = WindowGeometry.Place(
                minimized, 96, screens, works, out location, out placement);
            AssertEqual(WindowPlacement.Restored, placement);
            AssertEqual(900, restored.Width);
            AssertEqual(600, restored.Height);
            AssertEqual(100, location.X);
            AssertEqual(50, location.Y);

            // 最大化：Bounds 是整屏、ClientSize 可能是整屏，RestoreBounds 才是普通几何。
            var maximized = FormMain.BuildWindowState(
                new Rectangle(0, 0, 1920, 1040),
                new Size(1920, 1040),
                new Rectangle(100, 50, 916, 639),
                new Size(16, 39),
                true,
                false,
                96,
                640,
                480);

            AssertEqual(900, maximized.W);
            AssertEqual(600, maximized.H);
            AssertEqual(100, maximized.X);
            AssertEqual(50, maximized.Y);
            AssertEqual(true, maximized.Max);

            // 窗体级：chrome 由窗口样式计算，最小化/最大化切换不改变它。
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "状态用例", Program = "cmd.exe" }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnDedicatedSta(() =>
                {
                    int previousDpi = FormMain.StartupDpiOverride;
                    int previousPaint = ThemePaint.DpiOverride;
                    FormMain.StartupDpiOverride = 96;
                    ThemePaint.DpiOverride = 96;
                    try
                    {
                        using (var form = new FormMain(true, manager))
                        {
                            form.CreateControl();
                            ForceControlHandles(form);
                            form.SetWindowGeometryForTest(new Size(900, 600), new Point(100, 50));

                            Size expectedChrome = form.WindowChromeForTest();
                            Assert(expectedChrome.Width > 0 && expectedChrome.Height > 0,
                                "外框尺寸应大于 0：" + expectedChrome);

                            ConfigWindow normal = form.CaptureWindowStateForTest();
                            AssertEqual(900, normal.W);
                            AssertEqual(600, normal.H);

                            form.WindowState = FormWindowState.Maximized;
                            AssertEqual(expectedChrome, form.WindowChromeForTest());
                            ConfigWindow fromMax = form.CaptureWindowStateForTest();
                            AssertEqual(900, fromMax.W);
                            AssertEqual(600, fromMax.H);
                            AssertEqual(100, fromMax.X);
                            AssertEqual(50, fromMax.Y);
                            AssertEqual(true, fromMax.Max);

                            form.WindowState = FormWindowState.Minimized;
                            AssertEqual(expectedChrome, form.WindowChromeForTest());
                            ConfigWindow fromMin = form.CaptureWindowStateForTest();
                            AssertEqual(900, fromMin.W);
                            AssertEqual(600, fromMin.H);
                            AssertEqual(100, fromMin.X);
                            AssertEqual(50, fromMin.Y);
                            AssertEqual(false, fromMin.Max);

                            form.WindowState = FormWindowState.Normal;
                        }
                    }
                    finally
                    {
                        FormMain.StartupDpiOverride = previousDpi;
                        ThemePaint.DpiOverride = previousPaint;
                    }
                });
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// Task 13 验收：96/120/144 DPI 下窗体都必须按同一比例排版，左右两栏不重叠、
        /// 可见控件不越出父控件、列表行高和按钮高度随 DPI 放大。
        /// </summary>
        private static void FormLayoutAt120And144DpiIsNotClipped()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "布局用例", Program = "cmd.exe" }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                int[] dpis = { 96, 120, 144 };
                foreach (int dpi in dpis)
                {
                    var manager = new ConfigurationManager(path);
                    int expectedDpi = dpi;
                    RunOnDedicatedSta(() =>
                    {
                        int previousDpi = FormMain.StartupDpiOverride;
                        int previousPaint = ThemePaint.DpiOverride;
                        FormMain.StartupDpiOverride = expectedDpi;
                        ThemePaint.DpiOverride = expectedDpi;
                        try
                        {
                            using (var form = new FormMain(true, manager))
                            {
                                form.CreateControl();
                                ForceControlHandles(form);
                                form.PerformLayout();

                                AssertEqual(expectedDpi, form.LayoutDpiForTest);
                                AssertEqual(
                                    WindowGeometry.DipToPixels(1024, expectedDpi),
                                    form.ClientSize.Width);
                                AssertEqual(
                                    WindowGeometry.DipToPixels(640, expectedDpi),
                                    form.ClientSize.Height);

                                Rectangle content = form.ContentLayoutBoundsForTest;
                                Assert(content.Width > 0 && content.Height > 0, "内容区应有尺寸。");
                                Assert(content.Right <= form.ClientSize.Width, "内容区越出窗体右缘。");
                                Assert(content.Bottom <= form.ClientSize.Height, "内容区越出窗体下缘。");

                                Rectangle left = form.LeftPanelBoundsForTest;
                                Rectangle right = form.RightColumnBoundsForTest;
                                Assert(left.Width > 0 && left.Height > 0, "左栏应有尺寸。");
                                Assert(right.Width > 0 && right.Height > 0, "右栏应有尺寸。");
                                Assert(left.Right <= right.Left, "左右两栏不应重叠。");
                                Assert(right.Right <= form.ClientSize.Width, "右栏越出窗体右缘。");

                                Rectangle launch = form.LaunchCardBoundsForTest;
                                Rectangle output = form.OutputCardBoundsForTest;
                                Assert(launch.Height > 0 && output.Height > 0, "启动设置/输出卡片应有高度。");
                                Assert(launch.Bottom <= output.Top, "启动设置卡片不应压住输出卡片。");
                                Assert(output.Bottom <= right.Bottom, "输出卡片越出右栏。");

                                AssertEqual(
                                    (int)Math.Round(44.0 * expectedDpi / 96.0, MidpointRounding.AwayFromZero),
                                    form.ConfigListItemHeightForTest);
                                Assert(
                                    form.NewButtonHeightForTest >=
                                    (int)Math.Round(30.0 * expectedDpi / 96.0, MidpointRounding.AwayFromZero),
                                    "按钮高度应随 DPI 放大，实际 " + form.NewButtonHeightForTest + "px。");

                                AssertNoVisibleChildEscapesParent(form, "Form");
                            }
                        }
                        finally
                        {
                            FormMain.StartupDpiOverride = previousDpi;
                            ThemePaint.DpiOverride = previousPaint;
                        }
                    });
                }
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// Task 13：活 DPI 变化走与 WM_DPICHANGED 相同的方法（Form.OnDpiChanged）：
        /// 基类按新旧 DPI 比例缩放字体/边距/绝对列宽，本窗口再重算最小尺寸与折叠宽度。
        /// </summary>
        private static void FormDpiChangeRescalesLayout()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "热切换用例", Program = "cmd.exe" }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnDedicatedSta(() =>
                {
                    int previousDpi = FormMain.StartupDpiOverride;
                    int previousPaint = ThemePaint.DpiOverride;
                    FormMain.StartupDpiOverride = 96;
                    ThemePaint.DpiOverride = 96;
                    try
                    {
                        using (var form = new FormMain(true, manager))
                        {
                            form.CreateControl();
                            ForceControlHandles(form);
                            form.PerformLayout();

                            int beforeClientWidth = form.ClientSize.Width;
                            int beforeClientHeight = form.ClientSize.Height;
                            int beforeButtonHeight = form.NewButtonHeightForTest;
                            int beforeColumnWidth = form.LeftColumnWidthForTest;
                            int borderWidth = form.Width - form.ClientSize.Width;
                            int borderHeight = form.Height - form.ClientSize.Height;

                            var suggested = new Rectangle(
                                form.Location,
                                new Size(
                                    (beforeClientWidth * 3 / 2) + borderWidth,
                                    (beforeClientHeight * 3 / 2) + borderHeight));
                            DpiChangedEventArgs args = CreateDpiChangedEventArgs(96, 144, suggested);
                            form.HandleDpiChangedForTest(args);

                            AssertEqual(144, form.LayoutDpiForTest);
                            Assert(
                                Math.Abs(form.ClientSize.Width - (beforeClientWidth * 3 / 2)) <= 2,
                                "DPI 变化后客户区宽度应按 1.5 倍缩放：实际 " + form.ClientSize.Width);
                            Assert(
                                Math.Abs(form.ClientSize.Height - (beforeClientHeight * 3 / 2)) <= 2,
                                "DPI 变化后客户区高度应按 1.5 倍缩放：实际 " + form.ClientSize.Height);
                            Assert(
                                Math.Abs(form.NewButtonHeightForTest - (beforeButtonHeight * 3 / 2)) <= 2,
                                "按钮高度应按 1.5 倍缩放：实际 " + form.NewButtonHeightForTest);
                            AssertEqual(
                                (int)Math.Round(beforeColumnWidth * 1.5),
                                form.LeftColumnWidthForTest);
                            AssertEqual(
                                Math.Min(
                                    (int)Math.Round(880 * 144.0 / 96.0),
                                    Screen.FromControl(form).WorkingArea.Width),
                                form.MinimumSize.Width);
                        }
                    }
                    finally
                    {
                        FormMain.StartupDpiOverride = previousDpi;
                        ThemePaint.DpiOverride = previousPaint;
                    }
                });
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// 递归断言：可见子控件的矩形不越出父控件客户区。AutoScroll 面板的内容区允许比视口大，
        /// 不再向下递归（否则把“可滚动”误判成“被裁切”）。
        /// </summary>
        private static void AssertNoVisibleChildEscapesParent(Control parent, string path)
        {
            if (parent == null)
            {
                return;
            }

            foreach (Control child in parent.Controls)
            {
                if (!child.Visible || child.Width <= 0 || child.Height <= 0)
                {
                    continue;
                }

                string name = path + " > " + child.GetType().Name + "(" + child.Name + ")";
                Assert(child.Left >= -1, name + " 左缘越出父控件：" + child.Bounds);
                Assert(child.Top >= -1, name + " 上缘越出父控件：" + child.Bounds);
                Assert(
                    child.Right <= parent.ClientSize.Width + 1,
                    name + " 右缘越出父控件：" + child.Bounds + " / 父 " + parent.ClientSize);
                Assert(
                    child.Bottom <= parent.ClientSize.Height + 1,
                    name + " 下缘越出父控件：" + child.Bounds + " / 父 " + parent.ClientSize);

                var scrollable = child as ScrollableControl;
                if (scrollable != null && scrollable.AutoScroll)
                {
                    continue;
                }

                AssertNoVisibleChildEscapesParent(child, name);
            }
        }

        /// <summary>
        /// 用反射构造内部 DpiChangedEventArgs(old, Message)：wParam 高/低字都是新 DPI，
        /// lParam 指向建议的窗口矩形。仅自检使用。
        /// </summary>
        private static DpiChangedEventArgs CreateDpiChangedEventArgs(int oldDpi, int newDpi, Rectangle suggested)
        {
            IntPtr memory = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.WriteInt32(memory, 0, suggested.Left);
                Marshal.WriteInt32(memory, 4, suggested.Top);
                Marshal.WriteInt32(memory, 8, suggested.Right);
                Marshal.WriteInt32(memory, 12, suggested.Bottom);

                var message = new Message
                {
                    Msg = 0x02E0,
                    WParam = (IntPtr)(newDpi | (newDpi << 16)),
                    LParam = memory
                };

                ConstructorInfo constructor = typeof(DpiChangedEventArgs).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(int), typeof(Message) },
                    null);
                Assert(constructor != null, "找不到 DpiChangedEventArgs 的内部构造器。");
                return (DpiChangedEventArgs)constructor.Invoke(new object[] { oldDpi, message });
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }
    }
}
