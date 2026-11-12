// =====================================================================================
// SelfTest.Core.cs —— 自检骨架：用例注册表、Check/Assert、日志与 SKIP 管道、共享临时目录夹具、入口（RunAll / RunRunnerSmoke）。
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
    /// <summary>
    /// 隐藏的 --selftest 自检骨架：按登记顺序执行用例，结果写到控制台与 selftest.log。
    /// 退出码与 selftest.log 是权威输出，控制台输出只是尽力而为。
    /// </summary>
    internal static partial class SelfTest
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

            // 保存失败契约（Task 4）：Save() 失败必须同时可观测——返回 false、SaveError 是中文原因、
            // SaveFailed 事件带同一原因只触发一次；数据层不得吞掉失败，也不得静默覆盖用户的配置文件。
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
