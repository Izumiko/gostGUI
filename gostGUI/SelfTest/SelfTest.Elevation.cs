// =====================================================================================
// SelfTest.Elevation.cs —— 提权用例：错误码映射、令牌判定、权限矩阵、降权启动与 ACL。
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
    }
}
