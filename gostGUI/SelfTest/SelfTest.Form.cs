// =====================================================================================
// SelfTest.Form.cs —— 主窗口用例：骨架、详情编辑器、日志面板、启动恢复与快照渲染。
// 本文件是自检套件的 partial：原单体 SelfTest.cs 已按领域拆分（该文件已不存在），用例名/断言/注册顺序未改。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace gostGUI
{
    internal static partial class SelfTest
    {
        /// <summary>
        /// 回归（review finding 1）：句柄还没有时，<c>InvokeRequired</c> 对任何线程都返回 false，
        /// 于是“在不在 UI 线程”必须由构造函数记下的线程 id 判断。这里在窗口句柄建立之前
        /// 从另一个线程走真实 PostToUi 路径投递一个回调，断言它不在调用线程上内联执行，
        /// 并且在 CreateControl 建好句柄之后于 UI 线程上被执行。
        /// </summary>
        private static void FormPostToUiMarshalsBeforeHandleExists()
        {
            var state = new UiDispatchState();
            var uiThread = new Thread(() =>
            {
                FormMain form = null;
                try
                {
                    form = new FormMain(true);
                    state.Form = form;
                    state.UiThreadId = Thread.CurrentThread.ManagedThreadId;
                    state.HandleExistedAtConstruction = form.IsHandleCreated;
                    state.Ready.Set();
                    state.Posted.Wait(TimeSpan.FromSeconds(30));

                    // 句柄还没建好：跨线程投递绝对不能内联执行（这正是被 review 揪出的漏洞）。
                    state.ExecutedBeforeHandle = state.Executed;

                    // 隐藏窗体上 Control.CreateControl() 可能什么都不做，必须访问 Handle
                    // 真正把句柄建出来（生产路径的 PrepareSnapshotScene 用 ForceHandles 做同一件事）。
                    form.CreateControl();
                    state.HandleCreated = form.Handle != IntPtr.Zero;
                    state.ExecutedAfterHandle = state.Executed;
                }
                catch (Exception ex)
                {
                    state.Failure = ex;
                }
                finally
                {
                    state.Done.Set();
                    try
                    {
                        form?.Dispose();
                    }
                    catch (Exception)
                    {
                        // 释放失败不影响断言结论。
                    }
                }
            });
            uiThread.IsBackground = true;
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();

            try
            {
                Assert(state.Ready.Wait(TimeSpan.FromSeconds(60)), "窗体线程未就绪。");

                var poster = new Thread(() =>
                {
                    try
                    {
                        state.PostedThreadId = Thread.CurrentThread.ManagedThreadId;
                        state.Form.PostToUiForTest(() =>
                        {
                            state.Executed = true;
                            state.ExecutedThreadId = Thread.CurrentThread.ManagedThreadId;
                        });
                    }
                    catch (Exception ex)
                    {
                        state.Failure = ex;
                    }
                    finally
                    {
                        state.Posted.Set();
                    }
                });
                poster.IsBackground = true;
                poster.Start();

                Assert(state.Posted.Wait(TimeSpan.FromSeconds(30)), "后台线程投递未完成。");
                Assert(state.Done.Wait(TimeSpan.FromSeconds(60)), "窗体线程未完成。");
            }
            finally
            {
                state.Ready.Set();
                state.Posted.Set();
                uiThread.Join(TimeSpan.FromSeconds(10));
            }

            if (state.Failure != null)
            {
                throw new SelfTestFailureException("投递用例异常：" + DescribeFailure(state.Failure));
            }

            Assert(state.UiThreadId != 0, "没有记录到 UI 线程 id。");
            Assert(state.PostedThreadId != state.UiThreadId, "投递线程必须与 UI 线程不同，否则测不到跨线程路径。");
            Assert(!state.HandleExistedAtConstruction,
                "构造完成时不应该已经创建窗体句柄，否则这条用例覆盖不到“句柄建立前”的窗口。");
            Assert(!state.ExecutedBeforeHandle,
                "句柄未建立时，跨线程投递的回调不得在调用线程上内联执行（会在工作线程上碰 UI）。");
            Assert(state.HandleCreated, "用例没能把窗体句柄建出来，无法验证排空行为。");
            Assert(state.ExecutedAfterHandle, "句柄建立后应排空积压回调并执行。");
            AssertEqual(state.UiThreadId, state.ExecutedThreadId);
        }

        /// <summary>
        /// 回归（review finding 3）：复制的副本在配置里紧插在源项后面，界面顺序必须与之同步，
        /// 否则列表底部多一行而 config.json 在源项后面，↑/↓ 会作用到用户看不到的行。
        /// </summary>
        private static void FormDuplicateKeepsListOrderInSyncWithManager()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                var data = new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "a", Program = "a.exe" },
                        new ConfigItem { Name = "b", Program = "b.exe" },
                        new ConfigItem { Name = "c", Program = "c.exe" }
                    }
                };
                Assert(Common.SaveTo(data, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnSta(() =>
                {
                    using (var form = new FormMain(true, manager))
                    {
                        form.DuplicateForTest(manager.Items[0]);

                        AssertEqual(4, manager.Items.Count);
                        AssertEqual("a 副本", manager.Items[1].Name);

                        IList<ConfigItem> listOrder = form.ListItemsForTest();
                        AssertEqual(manager.Items.Count, listOrder.Count);
                        for (int i = 0; i < manager.Items.Count; i++)
                        {
                            Assert(
                                ReferenceEquals(manager.Items[i], listOrder[i]),
                                "界面顺序必须与 config.json 顺序一致，第 " + i + " 项不一致：" +
                                (listOrder[i] == null ? "null" : listOrder[i].Name) + " vs " + manager.Items[i].Name);
                        }

                        Assert(ReferenceEquals(listOrder[1], form.SelectedItem), "复制后应选中副本，且它的下标是 1。");
                    }
                });

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "落盘后的配置应可解析：" + reloaded.Error);
                AssertEqual("a 副本", reloaded.Data.Items[1].Name);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// 回归（review minor）：行内重命名编辑框必须与绘制出来的名称对齐——
        /// 不能压住左侧状态点，纵向要落在名称那一行里。ThemedListBox 的行几何是
        /// padding 10 / 状态点 10 / gap 10，名称盒从行首 +3 DIP 起、高半行。
        /// </summary>
        private static void FormRenameEditorAlignsWithPaintedName()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                var data = new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "节点一", Program = "gost.exe", Args = "-L 127.0.0.1:8080" },
                        new ConfigItem { Name = "节点二", Program = "gost.exe" }
                    }
                };
                Assert(Common.SaveTo(data, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnSta(() =>
                {
                    using (var form = new FormMain(true, manager))
                    {
                        form.PrepareHandlesForTest();

                        Rectangle row = form.ListRowBoundsForTest(0);
                        Assert(row.Height > 0, "列表行矩形无效，无法验证编辑框对齐。");

                        form.BeginRenameForTest(manager.Items[0]);
                        Rectangle editor = form.RenameEditorBoundsForTest;
                        Assert(editor.Height > 0 && editor.Width > 0, "行内编辑框没有显示出来。");

                        // R43：用 DeviceDpi 把 DIP 换算成像素。原来的 row.Height / 44 是整数除法，
                        // 125%/150% 缩放下会得到 1，把按 DIP 正确的几何误判成越界。
                        int dpi = Math.Max(96, form.DeviceDpi);
                        int ToPixels(int dip)
                        {
                            return (int)Math.Round(dip * dpi / 96.0, MidpointRounding.AwayFromZero);
                        }

                        int dotRight = row.Left + ToPixels(20);
                        int nameTop = row.Top + ToPixels(3);
                        int nameBottom = row.Top + ToPixels(22);

                        Assert(editor.Left >= dotRight,
                            "重命名编辑框压住了状态点：editor.Left=" + editor.Left + "，状态点右边缘=" + dotRight);
                        Assert(editor.Top >= nameTop,
                            "重命名编辑框比名称文本高：" + editor.Top + " < " + nameTop);
                        Assert(editor.Bottom <= nameBottom + ToPixels(2),
                            "重命名编辑框超出名称行：" + editor.Bottom + " > " + nameBottom);
                        Assert(editor.Right <= row.Right,
                            "重命名编辑框超出列表宽度：" + editor.Right + " > " + row.Right);
                    }
                });
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// Task 12b：程序框下方的解析提示必须按“有效环境”（父环境 + 本项 env）解析，
        /// 命中时显示 → 完整路径 + 成功色，裸名找不到时显示中文错误 + 危险色。
        /// </summary>
        private static void FormResolvesProgramHint()
        {
            string dir = CreateTempDirectory();
            try
            {
                string existing = Path.Combine(dir, "resolved-tool.exe");
                File.WriteAllText(existing, string.Empty);

                string path = Path.Combine(dir, "config.json");
                var data = new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "提示用例", Program = string.Empty, Args = string.Empty }
                    }
                };
                Assert(Common.SaveTo(data, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                string missingText = null;
                Color missingColor = default(Color);
                Color danger = default(Color);
                string foundText = null;
                Color foundColor = default(Color);
                Color success = default(Color);

                // 窗体用例一律在独立 STA 线程上构造（与快照用例一致）。
                RunOnDedicatedSta(() =>
                {
                    using (var form = new FormMain(true, manager))
                    {
                        danger = form.CurrentPalette.Danger;
                        success = form.CurrentPalette.Success;

                        form.SetProgramFieldForTest("gostgui-12b-definitely-missing");
                        missingText = form.ProgramHintTextForTest;
                        missingColor = form.ProgramHintColorForTest;

                        form.SetProgramFieldForTest(existing);
                        foundText = form.ProgramHintTextForTest;
                        foundColor = form.ProgramHintColorForTest;
                    }
                });

                Assert(!string.IsNullOrWhiteSpace(missingText), "裸名找不到时提示不能为空。");
                Assert(missingText.IndexOfAny(new[] { '未', '找', '不', '错' }) >= 0,
                    "裸名找不到时应在提示里给出中文错误：" + missingText);
                AssertEqual(danger, missingColor);

                Assert(foundText != null && foundText.StartsWith("→", StringComparison.Ordinal),
                    "解析成功时提示应以 → 开头：" + foundText);
                Assert(foundText.Contains(existing), "解析成功时提示应包含绝对路径：" + foundText);
                AssertEqual(success, foundColor);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// R11（Task 12b）：环境变量值里的 CR/LF/TAB 无法用“每行一条 KEY=VALUE”表达，
        /// 渲染时折叠成一个空格；提交后保存的也必须是折叠后的值，且保持键顺序。
        /// </summary>
        private static void FormEnvEditorSanitizesNewlines()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                var item = new ConfigItem
                {
                    Name = "环境用例",
                    Program = "cmd.exe",
                    Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["KEEP"] = "1",
                        ["MULTI"] = "第一行\r\n第二行",
                        ["TAB"] = "a\tb"
                    }
                };
                Assert(Common.SaveTo(new ConfigData { Items = new List<ConfigItem> { item } }, path),
                    "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                string editorText = null;

                RunOnDedicatedSta(() =>
                {
                    using (var form = new FormMain(true, manager))
                    {
                        editorText = form.EnvFieldTextForTest;
                        form.CommitEnvFieldForTest();
                    }
                });

                string[] lines = (editorText ?? string.Empty).Replace("\r\n", "\n").Split('\n');
                AssertEqual(3, lines.Length);
                AssertEqual("KEEP=1", lines[0]);
                AssertEqual("MULTI=第一行 第二行", lines[1]);
                AssertEqual("TAB=a b", lines[2]);

                AssertEqual("第一行 第二行", manager.Items[0].Env["MULTI"]);
                AssertEqual("a b", manager.Items[0].Env["TAB"]);

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "落盘后的配置应可解析：" + reloaded.Error);
                Dictionary<string, string> saved = reloaded.Data.Items[0].Env;
                AssertEqual("第一行 第二行", saved["MULTI"]);
                AssertEqual("a b", saved["TAB"]);
                AssertEqual("KEEP|MULTI|TAB", string.Join("|", saved.Keys));
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// Task 12b 绑定约束：切项必须整组回填，不同项绝不能继承上一项的输入框文字。
        /// </summary>
        private static void FormDetailEditorLoadsSelectedItemOnly()
        {
            string dir = CreateTempDirectory();
            try
            {
                string workDirectory = Path.GetTempPath();
                string path = Path.Combine(dir, "config.json");
                var data = new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem
                        {
                            Name = "甲",
                            Program = "cmd.exe",
                            Args = "-a 甲",
                            WorkDir = string.Empty,
                            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["A_ONLY"] = "1"
                            }
                        },
                        new ConfigItem
                        {
                            Name = "乙",
                            Program = string.Empty,
                            Args = "-b 乙",
                            WorkDir = workDirectory,
                            RunAsAdmin = true,
                            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["B_ONLY"] = "2"
                            }
                        }
                    }
                };
                Assert(Common.SaveTo(data, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnDedicatedSta(() =>
                {
                    using (var form = new FormMain(true, manager))
                    {
                        form.SelectItemForTest(manager.Items[0]);
                        AssertEqual("cmd.exe", form.ProgramFieldTextForTest);
                        AssertEqual("-a 甲", form.ArgsFieldTextForTest);
                        AssertEqual(string.Empty, form.WorkDirFieldTextForTest);
                        AssertEqual(false, form.RunAsAdminFieldForTest);
                        AssertEqual("A_ONLY=1", form.EnvFieldTextForTest);

                        form.SelectItemForTest(manager.Items[1]);
                        AssertEqual(string.Empty, form.ProgramFieldTextForTest);
                        AssertEqual("-b 乙", form.ArgsFieldTextForTest);
                        AssertEqual(workDirectory, form.WorkDirFieldTextForTest);
                        AssertEqual(true, form.RunAsAdminFieldForTest);
                        AssertEqual("B_ONLY=2", form.EnvFieldTextForTest);

                        form.SelectItemForTest(manager.Items[0]);
                        AssertEqual("cmd.exe", form.ProgramFieldTextForTest);
                        AssertEqual("-a 甲", form.ArgsFieldTextForTest);
                        AssertEqual(string.Empty, form.WorkDirFieldTextForTest);
                        AssertEqual(false, form.RunAsAdminFieldForTest);
                        AssertEqual("A_ONLY=1", form.EnvFieldTextForTest);
                    }
                });
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// Task 12b：程序框的解析提示必须防抖——只改文字时不能同步扫 PATH，200ms 到期后才刷新。
        /// </summary>
        private static void FormProgramHintIsDebounced()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "防抖用例", Program = "cmd.exe" }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnDedicatedSta(() =>
                {
                    using (var form = new FormMain(true, manager))
                    {
                        string initial = form.ProgramHintTextForTest;
                        Assert(initial.StartsWith("→", StringComparison.Ordinal),
                            "初始程序应解析成功：" + initial);

                        form.SetProgramTextOnlyForTest("gostgui-12b-missing-debounce");
                        AssertEqual(initial, form.ProgramHintTextForTest);

                        form.FireProgramHintDebounceForTest();
                        AssertEqual(form.CurrentPalette.Danger, form.ProgramHintColorForTest);
                        Assert(form.ProgramHintTextForTest.IndexOf("未找到", StringComparison.Ordinal) >= 0,
                            "防抖到期后应显示中文未找到错误：" + form.ProgramHintTextForTest);
                    }
                });
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// review minor：改环境变量（例如给 PATH 追加目录）后必须立刻重算程序提示。
        /// 旧的“未找到”红提示不能留在屏幕上——那和“程序其实已经可以解析”互相矛盾。
        /// </summary>
        private static void FormEnvPathEditRefreshesHint()
        {
            string dir = CreateTempDirectory();
            try
            {
                string bin = Path.Combine(dir, "bin");
                Directory.CreateDirectory(bin);
                string tool = Path.Combine(bin, "gostgui-path-tool.exe");
                File.WriteAllText(tool, string.Empty);

                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "PATH 用例", Program = "gostgui-path-tool" }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnDedicatedSta(() =>
                {
                    using (var form = new FormMain(true, manager))
                    {
                        AssertEqual(form.CurrentPalette.Danger, form.ProgramHintColorForTest);
                        Assert(form.ProgramHintTextForTest.IndexOf("未找到", StringComparison.Ordinal) >= 0,
                            "PATH 里没有工具时应先显示未找到：" + form.ProgramHintTextForTest);

                        string parentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                        form.SetEnvTextForTest("PATH=" + bin + ";" + parentPath);
                        form.CommitEnvFieldForTest();

                        AssertEqual(form.CurrentPalette.Success, form.ProgramHintColorForTest);
                        Assert(form.ProgramHintTextForTest.IndexOf(tool, StringComparison.OrdinalIgnoreCase) >= 0,
                            "PATH 更新后提示应包含解析到的绝对路径：" + form.ProgramHintTextForTest);
                    }
                });

                AssertEqual(bin + ";" + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty),
                    manager.Items[0].Env["PATH"]);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// review Important：窗口关闭/托盘退出时，用户刚输入但还没触发 Leave 的字段必须落盘。
        /// 这里分别走 FormClosing 处理器与托盘退出的“退出前提交”路径。
        /// </summary>
        private static void FormCommitsPendingEditsOnExit()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "退出用例", Program = "cmd.exe", Args = string.Empty }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                RunOnDedicatedSta(() =>
                {
                    using (var form = new FormMain(true, manager))
                    {
                        // ① 关闭窗口（UserClosing → 隐藏到托盘）前提交。
                        form.SetArgsTextForTest("关闭窗口前的未提交参数");
                        form.SetEnvTextForTest("LATE=1");
                        form.TriggerFormClosingForTest();
                        AssertEqual("关闭窗口前的未提交参数", manager.Items[0].Args);
                        AssertEqual("1", manager.Items[0].Env["LATE"]);

                        // ② 托盘/按钮“退出”前提交。
                        form.SetArgsTextForTest("托盘退出前的未提交参数");
                        form.SetEnvTextForTest("LATE2=2");
                        form.CommitPendingEditsForTest();
                        AssertEqual("托盘退出前的未提交参数", manager.Items[0].Args);
                        AssertEqual("2", manager.Items[0].Env["LATE2"]);
                    }
                });

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "落盘后的配置应可解析：" + reloaded.Error);
                AssertEqual("托盘退出前的未提交参数", reloaded.Data.Items[0].Args);
                AssertEqual("2", reloaded.Data.Items[0].Env["LATE2"]);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// R45：退出前保存失败必须记下中文原因并走可见提示（自检里抑制模态框，
        /// 但失败原因必须可断言）。目标路径是一个目录，保存必然失败。
        /// </summary>
        private static void FormExitSaveFailureIsReported()
        {
            string dir = CreateTempDirectory();
            try
            {
                string unsavablePath = Path.Combine(dir, "config-as-directory");
                Directory.CreateDirectory(unsavablePath);

                RunOnDedicatedSta(() =>
                {
                    int previousDpi = FormMain.StartupDpiOverride;
                    int previousPaint = ThemePaint.DpiOverride;
                    FormMain.StartupDpiOverride = 96;
                    ThemePaint.DpiOverride = 96;
                    try
                    {
                        var manager = new ConfigurationManager(unsavablePath);
                        using (var form = new FormMain(true, manager))
                        {
                            form.SuppressDialogsForTest = true;
                            form.SetWindowGeometryForTest(new Size(1000, 600), new Point(10, 20));
                            Assert(!form.BannerVisibleForTest, "保存失败前不应有横幅。");

                            form.PersistWindowStateForTest(true);

                            Assert(
                                form.LastWindowSaveFailureForTest.Length > 0,
                                "退出前保存失败必须被记录下来。");
                            Assert(
                                form.LastWindowSaveFailureForTest.Contains("保存"),
                                "失败原因应为中文保存错误：" + form.LastWindowSaveFailureForTest);
                            Assert(
                                manager.SaveError.Length > 0,
                                "配置管理器应保留中文保存错误：" + manager.SaveError);
                            Assert(
                                !form.BannerVisibleForTest,
                                "退出路径只保留 R45 模态框，不应再弹退出后不可见的 SaveFailed 横幅。");

                            // 隐藏到托盘路径仍保留原有横幅信号（气泡在自检里被抑制）。
                            form.PersistWindowStateForTest(false);
                            Assert(
                                form.BannerVisibleForTest,
                                "隐藏到托盘时保存失败应显示横幅（应用仍在运行）。");
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
        /// R2-13：退出前提交升起的横幅（从托盘退出时窗口已隐藏，没人看得见）必须转成退出保存的
        /// 可见失败原因；没有横幅时退出保存仍然静默成功。
        /// </summary>
        private static void FormExitCommitBannerBecomesExitFailure()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "退出横幅", Program = "cmd.exe", Args = string.Empty }
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
                            form.SetWindowGeometryForTest(new Size(1000, 600), new Point(10, 20));

                            // ① 成功路径保持静默：没有横幅，窗口状态保存成功。
                            form.SaveExitWindowStateForTest();
                            AssertEqual("", form.LastWindowSaveFailureForTest);
                            Assert(!form.BannerVisibleForTest, "成功路径不应留下横幅。");
                        }

                        using (var form = new FormMain(true, manager))
                        {
                            form.SuppressDialogsForTest = true;
                            form.SetWindowGeometryForTest(new Size(1000, 600), new Point(10, 20));

                            // ② 提交本身能落盘，但合并后的环境块超过 32K，会升起“环境变量无效”横幅。
                            // 从托盘退出时窗口已隐藏，这个横幅看不见，必须转成退出保存的失败原因。
                            // 断言取精确值：窗口状态保存本来会成功，任何额外合并/别的失败都会让它不等。
                            string bigValue = new string('y', 40 * 1024);
                            var bigOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                { "BIG", bigValue }
                            };
                            string envError;
                            gostGUI.Shared.EnvBlock.BuildEffective(
                                gostGUI.Shared.ChildEnvironment.SnapshotParentEnvironment(),
                                bigOverrides,
                                out envError);
                            Assert(!string.IsNullOrEmpty(envError), "夹具环境应超过 32K 上限。");
                            string expectedReason = "环境变量无效：" + envError;

                            form.SetEnvTextForTest("BIG=" + bigValue);
                            form.SaveExitWindowStateForTest();

                            AssertEqual(expectedReason, form.LastWindowSaveFailureForTest);
                            Assert(
                                !form.BannerVisibleForTest,
                                "退出路径不应把横幅留在不可见的窗口上。");
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
        /// R2-13（系统关机/任务管理器关闭）：这条 FormClosing 分支也必须捕获提交横幅。
        /// 它不弹模态框（会阻塞/延长系统关机），但原因必须落进 <c>_lastWindowSaveFailure</c>，
        /// 生产路径再把它写进托盘气泡与系统日志。
        /// </summary>
        private static void FormSystemCloseCommitBannerIsRecorded()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "关机用例", Program = "cmd.exe", Args = string.Empty }
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
                            form.SetWindowGeometryForTest(new Size(1000, 600), new Point(10, 20));

                            string bigValue = new string('y', 40 * 1024);
                            var bigOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                { "BIG", bigValue }
                            };
                            string envError;
                            gostGUI.Shared.EnvBlock.BuildEffective(
                                gostGUI.Shared.ChildEnvironment.SnapshotParentEnvironment(),
                                bigOverrides,
                                out envError);
                            Assert(!string.IsNullOrEmpty(envError), "夹具环境应超过 32K 上限。");
                            string expectedReason = "环境变量无效：" + envError;

                            form.SetEnvTextForTest("BIG=" + bigValue);
                            form.TriggerSystemCloseForTest();

                            AssertEqual(expectedReason, form.LastWindowSaveFailureForTest);
                            Assert(
                                !form.BannerVisibleForTest,
                                "系统关闭路径不应把横幅留在不可见的窗口上。");
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
        /// R2-13（提交期 SaveFailed）：提交时的配置保存失败会升起 SaveFailed 横幅。
        /// 同一个配置文件被独占读句柄锁住，提交与窗口状态保存都会失败；断言退出失败原因
        /// 精确等于“提交横幅；窗口状态保存失败”，证明横幅先被捕获、再与保存失败合并。
        /// </summary>
        private static void FormExitCommitSaveFailedBannerBecomesExitFailure()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "locked-config.json");
                Assert(Common.SaveTo(new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "保存失败横幅", Program = "cmd.exe", Args = string.Empty }
                    }
                }, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
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
                                form.SetWindowGeometryForTest(new Size(1000, 600), new Point(10, 20));

                                // 改 env 会触发 UpdateItem → Save；文件被锁住，SaveFailed 横幅升起。
                                // 再叠加一个超过 32K 的环境块，让同一次提交升起第二条不同的原因（环境变量无效）。
                                string bigValue = new string('y', 40 * 1024);
                                var bigOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    { "BIG", bigValue }
                                };
                                string envError;
                                gostGUI.Shared.EnvBlock.BuildEffective(
                                    gostGUI.Shared.ChildEnvironment.SnapshotParentEnvironment(),
                                    bigOverrides,
                                    out envError);
                                Assert(!string.IsNullOrEmpty(envError), "夹具环境应超过 32K 上限。");
                                string envReason = "环境变量无效：" + envError;

                                form.SetEnvTextForTest("BIG=" + bigValue);
                                form.SaveExitWindowStateForTest();

                                Assert(manager.SaveError.Length > 0, "夹具应让配置保存失败。");
                                string banner = manager.SaveError;

                                // 提交期的两条不同原因（先 SaveFailed，后环境变量无效）必须都保留，
                                // 再与窗口状态保存失败（同一 SaveError）按顺序合并。
                                AssertEqual(banner + "；" + envReason + "；" + banner,
                                    form.LastWindowSaveFailureForTest);
                                Assert(
                                    form.LastWindowSaveFailureForTest.Contains(envReason),
                                    "两条不同的提交原因都必须保留：" + form.LastWindowSaveFailureForTest);
                                Assert(
                                    !form.BannerVisibleForTest,
                                    "退出路径不应把横幅留在不可见的窗口上。");
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
        /// R36：启动时的自动恢复/开机自启只拉起 <c>enable && !runAsAdmin</c> 的项。
        /// 纯函数断言（不启动任何进程），同时逐字钉住跳过那一行的文案。
        /// </summary>
        private static void StartupSkipsAdminItems()
        {
            Assert(!FormMain.ShouldAutoRestore(null), "null 项不应参与自动恢复。");

            Assert(
                FormMain.ShouldAutoRestore(new ConfigItem { Enable = true, RunAsAdmin = false }),
                "启用且非管理员的项应参与自动恢复。");
            Assert(
                !FormMain.ShouldAutoRestore(new ConfigItem { Enable = true, RunAsAdmin = true }),
                "管理员项不应参与自动恢复（R36：留给显式“启动/全部启动”）。");
            Assert(
                !FormMain.ShouldAutoRestore(new ConfigItem { Enable = false, RunAsAdmin = false }),
                "未启用的项不应参与自动恢复。");
            Assert(
                !FormMain.ShouldAutoRestore(new ConfigItem { Enable = false, RunAsAdmin = true }),
                "未启用的管理员项不应参与自动恢复。");

            AssertEqual("已跳过：需要管理员权限，请手动启动", FormMain.AutoRestoreSkipMessage);
        }

        /// <summary>
        /// Task 12b：日志面板必须批量排空，stdout/stderr/系统三类各用各自的颜色，
        /// 并且超过 1000 行时整段清空（沿用 v1.8 的策略）。
        /// </summary>
        private static void LogBatchesAndColors()
        {
            RunOnDedicatedSta(() =>
            {
                using (var host = new Panel { Size = new Size(900, 300) })
                using (var manager = new LogViewManager(host, new Font("Consolas", 10f), action =>
                {
                    action();
                    return true;
                }))
                {
                    var item = new ConfigItem { Name = "日志用例" };
                    manager.AddLogView(item);
                    ForceControlHandles(host);

                    manager.AppendText(item, LogKind.Stdout, "标准输出\n");
                    manager.AppendText(item, LogKind.Stderr, "错误输出\n");
                    manager.AppendText(item, LogKind.System, "系统消息\n");
                    manager.DrainForTest();

                    ThemedRichTextBox view = manager.GetViewForTest(item);
                    Assert(view != null, "日志视图没有创建。");
                    RichTextBox box = view.Inner;
                    string text = box.Text;
                    int stdoutIndex = text.IndexOf("标准输出", StringComparison.Ordinal);
                    int stderrIndex = text.IndexOf("错误输出", StringComparison.Ordinal);
                    int systemIndex = text.IndexOf("系统消息", StringComparison.Ordinal);
                    Assert(stdoutIndex >= 0 && stderrIndex >= 0 && systemIndex >= 0,
                        "三类日志没有全部写入：" + text);

                    AssertRunColour(box, stdoutIndex, Palette.Light.LogStdout);
                    AssertRunColour(box, stderrIndex, Palette.Light.LogStderr);
                    AssertRunColour(box, systemIndex, Palette.Light.LogSystem);

                    for (int i = 0; i < 1200; i++)
                    {
                        manager.AppendText(item, LogKind.Stdout, "第 " + i + " 行\n");
                    }

                    manager.DrainForTest();
                    Assert(manager.LineCountForTest(item) <= 1000,
                        "日志行数上限失效：" + manager.LineCountForTest(item));
                    Assert(box.TextLength > 0, "超过上限后应保留最新内容。");
                    Assert(!box.Text.Contains("第 0 行\n", StringComparison.Ordinal),
                        "超过上限时应整段清空旧内容。");

                    // 系统行里带“失败”时按危险色显示（review test gap）。
                    manager.AppendText(item, LogKind.System, "启动失败：演示错误\n");
                    manager.DrainForTest();
                    int errorIndex = box.Text.IndexOf("启动失败", StringComparison.Ordinal);
                    Assert(errorIndex >= 0, "系统错误行没有写入日志。");
                    AssertRunColour(box, errorIndex, Palette.Light.Danger);
                }
            });
        }

        /// <summary>
        /// Task 12b：用户往上翻日志时不抢滚动条；只有已经贴底时才跟着新日志走。
        /// </summary>
        private static void LogDoesNotAutoScrollWhenUserScrolledUp()
        {
            RunOnDedicatedSta(() =>
            {
                using (var host = new Panel { Size = new Size(900, 240) })
                using (var manager = new LogViewManager(host, new Font("Consolas", 10f), action =>
                {
                    action();
                    return true;
                }))
                {
                    var item = new ConfigItem { Name = "滚动用例" };
                    manager.AddLogView(item);
                    ForceControlHandles(host);
                    host.PerformLayout();

                    for (int i = 0; i < 120; i++)
                    {
                        manager.AppendText(item, LogKind.Stdout, "第 " + i + " 行：用于制造可滚动日志\n");
                    }

                    manager.DrainForTest();

                    ThemedRichTextBox view = manager.GetViewForTest(item);
                    Assert(view != null, "日志视图没有创建。");
                    RichTextBox box = view.Inner;
                    Assert(box.TextLength > 0, "日志内容为空，无法验证滚动行为。");

                    box.SelectionStart = 0;
                    box.SelectionLength = 0;
                    box.ScrollToCaret();
                    int firstVisibleBefore = box.GetCharIndexFromPosition(new Point(1, 1));
                    Assert(firstVisibleBefore <= 2, "用例没能把日志滚到顶部：" + firstVisibleBefore);

                    manager.AppendText(item, LogKind.Stdout, "用户在看上面时追加的一行\n");
                    manager.DrainForTest();
                    int firstVisibleAfter = box.GetCharIndexFromPosition(new Point(1, 1));
                    AssertEqual(firstVisibleBefore, firstVisibleAfter);

                    // 贴底后再追加：必须跟随到最新一行。
                    box.SelectionStart = box.TextLength;
                    box.SelectionLength = 0;
                    box.ScrollToCaret();
                    manager.AppendText(item, LogKind.Stdout, "贴底时追加的一行\n");
                    manager.DrainForTest();

                    Point last = box.GetPositionFromCharIndex(box.TextLength - 1);
                    Assert(last.Y + box.Font.Height <= box.ClientSize.Height + 2,
                        "贴底时追加应自动滚动到底部；last.Y=" + last.Y + "，clientHeight=" + box.ClientSize.Height);
                }
            });
        }

        /// <summary>
        /// review test gap：清空前已经排队的日志不能在清空后又冒出来（按 generation 丢弃）。
        /// </summary>
        private static void LogClearDropsQueuedLines()
        {
            RunOnDedicatedSta(() =>
            {
                using (var host = new Panel { Size = new Size(900, 300) })
                using (var manager = new LogViewManager(host, new Font("Consolas", 10f), action =>
                {
                    action();
                    return true;
                }))
                {
                    var item = new ConfigItem { Name = "清空用例" };
                    manager.AddLogView(item);
                    ForceControlHandles(host);

                    manager.AppendText(item, LogKind.Stdout, "清空前排队的一行\n");
                    manager.ClearLog(item);
                    manager.DrainForTest();

                    ThemedRichTextBox view = manager.GetViewForTest(item);
                    Assert(view != null, "日志视图没有创建。");
                    Assert(!view.Text.Contains("清空前排队的一行", StringComparison.Ordinal),
                        "清空前排队的日志不应在清空后出现：" + view.Text);
                    AssertEqual(0, manager.LineCountForTest(item));

                    manager.AppendText(item, LogKind.System, "清空后的一行\n");
                    manager.DrainForTest();
                    Assert(view.Text.Contains("清空后的一行", StringComparison.Ordinal),
                        "清空后的新日志应正常写入。");
                    AssertEqual(1, manager.LineCountForTest(item));
                }
            });
        }

        /// <summary>
        /// review test gap：Timer 批量排空路径——AppendText 只入队、不同步写控件；
        /// Tick 在 postToUi 的（UI）线程上一次性排空整批。
        /// </summary>
        private static void LogTimerDrainsInBatch()
        {
            RunOnDedicatedSta(() =>
            {
                int uiThreadId = Thread.CurrentThread.ManagedThreadId;
                int drainedThreadId = 0;

                using (var host = new Panel { Size = new Size(900, 300) })
                using (var manager = new LogViewManager(host, new Font("Consolas", 10f), action =>
                {
                    drainedThreadId = Thread.CurrentThread.ManagedThreadId;
                    action();
                    return true;
                }))
                {
                    var item = new ConfigItem { Name = "定时器用例" };
                    manager.AddLogView(item);
                    ForceControlHandles(host);
                    ThemedRichTextBox view = manager.GetViewForTest(item);
                    Assert(view != null, "日志视图没有创建。");

                    string[] lines = { "第一行\n", "第二行\n", "第三行\n", "第四行\n", "第五行\n" };
                    foreach (string line in lines)
                    {
                        manager.AppendText(item, LogKind.Stdout, line);
                    }

                    // 只入队，不允许同步写控件（工作线程绝不能阻塞在 UI 上）。
                    AssertEqual(string.Empty, view.Text);

                    manager.PumpTimerForTest();

                    foreach (string line in lines)
                    {
                        Assert(view.Text.Contains(line.TrimEnd('\n'), StringComparison.Ordinal),
                            "Timer 排空后应包含：" + line.TrimEnd('\n'));
                    }

                    AssertEqual(uiThreadId, drainedThreadId);
                }
            });
        }

        /// <summary>窗体/控件用例专用的独立 STA 线程：无论调用线程是不是 STA 都另起一条并等待结束。</summary>
        private static void RunOnDedicatedSta(Action body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert(thread.Join(TimeSpan.FromSeconds(120)), "独立 STA 用例线程未在 120 秒内结束。");

            if (failure != null)
            {
                if (failure is SelfTestFailureException)
                {
                    throw failure;
                }

                throw new SelfTestFailureException("独立 STA 线程中的断言失败：" + DescribeFailure(failure));
            }
        }

        /// <summary>
        /// Task 13：主题切换后日志面板必须用新调色板重渲染保留的行，
        /// 同时保留 stdout/stderr/system 三类各自的颜色，不能压成同一种。
        /// </summary>
        private static void LogRetainedRunsRecolourOnThemeChange()
        {
            RunOnDedicatedSta(() =>
            {
                using (var host = new Panel { Size = new Size(600, 300) })
                using (var manager = new LogViewManager(host, new Font("Consolas", 10f), action =>
                {
                    action();
                    return true;
                }))
                {
                    var item = new ConfigItem { Name = "重着色用例" };
                    manager.AddLogView(item);
                    manager.AppendText(item, LogKind.Stdout, "out\n");
                    manager.AppendText(item, LogKind.Stderr, "err\n");
                    manager.AppendText(item, LogKind.System, "sys\n");
                    manager.DrainForTest();

                    ThemedRichTextBox view = manager.GetViewForTest(item);
                    Assert(view != null, "日志视图没有创建。");
                    RichTextBox box = view.Inner;

                    manager.ApplyTheme(Palette.Light);
                    AssertRunColour(box, 0, Palette.Light.LogStdout);
                    AssertRunColour(box, 4, Palette.Light.LogStderr);
                    AssertRunColour(box, 8, Palette.Light.LogSystem);

                    manager.ApplyTheme(Palette.Dark);
                    AssertEqual("out\nerr\nsys\n", view.Text);
                    AssertRunColour(box, 0, Palette.Dark.LogStdout);
                    AssertRunColour(box, 4, Palette.Dark.LogStderr);
                    AssertRunColour(box, 8, Palette.Dark.LogSystem);

                    manager.ApplyTheme(Palette.Light);
                    AssertRunColour(box, 0, Palette.Light.LogStdout);
                    AssertRunColour(box, 4, Palette.Light.LogStderr);
                    AssertRunColour(box, 8, Palette.Light.LogSystem);

                    // 已重渲染的 run 不能影响后续追加行的默认颜色。
                    manager.AppendText(item, LogKind.Stdout, "tail\n");
                    manager.DrainForTest();
                    AssertRunColour(box, 12, Palette.Light.LogStdout);
                }
            });
        }

        /// <summary>递归建句柄（隐藏窗体上 CreateControl 未必足够，访问 Handle 才会真正创建）。</summary>
        private static void ForceControlHandles(Control control)
        {
            if (control == null)
            {
                return;
            }

            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls)
            {
                ForceControlHandles(child);
            }
        }

        /// <summary>
        /// R42 + Task 13：在独立 STA 线程上跑与 --ui-snapshot 相同的例程，断言 96 DPI 的
        /// 1024×640、144 DPI 的 1536×960 两对 PNG 都是非空真 PNG；R49 起，192 DPI 的
        /// 2048×1280 对只在宿主真的是 192 DPI 时才要求产出（否则断言它被跳过并记 Notice）。
        /// 每对浅色/深色不同，不同 DPI 之间也不同（说明缩放真的生效）。
        /// 只有“窗体根本创建不出来”时才 SKIP 并记 Notice（其余失败一律判 FAIL）。
        /// </summary>
        private static void FormSnapshotRendersBothThemes()
        {
            string dir = CreateTempDirectory();
            try
            {
                var attempt = new SnapshotAttempt();
                var thread = new Thread(() =>
                {
                    try
                    {
                        attempt.Ok = FormMain.TryRenderSnapshots(dir, out attempt.Error);
                    }
                    catch (Exception ex)
                    {
                        attempt.Failure = ex;
                    }
                });
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                Assert(thread.Join(TimeSpan.FromSeconds(120)), "快照线程未在 120 秒内结束。");

                if (attempt.Failure != null)
                {
                    string message = DescribeFailure(attempt.Failure);
                    if (message.Contains("无法创建窗体"))
                    {
                        Notice("SKIP Form_SnapshotRendersBothThemes：当前环境无法创建窗体：" + message);
                        return;
                    }

                    throw new SelfTestFailureException("快照渲染异常：" + message);
                }

                if (!attempt.Ok)
                {
                    string error = attempt.Error ?? "未知错误";
                    if (error.StartsWith("无法创建窗体", StringComparison.Ordinal))
                    {
                        Notice("SKIP Form_SnapshotRendersBothThemes：当前环境无法创建窗体：" + error);
                        return;
                    }

                    throw new SelfTestFailureException("快照渲染失败：" + error);
                }

                string lightPath = Path.Combine(dir, "form-light.png");
                string darkPath = Path.Combine(dir, "form-dark.png");
                string light150Path = Path.Combine(dir, "form-150-light.png");
                string dark150Path = Path.Combine(dir, "form-150-dark.png");
                string light200Path = Path.Combine(dir, "form-200-light.png");
                string dark200Path = Path.Combine(dir, "form-200-dark.png");

                AssertPng(lightPath, 1024, 640);
                AssertPng(darkPath, 1024, 640);
                AssertPng(light150Path, 1536, 960);
                AssertPng(dark150Path, 1536, 960);

                Assert(
                    !BytesEqual(File.ReadAllBytes(lightPath), File.ReadAllBytes(darkPath)),
                    "96 DPI 浅色与深色快照不应逐字节相同（说明主题切换没生效）。");
                Assert(
                    !BytesEqual(File.ReadAllBytes(light150Path), File.ReadAllBytes(dark150Path)),
                    "144 DPI 浅色与深色快照不应逐字节相同（说明主题切换没生效）。");
                Assert(
                    !BytesEqual(File.ReadAllBytes(lightPath), File.ReadAllBytes(light150Path)),
                    "144 DPI 快照不应与 96 DPI 相同（说明 DPI 缩放没生效）。");

                // R49：192 DPI 对只在宿主真的是 192 DPI 时产出；否则必须确认它被跳过。
                int hostDpi = FormMain.HostSystemDpi();
                if (hostDpi == 192)
                {
                    AssertPng(light200Path, 2048, 1280);
                    AssertPng(dark200Path, 2048, 1280);

                    Assert(
                        !BytesEqual(File.ReadAllBytes(light200Path), File.ReadAllBytes(dark200Path)),
                        "192 DPI 浅色与深色快照不应逐字节相同（说明主题切换没生效）。");
                    Assert(
                        !BytesEqual(File.ReadAllBytes(lightPath), File.ReadAllBytes(light200Path)),
                        "192 DPI 快照不应与 96 DPI 相同（说明 DPI 缩放没生效）。");
                }
                else
                {
                    Assert(
                        !File.Exists(light200Path) && !File.Exists(dark200Path),
                        "宿主不是 192 DPI（实际 " + hostDpi + "）时不应产出 200% 快照对。");
                    Notice("SKIP Form_SnapshotRendersBothThemes 的 192 DPI 快照对：宿主系统 DPI=" +
                        hostDpi + "（不是 192）。");
                }
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>断言文件存在、是 PNG、尺寸为 expectedWidth×expectedHeight，且内容非空。</summary>
        private static void AssertPng(string path, int expectedWidth, int expectedHeight)
        {
            Assert(File.Exists(path), "快照文件未生成：" + path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert(bytes.Length > 24, "快照文件内容为空：" + path);

            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (int i = 0; i < signature.Length; i++)
            {
                AssertEqual(signature[i], bytes[i]);
            }

            // 8 字节签名 + 4 字节长度 + "IHDR"，随后是宽度/高度各 4 字节大端。
            AssertEqual((byte)'I', bytes[12]);
            AssertEqual((byte)'H', bytes[13]);
            AssertEqual((byte)'D', bytes[14]);
            AssertEqual((byte)'R', bytes[15]);

            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            AssertEqual(expectedWidth, width);
            AssertEqual(expectedHeight, height);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void AssertRunColour(RichTextBox box, int index, Color expected)
        {
            box.SelectionStart = index;
            box.SelectionLength = 1;
            AssertEqual(expected, box.SelectionColor);
        }

        /// <summary>在 STA 线程上执行控件用例；当前线程已是 STA 时直接执行，避免无谓的线程切换。</summary>
        private static void RunOnSta(Action body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                body();
                return;
            }

            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new SelfTestFailureException("STA 线程中的断言失败：" + DescribeFailure(failure));
            }
        }

        /// <summary>快照用例的跨线程搬运盒：字段可以被线程内部的 out 参数直接写入。</summary>
        private sealed class SnapshotAttempt
        {
            public bool Ok;

            public string Error;

            public Exception Failure;
        }

        /// <summary>投递用例（finding 1）的跨线程搬运盒。</summary>
        private sealed class UiDispatchState
        {
            public readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);

            public readonly ManualResetEventSlim Posted = new ManualResetEventSlim(false);

            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);

            public FormMain Form;

            public int UiThreadId;

            public int PostedThreadId;

            public int ExecutedThreadId;

            public bool HandleExistedAtConstruction;

            public bool HandleCreated;

            public bool Executed;

            public bool ExecutedBeforeHandle;

            public bool ExecutedAfterHandle;

            public Exception Failure;
        }
    }
}
