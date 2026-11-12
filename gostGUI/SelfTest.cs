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
    /// <summary>
    /// 隐藏的 --selftest 自检骨架：按登记顺序执行用例，结果写到控制台与 selftest.log。
    /// 退出码与 selftest.log 是权威输出，控制台输出只是尽力而为。
    /// </summary>
    internal static class SelfTest
    {
        private const string LogFileName = "selftest.log";
        private static readonly List<TestCase> Cases = new List<TestCase>();
        private static readonly List<string> Notices = new List<string>();

        static SelfTest()
        {
            RegisterCases();
        }

        /// <summary>在这里按顺序登记所有自检用例。</summary>
        private static void RegisterCases()
        {
            Check("sanity", () => Assert(1 + 1 == 2));
            Check("Load_OldFormat_GetsDefaults", LoadOldFormatGetsDefaults);
            Check("Save_ThenLoad_RoundTripsNewFields", SaveThenLoadRoundTripsNewFields);
            Check("Load_UnknownFieldsAndWrongTypes_DoesNotThrow", LoadUnknownFieldsAndWrongTypesDoesNotThrow);
            Check("Load_MissingFile_ReturnsOkWithDefaults", LoadMissingFileReturnsOkWithDefaults);

            // Task 13：窗口几何以 DIP 存储。纯函数用例，可在 96/120/144 三档 DPI 上往返。
            Check("Window_PersistsInDips", WindowPersistsInDips);
            Check("Window_InvalidValues_FallBackToDefault", WindowInvalidValuesFallBackToDefault);
            Check("Window_Placement_ClampsAndFallsBack", WindowPlacementClampsAndFallsBack);
            Check("Window_MixedDpi_UsesCaptureDpi", WindowMixedDpiUsesCaptureDpi);
            Check("Window_MinimumSource_RelaxesWithWorkArea", WindowMinimumSourceRelaxesWithWorkArea);
            Check("Env_IsCaseInsensitive", EnvIsCaseInsensitive);
            Check("Env_ParseLines_SkipsBlanksAndComments", EnvParseLinesSkipsBlanksAndComments);
            Check("Env_ParseLines_SplitsOnFirstEquals", EnvParseLinesSplitsOnFirstEquals);
            Check("Env_Expand_ItemOrderThenParent", EnvExpandItemOrderThenParent);
            Check("Env_BuildEffective_OverridesCaseInsensitive", EnvBuildEffectiveOverridesCaseInsensitive);
            Check("Env_BuildEffective_TooLarge_ReturnsError", EnvBuildEffectiveTooLargeReturnsError);
            Check("Env_FormatLines_RoundTrips", EnvFormatLinesRoundTrips);

            // 子进程环境块应用：32767+2 守卫与“无覆盖即不碰 psi”。纯内存用例，不启动进程。
            Check("ChildEnv_TooLargeBlock_ReturnsChineseError", ChildEnvTooLargeBlockReturnsChineseError);
            Check("ChildEnv_NoOverrides_IsNoOp", ChildEnvNoOverridesIsNoOp);

            // 程序解析：PATH / PATHEXT / 有效环境。夹具只建空文件，绝不启动进程。
            Check("Resolve_BareName_FoundInPathDir", ResolveBareNameFoundInPathDir);
            Check("Resolve_BareName_AppDirWins", ResolveBareNameAppDirWins);
            Check("Resolve_AppliesPathext", ResolveAppliesPathext);
            Check("Resolve_UsesEffectiveEnvironment", ResolveUsesEffectiveEnvironment);
            Check("Resolve_PathWithSpacesAndQuotes", ResolvePathWithSpacesAndQuotes);
            Check("Resolve_RelativeToBaseDir", ResolveRelativeToBaseDir);
            Check("Resolve_BatchFile_KindIsBatch", ResolveBatchFileKindIsBatch);
            Check("Resolve_Missing_ReportsSearchedCount", ResolveMissingReportsSearchedCount);
            Check("Resolve_PathLookup_IsCaseInsensitive", ResolvePathLookupIsCaseInsensitive);
            Check("Resolve_Pathext_FallsBackToDefault", ResolvePathextFallsBackToDefault);
            Check("Resolve_PathEntry_QuotesAndVariables", ResolvePathEntryQuotesAndVariables);
            Check("Resolve_Ps1_IsRejected", ResolvePs1IsRejected);
            Check("Resolve_MalformedPath_DoesNotThrow", ResolveMalformedPathDoesNotThrow);
            Check("Resolve_EmptyProgram_ReturnsError", ResolveEmptyProgramReturnsError);

            // 启动管线：有效环境 → 解析程序 → 解析工作目录 → 得到启动参数。夹具只建空文件，绝不启动进程。
            Check("Pipeline_EmptyProgram_ReturnsError", PipelineEmptyProgramReturnsError);
            Check("Pipeline_ResolvesAndExpandsWorkDir", PipelineResolvesAndExpandsWorkDir);
            Check("Pipeline_DefaultWorkDirIsBaseDir", PipelineDefaultWorkDirIsBaseDir);
            Check("Pipeline_MissingWorkDir_ReturnsError", PipelineMissingWorkDirReturnsError);
            Check("Pipeline_BatchWrapsWithCmd", PipelineBatchWrapsWithCmd);
            Check("Pipeline_KeepsArgsVerbatim", PipelineKeepsArgsVerbatim);
            Check("Pipeline_PropagatesEnvOverrides", PipelinePropagatesEnvOverrides);
            Check("Pipeline_WorkDir_UsesParentEffectiveEnv", PipelineWorkDirUsesParentEffectiveEnv);
            Check("Pipeline_RelativeWorkDir_ResolvesAgainstBaseDir", PipelineRelativeWorkDirResolvesAgainstBaseDir);
            Check("Pipeline_MalformedWorkDir_DoesNotThrow", PipelineMalformedWorkDirDoesNotThrow);
            Check("Pipeline_EnvTooLarge_ReturnsEnvError", PipelineEnvTooLargeReturnsEnvError);
            Check("Pipeline_NotFound_UsesR13Message", PipelineNotFoundUsesR13Message);
            Check("Pipeline_BatchWithEmptyArgs_WrapsWithTrailingQuotes", PipelineBatchWithEmptyArgsWrapsWithTrailingQuotes);
            Check("Pipeline_BatchWithSpacesAndQuotedArgs_ExactWrapper", PipelineBatchWithSpacesAndQuotedArgsExactWrapper);
            Check("Pipeline_NullWorkDir_IsTolerated", PipelineNullWorkDirIsTolerated);

            // IPC 契约：spec 编解码与 NDJSON 帧。全部是内存往返，不建临时文件、不启动进程。
            Check("Spec_Base64RoundTrip", SpecBase64RoundTrip);
            Check("Spec_NeedsTempFile_WhenTooLong", SpecNeedsTempFileWhenTooLong);
            Check("Spec_JsonRoundTrip_PreservesChineseAndQuotes", SpecJsonRoundTripPreservesChineseAndQuotes);
            Check("Spec_FromBase64_Malformed_ThrowsChinese", SpecFromBase64MalformedThrowsChinese);
            Check("Ipc_FrameRoundTrip_EscapesNewlineTabQuoteBackslash", IpcFrameRoundTripEscapesNewlineTabQuoteBackslash);
            Check("Ipc_FrameRoundTrip_ChineseAndEmptyString", IpcFrameRoundTripChineseAndEmptyString);
            Check("Ipc_Read_ReturnsNullAtEof", IpcReadReturnsNullAtEof);
            Check("Ipc_Read_MalformedJson_ThrowsChinese", IpcReadMalformedJsonThrowsChinese);

            // 直连启动：真实拉起 cmd /d /s /c 子进程；等待一律有 10s 上限，用例结束即释放管理器（Job 兜底清理）。
            Check("Direct_CapturesStdoutAndStderr", DirectCapturesStdoutAndStderr);
            Check("Direct_ReportsExitCode", DirectReportsExitCode);
            Check("Direct_AppliesEnvOverrides", DirectAppliesEnvOverrides);
            Check("Direct_UsesWorkingDirectory", DirectUsesWorkingDirectory);
            Check("Direct_MissingProgram_SetsErrorAndDoesNotStart", DirectMissingProgramSetsErrorAndDoesNotStart);
            Check("Direct_NonexistentFile_ReturnsFalseWithoutThrowing", DirectNonexistentFileReturnsFalseWithoutThrowing);
            Check("Direct_GetChildPid_IsLiveThenZero", DirectGetChildPidIsLiveThenZero);
            Check("Direct_ElevationRequired_IsDetected", DirectElevationRequiredIsDetected);

            // 边界用例放在 Direct 之后：accept 分支会触发一次性的父环境规范化，
            // 不能抢在 Direct_AppliesEnvOverrides 之前消耗掉它要断言的“已自动规范化”日志。
            Check("ChildEnv_BlockSizeBoundary_32767Accepted_32768Rejected", ChildEnvBlockSizeBoundary);

            // 中转进程：真实拉起与 UI 同目录的 gostGUI.Runner.exe 走 --spec，
            // 验证 ready → started → out → exit 的顺序、流别与退出清理。
            Check("Runner_RelaysStdoutStderrAndExitCode", RunnerRelaysStdoutStderrAndExitCode);

            // UI 侧 RunnerSession：真实走命名管道握手、事件转发与停止。runner exe 缺失时按失败处理（不 SKIP）：
            // 这些用例只在构建输出目录里运行，缺 exe 意味着打包/构建坏了，必须让套件失败。
            Check("Runner_Smoke_RelaysOutputAndCleansUp", RunnerSmokeRelaysOutputAndCleansUp);
            Check("Runner_Smoke_StopKillsChildTree", RunnerSmokeStopKillsChildTree);
            Check("Runner_SpecFileFallback_RelaysOutput", RunnerSpecFileFallbackRelaysOutput);
            Check("Runner_ForceRunnerRouting_RelaysAndStops", RunnerForceRunnerRoutingRelaysAndStops);

            // 终审回归：管道强制标签（降权 runner 必须能读写 High 服务器建的管道）、死 runner 快速失败、
            // 以及决策 13 / §6.4 要求的中转身份与基础环境来源日志。
            Check("Runner_PipeHasMediumIntegrityLabel", RunnerPipeHasMediumIntegrityLabel);
            Check("Runner_DeadRelayFailsFast", RunnerDeadRelayFailsFast);
            Check("Runner_IdentityAndBaseEnvironmentAreReported", RunnerIdentityAndBaseEnvironmentAreReported);

            // 提权：错误码映射、令牌提权判定与权限矩阵选路，全部是纯/本地判断，绝不弹 UAC。
            // 真实的提权启动路径（点“启动”看 UAC）属于人工清单，不在自检里模拟。
            Check("Elev_DescribeWin32Error_IsDistinctAndChinese", ElevDescribeWin32ErrorIsDistinctAndChinese);
            Check("Elev_IsProcessElevated_MatchesToken", ElevIsProcessElevatedMatchesToken);
            Check("Elev_Matrix_DecideMode_CoversFourCells", ElevMatrixDecideModeCoversFourCells);

            // 降权启动（Task 10）：模式必须已接线、长 spec 必须改走 --spec-file、ACL 必须覆盖交互用户，
            // 直连用例的配置在两种完整性下都必须选到直连。
            // 本机是过滤管理员（中等 IL），降权能真的成功，因此“子进程令牌非提权”这条证据不需要 UAC 就能跑；
            // 标准权限下被拒绝时只接受登记在案的 1314/5，其它错误码一律判失败（不拿 193/2 当“环境不允许”）。
            Check("Elev_StandardUserMode_IsWired", ElevStandardUserModeIsWired);
            Check("Elev_StandardUserLaunch_RequiresElevatedCaller", ElevStandardUserLaunchRequiresElevatedCaller);
            Check("Elev_DeElevatedRunner_TokenIsStandardUser", ElevDeElevatedRunnerTokenIsStandardUser);
            Check("Elev_StandardUserLongSpec_UsesSpecFileTransport", ElevStandardUserLongSpecUsesSpecFileTransport);
            Check("Elev_StandardUserAcl_IncludesInteractiveUser", ElevStandardUserAclIncludesInteractiveUser);
            Check("Elev_StandardUserLaunch_DoesNotLeavePrivilegesEnabled",
                ElevStandardUserLaunchDoesNotLeavePrivilegesEnabled);
            Check("Elev_DirectItems_ChooseDirectAtBothIntegrities", ElevDirectItemsChooseDirectAtBothIntegrities);

            // 主题（Task 11）：调色板、模式解析、跟随系统、递归应用与 IThemed 契约。
            // 控件只在 STA 线程上创建，不跑消息循环、不显示窗口。
            Check("Theme_PalettesDiffer", ThemePalettesDiffer);
            Check("Theme_ModeParsing_RoundTrips", ThemeModeParsingRoundTrips);
            Check("Theme_SystemPrefersDark_UsesOverride", ThemeSystemPrefersDarkUsesOverride);
            Check("Theme_RefreshSystemPreference_ChangesOnlyWhenPaletteChanges",
                ThemeRefreshSystemPreferenceChangesOnlyWhenPaletteChanges);
            Check("Theme_ApplyTo_IsIdempotent", ThemeApplyToIsIdempotent);
            Check("Theme_Controls_ImplementIThemed", ThemeControlsImplementIThemed);
            Check("Theme_DarkScrollbars_ApplyIsSafe", ThemeDarkScrollbarsApplyIsSafe);
            Check("Theme_TextMeasure_UsesRequestedDpi", ThemeTextMeasureUsesRequestedDpi);

            // 控件内容/事件契约（Task 11 review）：已有日志颜色不能被主题切换抹平，Text 与 TextChanged 必须转发。
            Check("ThemedRichTextBox_ExistingRunsKeepTheirColours_AfterApplyTheme",
                ThemedRichTextBoxExistingRunsKeepTheirColoursAfterApplyTheme);
            Check("ThemedTextBox_TextChanged_ForwardsToBaseEvent", ThemedTextBoxTextChangedForwardsToBaseEvent);
            Check("ThemedRichTextBox_Text_ForwardsToInner", ThemedRichTextBoxTextForwardsToInner);

            // 主窗口骨架（Task 12a）：配置层“加载失败先备份”与复制/排序，以及不显示窗口的两套主题快照。
            Check("Config_SaveAfterFailedLoad_BacksUpOriginal", ConfigSaveAfterFailedLoadBacksUpOriginal);
            Check("Config_DuplicateItem_NamesAndInsertsAfterSource", ConfigDuplicateItemNamesAndInsertsAfterSource);
            Check("Config_MoveItem_PersistsOrder", ConfigMoveItemPersistsOrder);
            Check("Config_SaveFailureSurfaces", ConfigSaveFailureSurfaces);
            Check("Form_PostToUi_MarshalsBeforeHandleExists", FormPostToUiMarshalsBeforeHandleExists);
            Check("Form_DuplicateKeepsListOrderInSyncWithManager", FormDuplicateKeepsListOrderInSyncWithManager);
            Check("Form_RenameEditorAlignsWithPaintedName", FormRenameEditorAlignsWithPaintedName);

            // 主窗口详情编辑器（Task 12b）：程序解析提示、环境变量编辑框的换行消毒与保存回写。
            Check("Form_ResolvesProgramHint", FormResolvesProgramHint);
            Check("Form_EnvEditorSanitizesNewlines", FormEnvEditorSanitizesNewlines);
            Check("Form_DetailEditorLoadsSelectedItemOnly", FormDetailEditorLoadsSelectedItemOnly);
            Check("Form_ProgramHintIsDebounced", FormProgramHintIsDebounced);
            Check("Form_EnvPathEditRefreshesHint", FormEnvPathEditRefreshesHint);
            Check("Form_CommitsPendingEditsOnExit", FormCommitsPendingEditsOnExit);

            // Task 13：DIP 窗口状态、DPI 布局/热切换与退出保存失败的可见性。
            Check("Form_WindowState_PersistsOnHide", FormWindowStatePersistsOnHide);
            Check("Form_WindowState_RestoresFromConfig", FormWindowStateRestoresFromConfig);
            Check("Form_WindowState_CapturesMinimizedAndMaximized", FormWindowStateCapturesMinimizedAndMaximized);
            Check("Form_LayoutAt120And144Dpi_IsNotClipped", FormLayoutAt120And144DpiIsNotClipped);
            Check("Form_DpiChange_RescalesLayout", FormDpiChangeRescalesLayout);
            Check("Form_ExitSaveFailure_IsReported", FormExitSaveFailureIsReported);

            // Task 14 / R36：启动自动恢复只拉起 enable && !runAsAdmin 的项；管理员项跳过并只记一行日志。
            Check("Startup_SkipsAdminItems", StartupSkipsAdminItems);

            // 主题化批量日志（Task 12b）：三类着色 + 1000 行上限 + 用户上翻时不抢滚动条。
            Check("Log_BatchesAndColors", LogBatchesAndColors);
            Check("Log_DoesNotAutoScrollWhenUserScrolledUp", LogDoesNotAutoScrollWhenUserScrolledUp);
            Check("Log_ClearDropsQueuedLines", LogClearDropsQueuedLines);
            Check("Log_TimerDrainsInBatch", LogTimerDrainsInBatch);
            Check("Log_RetainedRunsRecolourOnThemeChange", LogRetainedRunsRecolourOnThemeChange);

            Check("Form_SnapshotRendersBothThemes", FormSnapshotRendersBothThemes);

            // P/Invoke 收口：INPUT 内存布局与 Alt 击键构造器。只检查布局与返回值，
            // 绝不调用 SendInput —— 这台桌面是无人值守的，注入输入会打扰真实会话。
            Check("Native_InputStructHasExpectedSize", NativeInputStructHasExpectedSize);
            Check("Native_BuildAltKeyInputs_ReturnsAltDownThenAltUp", NativeBuildAltKeyInputsReturnsAltDownThenAltUp);
        }

        // 配置模型用例全部使用绝对临时路径：--selftest 不改变当前目录。
        private static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gostgui-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTempDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception)
            {
                // 临时目录清理失败不影响用例结论。
            }
        }

        /// <summary>删除用例夹具文件；失败只影响清理，不影响用例结论。</summary>
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 同上。
            }
        }

        private static string WriteConfig(string dir, string json)
        {
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }

        private static void LoadOldFormatGetsDefaults()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = WriteConfig(dir,
                    "{\n" +
                    "  \"autoRun\": true,\n" +
                    "  \"items\": [\n" +
                    "    { \"name\": \"old\", \"enable\": true, \"program\": \"gost.exe\", \"args\": \"-L 127.0.0.1:8080\" }\n" +
                    "  ]\n" +
                    "}");

                ConfigLoadResult result = Common.LoadFrom(path);
                Assert(result != null, "LoadFrom 不应返回 null");
                Assert(result.Ok, "旧格式配置应加载成功：" + result.Error);
                Assert(result.Data != null, "加载结果应包含数据");
                AssertEqual(1, result.Data.Items.Count);

                ConfigItem item = result.Data.Items[0];
                AssertEqual("old", item.Name);
                AssertEqual(true, item.Enable);
                AssertEqual("gost.exe", item.Program);
                AssertEqual("-L 127.0.0.1:8080", item.Args);
                AssertEqual("", item.WorkDir);
                Assert(item.Env == null, "旧格式没有 env 时 Env 应为 null（表示不覆盖）");
                AssertEqual(false, item.RunAsAdmin);

                AssertEqual(ThemeMode.System, result.Data.Theme);
                Assert(result.Data.Window != null, "旧格式没有 window 时应使用默认窗口状态");
                AssertEqual(1024, result.Data.Window.W);
                AssertEqual(640, result.Data.Window.H);
                AssertEqual(-1, result.Data.Window.X);
                AssertEqual(-1, result.Data.Window.Y);
                AssertEqual(false, result.Data.Window.Max);
                AssertEqual("", result.Error);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        private static void SaveThenLoadRoundTripsNewFields()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                ConfigData data = new ConfigData
                {
                    AutoRun = true,
                    Theme = ThemeMode.Dark,
                    Window = new ConfigWindow { W = 1280, H = 800, X = 100, Y = 50, Max = true },
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem
                        {
                            Name = "节点一",
                            Enable = true,
                            Program = "gost.exe",
                            Args = "-L 127.0.0.1:8080",
                            WorkDir = "D:\\工作 目录",
                            Env = new Dictionary<string, string>
                            {
                                { "GOST_LOG", "debug" },
                                { "EMPTY", "" },
                                { "中文键", "中文值" }
                            },
                            RunAsAdmin = true
                        }
                    }
                };

                Assert(Common.SaveTo(data, path), "保存应成功：" + Common.LastSaveError);
                Assert(File.Exists(path), "保存后配置文件应存在");
                Assert(!File.Exists(path + ".tmp"), "保存后不应残留临时文件");

                ConfigLoadResult result = Common.LoadFrom(path);
                Assert(result.Ok, "重新加载应成功：" + result.Error);
                ConfigData loaded = result.Data;
                AssertEqual(true, loaded.AutoRun);
                AssertEqual(ThemeMode.Dark, loaded.Theme);
                AssertEqual(1280, loaded.Window.W);
                AssertEqual(800, loaded.Window.H);
                AssertEqual(100, loaded.Window.X);
                AssertEqual(50, loaded.Window.Y);
                AssertEqual(true, loaded.Window.Max);

                AssertEqual(1, loaded.Items.Count);
                ConfigItem item = loaded.Items[0];
                AssertEqual("节点一", item.Name);
                AssertEqual("D:\\工作 目录", item.WorkDir);
                AssertEqual(true, item.RunAsAdmin);
                Assert(item.Env != null, "env 不应丢失");
                AssertEqual("debug", item.Env["GOST_LOG"]);
                AssertEqual("", item.Env["EMPTY"]);
                AssertEqual("中文值", item.Env["中文键"]);
                AssertEqual(3, item.Env.Count);
                AssertEqual("GOST_LOG", new List<string>(item.Env.Keys)[0]);
                Assert(File.ReadAllText(path, Encoding.UTF8).Contains("中文值"), "中文值不应被转义成 \\uXXXX");
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        private static void LoadUnknownFieldsAndWrongTypesDoesNotThrow()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = WriteConfig(dir,
                    "{\n" +
                    "  \"unknownTop\": { \"x\": 1 },\n" +
                    "  \"theme\": \"unknown-theme\",\n" +
                    "  \"items\": [\n" +
                    "    { \"name\": \"a\", \"enable\": true, \"program\": \"gost.exe\", \"args\": \"\", \"unknownItem\": [1, 2, 3] }\n" +
                    "  ]\n" +
                    "}");

                ConfigLoadResult ignored = Common.LoadFrom(path);
                Assert(ignored.Ok, "未知字段应被忽略且加载成功：" + ignored.Error);
                AssertEqual(1, ignored.Data.Items.Count);
                AssertEqual(ThemeMode.System, ignored.Data.Theme);

                // 类型不符：enable 是字符串而不是 bool，必须返回 Ok=false 而不是抛异常。
                path = WriteConfig(dir, "{\"items\":[{\"name\":\"a\",\"enable\":\"yes\"}]}");
                ConfigLoadResult wrongType = Common.LoadFrom(path);
                Assert(!wrongType.Ok, "类型不符时 Ok 应为 false");
                Assert(!string.IsNullOrEmpty(wrongType.Error), "类型不符时应给出中文错误说明");
                Assert(wrongType.Data != null, "加载失败时也应返回可用的默认数据");
                Assert(wrongType.Data.Items != null, "加载失败时 Items 不应为 null");
                AssertEqual(0, wrongType.Data.Items.Count);

                // window 类型不符同样返回失败而不是崩溃。
                path = WriteConfig(dir, "{\"window\": \"big\"}");
                ConfigLoadResult wrongWindow = Common.LoadFrom(path);
                Assert(!wrongWindow.Ok, "window 类型不符时 Ok 应为 false");

                // env 类型不符。
                path = WriteConfig(dir, "{\"items\":[{\"name\":\"a\",\"env\":\"K=V\"}]}");
                ConfigLoadResult wrongEnv = Common.LoadFrom(path);
                Assert(!wrongEnv.Ok, "env 类型不符时 Ok 应为 false");
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        private static void LoadMissingFileReturnsOkWithDefaults()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "no-such-config.json");
                ConfigLoadResult result = Common.LoadFrom(path);
                Assert(result != null, "LoadFrom 不应返回 null");
                Assert(result.Ok, "文件不存在时应按默认值成功：" + result.Error);
                AssertEqual("", result.Error);
                Assert(result.Data != null, "加载结果应包含默认数据");
                Assert(result.Data.Items != null, "Items 不应为 null");
                AssertEqual(0, result.Data.Items.Count);
                AssertEqual(false, result.Data.AutoRun);
                AssertEqual(ThemeMode.System, result.Data.Theme);
                Assert(result.Data.Window != null, "Window 不应为 null");
                AssertEqual(1024, result.Data.Window.W);
                AssertEqual(640, result.Data.Window.H);
                Assert(!File.Exists(path), "加载不存在的文件不应创建它");
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

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

        private static void EnvIsCaseInsensitive()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = WriteConfig(dir,
                    "{\n" +
                    "  \"items\": [\n" +
                    "    { \"name\": \"a\", \"env\": { \"Path\": \"C:\\\\tools\", \"GOST_LOG\": \"debug\" } }\n" +
                    "  ]\n" +
                    "}");

                ConfigLoadResult result = Common.LoadFrom(path);
                Assert(result.Ok, "env 应能加载：" + result.Error);
                Dictionary<string, string> env = result.Data.Items[0].Env;
                Assert(env != null, "env 不应为 null");
                AssertEqual(2, env.Count);
                AssertEqual("C:\\tools", env["PATH"]);
                AssertEqual("debug", env["gost_log"]);
                Assert(env.ContainsKey("path"), "键比较应忽略大小写");
                // 保持 JSON 里的键顺序，供后续 %VAR% 展开按出现顺序处理。
                AssertEqual("Path", new List<string>(env.Keys)[0]);

                // 代码里直接赋值时也应自动变成大小写不敏感。
                ConfigItem item = new ConfigItem();
                item.Env = new Dictionary<string, string> { { "Key", "v" } };
                AssertEqual("v", item.Env["KEY"]);
                AssertEqual("Key", new List<string>(item.Env.Keys)[0]);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        // 环境变量块：解析 / 展开 / 合并 / 32K 上限 / 回写。用例只用内存数据，不碰文件。

        private static void EnvParseLinesSkipsBlanksAndComments()
        {
            EnvParseResult parsed = EnvBlock.ParseLines(
                "A=1\r\n" +
                "\r\n" +
                "   \r\n" +
                "# 整行注释\r\n" +
                "  # 缩进注释\r\n" +
                "B=2");

            Assert(parsed != null, "ParseLines 不应返回 null");
            Assert(parsed.Overrides != null, "Overrides 不应为 null");
            Assert(parsed.Warnings != null, "Warnings 不应为 null");
            AssertEqual(2, parsed.Overrides.Count);
            AssertEqual("1", parsed.Overrides["A"]);
            AssertEqual("2", parsed.Overrides["B"]);
            AssertEqual(0, parsed.Warnings.Count);

            // 缺 '=' 的行与空键名的行只产生中文警告，绝不能抛异常。
            EnvParseResult broken = EnvBlock.ParseLines("NOEQUALS\r\n= 空键\r\nC=3");
            AssertEqual(1, broken.Overrides.Count);
            AssertEqual("3", broken.Overrides["C"]);
            AssertEqual(2, broken.Warnings.Count);
            Assert(broken.Warnings[0].Contains("第 1 行"), "缺少 '=' 的警告应带行号：" + broken.Warnings[0]);
            Assert(broken.Warnings[1].Contains("第 2 行"), "空键名的警告应带行号：" + broken.Warnings[1]);

            // null / 空白文本 → 空表、无警告，且不抛异常。
            foreach (string text in new[] { null, "", "   \r\n\t" })
            {
                EnvParseResult empty = EnvBlock.ParseLines(text);
                Assert(empty.Overrides != null && empty.Overrides.Count == 0, "空文本应返回空覆盖表");
                Assert(empty.Warnings != null && empty.Warnings.Count == 0, "空文本不应产生警告");
            }
        }

        private static void EnvParseLinesSplitsOnFirstEquals()
        {
            EnvParseResult parsed = EnvBlock.ParseLines(
                "A=b=c\r\n" +
                "  KEY  =  value with spaces  \r\n" +
                "EMPTY=\r\n" +
                "EQ==x\r\n" +
                "DUP=first\r\n" +
                "DUP=second\r\n" +
                "dup=third");

            AssertEqual(5, parsed.Overrides.Count);
            AssertEqual("b=c", parsed.Overrides["A"]);
            AssertEqual("value with spaces", parsed.Overrides["KEY"]);
            AssertEqual("", parsed.Overrides["EMPTY"]);
            AssertEqual("=x", parsed.Overrides["EQ"]);
            // 重复键（大小写不敏感）只保留最后一个值。
            AssertEqual("third", parsed.Overrides["DUP"]);
            AssertEqual(0, parsed.Warnings.Count);
        }

        private static void EnvExpandItemOrderThenParent()
        {
            var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ROOT", @"C:\root" }
            };
            var overrides = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("BASE", @"C:\tools"),
                new KeyValuePair<string, string>("PATH", @"%BASE%;%ROOT%;%UNKNOWN%"),
                new KeyValuePair<string, string>("COPY", "%PATH%")
            };

            string error;
            Dictionary<string, string> effective = EnvBlock.BuildEffective(parent, overrides, out error);
            AssertEqual("", error);
            AssertEqual(@"C:\tools;C:\root;%UNKNOWN%", effective["PATH"]);
            // 单次展开：后出现的键看到的是前一项已展开的值，%UNKNOWN% 不会二次展开。
            AssertEqual(@"C:\tools;C:\root;%UNKNOWN%", effective["COPY"]);
            AssertEqual(@"C:\root", effective["ROOT"]);

            IDictionary<string, string> lookup = effective;
            AssertEqual(@"C:\root", EnvBlock.ExpandPercent("%ROOT%", lookup));
            AssertEqual("%NOPE%", EnvBlock.ExpandPercent("%NOPE%", lookup));
            AssertEqual("%%", EnvBlock.ExpandPercent("%%", lookup));
            AssertEqual("100%", EnvBlock.ExpandPercent("100%", lookup));
            AssertEqual("tail%ROOT", EnvBlock.ExpandPercent("tail%ROOT", lookup));
            AssertEqual("", EnvBlock.ExpandPercent(null, lookup));
        }

        private static void EnvBuildEffectiveOverridesCaseInsensitive()
        {
            var parent = new Dictionary<string, string>
            {
                { "Path", @"C:\windows" },
                { "KEEP", "keep" }
            };
            var overrides = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("PATH", @"%Path%;C:\tools")
            };

            string error;
            Dictionary<string, string> effective = EnvBlock.BuildEffective(parent, overrides, out error);
            AssertEqual("", error);
            AssertEqual(2, effective.Count);
            // 展开时按大小写不敏感命中父环境；最终键名以覆盖表的写法为准，且只出现一次。
            AssertEqual(@"C:\windows;C:\tools", effective["PATH"]);
            List<string> keys = new List<string>(effective.Keys);
            Assert(keys.Contains("PATH"), "父环境键应被覆盖表键名替换");
            Assert(!keys.Contains("Path"), "同一键不应同时保留两种大小写");
            AssertEqual("keep", effective["keep"]);

            // 覆盖表内部只有大小写差异的重复键也只留最后一个。
            var duplicates = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Foo", "1"),
                new KeyValuePair<string, string>("foo", "2")
            };
            Dictionary<string, string> deduped = EnvBlock.BuildEffective(
                new Dictionary<string, string>(), duplicates, out error);
            AssertEqual("", error);
            AssertEqual(1, deduped.Count);
            AssertEqual("foo", new List<string>(deduped.Keys)[0]);
            AssertEqual("2", deduped["FOO"]);
        }

        private static void EnvBuildEffectiveTooLargeReturnsError()
        {
            string error;

            // key(1) + '='(1) + value + '\0'(1) = value.Length + 3；32764 正好等于上限 32767。
            var atLimit = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("K", new string('x', 32764))
            };
            Dictionary<string, string> ok = EnvBlock.BuildEffective(
                new Dictionary<string, string>(), atLimit, out error);
            AssertEqual("", error);
            AssertEqual(32764, ok["K"].Length);

            var tooLarge = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("K", new string('x', 32765))
            };
            Dictionary<string, string> failed = EnvBlock.BuildEffective(
                new Dictionary<string, string>(), tooLarge, out error);
            Assert(!string.IsNullOrEmpty(error), "超过 32K 上限时必须返回中文错误");
            Assert(error.Contains("32767"), "错误信息应包含上限值：" + error);
            Assert(failed != null, "失败时仍返回字典，但调用方只认 error");

            var big = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("BIG", new string('y', 40 * 1024))
            };
            EnvBlock.BuildEffective(new Dictionary<string, string>(), big, out error);
            Assert(!string.IsNullOrEmpty(error), "40KB 的环境块必须失败");
        }

        private static void EnvFormatLinesRoundTrips()
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "B", "2" },
                { "A", "1" },
                { "EMPTY", "" },
                { "中文键", "中文值" }
            };

            string text = EnvBlock.FormatLines(overrides);
            AssertEqual("B=2\r\nA=1\r\nEMPTY=\r\n中文键=中文值", text);
            Assert(!text.EndsWith("\r\n"), "最后一行不应带多余换行");
            AssertEqual("", EnvBlock.FormatLines(null));
            AssertEqual("", EnvBlock.FormatLines(new List<KeyValuePair<string, string>>()));

            EnvParseResult parsed = EnvBlock.ParseLines(text);
            AssertEqual(0, parsed.Warnings.Count);
            AssertEqual(4, parsed.Overrides.Count);
            AssertEqual("", parsed.Overrides["EMPTY"]);
            AssertEqual("中文值", parsed.Overrides["中文键"]);
            AssertEqual(text, EnvBlock.FormatLines(parsed.Overrides));
        }

        // 子进程环境块应用：R12 的 32767+2 守卫与“无覆盖即完全不碰 psi”的纯内存用例，不启动进程。

        /// <summary>
        /// 当前父环境按 Windows 口径的块大小：每项 key + '=' + value + '\0'，再加结尾两个 '\0'。
        /// 与 ChildEnvironment 的守卫口径一致（大小写不敏感、同名只留最后一个值）。
        /// 用例真正钉住的是错误文本与“接受/拒绝”边界；这里的公式只用于把夹具推到边界。
        /// </summary>
        private static long ParentEnvironmentBlockSize()
        {
            var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = Convert.ToString(entry.Key);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                effective[key] = Convert.ToString(entry.Value) ?? "";
            }

            long total = 2;
            foreach (KeyValuePair<string, string> pair in effective)
            {
                total += pair.Key.Length + 1 + pair.Value.Length + 1;
            }

            return total;
        }

        /// <summary>
        /// 直接读取 psi.EnvironmentVariables 在 net48 里的私有懒播种字段，不触发属性 getter：
        /// 字段为 null 表示 Apply 从未访问/写过它，这是“无覆盖项即不碰 psi”的最强证据。
        /// 字段名在 net48 固定；取不到时返回 false，由调用用例写 SKIP 记录，而不是静默放过断言。
        /// </summary>
        private static bool TryGetStartInfoEnvironment(ProcessStartInfo psi,
            out System.Collections.Specialized.StringDictionary environment)
        {
            environment = null;
            var field = typeof(ProcessStartInfo).GetField("environmentVariables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null)
            {
                return false;
            }

            environment = field.GetValue(psi) as System.Collections.Specialized.StringDictionary;
            return true;
        }

        private static void ChildEnvTooLargeBlockReturnsChineseError()
        {
            // 父环境 + 覆盖项都按“key + '=' + value + '\0'”累计，再加结尾两个 '\0'；把夹具推到 32777。
            const int overflowBytes = 10;
            string key = "GOSTGUI_SELFTEST_FAT_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            long parentSize = ParentEnvironmentBlockSize();
            int valueLength = (int)(32767 + overflowBytes - parentSize - (key.Length + 2));
            Assert(valueLength > 0, "父环境过大，构造不出超限夹具（父环境块 " + parentSize + " 字节）");

            var psi = new ProcessStartInfo("cmd.exe");
            var logs = new List<string>();
            string error;
            ChildEnvironment.Apply(
                psi,
                new[] { new KeyValuePair<string, string>(key, new string('x', valueLength)) },
                logs.Add,
                out error);

            Assert(!string.IsNullOrEmpty(error), "环境块超过 32767 时必须返回错误");
            Assert(ContainsChinese(error), "超限错误应为中文：" + error);
            Assert(error.Contains("32767"), "超限错误应点名 Windows 上限：" + error);
            // 守卫在规范化/写入之前：既不应产生 System 日志，也不应碰 psi 与本进程环境。
            AssertEqual(0, logs.Count);
            System.Collections.Specialized.StringDictionary psiEnvironment;
            if (TryGetStartInfoEnvironment(psi, out psiEnvironment))
            {
                Assert(psiEnvironment == null, "超限时不应触碰 psi.EnvironmentVariables");
            }
            else
            {
                Notice("SKIP ChildEnv_TooLargeBlock_ReturnsChineseError 的 psi 未播种断言：找不到 environmentVariables 私有字段");
            }

            Assert(Environment.GetEnvironmentVariable(key) == null, "超限时不应把覆盖项写进本进程环境：" + key);
        }

        private static void ChildEnvNoOverridesIsNoOp()
        {
            var psi = new ProcessStartInfo("cmd.exe");
            string key = "GOSTGUI_SELFTEST_NOOP_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var logs = new List<string>();

            string error;
            ChildEnvironment.Apply(psi, null, logs.Add, out error);
            AssertEqual("", error);
            ChildEnvironment.Apply(psi, new List<KeyValuePair<string, string>>(), logs.Add, out error);
            AssertEqual("", error);

            AssertEqual(0, logs.Count);
            System.Collections.Specialized.StringDictionary psiEnvironment;
            if (TryGetStartInfoEnvironment(psi, out psiEnvironment))
            {
                Assert(psiEnvironment == null, "无覆盖项时不应访问 psi.EnvironmentVariables");
            }
            else
            {
                Notice("SKIP ChildEnv_NoOverrides_IsNoOp 的 psi 未播种断言：找不到 environmentVariables 私有字段");
            }

            Assert(Environment.GetEnvironmentVariable(key) == null, "无覆盖项时不应写本进程环境：" + key);
        }

        // 程序解析用例的夹具：临时目录 + 指定的空文件（相对路径可以带子目录），只建不跑。

        private static string CreateTempDirectoryWithFiles(params string[] relativePaths)
        {
            string dir = CreateTempDirectory();
            foreach (string relativePath in relativePaths)
            {
                string fullPath = Path.Combine(dir, relativePath);
                string parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.WriteAllBytes(fullPath, new byte[0]);
            }

            return dir;
        }

        /// <summary>把若干片段拼成绝对路径，供断言里的 FullPath 期望值使用。</summary>
        private static string AbsolutePathOf(string dir, params string[] parts)
        {
            string combined = dir;
            foreach (string part in parts)
            {
                combined = Path.Combine(combined, part);
            }

            return Path.GetFullPath(combined);
        }

        /// <summary>
        /// Windows 路径大小写不敏感：按 PATHEXT 补全时可能带出 .EXE / .BAT 这种大写写法，
        /// 比较 FullPath 时忽略大小写（指向的确实是同一个文件）。
        /// </summary>
        private static void AssertSamePath(string expected, string actual)
        {
            Assert(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
                $"断言失败：期望路径 <{expected}>，实际 <{actual}>");
        }

        private static void ResolveBareNameFoundInPathDir()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("fake.exe");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir }
                };

                ResolvedProgram result = ProgramResolver.Resolve("fake.exe", appDir, env);

                Assert(result != null, "Resolve 不应返回 null");
                Assert(result.Found, "PATH 目录里的 fake.exe 应命中：" + result.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "fake.exe"), result.FullPath);
                AssertEqual(ChildKind.Executable, result.Kind);
                AssertEqual("", result.Error);
                // 应用目录 + PATH 里的 1 个目录。
                AssertEqual(2, result.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolveBareNameAppDirWins()
        {
            string appDir = CreateTempDirectoryWithFiles("fake.exe");
            string toolsDir = CreateTempDirectoryWithFiles("fake.exe");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir }
                };

                ResolvedProgram result = ProgramResolver.Resolve("fake.exe", appDir, env);

                Assert(result.Found, "应用目录里的 fake.exe 应命中：" + result.Error);
                // 同名文件同时存在时应用目录优先，且只搜到第一个目录就停。
                AssertSamePath(AbsolutePathOf(appDir, "fake.exe"), result.FullPath);
                AssertEqual(1, result.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolveAppliesPathext()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("tool.exe");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir }
                };

                ResolvedProgram result = ProgramResolver.Resolve("tool", appDir, env);

                Assert(result.Found, "裸名 tool 应按 PATHEXT 补全成 tool.exe：" + result.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.exe"), result.FullPath);
                AssertEqual(ChildKind.Executable, result.Kind);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolveUsesEffectiveEnvironment()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string emptyDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("onlyineffective.exe");
            try
            {
                // 还没追加临时目录时的有效环境：PATH 里只有空目录，找不到。
                var parentLike = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", emptyDir }
                };
                ResolvedProgram withoutOverride = ProgramResolver.Resolve("onlyineffective", appDir, parentLike);
                Assert(!withoutOverride.Found, "这一轮不应找到 onlyineffective");

                // 本项 env 追加 PATH 后（模拟 PATH=%PATH%;临时目录），有效环境里才有它；不改进程环境。
                var withOverride = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", emptyDir + ";" + toolsDir }
                };
                ResolvedProgram withEnv = ProgramResolver.Resolve("onlyineffective", appDir, withOverride);

                Assert(withEnv.Found, "PATH 来自有效环境，追加后应命中：" + withEnv.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "onlyineffective.exe"), withEnv.FullPath);
                // 应用目录 + 空目录 + 临时工具目录。
                AssertEqual(3, withEnv.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(emptyDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolvePathWithSpacesAndQuotes()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("a b", "x.exe"));
            try
            {
                string program = "\"" + AbsolutePathOf(appDir, "a b", "x.exe") + "\"";
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // 首尾空白 + 成对引号都必须被剥掉。
                ResolvedProgram result = ProgramResolver.Resolve("  " + program + "  ", appDir, env);

                Assert(result.Found, "带空格并加引号的完整路径应命中：" + result.Error);
                AssertSamePath(AbsolutePathOf(appDir, "a b", "x.exe"), result.FullPath);
                AssertEqual(ChildKind.Executable, result.Kind);
                // 显式路径不搜 PATH，目录数应为 0。
                AssertEqual(0, result.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveRelativeToBaseDir()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("sub", "rel.exe"));
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ResolvedProgram result = ProgramResolver.Resolve(Path.Combine("sub", "rel.exe"), appDir, env);
                Assert(result.Found, "相对路径应按 baseDir 解析：" + result.Error);
                AssertSamePath(AbsolutePathOf(appDir, "sub", "rel.exe"), result.FullPath);

                // 相对路径没有扩展名时同样按 PATHEXT 补全。
                ResolvedProgram completed = ProgramResolver.Resolve(Path.Combine("sub", "rel"), appDir, env);
                Assert(completed.Found, "相对路径应按 PATHEXT 补全：" + completed.Error);
                AssertSamePath(AbsolutePathOf(appDir, "sub", "rel.exe"), completed.FullPath);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveBatchFileKindIsBatch()
        {
            string appDir = CreateTempDirectoryWithFiles("run.cmd", "run.bat");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ResolvedProgram cmd = ProgramResolver.Resolve("run.cmd", appDir, env);
                Assert(cmd.Found, "run.cmd 应命中：" + cmd.Error);
                AssertEqual(ChildKind.Batch, cmd.Kind);

                ResolvedProgram bat = ProgramResolver.Resolve("run.bat", appDir, env);
                Assert(bat.Found, "run.bat 应命中：" + bat.Error);
                AssertEqual(ChildKind.Batch, bat.Kind);

                // 裸名 run 按默认 PATHEXT 顺序（.COM;.EXE;.BAT;.CMD）先命中 .bat。
                ResolvedProgram bare = ProgramResolver.Resolve("run", appDir, env);
                Assert(bare.Found, "裸名 run 应按 PATHEXT 命中：" + bare.Error);
                AssertSamePath(AbsolutePathOf(appDir, "run.bat"), bare.FullPath);
                AssertEqual(ChildKind.Batch, bare.Kind);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveMissingReportsSearchedCount()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string dir1 = CreateTempDirectoryWithFiles();
            string dir2 = CreateTempDirectoryWithFiles();
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", dir1 + ";" + dir2 }
                };

                ResolvedProgram result = ProgramResolver.Resolve("nope", appDir, env);

                Assert(!result.Found, "不存在的程序不应命中");
                // 应用目录 + PATH 的 2 个目录。
                AssertEqual(3, result.SearchedDirectories);
                Assert(!string.IsNullOrEmpty(result.Error), "找不到时应给出中文错误");
                Assert(result.Error.Contains("未找到"), "错误应说明找不到：" + result.Error);
                Assert(result.Error.Contains("3"), "错误应带上搜索目录数：" + result.Error);
                Assert(string.IsNullOrEmpty(result.FullPath), "找不到时 FullPath 应为空");
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(dir1);
                DeleteTempDirectory(dir2);
            }
        }

        private static void ResolvePathLookupIsCaseInsensitive()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("tool.exe");
            try
            {
                // 默认比较器是区分大小写的，键名故意写成 pAtH，解析器仍须大小写不敏感地取到 PATH。
                var env = new Dictionary<string, string>
                {
                    { "pAtH", toolsDir }
                };

                ResolvedProgram result = ProgramResolver.Resolve("tool.exe", appDir, env);

                Assert(result.Found, "PATH 查找应忽略大小写：" + result.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.exe"), result.FullPath);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolvePathextFallsBackToDefault()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("tool.cmd");
            try
            {
                // 完全没有 PATHEXT 时必须用默认的 .COM;.EXE;.BAT;.CMD。
                var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir }
                };
                ResolvedProgram absent = ProgramResolver.Resolve("tool", appDir, missing);
                Assert(absent.Found, "PATHEXT 缺失时应回落到默认值：" + absent.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.cmd"), absent.FullPath);
                AssertEqual(ChildKind.Batch, absent.Kind);

                // PATHEXT 为空字符串时同样回落。
                var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir },
                    { "PATHEXT", "" }
                };
                ResolvedProgram blank = ProgramResolver.Resolve("tool", appDir, empty);
                Assert(blank.Found, "PATHEXT 为空时应回落到默认值：" + blank.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.cmd"), blank.FullPath);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolvePathEntryQuotesAndVariables()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string root = CreateTempDirectoryWithFiles(Path.Combine("tools dir", "tool.exe"));
            try
            {
                string toolsDir = Path.Combine(root, "tools dir");
                // PATH 项带引号且含 %VAR%，两件事都要在搜索前处理掉。
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TOOLS", toolsDir },
                    { "PATH", "\"%TOOLS%\"" }
                };

                ResolvedProgram result = ProgramResolver.Resolve("tool", appDir, env);

                Assert(result.Found, "带引号且含 %VAR% 的 PATH 项应命中：" + result.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.exe"), result.FullPath);
                AssertEqual(2, result.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(root);
            }
        }

        private static void ResolvePs1IsRejected()
        {
            string appDir = CreateTempDirectoryWithFiles("script.ps1");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ResolvedProgram result = ProgramResolver.Resolve(AbsolutePathOf(appDir, "script.ps1"), appDir, env);

                Assert(!result.Found, ".ps1 不应被判为可启动程序");
                Assert(!string.IsNullOrEmpty(result.Error), "不支持的类型应给出中文错误");
                Assert(result.Error.Contains(".ps1"), "错误应点名 .ps1：" + result.Error);
                Assert(result.Error.Contains("powershell -File"), "错误应提示改用 powershell -File：" + result.Error);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveMalformedPathDoesNotThrow()
        {
            string appDir = CreateTempDirectoryWithFiles();
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // 内嵌 NUL 的路径无法规范化：必须返回失败而不是抛异常。
                string malformed = appDir + Path.DirectorySeparatorChar + "bad\0name" + Path.DirectorySeparatorChar + "x.exe";
                ResolvedProgram result = ProgramResolver.Resolve(malformed, appDir, env);

                Assert(result != null, "Resolve 不应返回 null");
                Assert(!result.Found, "非法路径不应命中");
                Assert(!string.IsNullOrEmpty(result.Error), "非法路径应给出中文错误");
                Assert(result.Error.Contains("路径无效"), "错误应说明路径无效：" + result.Error);
                Assert(string.IsNullOrEmpty(result.FullPath), "非法路径不应返回 FullPath");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveEmptyProgramReturnsError()
        {
            string appDir = CreateTempDirectoryWithFiles();
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (string program in new[] { null, "", "   ", "\"\"", "  \"  \"  " })
                {
                    ResolvedProgram result = ProgramResolver.Resolve(program, appDir, env);
                    Assert(result != null, "Resolve 不应返回 null");
                    Assert(!result.Found, "空程序不应命中");
                    Assert(result.Error.Contains("未设置程序"), "空程序应提示未设置程序：" + result.Error);
                    AssertEqual(0, result.SearchedDirectories);
                }
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        // 启动管线用例的夹具：配置项 + 空父环境；全部使用绝对路径，临时目录由用例自己清理。

        private static Dictionary<string, string> EmptyEnv()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static ConfigItem NewLaunchItem(string program, string args = "", string workDir = "",
            Dictionary<string, string> env = null)
        {
            var item = new ConfigItem
            {
                Program = program,
                Args = args,
                WorkDir = workDir
            };
            if (env != null)
            {
                item.Env = env;
            }

            return item;
        }

        private static void PipelineEmptyProgramReturnsError()
        {
            string appDir = CreateTempDirectoryWithFiles();
            try
            {
                foreach (string program in new[] { null, "", "   " })
                {
                    LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(program), appDir, EmptyEnv());

                    Assert(request != null, "Build 不应返回 null");
                    Assert(!string.IsNullOrEmpty(request.Error), "空程序应填入中文错误");
                    Assert(request.Error.Contains("未设置程序"), "空程序错误应提示未设置程序：" + request.Error);
                    Assert(string.IsNullOrEmpty(request.FileName), "空程序不应给出启动文件名");
                    AssertEqual(ChildKind.Executable, request.Kind);
                    Assert(!request.IsBatch, "空程序不应按批处理包装");
                    Assert(request.EnvOverrides != null, "失败时 EnvOverrides 也不应为 null");
                }
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineResolvesAndExpandsWorkDir()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            string runDir = CreateTempDirectory();
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "MYDIR", runDir }
                };
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "-x 1", "%MYDIR%", env);

                LaunchRequest request = LaunchPipeline.Build(item, appDir, EmptyEnv());

                AssertEqual("", request.Error);
                AssertSamePath(AbsolutePathOf(appDir, "app.exe"), request.FileName);
                AssertEqual("-x 1", request.Arguments);
                // 覆盖表里给值的 %MYDIR% 必须展开成覆盖表里的目录。
                AssertSamePath(runDir, request.WorkingDirectory);
                AssertEqual(ChildKind.Executable, request.Kind);
                Assert(!request.IsBatch, "exe 不应按批处理包装");
                Assert(request.EnvOverrides != null, "EnvOverrides 不应为 null");
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(runDir);
            }
        }

        private static void PipelineDefaultWorkDirIsBaseDir()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                LaunchRequest request = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe")), appDir, EmptyEnv());

                AssertEqual("", request.Error);
                AssertSamePath(Path.GetFullPath(appDir), request.WorkingDirectory);

                // WorkDir 为 null 与空字符串等价，同样回落到 baseDir。
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"));
                item.WorkDir = null;
                LaunchRequest nullDir = LaunchPipeline.Build(item, appDir, EmptyEnv());

                AssertEqual("", nullDir.Error);
                AssertSamePath(Path.GetFullPath(appDir), nullDir.WorkingDirectory);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineMissingWorkDirReturnsError()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                string missing = Path.Combine(appDir, "no-such-dir");
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", missing);

                LaunchRequest request = LaunchPipeline.Build(item, appDir, EmptyEnv());

                Assert(!string.IsNullOrEmpty(request.Error), "工作目录不存在时必须填入错误");
                AssertEqual("工作目录不存在：" + Path.GetFullPath(missing), request.Error);
                Assert(string.IsNullOrEmpty(request.WorkingDirectory), "失败时 WorkingDirectory 应为空字符串");
                Assert(string.IsNullOrEmpty(request.FileName), "工作目录失败时不应给出启动文件名");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineBatchWrapsWithCmd()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("a b", "x.bat"));
            try
            {
                string bat = AbsolutePathOf(appDir, "a b", "x.bat");
                string expectedComSpec = Environment.GetEnvironmentVariable("COMSPEC");
                if (string.IsNullOrEmpty(expectedComSpec))
                {
                    expectedComSpec = "cmd.exe";
                }

                // COMSPEC 现在从父环境快照（有效环境）取，不再读活环境：快照里显式给出才能断言具体路径。
                var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "COMSPEC", expectedComSpec }
                };
                LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(bat, "-flag"), appDir, parent);

                AssertEqual("", request.Error);
                AssertSamePath(expectedComSpec, request.FileName);
                // 标准 cmd /s /c 形式：整条命令再包一层引号，bat 路径单独加引号。
                AssertEqual("/d /s /c \"\"" + bat + "\" -flag\"", request.Arguments);
                AssertEqual(ChildKind.Batch, request.Kind);
                Assert(request.IsBatch, "Batch 类型的 IsBatch 应为 true");
                AssertSamePath(Path.GetFullPath(appDir), request.WorkingDirectory);

                // 快照里没有 COMSPEC 时回落到裸名 cmd.exe（不再偷看活环境）。
                LaunchRequest withoutComSpec = LaunchPipeline.Build(NewLaunchItem(bat, "-flag"), appDir, EmptyEnv());
                AssertEqual("", withoutComSpec.Error);
                AssertEqual("cmd.exe", withoutComSpec.FileName);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineKeepsArgsVerbatim()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("dir with space", "app.exe"));
            try
            {
                string exe = AbsolutePathOf(appDir, "dir with space", "app.exe");
                const string args = "-L \"127.0.0.1:8080\" --header='a b' \"\"";
                LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(exe, args), appDir, EmptyEnv());

                AssertEqual("", request.Error);
                AssertSamePath(exe, request.FileName);
                // 参数原样透传：不加引号、不剥引号、不 Trim。
                AssertEqual(args, request.Arguments);
                AssertEqual(ChildKind.Executable, request.Kind);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelinePropagatesEnvOverrides()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ROOT", @"C:\root" },
                    { "PATH", @"C:\windows" }
                };
                var overrides = new Dictionary<string, string>
                {
                    { "BASE", @"C:\tools" },
                    { "PATH", @"%BASE%;%ROOT%" },
                    { "COPY", "%PATH%" }
                };
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", "", overrides);

                LaunchRequest request = LaunchPipeline.Build(item, appDir, parent);

                AssertEqual("", request.Error);
                Assert(request.EnvOverrides != null, "EnvOverrides 不应为 null");
                // 只带本项覆盖项，父环境不塞进来（启动方自己合并）。
                AssertEqual(3, request.EnvOverrides.Count);
                AssertEqual(@"C:\tools;C:\root", request.EnvOverrides["PATH"]);
                AssertEqual(@"C:\tools;C:\root", request.EnvOverrides["COPY"]);
                AssertEqual(@"C:\tools", request.EnvOverrides["BASE"]);
                Assert(!request.EnvOverrides.ContainsKey("ROOT"), "父环境变量不应出现在 EnvOverrides 里");
                Assert(request.EnvOverrides.ContainsKey("path"), "EnvOverrides 的键应大小写不敏感");

                // 没有任何覆盖项时是空表而不是 null。
                LaunchRequest plain = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe")), appDir, EmptyEnv());
                Assert(plain.EnvOverrides != null, "无覆盖项时 EnvOverrides 不应为 null");
                AssertEqual(0, plain.EnvOverrides.Count);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineWorkDirUsesParentEffectiveEnv()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            string runDir = CreateTempDirectory();
            try
            {
                // 变量来自父环境：有效环境包含父环境 + 覆盖项，所以 workDir 里的 %PARENTDIR% 同样要展开。
                var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PARENTDIR", runDir }
                };
                LaunchRequest request = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", "%PARENTDIR%"), appDir, parent);

                AssertEqual("", request.Error);
                AssertSamePath(runDir, request.WorkingDirectory);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(runDir);
            }
        }

        private static void PipelineRelativeWorkDirResolvesAgainstBaseDir()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                Directory.CreateDirectory(Path.Combine(appDir, "sub run"));
                LaunchRequest request = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", Path.Combine("sub run", "nested")),
                    appDir, EmptyEnv());

                // nested 不存在：错误里应出现按 baseDir 解析后的绝对路径，而不是当前目录。
                Assert(!string.IsNullOrEmpty(request.Error), "相对路径不存在时应填入错误");
                AssertEqual("工作目录不存在：" + AbsolutePathOf(appDir, "sub run", "nested"), request.Error);

                LaunchRequest existing = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", "sub run"), appDir, EmptyEnv());
                AssertEqual("", existing.Error);
                AssertSamePath(AbsolutePathOf(appDir, "sub run"), existing.WorkingDirectory);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineMalformedWorkDirDoesNotThrow()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                string malformed = appDir + Path.DirectorySeparatorChar + "bad\0dir";
                LaunchRequest request = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", malformed), appDir, EmptyEnv());

                Assert(request != null, "Build 不应返回 null");
                Assert(!string.IsNullOrEmpty(request.Error), "畸形工作目录应填入中文错误");
                Assert(request.Error.Contains("工作目录"), "错误应说明工作目录无效：" + request.Error);
                Assert(string.IsNullOrEmpty(request.WorkingDirectory), "失败时 WorkingDirectory 应为空字符串");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineEnvTooLargeReturnsEnvError()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                // 覆盖项本身就超过 32K：错误必须原样从 EnvBlock 传到 LaunchRequest。
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "-x");
                item.Env = new Dictionary<string, string>
                {
                    { "BIG", new string('x', 32765) }
                };

                string envError;
                EnvBlock.BuildEffective(EmptyEnv(), item.Env, out envError);
                Assert(!string.IsNullOrEmpty(envError), "夹具应超过 32K 上限");

                LaunchRequest request = LaunchPipeline.Build(item, appDir, EmptyEnv());

                AssertEqual(envError, request.Error);
                // 顺序决定：环境先失败时不再解析程序，也不该给出启动文件名。
                Assert(string.IsNullOrEmpty(request.FileName), "环境块超限时不应解析程序");

                // 父环境已经超限时同样必须失败（不能只在覆盖项上校验）。
                var bigParent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "BIG", new string('x', 32765) }
                };
                string parentError;
                EnvBlock.BuildEffective(bigParent, EmptyEnv(), out parentError);
                Assert(!string.IsNullOrEmpty(parentError), "父环境夹具应超过 32K 上限");

                LaunchRequest fromParent = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe")), appDir, bigParent);
                AssertEqual(parentError, fromParent.Error);
                Assert(string.IsNullOrEmpty(fromParent.FileName), "父环境超限时不应解析程序");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineNotFoundUsesR13Message()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string emptyDir = CreateTempDirectoryWithFiles();
            try
            {
                var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", emptyDir }
                };

                LaunchRequest bare = LaunchPipeline.Build(
                    NewLaunchItem("definitely-not-a-real-tool-xyz"), appDir, parent);

                Assert(!string.IsNullOrEmpty(bare.Error), "裸名找不到时应填入错误");
                AssertEqual("在应用目录与有效 PATH 中均未找到 definitely-not-a-real-tool-xyz（共搜索 2 个目录）",
                    bare.Error);
                Assert(string.IsNullOrEmpty(bare.FileName), "找不到时不应给出启动文件名");

                // 显式路径未命中：沿用解析器原文（已经点名了路径），不能改写成 PATH 措辞。
                string explicitPath = Path.Combine(appDir, "missing", "nope.exe");
                LaunchRequest explicitRequest = LaunchPipeline.Build(
                    NewLaunchItem(explicitPath), appDir, parent);

                Assert(!string.IsNullOrEmpty(explicitRequest.Error), "显式路径不存在时应填入错误");
                Assert(explicitRequest.Error.Contains("未找到程序："),
                    "显式路径错误应沿用解析器原文：" + explicitRequest.Error);
                Assert(explicitRequest.Error.Contains(explicitPath),
                    "解析器错误应点名完整路径：" + explicitRequest.Error);
                Assert(!explicitRequest.Error.StartsWith("在应用目录与有效 PATH 中均未找到"),
                    "显式路径不应套用 PATH 措辞：" + explicitRequest.Error);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(emptyDir);
            }
        }

        private static void PipelineBatchWithEmptyArgsWrapsWithTrailingQuotes()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("a b", "x.bat"));
            try
            {
                string bat = AbsolutePathOf(appDir, "a b", "x.bat");

                LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(bat, ""), appDir, EmptyEnv());
                AssertEqual("", request.Error);
                // 空参数时结尾只剩两层引号：/d /s /c ""<path>""。
                AssertEqual("/d /s /c \"\"" + bat + "\"\"", request.Arguments);
                Assert(request.IsBatch, "空参数的 bat 仍应按批处理包装");

                // Args 为 null 与空字符串等价。
                LaunchRequest nullArgs = LaunchPipeline.Build(NewLaunchItem(bat, null), appDir, EmptyEnv());
                AssertEqual("", nullArgs.Error);
                AssertEqual("/d /s /c \"\"" + bat + "\"\"", nullArgs.Arguments);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        // R14：批处理路径含空格、参数含引号时的包装串必须逐字符正确；WorkDir = null 必须被容忍。

        private static void PipelineBatchWithSpacesAndQuotedArgsExactWrapper()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("a b", "x.bat"));
            try
            {
                string bat = AbsolutePathOf(appDir, "a b", "x.bat");
                LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(bat, "-x \"a b\""), appDir, EmptyEnv());

                AssertEqual("", request.Error);
                // 含空格路径 + 含引号参数：整条命令再包一层引号，用户参数原样拼接。
                AssertEqual("/d /s /c \"\"" + bat + "\" -x \"a b\"\"", request.Arguments);
                Assert(request.IsBatch, "含空格的 .bat 仍应按批处理包装");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineNullWorkDirIsTolerated()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"));
                item.WorkDir = null;

                LaunchRequest request = LaunchPipeline.Build(item, appDir, EmptyEnv());

                AssertEqual("", request.Error);
                AssertSamePath(Path.GetFullPath(appDir), request.WorkingDirectory);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        // IPC 契约用例的夹具：只构造内存里的绝对路径与流，绝不碰文件系统、绝不启动进程。

        private static LaunchSpec NewSpec()
        {
            string nonce = Guid.NewGuid().ToString("N");
            string appDir = Path.Combine(Path.GetTempPath(), "gostgui-spec");
            return new LaunchSpec
            {
                V = 1,
                Nonce = nonce,
                Pipe = "gostgui." + nonce,
                Program = Path.Combine(appDir, "gost.exe"),
                Arguments = "-L 127.0.0.1:8080",
                WorkingDirectory = appDir,
                Kind = ChildKind.Executable,
                EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "GOST_LOG", "debug" },
                    { "Path", @"C:\tools" },
                    { "中文键", "中文值" }
                },
                RequestedAdmin = false
            };
        }

        private static void AssertSpecEqual(LaunchSpec expected, LaunchSpec actual)
        {
            Assert(actual != null, "反序列化结果不应为 null");
            AssertEqual(expected.V, actual.V);
            AssertEqual(expected.Nonce, actual.Nonce);
            AssertEqual(expected.Pipe, actual.Pipe);
            AssertEqual(expected.Program, actual.Program);
            AssertEqual(expected.Arguments, actual.Arguments);
            AssertEqual(expected.WorkingDirectory, actual.WorkingDirectory);
            AssertEqual(expected.Kind, actual.Kind);
            AssertEqual(expected.RequestedAdmin, actual.RequestedAdmin);
            Assert(actual.EnvOverrides != null, "EnvOverrides 不应为 null");
            AssertEqual(expected.EnvOverrides.Count, actual.EnvOverrides.Count);
            foreach (KeyValuePair<string, string> pair in expected.EnvOverrides)
            {
                Assert(actual.EnvOverrides.ContainsKey(pair.Key), "反序列化后缺少环境变量：" + pair.Key);
                AssertEqual(pair.Value, actual.EnvOverrides[pair.Key]);
            }
        }

        /// <summary>把一条消息写进内存流，返回原始帧文本（含结尾换行，便于断言帧形态与多帧拼接）。</summary>
        private static string FrameText(IpcMessage message)
        {
            using (var stream = new MemoryStream())
            {
                IpcFraming.Write(stream, message);
                return new UTF8Encoding(false, true).GetString(stream.ToArray());
            }
        }

        private static StreamReader ReaderOver(string text)
        {
            return new StreamReader(
                new MemoryStream(new UTF8Encoding(false).GetBytes(text)),
                new UTF8Encoding(false, true));
        }

        private static int CountChar(string text, char value)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c == value)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>异常消息必须是中文：非法输入只允许得到中文的 FormatException/ArgumentException，绝不允许 NRE。</summary>
        private static void AssertMalformedInputRejected(Action body, string what)
        {
            Exception captured = null;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                captured = ex;
            }

            Assert(captured != null, what + " 应抛出异常，而不是被静默接受");
            Assert(captured is FormatException || captured is ArgumentException,
                what + " 应抛出 FormatException 或 ArgumentException，实际 " + captured.GetType().Name + ": " + captured.Message);
            Assert(ContainsChinese(captured.Message), what + " 的异常消息应为中文：" + captured.Message);
        }

        private static bool ContainsChinese(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char c in text)
            {
                if (c >= '\u4e00' && c <= '\u9fff')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>记录 Flush 次数的内存流：Write 必须对调用方流 Flush，保证对端立刻可读。</summary>
        private sealed class FlushCountingStream : MemoryStream
        {
            public int FlushCount;

            public override void Flush()
            {
                FlushCount++;
                base.Flush();
            }
        }

        private static void SpecBase64RoundTrip()
        {
            LaunchSpec spec = NewSpec();
            string base64 = LaunchSpecCodec.ToBase64(spec);

            Assert(!string.IsNullOrEmpty(base64), "Base64 不应为空");
            Assert(base64.IndexOf(' ') < 0, "标准 Base64 不应包含空格，可直接塞进 lpParameters");

            byte[] bytes = Convert.FromBase64String(base64);
            string json = new UTF8Encoding(false, true).GetString(bytes);
            Assert(json.Length > 0 && json[0] == '{', "Base64 解出的应是 JSON 对象：" + json);
            Assert(json.Contains("\"nonce\""), "JSON 必须使用短字段名 nonce：" + json);
            Assert(json.Contains("\"kind\":\"exe\""), "Kind 必须序列化成 \"exe\"：" + json);

            LaunchSpec back = LaunchSpecCodec.FromBase64(base64);
            AssertSpecEqual(spec, back);
            Assert(back.EnvOverrides.ContainsKey("path"), "反序列化后的 EnvOverrides 必须大小写不敏感");
            AssertEqual("debug", back.EnvOverrides["gost_log"]);
            // 保留 JSON 里的键顺序，供 %VAR% 展开等按出现顺序处理。
            AssertEqual("GOST_LOG", new List<string>(back.EnvOverrides.Keys)[0]);
        }

        private static void SpecNeedsTempFileWhenTooLong()
        {
            AssertEqual(8000, LaunchSpecCodec.CommandLineLimit);

            // Base64 长度只能是 4 的倍数：先找到恰好等于上限的那一档，再加一个字符。
            LaunchSpec spec = NewSpec();
            int argsLength = 4000;
            while (true)
            {
                spec.Arguments = new string('x', argsLength);
                if (LaunchSpecCodec.ToBase64(spec).Length == LaunchSpecCodec.CommandLineLimit)
                {
                    spec.Arguments = new string('x', argsLength + 1);
                    if (LaunchSpecCodec.ToBase64(spec).Length > LaunchSpecCodec.CommandLineLimit)
                    {
                        break;
                    }
                }

                argsLength++;
                Assert(argsLength < 10000, "未能构造出恰好 8000 字符 Base64 的用例夹具");
            }

            spec.Arguments = new string('x', argsLength);
            AssertEqual(8000, LaunchSpecCodec.ToBase64(spec).Length);
            Assert(!LaunchSpecCodec.NeedsTempFile(spec), "Base64 恰好 8000 字符时不应改用临时文件");

            spec.Arguments = new string('x', argsLength + 1);
            AssertEqual(8004, LaunchSpecCodec.ToBase64(spec).Length);
            Assert(LaunchSpecCodec.NeedsTempFile(spec), "Base64 超过 8000 字符时必须改用临时文件");

            spec.Arguments = new string('x', 60000);
            Assert(LaunchSpecCodec.NeedsTempFile(spec), "超长参数必须改用临时文件");
        }

        private static void SpecJsonRoundTripPreservesChineseAndQuotes()
        {
            LaunchSpec spec = NewSpec();
            spec.Program = Path.Combine(Path.GetTempPath(), "工具 目录", "gost.exe");
            spec.Arguments = "-L \"127.0.0.1:8080\" --tag \"含中文与\\反斜杠\"";
            spec.WorkingDirectory = Path.Combine(Path.GetTempPath(), "工作 目录");
            spec.Kind = ChildKind.Batch;
            spec.RequestedAdmin = true;
            spec.EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "PATH", @"C:\工具;%PATH%" },
                { "引号键", "值里有\"引号\"和\\反斜杠" }
            };

            string json = LaunchSpecCodec.ToJson(spec);
            Assert(json.Contains("中文"), "中文必须原样保留，不能转义成 \\uXXXX：" + json);
            Assert(!json.Contains("\\u4e2d"), "中文不应被转义成 \\uXXXX：" + json);
            Assert(json.Contains("\"kind\":\"batch\""), "Kind 必须序列化成 \"batch\"：" + json);

            LaunchSpec back = LaunchSpecCodec.FromJson(json);
            AssertSpecEqual(spec, back);
            AssertEqual(ChildKind.Batch, back.Kind);
            Assert(back.EnvOverrides.ContainsKey("path"), "反序列化后的 EnvOverrides 必须大小写不敏感");
        }

        private static void SpecFromBase64MalformedThrowsChinese()
        {
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromBase64(null), "null Base64");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromBase64(""), "空 Base64");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromBase64("   "), "纯空白 Base64");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromBase64("这不是 base64!!!"), "非法 Base64 字符");
            AssertMalformedInputRejected(
                () => LaunchSpecCodec.FromBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"v\":1"))),
                "截断的 JSON");
            AssertMalformedInputRejected(
                () => LaunchSpecCodec.FromBase64(Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0xFD })),
                "非法 UTF-8 字节");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromJson(null), "null JSON");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromJson("{"), "畸形 JSON");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromJson("{\"kind\":\"weird\"}"), "未知 kind");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromJson("null"), "JSON null");
        }

        private static void IpcFrameRoundTripEscapesNewlineTabQuoteBackslash()
        {
            var message = new IpcMessage
            {
                Type = "out",
                Nonce = Guid.NewGuid().ToString("N"),
                Pid = 4321,
                Stream = "o",
                Data = "a\tb\n\"c\"\\d"
            };

            using (var stream = new FlushCountingStream())
            {
                IpcFraming.Write(stream, message);
                Assert(stream.FlushCount >= 1, "Write 必须 Flush，保证对端能立刻读到");

                string text = new UTF8Encoding(false, true).GetString(stream.ToArray());
                Assert(text.EndsWith("\n", StringComparison.Ordinal), "一帧必须以 \\n 结尾");
                // 帧内只能有结尾这一个原始换行；\t / \n / " / \ 必须走 JSON 转义。
                AssertEqual(1, CountChar(text, '\n'));
                AssertEqual(0, CountChar(text, '\r'));
                AssertEqual(0, CountChar(text, '\t'));
                Assert(text.Contains("\\t"), "制表符必须转义成 \\t：" + text);
                Assert(text.Contains("\\n"), "换行必须转义成 \\n：" + text);
                Assert(text.Contains("\\\""), "双引号必须转义：" + text);
                Assert(text.Contains("\\\\"), "反斜杠必须转义：" + text);

                stream.Position = 0;
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                {
                    IpcMessage back = IpcFraming.Read(reader);
                    Assert(back != null, "应读回一帧");
                    AssertEqual("out", back.Type);
                    AssertEqual(message.Nonce, back.Nonce);
                    AssertEqual(4321, back.Pid);
                    AssertEqual("o", back.Stream);
                    AssertEqual(message.Data, back.Data);
                }
            }
        }

        private static void IpcFrameRoundTripChineseAndEmptyString()
        {
            var chinese = new IpcMessage { Type = "out", Stream = "e", Data = "中文输出：你好，世界" };
            var empty = new IpcMessage { Type = "ready", Nonce = "n-1", Data = "" };
            var nullData = new IpcMessage { Type = "stop" };

            string chineseLine = FrameText(chinese);
            string emptyLine = FrameText(empty);
            string nullLine = FrameText(nullData);

            Assert(chineseLine.Contains("中文输出：你好，世界"), "中文应原样出现在帧里：" + chineseLine);
            Assert(emptyLine.Contains("\"d\":\"\""), "空字符串必须写成 \"d\":\"\"，不能当成 null 省略：" + emptyLine);
            Assert(!nullLine.Contains("\"d\""), "null 的 Data 必须省略：" + nullLine);

            using (StreamReader reader = ReaderOver(chineseLine + emptyLine + nullLine))
            {
                IpcMessage first = IpcFraming.Read(reader);
                AssertEqual("中文输出：你好，世界", first.Data);
                AssertEqual("e", first.Stream);

                IpcMessage second = IpcFraming.Read(reader);
                AssertEqual("", second.Data);
                Assert(second.Data != null, "空字符串不能变成 null");

                IpcMessage third = IpcFraming.Read(reader);
                AssertEqual("stop", third.Type);
                Assert(third.Data == null, "省略的 Data 读回应为 null");
                Assert(third.Stream == null, "省略的 Stream 读回应为 null");

                Assert(IpcFraming.Read(reader) == null, "三帧读完后必须返回 null");
            }
        }

        private static void IpcReadReturnsNullAtEof()
        {
            using (StreamReader reader = ReaderOver(""))
            {
                Assert(IpcFraming.Read(reader) == null, "空流必须返回 null");
            }

            // 只有空行的流不是消息：跳过空白后仍是文件尾。
            using (StreamReader reader = ReaderOver("\r\n\n   \n"))
            {
                Assert(IpcFraming.Read(reader) == null, "空行不是消息，读到文件尾必须返回 null");
            }

            // 读完最后一帧之后再读一次同样是 null，而不是抛异常。
            string line = FrameText(new IpcMessage { Type = "exit", ExitCode = 0 });
            using (StreamReader reader = ReaderOver(line))
            {
                IpcMessage message = IpcFraming.Read(reader);
                AssertEqual("exit", message.Type);
                AssertEqual(0, message.ExitCode);
                Assert(IpcFraming.Read(reader) == null, "读完最后一帧后必须返回 null");
            }
        }

        private static void IpcReadMalformedJsonThrowsChinese()
        {
            using (StreamReader reader = ReaderOver("{not json}\n"))
            {
                AssertMalformedInputRejected(() => IpcFraming.Read(reader), "畸形 JSON 帧");
            }

            using (StreamReader reader = ReaderOver("{\"t\":\"out\"\n"))
            {
                AssertMalformedInputRejected(() => IpcFraming.Read(reader), "截断的 JSON 帧");
            }
        }

        // 直连启动用例的夹具：只拉起 cmd /d /s /c，显式路径不依赖 PATH 解析。

        private const int DirectWaitTimeoutMs = 10000;

        private static string CmdPath()
        {
            string comspec = Environment.GetEnvironmentVariable("COMSPEC");
            if (string.IsNullOrEmpty(comspec))
            {
                comspec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            }

            return comspec;
        }

        private static ConfigItem NewDirectItem(string args, string workDir = "", bool? launcherElevated = null)
        {
            return new ConfigItem
            {
                Program = CmdPath(),
                Args = args,
                WorkDir = workDir,
                // R32：这些用例要求**直连**启动。权限矩阵在“启动器已提权 + 普通项”时会把项送去降权中转，
                // 所以 RunAsAdmin 必须跟着启动器完整性走：标准启动器给 false、提权启动器给 true，
                // 两种完整性下都落到“直连”这一格，而不是假设套件总以标准权限运行。
                // launcherElevated 只给用例模拟另一种完整性，正常运行取本进程的真实状态。
                RunAsAdmin = launcherElevated ?? ElevationManager.IsProcessElevated()
            };
        }

        /// <summary>轮询等待条件成立；任何等待都不得超过 10s，直连用例不允许无限挂起。</summary>
        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            if (condition == null)
            {
                return false;
            }

            int waited = 0;
            while (waited < timeoutMs)
            {
                if (condition())
                {
                    return true;
                }

                System.Threading.Thread.Sleep(25);
                waited += 25;
            }

            return condition();
        }

        /// <summary>线程安全地收集 ProcessManager 事件，供直连用例断言。</summary>
        private sealed class DirectRun
        {
            private readonly object _gate = new object();
            private readonly List<KeyValuePair<LogKind, string>> _output = new List<KeyValuePair<LogKind, string>>();
            private bool _exited;
            private int _exitCode = int.MinValue;

            public void Attach(ProcessManager manager)
            {
                manager.OutputReceived += OnOutput;
                manager.ProcessExited += OnExited;
            }

            public bool Exited
            {
                get { lock (_gate) { return _exited; } }
            }

            public int ExitCode
            {
                get { lock (_gate) { return _exitCode; } }
            }

            public bool HasOutput(LogKind kind, string fragment)
            {
                return HasOutput(kind, data => data.Contains(fragment));
            }

            public bool HasOutput(LogKind kind, Func<string, bool> predicate)
            {
                lock (_gate)
                {
                    foreach (KeyValuePair<LogKind, string> pair in _output)
                    {
                        if (pair.Key == kind && pair.Value != null && predicate(pair.Value))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public int CountOutput(LogKind kind, string fragment)
            {
                lock (_gate)
                {
                    int count = 0;
                    foreach (KeyValuePair<LogKind, string> pair in _output)
                    {
                        if (pair.Key == kind && pair.Value != null && pair.Value.Contains(fragment))
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }

            /// <summary>失败时的诊断文本：把收到的输出按类别拼起来。</summary>
            public string Describe()
            {
                lock (_gate)
                {
                    var builder = new StringBuilder();
                    foreach (KeyValuePair<LogKind, string> pair in _output)
                    {
                        builder.Append('[').Append(pair.Key).Append("] ")
                            .Append((pair.Value ?? "").Trim()).Append(" | ");
                    }

                    if (_exited)
                    {
                        builder.Append("exit=").Append(_exitCode);
                    }

                    return builder.Length == 0 ? "（没有收到任何输出）" : builder.ToString();
                }
            }

            private void OnOutput(ConfigItem item, LogKind kind, string data)
            {
                lock (_gate)
                {
                    _output.Add(new KeyValuePair<LogKind, string>(kind, data));
                }
            }

            private void OnExited(ConfigItem item, int exitCode)
            {
                lock (_gate)
                {
                    _exited = true;
                    _exitCode = exitCode;
                }
            }
        }

        private static void DirectCapturesStdoutAndStderr()
        {
            using (ProcessManager manager = new ProcessManager())
            {
                DirectRun run = new DirectRun();
                run.Attach(manager);
                ConfigItem item = NewDirectItem("/d /s /c \"echo out& echo err 1>&2\"");

                Assert(manager.StartProcess(item), "cmd 子进程应启动成功");
                Assert(WaitUntil(() => run.CountOutput(LogKind.Stdout, "out") == 1, DirectWaitTimeoutMs),
                    "应收到恰好一行 stdout：" + run.Describe());
                Assert(WaitUntil(() => run.CountOutput(LogKind.Stderr, "err") == 1, DirectWaitTimeoutMs),
                    "应收到恰好一行 stderr：" + run.Describe());
                // 两类输出不能串台。
                AssertEqual(0, run.CountOutput(LogKind.Stdout, "err"));
                AssertEqual(0, run.CountOutput(LogKind.Stderr, "out"));
            }
        }

        private static void DirectReportsExitCode()
        {
            using (ProcessManager manager = new ProcessManager())
            {
                DirectRun run = new DirectRun();
                run.Attach(manager);
                ConfigItem item = NewDirectItem("/d /s /c \"exit 7\"");

                Assert(manager.StartProcess(item), "cmd 子进程应启动成功");
                Assert(WaitUntil(() => run.Exited, DirectWaitTimeoutMs), "应收到退出事件：" + run.Describe());
                AssertEqual(7, run.ExitCode);
                Assert(!manager.IsProcessRunning(item), "退出后不应仍在运行");
                AssertEqual(0, manager.GetChildPid(item));
            }
        }

        private static void DirectAppliesEnvOverrides()
        {
            bool hadDuplicates = HasDuplicateProcessEnvironmentKeys();
            string key = "GOSTGUI_SELFTEST_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string value = "gostgui-" + Guid.NewGuid().ToString("N");
            using (ProcessManager manager = new ProcessManager())
            {
                DirectRun run = new DirectRun();
                run.Attach(manager);
                ConfigItem item = NewDirectItem("/d /s /c \"echo %" + key + "%\"");
                item.Env = new Dictionary<string, string> { { key, value } };

                Assert(manager.StartProcess(item), "cmd 子进程应启动成功");
                Assert(WaitUntil(() => run.HasOutput(LogKind.Stdout, value), DirectWaitTimeoutMs),
                    "子进程应看到覆盖后的 " + key + "：" + run.Describe());
                // 覆盖只属于子进程：无论走 psi 环境字典还是临时环境兜底，都不能留在本进程环境里。
                Assert(Environment.GetEnvironmentVariable(key) == null,
                    "启动后本进程环境必须恢复，不能残留 " + key);

                // 本机父环境带大小写重名变量时，第一次 env 覆盖启动必须先规范化并记一条 System 日志。
                if (hadDuplicates)
                {
                    Assert(WaitUntil(() => run.HasOutput(LogKind.System, "已自动规范化"), DirectWaitTimeoutMs),
                        "重名父环境应先规范化并记录 System 日志：" + run.Describe());
                    Assert(!run.HasOutput(LogKind.System, "规范化未完成"),
                        "父环境规范化不应失败：" + run.Describe());
                }
            }
        }

        private static bool HasDuplicateProcessEnvironmentKeys()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = Convert.ToString(entry.Key);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(key))
                {
                    return true;
                }
            }

            return false;
        }

        private static void DirectUsesWorkingDirectory()
        {
            string workDir = CreateTempDirectory();
            try
            {
                using (ProcessManager manager = new ProcessManager())
                {
                    DirectRun run = new DirectRun();
                    run.Attach(manager);
                    ConfigItem item = NewDirectItem("/d /s /c \"cd\"", workDir);

                    Assert(manager.StartProcess(item), "cmd 子进程应启动成功");
                    string expected = Path.GetFullPath(workDir);
                    Assert(WaitUntil(() => run.HasOutput(LogKind.Stdout,
                            line => string.Equals(line.Trim(), expected, StringComparison.OrdinalIgnoreCase)),
                        DirectWaitTimeoutMs),
                        "子进程的工作目录应为 " + expected + "：" + run.Describe());
                }
            }
            finally
            {
                DeleteTempDirectory(workDir);
            }
        }

        private static void DirectMissingProgramSetsErrorAndDoesNotStart()
        {
            using (ProcessManager manager = new ProcessManager())
            {
                DirectRun run = new DirectRun();
                run.Attach(manager);
                ConfigItem item = new ConfigItem { Program = "" };

                Assert(!manager.StartProcess(item), "空程序不应启动");
                Assert(!manager.IsProcessRunning(item), "启动失败后状态必须是未运行");
                AssertEqual(0, manager.GetChildPid(item));
                Assert(WaitUntil(() => run.HasOutput(LogKind.System, "未设置程序"), DirectWaitTimeoutMs),
                    "应输出 System 级的失败原因：" + run.Describe());
            }
        }

        private static void DirectNonexistentFileReturnsFalseWithoutThrowing()
        {
            string dir = CreateTempDirectory();
            try
            {
                string missing = Path.Combine(dir, "no-such-program.exe");
                using (ProcessManager manager = new ProcessManager())
                {
                    DirectRun run = new DirectRun();
                    run.Attach(manager);
                    ConfigItem item = new ConfigItem { Program = missing };

                    // 关键：这条路径只能返回 false，绝不能把异常抛给界面线程。
                    bool started = manager.StartProcess(item);

                    Assert(!started, "不存在的程序不应启动");
                    Assert(!manager.IsProcessRunning(item), "启动失败后状态必须是未运行");
                    AssertEqual(0, manager.GetChildPid(item));
                    Assert(WaitUntil(() => run.HasOutput(LogKind.System, "未找到程序"), DirectWaitTimeoutMs),
                        "应输出 System 级的失败原因：" + run.Describe());
                }
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        private static void DirectGetChildPidIsLiveThenZero()
        {
            using (ProcessManager manager = new ProcessManager())
            {
                ConfigItem item = NewDirectItem("/d /s /c \"ping -n 30 127.0.0.1 >nul\"");

                Assert(manager.StartProcess(item), "cmd 子进程应启动成功");
                try
                {
                    Assert(WaitUntil(() => manager.GetChildPid(item) > 0, DirectWaitTimeoutMs),
                        "运行中应返回子进程 PID");
                    Assert(manager.IsProcessRunning(item), "运行中 IsProcessRunning 应为 true");
                }
                finally
                {
                    manager.StopProcess(item);
                }

                Assert(!manager.IsProcessRunning(item), "停止后不应仍在运行");
                AssertEqual(0, manager.GetChildPid(item));
            }
        }

        private static void DirectElevationRequiredIsDetected()
        {
            string msconfig = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "msconfig.exe");
            if (!File.Exists(msconfig))
            {
                Notice("SKIP Direct_ElevationRequired_IsDetected：找不到 msconfig.exe 夹具");
                return;
            }

            if (IsProcessElevated())
            {
                Notice("SKIP Direct_ElevationRequired_IsDetected：本进程已提权，无法制造 Win32 740");
                return;
            }

            using (ProcessManager manager = new ProcessManager())
            {
                DirectRun run = new DirectRun();
                run.Attach(manager);
                // msconfig.exe 的清单要求 requireAdministrator：标准权限下 CreateProcess 直接返回 740，不弹 UAC。
                ConfigItem item = new ConfigItem { Program = msconfig };

                Assert(!manager.StartProcess(item), "需要管理员权限的程序在标准权限下不应启动");
                Assert(manager.LastStartErrorIsElevationRequired, "应标记为需要管理员权限（Win32 740）");
                Assert(!manager.IsProcessRunning(item), "启动失败后状态必须是未运行");
                AssertEqual(0, manager.GetChildPid(item));
                Assert(WaitUntil(() => run.HasOutput(LogKind.System, "管理员"), DirectWaitTimeoutMs),
                    "System 日志应说明需要管理员权限：" + run.Describe());
                Assert(run.HasOutput(LogKind.System, "740"),
                    "System 日志应带上 Windows 错误码 740：" + run.Describe());
            }
        }

        /// <summary>
        /// 独立于 ElevationManager 的提权判定：直接读当前令牌的 TokenElevation，用作用例期望值。
        /// 判断失败按未提权处理（测试进程通常就是标准权限；真提权时断言会明确失败，而不是静默放过）。
        /// </summary>
        private static bool IsProcessElevated()
        {
            return TokenProbe.IsProcessElevated();
        }

        private static void ElevDescribeWin32ErrorIsDistinctAndChinese()
        {
            int[] codes = { 740, 1223, 1314, 2, 5, 193 };
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (int code in codes)
            {
                string text = ElevationManager.DescribeWin32Error(code);
                Assert(!string.IsNullOrEmpty(text), "错误码 " + code + " 的说明不应为空");
                Assert(ContainsChinese(text), "错误码 " + code + " 的说明应为中文：" + text);

                int other;
                Assert(!seen.TryGetValue(text, out other),
                    $"错误码 {code} 与 {other} 的说明不能相同：{text}");
                seen[text] = code;
            }

            // 1223 的文案就是 UAC 取消时日志里必须出现的那一行。
            AssertEqual("已取消管理员授权", ElevationManager.DescribeWin32Error(1223));
            Assert(ElevationManager.DescribeWin32Error(740).Contains("管理员"),
                "740 的说明应提示需要管理员权限：" + ElevationManager.DescribeWin32Error(740));
        }

        private static void ElevIsProcessElevatedMatchesToken()
        {
            // 同一进程两次取值必须稳定：排除“每次随机/未初始化”的假实现。
            bool first = ElevationManager.IsProcessElevated();
            bool second = ElevationManager.IsProcessElevated();
            AssertEqual(first, second);

            // 期望值来自本文件独立声明的 P/Invoke（与 ElevationManager 不共用代码），
            // 因此标准权限与管理员权限下都成立，不需要 SKIP。
            AssertEqual(IsProcessElevated(), first);
            Notice("Elev_IsProcessElevated_MatchesToken：本次运行进程提权=" + first);
        }

        private static void ElevMatrixDecideModeCoversFourCells()
        {
            AssertDecideMode(false, false, null, "标准进程 + 普通项 → 直连");
            AssertDecideMode(false, true, RunnerLaunchMode.Elevated, "标准进程 + 管理员项 → UAC 提权中转");
            AssertDecideMode(true, false, RunnerLaunchMode.StandardUser, "管理员进程 + 普通项 → 降权中转");
            AssertDecideMode(true, true, null, "管理员进程 + 管理员项 → 直连（本来就有权限）");
        }

        /// <summary>
        /// R32：直连用例的配置项必须让权限矩阵在**两种完整性**下都选到直连。
        /// 这里不只跑本进程的真实完整性，还显式模拟“提权启动器”那一格：对 <see cref="NewDirectItem"/>
        /// 传入 launcherElevated=true/false，断言 RunAsAdmin 跟着变、且矩阵结果都是直连。
        /// 这样即使自检只在标准权限下运行，也能证明提权套件里同样的用例不会掉进降权中转。
        /// </summary>
        private static void ElevDirectItemsChooseDirectAtBothIntegrities()
        {
            foreach (bool launcherElevated in new[] { false, true })
            {
                ConfigItem item = NewDirectItem("/d /s /c \"exit 0\"", "", launcherElevated);
                AssertEqual(launcherElevated, item.RunAsAdmin);

                RunnerLaunchMode? mode = ProcessManager.DecideMode(launcherElevated, item.RunAsAdmin);
                Assert(mode == null,
                    "直连用例在启动器提权=" + launcherElevated + " 时必须选到直连，实际 " + FormatMode(mode)
                    + "；R32 要求直连用例不依赖启动器完整性");
            }

            // 真实完整性下再跑一遍，确认工厂默认值没有被模拟参数带歪。
            bool elevated = ElevationManager.IsProcessElevated();
            ConfigItem real = NewDirectItem("/d /s /c \"exit 0\"");
            AssertEqual(elevated, real.RunAsAdmin);
            Assert(ProcessManager.DecideMode(elevated, real.RunAsAdmin) == null,
                "直连用例在当前完整性（提权=" + elevated + "）下必须选到直连");
            Notice("Elev_DirectItems_ChooseDirectAtBothIntegrities：本次运行进程提权=" + elevated
                   + "；两种完整性（false/true）的矩阵选路均已断言为直连");
        }

        /// <summary>
        /// Task 10 的接线用例：StandardUser 模式不能再在模式检查处抛 NotSupportedException，
        /// 而必须真的走到“创建进程”那一步。用一张不是有效可执行文件的同名文件做桩：
        /// 创建进程必然失败（无效镜像通常是 216/193，权限不足是 1314/5），失败形态必须是
        /// <see cref="Win32Exception"/> 或文件类异常——唯独不能是“暂不支持”。
        /// <para>
        /// 这里加 20 秒看门狗：GUI 子系统进程创建无效镜像时 Windows 会弹模态硬错误框，
        /// 如果降权路径没有压掉它（见 ElevationManager.SuppressHardErrorDialogs），调用会永久挂起；
        /// 看门狗把“挂起”变成可读的失败，而不是让整个自检卡死。
        /// </para>
        /// </summary>
        private static void ElevStandardUserModeIsWired()
        {
            string stub = Path.Combine(Path.GetTempPath(),
                "gostgui-not-an-executable-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(stub, "这不是一个可执行文件", new UTF8Encoding(false));
            try
            {
                var request = new LaunchRequest
                {
                    FileName = CmdPath(),
                    Arguments = "/d /s /c \"echo wired\"",
                    WorkingDirectory = Path.GetTempPath(),
                    Kind = ChildKind.Executable,
                    EnvOverrides = EmptyEnv()
                };

                Exception failure = null;
                RunnerSession session = null;
                Task startTask = Task.Run(() =>
                {
                    try
                    {
                        session = RunnerSession.StartAsync(request, RunnerLaunchMode.StandardUser, stub,
                            Path.GetTempPath(), CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });

                Assert(startTask.Wait(20000),
                    "StandardUser 模式在 20 秒内没有返回：创建进程失败时的系统硬错误框可能没有被抑制住");

                if (session != null)
                {
                    // 桩文件不可能是有效镜像；真出现了会话只能说明这条路径不再按预期失败。
                    session.Dispose();
                    throw new SelfTestFailureException("无效的中转进程可执行文件不应返回可用会话");
                }

                Assert(failure != null, "无效的中转进程可执行文件不应返回可用会话");
                Assert(!(failure is NotSupportedException),
                    "StandardUser 模式不能再抛 NotSupportedException（Task 10 必须接线），实际："
                    + failure.GetType().Name + ": " + failure.Message);
                Assert(failure is Win32Exception || failure is FileNotFoundException,
                    "StandardUser 模式应失败在创建进程/文件检查，实际抛出 "
                    + failure.GetType().Name + ": " + failure.Message);

                if (failure is Win32Exception win32)
                {
                    Assert(win32.NativeErrorCode != 0, "创建进程失败时必须带上真实的 Win32 错误码");
                    Notice("Elev_StandardUserMode_IsWired：StandardUser 模式已接线，桩失败码="
                           + win32.NativeErrorCode + "（" + ElevationManager.DescribeWin32Error(win32.NativeErrorCode) + "）");
                }
            }
            finally
            {
                TryDeleteFile(stub);
            }
        }

        /// <summary>
        /// 降权启动的调用方资格。文档预期：标准权限调用方拿不到降权令牌，应失败于 1314（权限不足）或 5（拒绝访问），
        /// 且不得拉起任何进程。
        /// <para>
        /// 本机实测补正：套件以标准权限运行（提权=False），但当前账号是<b>过滤管理员</b>
        /// （Administrators 仅用于拒绝、中等完整性级别）。这种令牌下 CreateProcessAsUser 的“分配主令牌”检查会放行，
        /// 所以文档里的 1314 在本机不会出现，降权会真的成功。
        /// </para>
        /// <para>
        /// 断言口径按完整性翻转：<b>标准权限</b>下只接受 1314/5 这条登记在案的拒绝（193/2 之类说明 runner 镜像或
        /// 路径真的坏了，必须让用例失败），成功则必须证明子进程令牌非提权；<b>提权</b>套件里降权本该成功，
        /// 任何失败都直接判失败（不再有“接受文档集合”的松口）。
        /// </para>
        /// </summary>
        private static void ElevStandardUserLaunchRequiresElevatedCaller()
        {
            string runnerPath = RunnerPath();
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            bool elevated = ElevationManager.IsProcessElevated();
            int runnersBefore = CountRunnerProcesses();
            Process launched = null;
            Win32Exception failure = null;
            try
            {
                try
                {
                    launched = ElevationManager.LaunchRunnerAsStandardUser(runnerPath,
                        BuildWaitingRunnerSpecArguments(), Common.GetApplicationPath());
                }
                catch (Win32Exception ex)
                {
                    failure = ex;
                }

                if (failure != null)
                {
                    Assert(!elevated,
                        "提权启动器下降权启动不应失败（期望翻转为必须成功），实际错误码 "
                        + failure.NativeErrorCode + ": " + failure.Message);
                    Assert(IsDocumentedStandardUserRefusal(failure.NativeErrorCode),
                        "标准权限下降权启动只接受 1314/5 这条登记在案的拒绝，实际 "
                        + failure.NativeErrorCode + ": " + failure.Message);
                    AssertEqual(runnersBefore, CountRunnerProcesses());
                    Notice("Elev_StandardUserLaunch_RequiresElevatedCaller：降权启动被拒绝，错误码 "
                           + failure.NativeErrorCode + "（"
                           + ElevationManager.DescribeWin32Error(failure.NativeErrorCode) + "），未拉起任何进程");
                }
                else
                {
                    Assert(launched != null, "降权启动成功时必须返回可用的 Process（供 R27 停止兜底）");

                    // 成功分支自己就要证明“没有继承调用方权限”，不把这条证据留给兄弟用例。
                    bool? childElevated = TokenProbe.TryIsProcessElevated(launched.Id);
                    Assert(childElevated.HasValue,
                        "应能查询降权启动的 runner 令牌（PID " + launched.Id + "）");
                    AssertEqual(false, childElevated.Value);
                    Notice("Elev_StandardUserLaunch_RequiresElevatedCaller：本进程提权=" + elevated
                           + "，降权启动成功且子进程令牌提权=False（PID " + launched.Id + "）");
                }
            }
            finally
            {
                KillAndDisposeQuietly(launched);
            }
        }

        /// <summary>
        /// 降权后的 runner 必须真的是标准权限：查询子进程令牌的 TokenElevation，并用**权威判断**
        /// （<see cref="ElevationManager.IsProcessElevated"/>）做对照。
        /// 本机（过滤管理员）能直接构造出降权启动，所以这条证据不需要提权启动器；
        /// 标准权限下被拒绝时只接受 1314/5（真实的“调用方资格不足”），其它错误码一律判失败；
        /// 提权套件里降权本该成功，失败即判失败。
        /// </summary>
        private static void ElevDeElevatedRunnerTokenIsStandardUser()
        {
            string runnerPath = RunnerPath();
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            Process runner = null;
            try
            {
                try
                {
                    runner = ElevationManager.LaunchRunnerAsStandardUser(runnerPath,
                        BuildWaitingRunnerSpecArguments(), Common.GetApplicationPath());
                }
                catch (Win32Exception ex)
                {
                    Assert(!ElevationManager.IsProcessElevated(),
                        "提权启动器下降权启动不应失败（期望翻转为必须成功），实际错误码 "
                        + ex.NativeErrorCode + ": " + ex.Message);
                    Assert(IsDocumentedStandardUserRefusal(ex.NativeErrorCode),
                        "标准权限下降权启动只接受 1314/5 这条登记在案的拒绝，实际 "
                        + ex.NativeErrorCode + ": " + ex.Message);
                    Notice("Elev_DeElevatedRunner_TokenIsStandardUser：本会话降权启动被拒绝（错误码 "
                           + ex.NativeErrorCode + "），子进程令牌核对改为 Task 14 人工清单第 8 项");
                    return;
                }

                Assert(runner != null && !runner.HasExited, "降权启动的 runner 应处于运行中");

                // 对照组：权威判断与测试内独立探针必须一致，否则下面的 False 不能算证据。
                bool selfElevated = ElevationManager.IsProcessElevated();
                bool? probeSelf = TokenProbe.TryIsProcessElevated(Process.GetCurrentProcess().Id);
                Assert(probeSelf.HasValue && probeSelf.Value == selfElevated,
                    "令牌探针读到的本进程提权状态（" + probeSelf + "）与权威判断（" + selfElevated + "）不一致");

                bool? runnerElevated = TokenProbe.TryIsProcessElevated(runner.Id);
                Assert(runnerElevated.HasValue,
                    "应能查询降权后 runner 的令牌（PID " + runner.Id + "）");
                AssertEqual(false, runnerElevated.Value);
                Notice("Elev_DeElevatedRunner_TokenIsStandardUser：本进程提权=" + selfElevated
                       + "；降权 runner PID " + runner.Id + " 提权=False");
            }
            finally
            {
                KillAndDisposeQuietly(runner);
            }
        }

        /// <summary>与 UI 同目录的中转进程路径。</summary>
        private static string RunnerPath()
        {
            return Path.Combine(Common.GetApplicationPath(), "gostGUI.Runner.exe");
        }

        /// <summary>
        /// 一份**有效但连不上任何管道**的 spec：runner 会正常解析并尝试连接（15s），
        /// 因此在这段时间里它一定是活着的，便于查询令牌或验证“真的启动了”。
        /// </summary>
        private static string BuildWaitingRunnerSpecArguments()
        {
            string nonce = Guid.NewGuid().ToString("N");
            var spec = new LaunchSpec
            {
                V = 1,
                Nonce = nonce,
                Pipe = "gostgui." + nonce,
                Program = CmdPath(),
                Arguments = "/d /s /c \"exit 0\"",
                WorkingDirectory = Path.GetTempPath(),
                Kind = ChildKind.Executable,
                EnvOverrides = EmptyEnv()
            };

            return "--spec " + LaunchSpecCodec.ToBase64(spec);
        }

        /// <summary>
        /// 标准权限调用方唯一登记在案的降权拒绝码：1314（权限不足）/ 5（拒绝访问）。
        /// 193（不是有效可执行文件）/ 2（文件不存在）说明 runner 镜像或路径真的坏了，不能当成“环境不允许降权”放过。
        /// </summary>
        private static bool IsDocumentedStandardUserRefusal(int code)
        {
            return code == 1314 || code == 5;
        }

        /// <summary>
        /// 降权路径（<c>CreateProcessWithTokenW</c>）的 lpCommandLine 上限只有 1024 字符，命令行走 <c>--spec</c>
        /// 时长 spec（大环境覆盖、长参数）会以 87 失败，1314 的兜底救不回来。这里钉死传输决策：
        /// 降权路径超过 1000 字符必须走 <c>--spec-file</c>；同权限/提权路径仍按通用 8000 字符上限判断；
        /// 超过 8000 时所有模式都走临时文件。
        /// 纯逻辑断言（不启动进程）+ 一次真实的降权长 spec 中转（本机可降权时执行，否则按 1314/5 记录到 Task 14）。
        /// </summary>
        private static void ElevStandardUserLongSpecUsesSpecFileTransport()
        {
            string runnerPath = RunnerPath();
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            LaunchSpec smallSpec = NewSpecForTransportTest("/d /s /c \"echo small\"", 0);
            Assert(!RunnerSession.NeedsTempSpecFile(smallSpec, RunnerLaunchMode.StandardUser, runnerPath),
                "小 spec 在降权路径应走 --spec（命令行）");
            Assert(!RunnerSession.NeedsTempSpecFile(smallSpec, RunnerLaunchMode.SameIntegrity, runnerPath),
                "小 spec 在同权限路径应走 --spec（命令行）");

            // 长的“环境覆盖”把 Base64 撑到 1000~8000 之间：这正是降权路径独有的危险区间。
            LaunchSpec longSpec = NewSpecForTransportTest("/d /s /c \"echo longspec\"", 1600);
            string inlineCommandLine = ElevationManager.QuoteIfNeeded(runnerPath) + " --spec "
                                       + LaunchSpecCodec.ToBase64(longSpec);
            Assert(inlineCommandLine.Length > RunnerSession.StandardUserCommandLineLimit,
                "夹具的命令行长度 " + inlineCommandLine.Length + " 必须超过降权路径上限 "
                + RunnerSession.StandardUserCommandLineLimit);
            Assert(!LaunchSpecCodec.NeedsTempFile(longSpec),
                "夹具必须仍在通用 8000 字符上限以内，才能证明降权上限的独立判定生效");
            Assert(RunnerSession.NeedsTempSpecFile(longSpec, RunnerLaunchMode.StandardUser, runnerPath),
                "降权路径的长 spec 必须改走 --spec-file");
            Assert(!RunnerSession.NeedsTempSpecFile(longSpec, RunnerLaunchMode.SameIntegrity, runnerPath),
                "同权限路径不受降权路径的 1000 字符上限影响");

            // 超过通用上限时，任何模式都必须走临时文件。
            LaunchSpec hugeSpec = NewSpecForTransportTest("/d /s /c \"echo huge\"", 9000);
            Assert(LaunchSpecCodec.NeedsTempFile(hugeSpec), "夹具必须超过通用 8000 字符上限");
            Assert(RunnerSession.NeedsTempSpecFile(hugeSpec, RunnerLaunchMode.SameIntegrity, runnerPath),
                "超过通用上限时同权限路径也必须走 --spec-file");

            Notice("Elev_StandardUserLongSpec_UsesSpecFileTransport：降权路径 "
                   + RunnerSession.StandardUserCommandLineLimit + " 字符上限判定生效（夹具命令行 "
                   + inlineCommandLine.Length + " 字符 → --spec-file）");

            // 真跑一次：大环境覆盖 + 降权 + --spec-file 全链路（runner 必须能读到临时 spec 并转发 stdout）。
            var request = new LaunchRequest
            {
                FileName = CmdPath(),
                Arguments = "/d /s /c \"echo longspec\"",
                WorkingDirectory = Path.GetTempPath(),
                Kind = ChildKind.Executable,
                EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "GOSTGUI_LONGSPEC", new string('x', 1600) }
                }
            };

            RunnerSession session = null;
            var output = new RunnerOutputCollector();
            var exitReceived = new ManualResetEventSlim(false);
            int exitCode = int.MinValue;
            try
            {
                try
                {
                    session = RunnerSession.StartAsync(request, RunnerLaunchMode.StandardUser, runnerPath,
                        Common.GetApplicationPath(), CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Win32Exception ex)
                {
                    Assert(!ElevationManager.IsProcessElevated(),
                        "提权启动器下降权长 spec 中转不应失败（期望翻转为必须成功），实际错误码 "
                        + ex.NativeErrorCode + ": " + ex.Message);
                    Assert(IsDocumentedStandardUserRefusal(ex.NativeErrorCode),
                        "标准权限下降权长 spec 中转只接受 1314/5 这条登记在案的拒绝，实际 "
                        + ex.NativeErrorCode + ": " + ex.Message);
                    Notice("Elev_StandardUserLongSpec_UsesSpecFileTransport：本会话降权启动被拒绝（错误码 "
                           + ex.NativeErrorCode + "），真实 --spec-file 中转留给 Task 14 人工清单");
                    return;
                }

                session.Output += output.Add;
                session.Exited += code =>
                {
                    exitCode = code;
                    exitReceived.Set();
                };

                Assert(session.ChildPid != 0, "started 帧应给出正数子进程 PID");
                Assert(exitReceived.Wait(RunnerWaitTimeoutMs),
                    "长 spec 经 --spec-file 应在限定时间内跑完：" + output.Describe());
                AssertEqual(0, exitCode);
                Assert(output.Has(LogKind.Stdout, "longspec"),
                    "长 spec 经 --spec-file 转发的 stdout 应包含 longspec：" + output.Describe());
                Notice("Elev_StandardUserLongSpec_UsesSpecFileTransport：长 spec 降权中转成功（子 PID "
                       + session.ChildPid + "）");
            }
            finally
            {
                if (session != null)
                {
                    session.Dispose();
                }

                exitReceived.Dispose();
            }
        }

        /// <summary>
        /// 降权场景的 ACL 必须覆盖“交互用户”：over-the-shoulder 时 gostGUI 以另一个管理员账号运行，
        /// 而降权后的 runner 是交互用户身份，默认 DACL 会让它读不到临时 spec、连不上管道。
        /// 这里断言两种口径的主体集合，以及命名管道 DACL 里确实有 INTERACTIVE 的全控规则。
        /// </summary>
        private static void ElevStandardUserAclIncludesInteractiveUser()
        {
            var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
            List<SecurityIdentifier> plainSids = RunnerSession.BuildAclSids(false);
            List<SecurityIdentifier> deElevatedSids = RunnerSession.BuildAclSids(true);

            Assert(!ContainsSid(plainSids, interactive),
                "同权限/提权路径的 ACL 不应包含 INTERACTIVE（不改动其它模式的 ACL）");
            Assert(ContainsSid(deElevatedSids, interactive),
                "降权路径的 ACL 必须包含 INTERACTIVE（S-1-5-4）");

            foreach (WellKnownSidType required in new[]
                     {
                         WellKnownSidType.BuiltinAdministratorsSid,
                         WellKnownSidType.LocalSystemSid
                     })
            {
                var sid = new SecurityIdentifier(required, null);
                Assert(ContainsSid(plainSids, sid), "标准 ACL 缺少 " + required);
                Assert(ContainsSid(deElevatedSids, sid), "降权 ACL 缺少 " + required);
            }

            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                SecurityIdentifier currentUser = identity.User;
                if (currentUser != null)
                {
                    Assert(ContainsSid(plainSids, currentUser), "标准 ACL 缺少当前用户");
                    Assert(ContainsSid(deElevatedSids, currentUser), "降权 ACL 缺少当前用户");
                }
            }

            PipeSecurity plainPipe = RunnerSession.BuildPipeSecurity(false);
            PipeSecurity deElevatedPipe = RunnerSession.BuildPipeSecurity(true);
            Assert(!HasFullControlPipeRule(plainPipe, interactive),
                "同权限/提权路径的命名管道 DACL 不应给 INTERACTIVE 全控");
            Assert(HasFullControlPipeRule(deElevatedPipe, interactive),
                "降权路径的命名管道 DACL 必须给 INTERACTIVE 全控");
            Assert(HasFullControlPipeRule(deElevatedPipe,
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)),
                "降权路径的命名管道 DACL 仍必须给 Administrators 全控");

            Notice("Elev_StandardUserAcl_IncludesInteractiveUser：降权管道/spec ACL 已包含 INTERACTIVE（S-1-5-4）");
        }

        /// <summary>
        /// 降权兜底会临时启用 <c>SeAssignPrimaryTokenPrivilege</c> / <c>SeIncreaseQuotaPrivilege</c>；
        /// 用例在调用前后各读一次本进程令牌的启用状态，断言**完全一致**——即“用完必须恢复”，
        /// 既不留下新启用的特权，也不会把本来已启用的特权关掉。
        /// </summary>
        private static void ElevStandardUserLaunchDoesNotLeavePrivilegesEnabled()
        {
            const string assignToken = "SeAssignPrimaryTokenPrivilege";
            const string quotaToken = "SeIncreaseQuotaPrivilege";

            // 探针自检：SeChangeNotifyPrivilege 在 Windows 上对所有用户默认启用，SeShutdownPrivilege 默认禁用；
            // 两条对照证明 IsPrivilegeEnabled 真的读到了属性位，而不是永远返回 false（否则下面的断言是假绿）。
            Assert(TokenProbe.IsPrivilegeEnabled("SeChangeNotifyPrivilege"),
                "令牌特权探针应读到默认启用的 SeChangeNotifyPrivilege");
            Assert(!TokenProbe.IsPrivilegeEnabled("SeShutdownPrivilege"),
                "令牌特权探针不应把默认禁用的 SeShutdownPrivilege 读成已启用");

            bool assignBefore = TokenProbe.IsPrivilegeEnabled(assignToken);
            bool quotaBefore = TokenProbe.IsPrivilegeEnabled(quotaToken);

            string runnerPath = RunnerPath();
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            bool elevated = ElevationManager.IsProcessElevated();
            Process runner = null;
            try
            {
                try
                {
                    runner = ElevationManager.LaunchRunnerAsStandardUser(runnerPath,
                        BuildWaitingRunnerSpecArguments(), Common.GetApplicationPath());
                }
                catch (Win32Exception ex)
                {
                    // 这条用例只关心特权状态；调用本身只允许“登记在案的拒绝”（标准权限）或（提权套件里）成功。
                    Assert(!elevated && IsDocumentedStandardUserRefusal(ex.NativeErrorCode),
                        "降权启动出现非登记在案的错误（本进程提权=" + elevated + "）："
                        + ex.NativeErrorCode + ": " + ex.Message);
                }

                AssertEqual(assignBefore, TokenProbe.IsPrivilegeEnabled(assignToken));
                AssertEqual(quotaBefore, TokenProbe.IsPrivilegeEnabled(quotaToken));
                Notice("Elev_StandardUserLaunch_DoesNotLeavePrivilegesEnabled：调用前后 "
                       + assignToken + "=" + assignBefore + " / " + quotaToken + "=" + quotaBefore
                       + "（未留下进程级特权残留）");
            }
            finally
            {
                KillAndDisposeQuietly(runner);
            }
        }

        /// <summary>为传输方式用例造一份 spec；envValueLength &gt; 0 时塞一个长环境覆盖把 Base64 撑大。</summary>
        private static LaunchSpec NewSpecForTransportTest(string args, int envValueLength)
        {
            string nonce = Guid.NewGuid().ToString("N");
            var spec = new LaunchSpec
            {
                V = 1,
                Nonce = nonce,
                Pipe = "gostgui." + nonce,
                Program = CmdPath(),
                Arguments = args,
                WorkingDirectory = Path.GetTempPath(),
                Kind = ChildKind.Executable,
                EnvOverrides = EmptyEnv()
            };

            if (envValueLength > 0)
            {
                spec.EnvOverrides["GOSTGUI_LONGSPEC"] = new string('x', envValueLength);
            }

            return spec;
        }

        private static bool ContainsSid(List<SecurityIdentifier> sids, SecurityIdentifier expected)
        {
            foreach (SecurityIdentifier sid in sids)
            {
                if (sid != null && sid.Equals(expected))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>管道 DACL 里是否有“指定主体全控（Allow）”的规则。</summary>
        private static bool HasFullControlPipeRule(PipeSecurity security, SecurityIdentifier expected)
        {
            foreach (AuthorizationRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var pipeRule = rule as PipeAccessRule;
                if (pipeRule == null || pipeRule.AccessControlType != AccessControlType.Allow)
                {
                    continue;
                }

                if (pipeRule.IdentityReference.Equals(expected) &&
                    (pipeRule.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl)
                {
                    return true;
                }
            }

            return false;
        }

        private static void KillAndDisposeQuietly(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // 用例收尾：进程可能已经退出或句柄失效，清理尽力而为。
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private static void AssertDecideMode(bool launcherElevated, bool runAsAdmin,
            RunnerLaunchMode? expected, string scenario)
        {
            RunnerLaunchMode? actual = ProcessManager.DecideMode(launcherElevated, runAsAdmin);
            Assert(actual == expected,
                $"权限矩阵选路错误（{scenario}）：期望 {FormatMode(expected)}，实际 {FormatMode(actual)}");
        }

        private static string FormatMode(RunnerLaunchMode? mode)
        {
            return mode.HasValue ? mode.Value.ToString() : "直连";
        }

        // 测试专用 interop：本文件里的 TokenProbe / PipeLabelProbe / ConsoleOutput.CreateFile 探针
        // 刻意留在自检套件内、不从 Native\ 暴露，避免生产程序集提供任何测试钩子。
        /// <summary>用例专用的令牌探针：与 ElevationManager 的实现各自独立声明同一组 P/Invoke。</summary>
        private static class TokenProbe
        {
            private const int TokenElevationClass = 20;
            private const int TokenPrivilegesClass = 3;
            private const uint TokenQuery = 0x0008;
            private const uint ProcessQueryLimitedInformation = 0x1000;

            /// <summary>SE_PRIVILEGE_ENABLED：特权已启用。</summary>
            private const uint SePrivilegeEnabled = 0x00000002;

            [StructLayout(LayoutKind.Sequential)]
            private struct TokenElevation
            {
                public int TokenIsElevated;
            }

            public static bool IsProcessElevated()
            {
                return ReadTokenElevation(GetCurrentProcess()) ?? false;
            }

            /// <summary>
            /// 查询指定进程的令牌是否提权。进程已退出、拒绝访问或查询失败时返回 null——
            /// 调用方必须把 null 当作“证据不足”，不能当成“未提权”。
            /// </summary>
            public static bool? TryIsProcessElevated(int pid)
            {
                IntPtr process = IntPtr.Zero;
                try
                {
                    process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                    if (process == IntPtr.Zero)
                    {
                        return null;
                    }

                    return ReadTokenElevation(process);
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    if (process != IntPtr.Zero)
                    {
                        CloseHandle(process);
                    }
                }
            }

            /// <summary>读令牌的 TokenElevation；查询失败返回 null。不关闭传入的进程句柄。</summary>
            private static bool? ReadTokenElevation(IntPtr process)
            {
                IntPtr token = IntPtr.Zero;
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    if (!OpenProcessToken(process, TokenQuery, out token))
                    {
                        return null;
                    }

                    int size = Marshal.SizeOf(typeof(TokenElevation));
                    buffer = Marshal.AllocHGlobal(size);
                    uint returned;
                    if (!GetTokenInformation(token, TokenElevationClass, buffer, (uint)size, out returned))
                    {
                        return null;
                    }

                    var elevation = (TokenElevation)Marshal.PtrToStructure(buffer, typeof(TokenElevation));
                    return elevation.TokenIsElevated != 0;
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    if (token != IntPtr.Zero)
                    {
                        CloseHandle(token);
                    }
                }
            }

            /// <summary>
            /// 读本进程令牌里某个特权当前是否**已启用**（SE_PRIVILEGE_ENABLED）。查询失败返回 false。
            /// 用于核查降权路径“临时启用特权后必须恢复”的纪律。
            /// </summary>
            public static bool IsPrivilegeEnabled(string privilegeName)
            {
                long targetLuid;
                if (string.IsNullOrEmpty(privilegeName) || !LookupPrivilegeValue(null, privilegeName, out targetLuid))
                {
                    return false;
                }

                IntPtr token = IntPtr.Zero;
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                    {
                        return false;
                    }

                    uint needed;
                    GetTokenInformation(token, TokenPrivilegesClass, IntPtr.Zero, 0, out needed);
                    if (needed == 0)
                    {
                        return false;
                    }

                    buffer = Marshal.AllocHGlobal((int)needed);
                    if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, needed, out needed))
                    {
                        return false;
                    }

                    // TOKEN_PRIVILEGES：DWORD PrivilegeCount，随后是 PrivilegeCount 个 LUID_AND_ATTRIBUTES（12 字节）。
                    int count = Marshal.ReadInt32(buffer);
                    for (int i = 0; i < count; i++)
                    {
                        int offset = 4 + i * 12;
                        long luid = Marshal.ReadInt64(buffer, offset);
                        uint attributes = (uint)Marshal.ReadInt32(buffer, offset + 8);
                        if (luid == targetLuid && (attributes & SePrivilegeEnabled) != 0)
                        {
                            return true;
                        }
                    }

                    return false;
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    if (token != IntPtr.Zero)
                    {
                        CloseHandle(token);
                    }
                }
            }

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr OpenProcess(uint desiredAccess,
                [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information,
                uint informationLength, out uint returnLength);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool LookupPrivilegeValue(string systemName, string name, out long luid);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);
        }

        /// <summary>
        /// 用例专用的管道强制标签读回探针：与生产写入路径各自独立声明 P/Invoke，
        /// 用 GetSecurityInfo(LABEL_SECURITY_INFORMATION) + SDDL 转换证明标签真的落在管道对象上。
        /// </summary>
        private static class PipeLabelProbe
        {
            private const int SeKernelObject = 6;
            private const uint LabelSecurityInformation = 0x00000010;
            private const uint SddlRevision1 = 1;

            public static string ReadLabelSddl(IntPtr pipeHandle)
            {
                IntPtr owner = IntPtr.Zero;
                IntPtr group = IntPtr.Zero;
                IntPtr dacl = IntPtr.Zero;
                IntPtr sacl = IntPtr.Zero;
                IntPtr descriptor = IntPtr.Zero;
                IntPtr text = IntPtr.Zero;
                try
                {
                    uint result = GetSecurityInfo(pipeHandle, SeKernelObject, LabelSecurityInformation,
                        out owner, out group, out dacl, out sacl, out descriptor);
                    if (result != 0 || descriptor == IntPtr.Zero)
                    {
                        return null;
                    }

                    uint textLength;
                    if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, SddlRevision1,
                            LabelSecurityInformation, out text, out textLength) || text == IntPtr.Zero)
                    {
                        return null;
                    }

                    return Marshal.PtrToStringUni(text);
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    if (text != IntPtr.Zero)
                    {
                        LocalFree(text);
                    }

                    if (descriptor != IntPtr.Zero)
                    {
                        LocalFree(descriptor);
                    }
                }
            }

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
                out IntPtr psidOwner, out IntPtr psidGroup, out IntPtr pDacl, out IntPtr pSacl,
                out IntPtr ppSecurityDescriptor);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
                IntPtr securityDescriptor, uint stringSecurityDescriptorRevision, uint securityInformation,
                out IntPtr stringSecurityDescriptor, out uint stringSecurityDescriptorLength);

            [DllImport("kernel32.dll")]
            private static extern IntPtr LocalFree(IntPtr handle);
        }

        /// <summary>
        /// R12 的 `&gt;` 比较边界：环境块恰好 32767 字节必须接受，32768 字节必须拒绝。
        /// 先跑拒绝分支（守卫不触发规范化），再用同一父环境口径跑接受分支（它才会触发一次性规范化）。
        /// </summary>
        private static void ChildEnvBlockSizeBoundary()
        {
            const int limit = 32767;
            string key = "GOSTGUI_SELFTEST_BOUNDARY_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            long parentSize = ParentEnvironmentBlockSize();
            int atLimitLength = (int)(limit - parentSize - (key.Length + 2));
            Assert(atLimitLength > 0, "父环境过大，构造不出 32767 边界夹具（父环境块 " + parentSize + " 字节）");

            // 32768：拒绝，错误文本应报告真实大小与上限；守卫在规范化之前，不应产生日志。
            var tooBigPsi = new ProcessStartInfo("cmd.exe");
            var tooBigLogs = new List<string>();
            string error;
            ChildEnvironment.Apply(tooBigPsi,
                new[] { new KeyValuePair<string, string>(key, new string('x', atLimitLength + 1)) },
                tooBigLogs.Add, out error);
            Assert(!string.IsNullOrEmpty(error), "32768 字节必须被拒绝");
            Assert(error.Contains("32768"), "错误信息应报告实际大小 32768：" + error);
            Assert(error.Contains("32767"), "错误信息应点名上限 32767：" + error);
            AssertEqual(0, tooBigLogs.Count);

            // 恰好 32767：接受（error 为空）。本分支会触发一次性的父环境规范化，所以用例登记在 Direct 之后。
            var atLimitPsi = new ProcessStartInfo("cmd.exe");
            var atLimitLogs = new List<string>();
            ChildEnvironment.Apply(atLimitPsi,
                new[] { new KeyValuePair<string, string>(key, new string('x', atLimitLength)) },
                atLimitLogs.Add, out error);
            AssertEqual("", error);
        }

        // 中转进程用例夹具：runner 由 post-build 复制到 UI 输出目录，路径与 gostGUI.exe 同目录。

        private const int RunnerWaitTimeoutMs = 15000;

        /// <summary>
        /// 真实拉起 gostGUI.Runner.exe：它应按 --spec 连上命名管道，转发 cmd 的 stdout/stderr，
        /// 发 exit(0) 后自行退出，并让子进程随之消失。每一步等待都有 15s 上限，失败时释放全部句柄。
        /// </summary>
        private static void RunnerRelaysStdoutStderrAndExitCode()
        {
            string runnerPath = Path.Combine(Common.GetApplicationPath(), "gostGUI.Runner.exe");

            // R26/Task 14：套件从构建输出目录运行，runner 缺失就是打包回归，必须失败而不是 SKIP。
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            string pipeName = "gostgui." + Guid.NewGuid().ToString("N");
            string nonce = Guid.NewGuid().ToString("N");
            var spec = new LaunchSpec
            {
                Nonce = nonce,
                Pipe = pipeName,
                Program = CmdPath(),
                Arguments = "/d /s /c \"echo hello & echo bye 1>&2\"",
                WorkingDirectory = Path.GetTempPath(),
                Kind = ChildKind.Executable
            };

            var frames = new List<IpcMessage>();
            var frameGate = new object();
            var exitReceived = new ManualResetEventSlim(false);
            NamedPipeServerStream server = null;
            Process runner = null;
            Task connectionTask = null;
            Task readerTask = null;
            StreamReader reader = null;
            try
            {
                server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                connectionTask = server.WaitForConnectionAsync();

                runner = Process.Start(new ProcessStartInfo
                {
                    FileName = runnerPath,
                    Arguments = "--spec " + LaunchSpecCodec.ToBase64(spec),
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                Assert(runner != null, "中转进程应成功启动");

                // 轮询而不是死等连接：runner 提前退出（参数/协议坏了）时立刻失败，不白等满 15s。
                Assert(WaitUntil(() => connectionTask.IsCompleted || runner.HasExited, RunnerWaitTimeoutMs),
                    "中转进程应在限定时间内连上命名管道");
                string runnerExitHint = runner.HasExited
                    ? "，runner 已退出，退出码 " + runner.ExitCode
                    : "，runner 仍在运行";
                Assert(connectionTask.IsCompleted,
                    "中转进程在连上命名管道前就退出了" + runnerExitHint);
                try
                {
                    connectionTask.Wait();
                }
                catch (Exception ex)
                {
                    throw new SelfTestFailureException("等待命名管道连接失败：" + ex.Message);
                }

                reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, true);

                // 读帧放到后台：主线程只按 exitReceived 做有上限的等待，收尾时关管道即可解除阻塞。
                StreamReader frameReader = reader;
                readerTask = Task.Run(() =>
                {
                    while (true)
                    {
                        IpcMessage message;
                        try
                        {
                            message = IpcFraming.Read(frameReader);
                        }
                        catch (Exception)
                        {
                            // 收尾阶段关闭管道会让阻塞中的 ReadLine 抛异常，这不是用例结论。
                            return;
                        }

                        if (message == null)
                        {
                            return;
                        }

                        lock (frameGate)
                        {
                            frames.Add(message);
                        }

                        if (message.Type == "exit")
                        {
                            exitReceived.Set();
                            return;
                        }
                    }
                });

                Assert(exitReceived.Wait(RunnerWaitTimeoutMs),
                    "应在限定时间内收到 exit 帧：" + DescribeFrames(frames, frameGate));

                List<IpcMessage> received;
                lock (frameGate)
                {
                    received = new List<IpcMessage>(frames);
                }

                Assert(received.Count >= 4, "应至少收到 ready/started/out/exit 四类帧：" + DescribeFrames(received));
                AssertEqual("ready", received[0].Type);
                AssertEqual(nonce, received[0].Nonce);

                int startedIndex = received.FindIndex(m => m.Type == "started");
                Assert(startedIndex > 0, "应收到带子进程 PID 的 started 帧：" + DescribeFrames(received));
                int childPid = received[startedIndex].Pid;
                Assert(childPid > 0, "started 帧应带正数子进程 PID：" + DescribeFrames(received));

                int firstOutIndex = received.FindIndex(m => m.Type == "out");
                Assert(firstOutIndex > startedIndex, "started 必须先于任何 out 帧：" + DescribeFrames(received));

                Assert(received.Exists(m => m.Type == "out" && m.Stream == "o" && ContainsFrameData(m, "hello")),
                    "应收到包含 hello 的 stdout 帧：" + DescribeFrames(received));
                Assert(received.Exists(m => m.Type == "out" && m.Stream == "e" && ContainsFrameData(m, "bye")),
                    "应收到包含 bye 的 stderr 帧：" + DescribeFrames(received));

                // 两条流不能串台。
                Assert(!received.Exists(m => m.Type == "out" && m.Stream == "e" && ContainsFrameData(m, "hello")),
                    "stdout 内容不应从 stderr 通道送达：" + DescribeFrames(received));
                Assert(!received.Exists(m => m.Type == "out" && m.Stream == "o" && ContainsFrameData(m, "bye")),
                    "stderr 内容不应从 stdout 通道送达：" + DescribeFrames(received));

                IpcMessage last = received[received.Count - 1];
                AssertEqual("exit", last.Type);
                AssertEqual(0, last.ExitCode);

                Assert(runner.WaitForExit(RunnerWaitTimeoutMs), "中转进程应在发完 exit 帧后退出");
                Assert(WaitUntil(() => IsProcessGone(childPid), RunnerWaitTimeoutMs),
                    "子进程应随中转进程消失（PID " + childPid + "）");
            }
            finally
            {
                // 顺序：先关管道解除读线程阻塞，再收线程，最后兜底杀 runner（超时时不留孤儿）。
                try
                {
                    server?.Dispose();
                }
                catch (Exception)
                {
                }

                if (readerTask != null)
                {
                    try
                    {
                        readerTask.Wait(5000);
                    }
                    catch (Exception)
                    {
                    }
                }

                try
                {
                    reader?.Dispose();
                }
                catch (Exception)
                {
                }

                if (runner != null)
                {
                    try
                    {
                        if (!runner.HasExited)
                        {
                            runner.Kill();
                        }

                        runner.WaitForExit(5000);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        runner.Dispose();
                    }
                }

                exitReceived.Dispose();
            }
        }

        // UI 侧 RunnerSession 用例：真实建管道、拉起 runner、转发输出并在停止后清理子进程树。

        /// <summary>
        /// R29 的端到端 smoke：经 RunnerSession 跑 cmd 的 hello/bye，断言流别、退出码与子进程清理。
        /// runner exe 缺失按失败处理（R26：构建输出目录里缺 runner 是打包回归，不能 SKIP）。
        /// </summary>
        private static void RunnerSmokeRelaysOutputAndCleansUp()
        {
            string runnerPath = Path.Combine(Common.GetApplicationPath(), "gostGUI.Runner.exe");
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            var request = new LaunchRequest
            {
                FileName = CmdPath(),
                Arguments = "/d /s /c \"echo hello & echo bye 1>&2\"",
                WorkingDirectory = Path.GetTempPath(),
                Kind = ChildKind.Executable,
                EnvOverrides = EmptyEnv()
            };

            var output = new RunnerOutputCollector();
            var exitReceived = new ManualResetEventSlim(false);
            int exitCode = int.MinValue;
            RunnerSession session = null;
            try
            {
                session = RunnerSession.StartAsync(request, RunnerLaunchMode.SameIntegrity, runnerPath,
                    Common.GetApplicationPath(), CancellationToken.None).GetAwaiter().GetResult();
                session.Output += output.Add;
                session.Exited += code =>
                {
                    exitCode = code;
                    exitReceived.Set();
                };

                Assert(session.ChildPid != 0, "started 帧应给出正数子进程 PID");
                Assert(exitReceived.Wait(RunnerWaitTimeoutMs),
                    "应在 15 秒内收到 Exited：" + output.Describe());
                AssertEqual(0, exitCode);
                Assert(output.Has(LogKind.Stdout, "hello"), "stdout 应包含 hello：" + output.Describe());
                Assert(output.Has(LogKind.Stderr, "bye"), "stderr 应包含 bye：" + output.Describe());
                Assert(!output.Has(LogKind.Stdout, "bye"), "stderr 内容不应从 stdout 通道送达：" + output.Describe());
                Assert(!output.Has(LogKind.Stderr, "hello"), "stdout 内容不应从 stderr 通道送达：" + output.Describe());
                Assert(WaitUntil(() => IsProcessGone(session.ChildPid), RunnerWaitTimeoutMs),
                    "子进程应随会话结束消失（PID " + session.ChildPid + "）");
            }
            finally
            {
                if (session != null)
                {
                    session.Dispose();
                }

                exitReceived.Dispose();
            }
        }

        /// <summary>
        /// R27 的真实 job-kill 用例：启动 ping 常驻子进程，StopAsync 超时前必须靠 stop 帧结束整棵树。
        /// 断言 Exited 已触发、子进程 PID 在限定时间内消失，并记录 StopAsync 用时。
        /// </summary>
        private static void RunnerSmokeStopKillsChildTree()
        {
            string runnerPath = Path.Combine(Common.GetApplicationPath(), "gostGUI.Runner.exe");
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            var request = new LaunchRequest
            {
                FileName = CmdPath(),
                Arguments = "/d /s /c \"ping -n 60 127.0.0.1\"",
                WorkingDirectory = Path.GetTempPath(),
                Kind = ChildKind.Executable,
                EnvOverrides = EmptyEnv()
            };

            var exitReceived = new ManualResetEventSlim(false);
            RunnerSession session = null;
            try
            {
                session = RunnerSession.StartAsync(request, RunnerLaunchMode.SameIntegrity, runnerPath,
                    Common.GetApplicationPath(), CancellationToken.None).GetAwaiter().GetResult();
                int childPid = session.ChildPid;
                Assert(childPid != 0, "started 帧应给出正数子进程 PID");
                session.Exited += code => exitReceived.Set();

                var stopwatch = Stopwatch.StartNew();
                session.StopAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
                stopwatch.Stop();

                Assert(exitReceived.Wait(RunnerWaitTimeoutMs), "StopAsync 返回后应已触发 Exited");
                Assert(WaitUntil(() => IsProcessGone(childPid), RunnerWaitTimeoutMs),
                    "停止后子进程应从系统消失（PID " + childPid + "）");
                Notice("Runner_Smoke_StopKillsChildTree：StopAsync 用时 " + stopwatch.ElapsedMilliseconds + " ms");
            }
            finally
            {
                if (session != null)
                {
                    session.Dispose();
                }

                exitReceived.Dispose();
            }
        }

        /// <summary>
        /// Ruling 3 的 --spec-file 兜底：把 spec 撑到 Base64 超过 Windows 命令行上限（32767 字符），
        /// 只有临时文件路径能启动成功；同时验证大环境覆盖仍然生效、临时文件不残留。
        /// </summary>
        private static void RunnerSpecFileFallbackRelaysOutput()
        {
            string runnerPath = Path.Combine(Common.GetApplicationPath(), "gostGUI.Runner.exe");
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            // 按本机父环境块大小反推一个“环境块不超限、但 JSON 的 Base64 超过命令行上限”的值长度。
            const string bigKey = "GOSTGUI_SMOKE_BIG";
            long parentSize = ParentEnvironmentBlockSize();
            int valueLength = (int)(32767 - parentSize - (bigKey.Length + 2) - 512);
            Assert(valueLength > 0, "父环境过大，构造不出 --spec-file 夹具（父环境块 " + parentSize + " 字节）");

            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { bigKey, new string('x', valueLength) }
            };

            var request = new LaunchRequest
            {
                FileName = CmdPath(),
                Arguments = "/d /s /c \"echo specfile\"",
                WorkingDirectory = Path.GetTempPath(),
                Kind = ChildKind.Executable,
                EnvOverrides = overrides
            };

            // 探针 spec 与 StartAsync 内部构造的 spec 字段长度一致（nonce/pipe 都是 32 字符 GUID），
            // 用它证明这个夹具确实只能走 --spec-file，而不是一条假的普通 smoke。
            var probe = new LaunchSpec
            {
                Nonce = new string('n', 32),
                Pipe = "gostgui." + new string('p', 32),
                Program = request.FileName,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                Kind = request.Kind,
                EnvOverrides = overrides
            };
            Assert(LaunchSpecCodec.NeedsTempFile(probe), "夹具的 spec 必须超过 8000 字符");
            int base64Length = LaunchSpecCodec.ToBase64(probe).Length;
            Assert(base64Length > 32767,
                "夹具的 spec Base64 长度 " + base64Length + " 未超过 Windows 命令行上限 32767，无法证明走了 --spec-file");

            var output = new RunnerOutputCollector();
            var exitReceived = new ManualResetEventSlim(false);
            int exitCode = int.MinValue;
            RunnerSession session = null;
            try
            {
                session = RunnerSession.StartAsync(request, RunnerLaunchMode.SameIntegrity, runnerPath,
                    Common.GetApplicationPath(), CancellationToken.None).GetAwaiter().GetResult();
                session.Output += output.Add;
                session.Exited += code =>
                {
                    exitCode = code;
                    exitReceived.Set();
                };

                Assert(session.ChildPid != 0, "started 帧应给出正数子进程 PID");
                Assert(exitReceived.Wait(RunnerWaitTimeoutMs),
                    "应在 15 秒内收到 Exited：" + output.Describe());
                AssertEqual(0, exitCode);
                Assert(output.Has(LogKind.Stdout, "specfile"),
                    "大 spec 经 --spec-file 转发的 stdout 应包含 specfile：" + output.Describe());
                Assert(WaitUntil(() => IsProcessGone(session.ChildPid), RunnerWaitTimeoutMs),
                    "子进程应随会话结束消失（PID " + session.ChildPid + "）");
            }
            finally
            {
                if (session != null)
                {
                    session.Dispose();
                }

                exitReceived.Dispose();
            }
        }

        /// <summary>
        /// R29 的强制中转选路：GOSTGUI_FORCE_RUNNER=1 时 ProcessManager 必须走 RunnerSession，
        /// 输出/退出事件照旧送达，StopProcess 结束后子进程与 runner 都不得残留。
        /// </summary>
        private static void RunnerForceRunnerRoutingRelaysAndStops()
        {
            string runnerPath = Path.Combine(Common.GetApplicationPath(), "gostGUI.Runner.exe");
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            string previous = Environment.GetEnvironmentVariable("GOSTGUI_FORCE_RUNNER");
            ProcessManager manager = null;
            var output = new RunnerOutputCollector();
            var exitReceived = new ManualResetEventSlim(false);
            int exitCode = int.MinValue;
            try
            {
                Assert(WaitUntil(() => CountRunnerProcesses() == 0, RunnerWaitTimeoutMs),
                    "强制 runner 用例开始前不应有残留的中转进程");

                Environment.SetEnvironmentVariable("GOSTGUI_FORCE_RUNNER", "1");
                manager = new ProcessManager();
                manager.OutputReceived += (item, kind, text) => output.Add(kind, text);
                manager.ProcessExited += (item, code) =>
                {
                    exitCode = code;
                    exitReceived.Set();
                };

                var configItem = new ConfigItem
                {
                    Name = "强制中转",
                    Enable = true,
                    Program = CmdPath(),
                    Args = "/d /s /c \"echo forced & echo routed 1>&2 & ping -n 60 127.0.0.1 >nul\"",
                    WorkDir = Path.GetTempPath()
                };

                Assert(manager.StartProcess(configItem), "GOSTGUI_FORCE_RUNNER=1 时应通过中转进程启动");
                int childPid = manager.GetChildPid(configItem);
                Assert(childPid != 0, "强制 runner 路径的 GetChildPid 应返回子进程 PID");
                Assert(manager.IsProcessRunning(configItem), "启动后应报告运行中");
                Assert(WaitUntil(() => CountRunnerProcesses() > 0, RunnerWaitTimeoutMs),
                    "应观察到中转进程在运行");
                Assert(WaitUntil(() => output.Has(LogKind.System, "中转进程身份"), RunnerWaitTimeoutMs),
                    "中转启动日志应包含 runner 的实际身份：" + output.Describe());
                Assert(output.Has(LogKind.System, "继承自当前进程"),
                    "同权限中转启动日志应说明基础环境继承自当前进程：" + output.Describe());
                Assert(WaitUntil(() => output.Has(LogKind.Stdout, "forced"), RunnerWaitTimeoutMs),
                    "强制 runner 路径应转发 stdout：" + output.Describe());
                Assert(WaitUntil(() => output.Has(LogKind.Stderr, "routed"), RunnerWaitTimeoutMs),
                    "强制 runner 路径应转发 stderr：" + output.Describe());

                manager.StopProcess(configItem);
                Assert(exitReceived.Wait(RunnerWaitTimeoutMs), "StopProcess 后应收到 ProcessExited");
                Assert(exitCode != int.MinValue, "ProcessExited 应带退出码");
                Assert(!manager.IsProcessRunning(configItem), "停止后不应报告运行中");
                Assert(WaitUntil(() => IsProcessGone(childPid), RunnerWaitTimeoutMs),
                    "停止后子进程应消失（PID " + childPid + "）");
                Assert(WaitUntil(() => CountRunnerProcesses() == 0, RunnerWaitTimeoutMs),
                    "停止后中转进程应退出");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GOSTGUI_FORCE_RUNNER", previous);
                exitReceived.Dispose();
                if (manager != null)
                {
                    manager.Dispose();
                }
            }
        }

        /// <summary>当前机器上名为 gostGUI.Runner 的进程数；用完立即释放 Process 句柄。</summary>
        private static int CountRunnerProcesses()
        {
            Process[] processes = Process.GetProcessesByName("gostGUI.Runner");
            try
            {
                return processes.Length;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// 评审 CRITICAL 的回归用例：提权 UI（High IL）创建的命名管道默认带 High 强制标签 + NO_WRITE_UP，
        /// 降权 runner（Medium IL）读写打开会被拒绝（runner 退出码 4）。生产函数必须把强制标签改成 Medium。
        /// 这里实际调用 <see cref="RunnerSession.CreatePipeServer"/> 建管道，再用独立 P/Invoke 读回标签，
        /// 断言 SDDL 含 <c>S:(ML;;NW;;;ME)</c>；普通与降权两种 DACL 口径都要带标签。
        /// </summary>
        private static void RunnerPipeHasMediumIntegrityLabel()
        {
            foreach (bool grantInteractiveUser in new[] { false, true })
            {
                string pipeName = "gostgui.label." + Guid.NewGuid().ToString("N");
                using (NamedPipeServerStream server = RunnerSession.CreatePipeServer(pipeName, grantInteractiveUser))
                {
                    string sddl = PipeLabelProbe.ReadLabelSddl(server.SafePipeHandle.DangerousGetHandle());
                    Assert(!string.IsNullOrEmpty(sddl),
                        "应能读回命名管道的强制标签 SDDL（grantInteractiveUser=" + grantInteractiveUser + "）");
                    Assert(sddl.Contains("S:(ML;;NW;;;ME)"),
                        "命名管道必须带中等完整性标签 S:(ML;;NW;;;ME)，实际：" + sddl);
                    Assert(!sddl.Contains("HI"), "命名管道不能保留 High 强制标签：" + sddl);
                }
            }

            Notice("Runner_PipeHasMediumIntegrityLabel：生产管道（两种 DACL 口径）读回 S:(ML;;NW;;;ME)");
        }

        /// <summary>
        /// 评审 IMPORTANT 2 的回归用例：runner 在连上管道前就死掉时，必须快速失败并带出真实退出码，
        /// 不能让用户看 15/60 秒的“管道连接超时”。用 whoami.exe 冒充中转进程——它对 --spec 参数
        /// 立即报错退出（退出码 1），绝不会连上管道。
        /// </summary>
        private static void RunnerDeadRelayFailsFast()
        {
            string fakeRunner = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
            Assert(File.Exists(fakeRunner), "找不到 whoami.exe，无法构造“死掉的中转进程”夹具：" + fakeRunner);

            var request = new LaunchRequest
            {
                FileName = CmdPath(),
                Arguments = "/d /s /c \"exit 0\"",
                WorkingDirectory = Path.GetTempPath(),
                Kind = ChildKind.Executable,
                EnvOverrides = EmptyEnv()
            };

            Exception failure = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                RunnerSession.StartAsync(request, RunnerLaunchMode.SameIntegrity, fakeRunner,
                    Common.GetApplicationPath(), CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                stopwatch.Stop();
            }

            Assert(failure != null, "冒充 runner 的进程立即离开后 StartAsync 必须失败");
            Assert(!(failure is TimeoutException), "死掉的中转进程不能被报成连接超时：" + failure.Message);
            Assert(stopwatch.ElapsedMilliseconds < 5000,
                "死掉的中转进程必须在连接超时（15s）前快速失败，实际 " + stopwatch.ElapsedMilliseconds + " ms");
            Assert(failure.Message.Contains("中转进程已退出"),
                "快速失败消息应说明中转进程已退出：" + failure.Message);
            Assert(failure.Message.Contains("错误码"), "快速失败消息应带出 runner 的退出码：" + failure.Message);
            Assert(failure.Message.Contains("未能连接管道"),
                "快速失败消息应说明未能连接管道：" + failure.Message);
            Notice("Runner_DeadRelayFailsFast：runner 立即退出后 " + stopwatch.ElapsedMilliseconds
                   + " ms 内失败：" + failure.Message);
        }

        /// <summary>
        /// 评审 IMPORTANT 3 的回归用例：每个中转会话都必须能报告 runner 的真实身份与子进程基础环境来源。
        /// 同权限路径的身份就是当前用户；三种基础环境文案逐字固定（设计决策 13 / §6.4）。
        /// </summary>
        private static void RunnerIdentityAndBaseEnvironmentAreReported()
        {
            AssertEqual("继承自提权令牌", RunnerSession.DescribeBaseEnvironment(RunnerLaunchMode.Elevated));
            AssertEqual("继承自标准用户令牌", RunnerSession.DescribeBaseEnvironment(RunnerLaunchMode.StandardUser));
            AssertEqual("继承自当前进程", RunnerSession.DescribeBaseEnvironment(RunnerLaunchMode.SameIntegrity));

            string runnerPath = RunnerPath();
            Assert(File.Exists(runnerPath), "找不到中转进程：" + runnerPath + "（请先构建解决方案）");

            var request = new LaunchRequest
            {
                FileName = CmdPath(),
                Arguments = "/d /s /c \"echo identity\"",
                WorkingDirectory = Path.GetTempPath(),
                Kind = ChildKind.Executable,
                EnvOverrides = EmptyEnv()
            };

            RunnerSession session = null;
            var exitReceived = new ManualResetEventSlim(false);
            try
            {
                session = RunnerSession.StartAsync(request, RunnerLaunchMode.SameIntegrity, runnerPath,
                    Common.GetApplicationPath(), CancellationToken.None).GetAwaiter().GetResult();
                session.Exited += _ => exitReceived.Set();

                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    // 身份必须真的是账号名，不能悄悄回落到“无法读取身份”兜底（IMPORTANT 3 的回归点）。
                    Assert(!string.Equals(RunnerSession.UnknownRunnerIdentity, session.RunnerIdentity,
                        StringComparison.Ordinal), "中转身份不能回落到“无法读取身份”兜底：" + session.RunnerIdentity);
                    Assert(string.Equals(identity.Name, session.RunnerIdentity, StringComparison.OrdinalIgnoreCase),
                        "同权限 runner 的身份应等于当前用户（" + identity.Name + "），实际：" + session.RunnerIdentity);
                }

                AssertEqual("继承自当前进程", session.BaseEnvironmentSource);
                Assert(exitReceived.Wait(RunnerWaitTimeoutMs), "identity 用例的子进程应在限定时间内结束");
                // 与 ProcessManager 写进每项日志的内容逐字同形，便于在 selftest.log 里直接核对真实账号。
                Notice("中转进程身份：" + session.RunnerIdentity
                       + "；子进程基础环境：" + session.BaseEnvironmentSource + "。");
            }
            finally
            {
                if (session != null)
                {
                    session.Dispose();
                }

                exitReceived.Dispose();
            }
        }

        /// <summary>收集中转会话的输出行；事件来自读帧线程，必须加锁。</summary>
        private sealed class RunnerOutputCollector
        {
            private readonly object _gate = new object();
            private readonly List<KeyValuePair<LogKind, string>> _lines =
                new List<KeyValuePair<LogKind, string>>();

            public void Add(LogKind kind, string text)
            {
                lock (_gate)
                {
                    _lines.Add(new KeyValuePair<LogKind, string>(kind, text ?? ""));
                }
            }

            public bool Has(LogKind kind, string fragment)
            {
                lock (_gate)
                {
                    foreach (KeyValuePair<LogKind, string> line in _lines)
                    {
                        if (line.Key == kind && line.Value.Contains(fragment))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public string Describe()
            {
                lock (_gate)
                {
                    if (_lines.Count == 0)
                    {
                        return "（没有收到任何输出）";
                    }

                    var builder = new StringBuilder();
                    foreach (KeyValuePair<LogKind, string> line in _lines)
                    {
                        builder.Append('[').Append(line.Key).Append("] ")
                            .Append(line.Value.Trim()).Append("；");
                    }

                    return builder.ToString();
                }
            }
        }

        /// <summary>失败诊断：把已收到的帧按类型/流别/负载拼成一行。</summary>
        private static string DescribeFrames(List<IpcMessage> frames, object gate)
        {
            lock (gate)
            {
                return DescribeFrames(new List<IpcMessage>(frames));
            }
        }

        private static string DescribeFrames(List<IpcMessage> frames)
        {
            if (frames == null || frames.Count == 0)
            {
                return "（没有收到任何帧）";
            }

            var builder = new StringBuilder();
            foreach (IpcMessage frame in frames)
            {
                builder.Append('[').Append(frame.Type);
                if (!string.IsNullOrEmpty(frame.Stream))
                {
                    builder.Append('/').Append(frame.Stream);
                }

                if (!string.IsNullOrEmpty(frame.Data))
                {
                    builder.Append(' ').Append(frame.Data.Trim());
                }

                if (frame.Pid != 0)
                {
                    builder.Append(" pid=").Append(frame.Pid);
                }

                if (frame.Type == "exit")
                {
                    builder.Append(" code=").Append(frame.ExitCode);
                }

                builder.Append("] ");
            }

            return builder.ToString();
        }

        private static bool ContainsFrameData(IpcMessage message, string fragment)
        {
            return message != null && message.Data != null &&
                   message.Data.IndexOf(fragment, StringComparison.Ordinal) >= 0;
        }

        /// <summary>PID 是否已从系统消失；进程存在但已退出也算消失。</summary>
        private static bool IsProcessGone(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

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

        /// <summary>
        /// 加载失败后第一次保存前必须把原文件备份成 config.json.bak（只备份一次），
        /// 备份不了就拒绝保存，绝不静默覆盖用户那份解析不了的配置。
        /// </summary>
        private static void ConfigSaveAfterFailedLoadBacksUpOriginal()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                const string corrupt = "{ 这不是合法 JSON ";
                File.WriteAllText(path, corrupt, new UTF8Encoding(false));

                var manager = new ConfigurationManager(path);
                Assert(!string.IsNullOrEmpty(manager.LoadError), "损坏的配置应记录中文加载错误。");
                AssertEqual(path + ".bak", manager.BackupFilePath);
                Assert(!File.Exists(manager.BackupFilePath), "加载阶段不应创建备份。");

                ConfigItem added = manager.AddNewItem();
                Assert(added != null, "AddNewItem 应返回新项。");
                Assert(File.Exists(manager.BackupFilePath), "加载失败后的第一次保存前必须备份原文件。");
                AssertEqual(corrupt, File.ReadAllText(manager.BackupFilePath, Encoding.UTF8));
                AssertEqual("", manager.SaveError);

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "覆盖后的配置应可解析：" + reloaded.Error);
                AssertEqual(1, reloaded.Data.Items.Count);

                // 只备份一次：第二次保存不再覆盖备份文件。
                File.WriteAllText(manager.BackupFilePath, "keep-me", new UTF8Encoding(false));
                manager.AddNewItem();
                AssertEqual("keep-me", File.ReadAllText(manager.BackupFilePath, Encoding.UTF8));
                AssertEqual("", manager.SaveError);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>复制配置项：名字为“原名 副本”（重名加序号），插在源项后面，且不继承运行状态。</summary>
        private static void ConfigDuplicateItemNamesAndInsertsAfterSource()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                var data = new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem
                        {
                            Name = "原始",
                            Enable = true,
                            Program = "gost.exe",
                            Args = "-L 127.0.0.1:8080",
                            WorkDir = "D:\\运行 目录",
                            RunAsAdmin = true,
                            Env = new Dictionary<string, string> { { "GOST_LOG", "debug" } }
                        },
                        new ConfigItem { Name = "第二", Program = "other.exe" }
                    }
                };
                Assert(Common.SaveTo(data, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                ConfigItem source = manager.Items[0];
                ConfigItem copy = manager.DuplicateItem(source);

                Assert(copy != null, "复制应返回新项。");
                AssertEqual("原始 副本", copy.Name);
                AssertEqual(1, new List<ConfigItem>(manager.Items).IndexOf(copy));
                AssertEqual(false, copy.Enable);
                AssertEqual(source.Program, copy.Program);
                AssertEqual(source.Args, copy.Args);
                AssertEqual(source.WorkDir, copy.WorkDir);
                AssertEqual(true, copy.RunAsAdmin);
                Assert(copy.Env != null && copy.Env["GOST_LOG"] == "debug", "复制应带上环境变量。");
                Assert(!ReferenceEquals(source.Env, copy.Env), "环境变量必须是独立副本。");

                ConfigItem second = manager.DuplicateItem(source);
                AssertEqual("原始 副本 2", second.Name);
                Assert(manager.DuplicateItem(null) == null, "复制 null 应返回 null 而不是抛异常。");

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "落盘后的配置应可解析：" + reloaded.Error);
                AssertEqual(4, reloaded.Data.Items.Count);
                // 每个副本都紧跟在源项后面：第二次复制后顺序是 原始 / 原始 副本 2 / 原始 副本 / 第二。
                AssertEqual("原始 副本 2", reloaded.Data.Items[1].Name);
                AssertEqual("原始 副本", reloaded.Data.Items[2].Name);
                AssertEqual("第二", reloaded.Data.Items[3].Name);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>上移/下移改变 ConfigurationManager.Items 的顺序并立即写盘；越界返回 false。</summary>
        private static void ConfigMoveItemPersistsOrder()
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
                Assert(manager.MoveItem(manager.Items[2], -1), "第三项上移应成功。");
                AssertEqual("a", manager.Items[0].Name);
                AssertEqual("c", manager.Items[1].Name);
                AssertEqual("b", manager.Items[2].Name);

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "落盘后的配置应可解析：" + reloaded.Error);
                AssertEqual("c", reloaded.Data.Items[1].Name);
                AssertEqual("b", reloaded.Data.Items[2].Name);

                Assert(!manager.MoveItem(manager.Items[0], -1), "已在最前时上移应返回 false。");
                Assert(!manager.MoveItem(manager.Items[2], 1), "已在最后时下移应返回 false。");
                Assert(!manager.MoveItem(null, 1), "null 项应返回 false。");
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// 数据层不得吞掉保存失败：以目录作为配置路径时 Save() 必然失败，失败必须同时可观测：
        /// 返回 false、SaveError 非空且为中文原因、SaveFailed 事件带同一原因触发一次。
        /// 全程使用绝对临时路径，不碰用户那份 config.json。
        /// </summary>
        private static void ConfigSaveFailureSurfaces()
        {
            string dir = CreateTempDirectory();
            try
            {
                // 目录不能被当作 config.json 写入：临时文件写完后替换/移动目标必然失败。
                string unsavablePath = Path.Combine(dir, "config-as-directory");
                Directory.CreateDirectory(unsavablePath);

                var manager = new ConfigurationManager(unsavablePath);
                AssertEqual("", manager.SaveError);

                int raised = 0;
                string reported = null;
                manager.SaveFailed += message =>
                {
                    raised++;
                    reported = message;
                };

                Assert(!manager.Save(), "以目录为配置路径时 Save() 必须返回 false。");
                Assert(!string.IsNullOrEmpty(manager.SaveError), "保存失败时 SaveError 必须保留原因。");
                Assert(manager.SaveError.Contains("保存"), "SaveError 应为中文保存错误：" + manager.SaveError);
                AssertEqual(1, raised);
                AssertEqual(manager.SaveError, reported);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

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

        /// <summary>INPUT 在 x64 必须是 40 字节、x86 必须是 28 字节；布局错了 SendInput 会拒绝整批输入。</summary>
        private static void NativeInputStructHasExpectedSize()
        {
            int expected = IntPtr.Size == 8 ? 40 : 28;
            AssertEqual(expected, Marshal.SizeOf<INPUT>());
        }

        /// <summary>构造器返回 Alt down + Alt up 两条键盘记录；只读数组，不注入输入。</summary>
        private static void NativeBuildAltKeyInputsReturnsAltDownThenAltUp()
        {
            INPUT[] inputs = NativeMethods.BuildAltKeyInputs();
            AssertEqual(2, inputs.Length);

            AssertEqual(NativeMethods.InputKeyboard, inputs[0].type);
            AssertEqual((ushort)NativeMethods.VkMenu, inputs[0].u.ki.wVk);
            AssertEqual(0u, inputs[0].u.ki.dwFlags);

            AssertEqual(NativeMethods.InputKeyboard, inputs[1].type);
            AssertEqual((ushort)NativeMethods.VkMenu, inputs[1].u.ki.wVk);
            AssertEqual(NativeMethods.KeyeventfKeyUp, inputs[1].u.ki.dwFlags);
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

        /// <summary>夹具缺失/环境不满足时跳过用例，并把跳过原因写进 selftest 结果与控制台。</summary>
        private static void Notice(string message)
        {
            Notices.Add(message);
        }

        public static void Check(string name, Action body)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (body == null) throw new ArgumentNullException(nameof(body));
            Cases.Add(new TestCase(name, body));
        }

        public static void Assert(bool condition, string message = null)
        {
            if (!condition)
            {
                throw new SelfTestFailureException(message ?? "断言失败");
            }
        }

        public static void AssertEqual<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new SelfTestFailureException($"断言失败：期望 <{FormatValue(expected)}>，实际 <{FormatValue(actual)}>");
            }
        }

        public static void AssertThrows<T>(Action body) where T : Exception
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            try
            {
                body();
            }
            catch (T)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new SelfTestFailureException($"断言失败：期望抛出 {typeof(T).Name}，实际抛出 {ex.GetType().Name}: {ex.Message}");
            }

            throw new SelfTestFailureException($"断言失败：期望抛出 {typeof(T).Name}，但没有抛出异常");
        }

        /// <summary>顺序执行全部用例，返回 0 表示全过、1 表示有失败。</summary>
        public static int RunAll()
        {
            var log = new StringBuilder();
            TextWriter console = ConsoleOutput.Open();
            int failed = 0;

            foreach (TestCase testCase in Cases)
            {
                try
                {
                    testCase.Body();
                    WriteLine(log, console, "PASS " + testCase.Name);
                }
                catch (Exception ex)
                {
                    failed++;
                    WriteLine(log, console, "FAIL " + testCase.Name + ": " + DescribeFailure(ex));
                }
            }

            foreach (string notice in Notices)
            {
                WriteLine(log, console, notice);
            }

            WriteLine(log, console, $"共 {Cases.Count} 个用例，失败 {failed} 个");

            if (!TryWriteLog(log.ToString()))
            {
                // 日志是权威输出之一，写不出来就按失败处理。
                WriteLine(console, "FAIL 无法写入日志文件: " + LogPath);
                failed++;
            }

            FlushQuietly(console);
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// 隐藏的 --runner-smoke：只跑端到端中转用例（与 Runner_Smoke_RelaysOutputAndCleansUp 同一段代码），
        /// 退出码 0/1 与套件约定一致，并把同样的 PASS/FAIL 文本写进 selftest.log。
        /// </summary>
        public static int RunRunnerSmoke()
        {
            var log = new StringBuilder();
            TextWriter console = ConsoleOutput.Open();
            int failed = 0;

            try
            {
                RunnerSmokeRelaysOutputAndCleansUp();
                WriteLine(log, console, "PASS Runner_Smoke_RelaysOutputAndCleansUp");
            }
            catch (Exception ex)
            {
                failed++;
                WriteLine(log, console, "FAIL Runner_Smoke_RelaysOutputAndCleansUp: " + DescribeFailure(ex));
            }

            WriteLine(log, console, failed == 0 ? "runner-smoke 通过" : "runner-smoke 失败");

            if (!TryWriteLog(log.ToString()))
            {
                WriteLine(console, "FAIL 无法写入日志文件: " + LogPath);
                failed++;
            }

            FlushQuietly(console);
            return failed == 0 ? 0 : 1;
        }

        private static string LogPath
        {
            get { return Path.Combine(Common.GetApplicationPath(), LogFileName); }
        }

        private static bool TryWriteLog(string text)
        {
            try
            {
                File.WriteAllText(LogPath, text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string DescribeFailure(Exception ex)
        {
            if (ex is SelfTestFailureException)
            {
                return ex.Message;
            }

            return ex.GetType().Name + ": " + ex.Message;
        }

        private static string FormatValue(object value)
        {
            return value == null ? "null" : Convert.ToString(value);
        }

        private static void WriteLine(StringBuilder log, TextWriter console, string line)
        {
            log.AppendLine(line);
            WriteLine(console, line);
        }

        private static void WriteLine(TextWriter console, string line)
        {
            if (console == null) return;

            try
            {
                console.WriteLine(line);
            }
            catch (Exception)
            {
                // 控制台输出只用于人眼查看，不可用就忽略。
            }
        }

        private static void FlushQuietly(TextWriter console)
        {
            if (console == null) return;

            try
            {
                console.Flush();
                console.Dispose();
            }
            catch (Exception)
            {
                // 进程即将退出，刷新失败不影响退出码与日志。
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

        private sealed class TestCase
        {
            public TestCase(string name, Action body)
            {
                Name = name;
                Body = body;
            }

            public string Name { get; }

            public Action Body { get; }
        }

        private sealed class SelfTestFailureException : Exception
        {
            public SelfTestFailureException(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// WinExe 默认没有控制台，Console.Out 不可靠；这里改用 CONOUT$ 打开控制台输出，
        /// 打不开就回落到继承来的标准输出（可能也是空的），失败一律忽略。
        /// </summary>
        private static class ConsoleOutput
        {
            private const uint GenericWrite = 0x40000000;
            private const uint FileShareRead = 0x00000001;
            private const uint FileShareWrite = 0x00000002;
            private const uint OpenExisting = 0x00000003;

            public static TextWriter Open()
            {
                try
                {
                    SafeFileHandle handle = CreateFile(
                        "CONOUT$",
                        GenericWrite,
                        FileShareRead | FileShareWrite,
                        IntPtr.Zero,
                        OpenExisting,
                        0,
                        IntPtr.Zero);

                    if (!handle.IsInvalid)
                    {
                        return new StreamWriter(new FileStream(handle, FileAccess.Write, 1024), ConsoleEncoding())
                        {
                            AutoFlush = true
                        };
                    }
                }
                catch (Exception)
                {
                    // 没有控制台就算了。
                }

                try
                {
                    return Console.Out;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private static Encoding ConsoleEncoding()
            {
                try
                {
                    return Console.OutputEncoding;
                }
                catch (Exception)
                {
                    return Encoding.Default;
                }
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateFile(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);
        }
    }
}

