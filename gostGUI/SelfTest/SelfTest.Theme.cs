// =====================================================================================
// SelfTest.Theme.cs —— 主题用例：调色板、模式解析、递归应用与主题化控件契约。
// 本文件是 SelfTest 的 partial：成员由 SelfTest.cs 原样搬来（用例名/断言/注册顺序未改）。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using gostGUI.Native;
using gostGUI.Shared;
using Microsoft.Win32.SafeHandles;

namespace gostGUI
{
    internal static partial class SelfTest
    {
        /// <summary>浅色/深色两套调色板不能写成一样：至少 8 组语义色必须不同。</summary>
        private static void ThemePalettesDiffer()
        {
            Palette light = Palette.Light;
            Palette dark = Palette.Dark;
            Color[] lightColors =
            {
                light.Surface, light.SurfaceAlt, light.Border, light.TextPrimary,
                light.TextMuted, light.Accent, light.AccentText, light.ButtonBg,
                light.ButtonHover, light.ButtonPress, light.Success, light.Danger,
                light.LogBg, light.LogStdout, light.LogStderr, light.LogSystem
            };
            Color[] darkColors =
            {
                dark.Surface, dark.SurfaceAlt, dark.Border, dark.TextPrimary,
                dark.TextMuted, dark.Accent, dark.AccentText, dark.ButtonBg,
                dark.ButtonHover, dark.ButtonPress, dark.Success, dark.Danger,
                dark.LogBg, dark.LogStdout, dark.LogStderr, dark.LogSystem
            };

            int differing = 0;
            for (int i = 0; i < lightColors.Length; i++)
            {
                if (lightColors[i] != darkColors[i])
                {
                    differing++;
                }
            }

            Assert(differing >= 8, $"浅色/深色调色板应至少有 8 组语义色不同，实际只有 {differing} 组。");
        }

        /// <summary>system / light / dark 与 ThemeMode 往返；大小写容忍，非法与空值回落 System。</summary>
        private static void ThemeModeParsingRoundTrips()
        {
            AssertEqual(ThemeMode.System, ThemeModes.Parse("system"));
            AssertEqual(ThemeMode.Light, ThemeModes.Parse("light"));
            AssertEqual(ThemeMode.Dark, ThemeModes.Parse("dark"));
            AssertEqual(ThemeMode.Light, ThemeModes.Parse("  LIGHT  "));
            AssertEqual("system", ThemeModes.ToConfigString(ThemeMode.System));
            AssertEqual("light", ThemeModes.ToConfigString(ThemeMode.Light));
            AssertEqual("dark", ThemeModes.ToConfigString(ThemeMode.Dark));

            string[] illegalValues = { null, "", "   ", "auto", "darkk", "0" };
            foreach (string value in illegalValues)
            {
                AssertEqual(ThemeMode.System, ThemeModes.Parse(value));
            }
        }

        /// <summary>System 模式必须通过可注入的探针解析；重复设置同一模式也要重新解析。</summary>
        private static void ThemeSystemPrefersDarkUsesOverride()
        {
            var manager = new ThemeManager();

            manager.systemPrefersDarkOverride = () => true;
            manager.Mode = ThemeMode.System;
            Assert(ReferenceEquals(Palette.Dark, manager.Current), "System 模式下探针为深色时应取 Palette.Dark。");

            manager.systemPrefersDarkOverride = () => false;
            manager.Mode = ThemeMode.System;
            Assert(ReferenceEquals(Palette.Light, manager.Current), "System 模式下探针为浅色时应取 Palette.Light。");

            manager.Mode = ThemeMode.Dark;
            Assert(ReferenceEquals(Palette.Dark, manager.Current), "显式 Dark 不应受探针影响。");

            manager.Mode = ThemeMode.Light;
            Assert(ReferenceEquals(Palette.Light, manager.Current), "显式 Light 不应受探针影响。");
        }

        /// <summary>
        /// WM_SETTINGCHANGE 可能由任意系统设置触发：只有“跟随系统”且解析出的调色板真的变了，
        /// 才允许重新铺整棵树（Changed 只触发一次）。
        /// </summary>
        private static void ThemeRefreshSystemPreferenceChangesOnlyWhenPaletteChanges()
        {
            var manager = new ThemeManager();
            int changes = 0;
            manager.Changed += () => changes++;

            manager.systemPrefersDarkOverride = () => false;
            manager.Mode = ThemeMode.System;
            Assert(ReferenceEquals(Palette.Light, manager.Current), "初始系统偏好应为浅色。");
            changes = 0;

            AssertEqual(false, manager.RefreshSystemPreference());
            AssertEqual(0, changes);
            Assert(ReferenceEquals(Palette.Light, manager.Current), "偏好没变时调色板不应改变。");

            manager.systemPrefersDarkOverride = () => true;
            AssertEqual(true, manager.RefreshSystemPreference());
            AssertEqual(1, changes);
            Assert(ReferenceEquals(Palette.Dark, manager.Current), "系统偏好变深色后应解析出深色调色板。");
            AssertEqual(ThemeMode.System, manager.Mode);

            AssertEqual(false, manager.RefreshSystemPreference());
            AssertEqual(1, changes);

            // 非 System 模式：忽略系统偏好变化。
            manager.Mode = ThemeMode.Light;
            changes = 0;
            AssertEqual(false, manager.RefreshSystemPreference());
            AssertEqual(0, changes);
            Assert(ReferenceEquals(Palette.Light, manager.Current), "显式浅色模式不应被系统偏好覆盖。");
        }

        /// <summary>ApplyTo 必须可重复调用且无副作用；控件颜色最终等于当前调色板的对应值。</summary>
        private static void ThemeApplyToIsIdempotent()
        {
            RunOnSta(() =>
            {
                using (var host = new CardPanel())
                {
                    var button = new FlatButton();
                    host.Controls.Add(button);

                    var manager = new ThemeManager();
                    manager.systemPrefersDarkOverride = () => false;
                    manager.Mode = ThemeMode.Light;

                    manager.ApplyTo(host);
                    AssertEqual(Palette.Light.SurfaceAlt, button.BackColor);
                    AssertEqual(Palette.Light.TextPrimary, button.ForeColor);

                    manager.ApplyTo(host);
                    AssertEqual(Palette.Light.SurfaceAlt, button.BackColor);
                    AssertEqual(Palette.Light.TextPrimary, button.ForeColor);

                    manager.Mode = ThemeMode.Dark;
                    manager.ApplyTo(host);
                    manager.ApplyTo(host);
                    AssertEqual(Palette.Dark.SurfaceAlt, button.BackColor);
                    AssertEqual(Palette.Dark.TextPrimary, button.ForeColor);

                    manager.ApplyTo(null);
                }
            });
        }

        /// <summary>七个主题类型都必须实现 IThemed（六个 Control 子类 + 菜单渲染器）。</summary>
        private static void ThemeControlsImplementIThemed()
        {
            Type[] themedTypes =
            {
                typeof(FlatButton), typeof(CardPanel), typeof(ToggleSwitch),
                typeof(ThemedTextBox), typeof(ThemedRichTextBox), typeof(ThemedListBox),
                typeof(ThemedMenuRenderer)
            };

            foreach (Type type in themedTypes)
            {
                Assert(typeof(IThemed).IsAssignableFrom(type), type.Name + " 必须实现 IThemed。");
            }

            Type[] controlTypes =
            {
                typeof(FlatButton), typeof(CardPanel), typeof(ToggleSwitch),
                typeof(ThemedTextBox), typeof(ThemedRichTextBox), typeof(ThemedListBox)
            };

            foreach (Type type in controlTypes)
            {
                Assert(typeof(Control).IsAssignableFrom(type), type.Name + " 必须是 Control 子类。");
            }
        }

        /// <summary>
        /// R44：滚动条主题助手必须对“句柄未创建”和 null 安全；主题切换时可重复调用。
        /// 深色滚动条的实际观感由深色快照人工确认。
        /// </summary>
        private static void ThemeDarkScrollbarsApplyIsSafe()
        {
            RunOnSta(() =>
            {
                using (var box = new ThemedTextBox { Multiline = true })
                {
                    box.ScrollBars = ScrollBars.Vertical;
                    box.ApplyTheme(Palette.Dark);

                    // 句柄尚未创建：必须安静跳过，绝不能为了设主题提前 Handle。
                    AssertEqual(false, WindowTheme.ApplyScrollbars(box.Inner, true));

                    box.Inner.Handle.ToString();
                    WindowTheme.ApplyScrollbars(box.Inner, true);
                    WindowTheme.ApplyScrollbars(box.Inner, false);
                    WindowTheme.ApplyScrollbars(null, true);
                    WindowTheme.ApplyScrollbarsRecursive(box, true);
                    WindowTheme.ApplyScrollbarsRecursive(null, true);

                    box.ApplyTheme(Palette.Light);
                    box.ApplyTheme(Palette.Dark);
                }

                using (var log = new ThemedRichTextBox())
                {
                    log.Inner.Handle.ToString();
                    log.ApplyTheme(Palette.Dark);
                    log.ApplyTheme(Palette.Light);
                }
            });
        }

        /// <summary>
        /// 快照模式用显式 DPI 的 GDI+ 度量，避免同一进程先渲染的 96 DPI
        /// 污染 TextRenderer 的测定缓存（192 DPI 下 ToggleSwitch 会按 96 宽度裁字）。
        /// </summary>
        private static void ThemeTextMeasureUsesRequestedDpi()
        {
            RunOnSta(() =>
            {
                using (var font = new Font("Microsoft YaHei UI", 9f))
                {
                    int previous = ThemeText.MeasureDpiOverride;
                    try
                    {
                        ThemeText.MeasureDpiOverride = 96;
                        Size at96 = ThemeText.Measure("开机自启", font);

                        ThemeText.MeasureDpiOverride = 144;
                        Size at144 = ThemeText.Measure("开机自启", font);

                        ThemeText.MeasureDpiOverride = 192;
                        Size at192 = ThemeText.Measure("开机自启", font);

                        Assert(at96.Width > 0 && at96.Height > 0, "96 DPI 文本度量应大于 0。");
                        Assert(
                            at144.Width >= at96.Width * 14 / 10,
                            $"144 DPI 度量应约放大 1.5 倍：96={at96.Width}，144={at144.Width}");
                        Assert(
                            at192.Width >= at96.Width * 19 / 10,
                            $"192 DPI 度量应约放大 2 倍：96={at96.Width}，192={at192.Width}");
                    }
                    finally
                    {
                        ThemeText.MeasureDpiOverride = previous;
                    }
                }
            });
        }

        /// <summary>
        /// 主题切换不得重写已有 run 的颜色；空日志仍要拿到主题默认前景色。
        /// 读取 SelectionColor 是 reviewer 在 net48 上验证过的回读方式。
        /// </summary>
        private static void ThemedRichTextBoxExistingRunsKeepTheirColoursAfterApplyTheme()
        {
            RunOnSta(() =>
            {
                using (var log = new ThemedRichTextBox())
                {
                    Assert(log.Inner.Handle != IntPtr.Zero, "RichTextBox 句柄创建失败。");

                    var manager = new ThemeManager();
                    manager.systemPrefersDarkOverride = () => false;

                    manager.Mode = ThemeMode.Dark;
                    manager.ApplyTo(log);
                    AssertEqual(Palette.Dark.LogStdout, log.Inner.ForeColor);

                    log.AppendColored("A", Palette.Light.LogStdout);
                    log.AppendColored("B", Palette.Light.LogStderr);
                    log.AppendColored("C", Palette.Light.LogSystem);

                    manager.Mode = ThemeMode.Light;
                    manager.ApplyTo(log);
                    AssertRunColour(log.Inner, 0, Palette.Light.LogStdout);
                    AssertRunColour(log.Inner, 1, Palette.Light.LogStderr);
                    AssertRunColour(log.Inner, 2, Palette.Light.LogSystem);

                    manager.Mode = ThemeMode.Dark;
                    manager.ApplyTo(log);
                    AssertRunColour(log.Inner, 0, Palette.Light.LogStdout);
                    AssertRunColour(log.Inner, 1, Palette.Light.LogStderr);
                    AssertRunColour(log.Inner, 2, Palette.Light.LogSystem);
                }
            });
        }

        /// <summary>即使通过 Control 订阅，也必须收到内层 TextBox 的 TextChanged。</summary>
        private static void ThemedTextBoxTextChangedForwardsToBaseEvent()
        {
            RunOnSta(() =>
            {
                using (var box = new ThemedTextBox())
                {
                    Control asControl = box;
                    int changes = 0;
                    asControl.TextChanged += (sender, args) => changes++;

                    box.Text = "alpha";
                    AssertEqual(1, changes);

                    box.Inner.Text = "beta";
                    AssertEqual(2, changes);
                }
            });
        }

        /// <summary>RichTextBox 的 Text 必须转发到内层，且日志区默认只读。</summary>
        private static void ThemedRichTextBoxTextForwardsToInner()
        {
            RunOnSta(() =>
            {
                using (var log = new ThemedRichTextBox())
                {
                    log.Text = "日志内容";
                    AssertEqual("日志内容", log.Text);
                    AssertEqual("日志内容", log.Inner.Text);
                    Assert(log.Inner.ReadOnly, "ThemedRichTextBox 的内层 RichTextBox 应默认只读。");
                }
            });
        }
    }
}
