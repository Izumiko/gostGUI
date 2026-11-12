using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using gostGUI.Native;
using gostGUI.Shared;

namespace gostGUI
{
    /// <summary>
    /// 中转进程的启动模式：SameIntegrity 直接 CreateProcess 同级拉起；Elevated 用 ShellExecuteEx(runas)
    /// 穿过 UAC；StandardUser 借 explorer.exe 的标准用户令牌降权拉起。选路由 ProcessManager 的权限矩阵决定。
    /// </summary>
    public enum RunnerLaunchMode
    {
        SameIntegrity,
        Elevated,
        StandardUser
    }

    /// <summary>
    /// UI 侧的中转会话：建带显式 ACL 的命名管道服务器、拉起 gostGUI.Runner.exe、完成 ready/started 握手，
    /// 再把 out / error / exit 帧翻译成 <see cref="Output"/> / <see cref="Exited"/> 事件。
    /// 事件在首个订阅者出现前先缓冲：会话启动完成与调用方挂事件之间即使有帧到达，也不会丢行或丢退出码。
    /// </summary>
    public sealed class RunnerSession : IDisposable
    {
        /// <summary>普通（同权限）场景下等待 runner 连上管道的上限。</summary>
        private const int ConnectTimeoutMs = 15000;

        /// <summary>
        /// 跨权限场景（提权 / 降权）的连线上限，比同权限路径宽裕。
        /// 注意它**不**覆盖用户在 UAC 弹窗里做决定的时间：ShellExecuteEx 同步阻塞到用户点完为止，
        /// 这里只覆盖 runner 启动之后连上管道的时间（R35）。
        /// </summary>
        private const int CrossIntegrityConnectTimeoutMs = 60000;

        /// <summary>
        /// 降权路径（CreateProcessWithTokenW）的 lpCommandLine 上限。文档写的是 1024 字符（创建者不同，
        /// CreateProcessAsUser 才是 32767），这里留 24 字符余量按 1000 判定：超过就必须改走 --spec-file。
        /// </summary>
        internal const int StandardUserCommandLineLimit = 1000;

        /// <summary>ready + started 两个握手帧共用的总上限。</summary>
        private const int HandshakeTimeoutMs = 15000;

        /// <summary>stop 超时并杀掉 runner 后，等待读线程报 EOF 的兜底上限。</summary>
        private const int ForceKillWaitMs = 3000;

        /// <summary>正常收到 exit 帧后，等待 runner 自己退出的上限。</summary>
        private const int RunnerExitWaitMs = 2000;

        /// <summary>连接等待期间轮询 runner 是否已退出的间隔：死掉的中转进程必须尽快失败，不能等满 15/60 秒。</summary>
        private const int RunnerExitPollMs = 50;

        /// <summary>中等完整性级别 SID（S-1-16-8192），管道强制标签的目标。</summary>
        private const string MediumIntegritySid = "S-1-16-8192";

        /// <summary>读不到 runner 令牌身份时的固定中文兜底。</summary>
        internal const string UnknownRunnerIdentity = "无法读取身份";

        private readonly NamedPipeServerStream _pipe;
        private readonly Process _runner;
        private readonly StreamReader _reader;
        private readonly TaskCompletionSource<int> _exitSource = new TaskCompletionSource<int>();
        private readonly int _childPid;
        private readonly string _runnerIdentity;
        private readonly string _baseEnvironmentSource;

        private readonly object _writeGate = new object();
        private readonly object _outputEventGate = new object();
        private readonly object _exitEventGate = new object();
        private readonly object _runnerGate = new object();
        private readonly object _transportGate = new object();
        private readonly object _stopGate = new object();

        private readonly List<KeyValuePair<LogKind, string>> _pendingOutput =
            new List<KeyValuePair<LogKind, string>>();

        private Action<LogKind, string> _outputHandlers;
        private Action<int> _exitHandlers;
        private bool _outputReplayDone;
        private bool _exitReplayDone;
        private bool _pendingExit;
        private int _pendingExitCode;
        private int _exitRaised;
        private int _eofNoticeRaised;
        private int _stopTimedOut;
        private int _disposed;
        private bool _transportDisposed;
        private bool _runnerHandleDisposed;
        private Task _stopTask;

        private RunnerSession(NamedPipeServerStream pipe, Process runner, StreamReader reader, int childPid,
            RunnerLaunchMode mode)
        {
            _pipe = pipe;
            _runner = runner;
            _reader = reader;
            _childPid = childPid;
            _baseEnvironmentSource = DescribeBaseEnvironment(mode);
            // 尽力读取 runner 的真实身份：UAC 可能把它换成了另一个管理员账号，日志必须写实际值而不是 UI 令牌。
            _runnerIdentity = ReadProcessUserName(runner) ?? UnknownRunnerIdentity;
        }

        /// <summary>runner 上报的子进程 PID；握手阶段拿不到正数 PID 时 StartAsync 会直接失败。</summary>
        public int ChildPid
        {
            get { return _childPid; }
        }

        /// <summary>
        /// runner 进程令牌里的实际身份（域\用户名）；打不开令牌或无法映射账号时固定为
        /// <see cref="UnknownRunnerIdentity"/>（“无法读取身份”），绝不冒充 UI 令牌的身份。
        /// </summary>
        public string RunnerIdentity
        {
            get { return _runnerIdentity; }
        }

        /// <summary>
        /// 子进程继承的基础环境来源（设计决策 13 / §6.4）：提权路径是提权令牌，降权路径是标准用户令牌，
        /// 同权限路径就是当前进程。runner 只保证本项 env 覆盖一定生效，不保证基础环境与 UI 进程逐字相同。
        /// </summary>
        public string BaseEnvironmentSource
        {
            get { return _baseEnvironmentSource; }
        }

        /// <summary>
        /// out 帧与 error 帧的出口。error 帧只作为普通系统提示（R28），不代表失败；
        /// 首个订阅者出现前的行会先缓冲，订阅时按原顺序补发。
        /// </summary>
        public event Action<LogKind, string> Output
        {
            add
            {
                if (value == null)
                {
                    return;
                }

                List<KeyValuePair<LogKind, string>> replay = null;
                Action<LogKind, string> handlers = null;
                lock (_outputEventGate)
                {
                    _outputHandlers += value;
                    if (!_outputReplayDone)
                    {
                        _outputReplayDone = true;
                        if (_pendingOutput.Count > 0)
                        {
                            replay = new List<KeyValuePair<LogKind, string>>(_pendingOutput);
                            _pendingOutput.Clear();
                        }

                        handlers = _outputHandlers;
                    }
                }

                if (replay != null)
                {
                    InvokeOutputHandlers(handlers, replay);
                }
            }
            remove
            {
                lock (_outputEventGate)
                {
                    _outputHandlers -= value;
                }
            }
        }

        /// <summary>退出码；-1 表示中转进程没有发 exit 帧就消失了。只触发一次，早于订阅的退出码会补发。</summary>
        public event Action<int> Exited
        {
            add
            {
                if (value == null)
                {
                    return;
                }

                bool replay = false;
                int replayCode = 0;
                Action<int> handlers = null;
                lock (_exitEventGate)
                {
                    _exitHandlers += value;
                    if (!_exitReplayDone)
                    {
                        _exitReplayDone = true;
                        if (_pendingExit)
                        {
                            _pendingExit = false;
                            replay = true;
                            replayCode = _pendingExitCode;
                        }

                        handlers = _exitHandlers;
                    }
                }

                if (replay)
                {
                    InvokeExitHandlers(handlers, replayCode);
                }
            }
            remove
            {
                lock (_exitEventGate)
                {
                    _exitHandlers -= value;
                }
            }
        }

        /// <summary>
        /// 启动一次中转会话。参数/模式不合法时同步抛出中文异常（不返回 faulted Task）；
        /// 连接、握手与读取阶段同样用中文异常报告失败。失败路径会杀掉已拉起的 runner 并删除临时 spec 文件。
        /// </summary>
        public static Task<RunnerSession> StartAsync(LaunchRequest request, RunnerLaunchMode mode,
            string runnerPath, string baseDir, CancellationToken ct)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request), "启动请求为空，无法启动中转进程。");
            }

            if (!string.IsNullOrEmpty(request.Error))
            {
                throw new InvalidOperationException("启动请求无效：" + request.Error);
            }

            // 三种模式走同一段装配：spec 编码、管道服务器、握手、帧翻译与停止逻辑都不重复；
            // 只有“怎么创建 runner 进程”不同（StartRunnerProcess 里分流）。
            switch (mode)
            {
                case RunnerLaunchMode.SameIntegrity:
                case RunnerLaunchMode.Elevated:
                case RunnerLaunchMode.StandardUser:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的中转进程启动模式。");
            }

            if (string.IsNullOrWhiteSpace(runnerPath))
            {
                throw new ArgumentException("中转进程路径为空，无法启动。", nameof(runnerPath));
            }

            if (!File.Exists(runnerPath))
            {
                throw new FileNotFoundException("找不到中转进程：" + runnerPath, runnerPath);
            }

            ct.ThrowIfCancellationRequested();

            // 跨权限路径（提权 / 降权）都按 60s 等连接：同权限路径保持 15s，避免掩盖 runner 的真实故障。
            int connectTimeoutMs = mode == RunnerLaunchMode.SameIntegrity
                ? ConnectTimeoutMs
                : CrossIntegrityConnectTimeoutMs;
            return StartCoreAsync(request, mode, runnerPath, baseDir, connectTimeoutMs, ct);
        }

        /// <summary>
        /// 发送 stop 并等待退出：超时后杀掉 runner（它自己的 Job 会带走子进程树），再兜底上报 Exited(-1)。
        /// 幂等，且按契约绝不抛异常；重复调用返回同一个停止任务。
        /// <para>
        /// 提权会话由标准权限的 UI 进程持有 Process 对象，跨完整性级别 Kill 通常会被拒绝；真正的停止
        /// 依赖 stop 帧让 runner 自己 TerminateJobObject，以及调用方释放会话时关闭管道、让 runner 在
        /// EOF 时自杀。<see cref="Dispose"/> 的关管道路径是最后一道兜底。
        /// </para>
        /// </summary>
        public Task StopAsync(TimeSpan timeout)
        {
            lock (_stopGate)
            {
                if (_stopTask == null)
                {
                    _stopTask = StopCoreAsync(timeout);
                }

                return _stopTask;
            }
        }

        /// <summary>释放管道、读线程与 runner 句柄；正在运行的 runner 会被结束，保证不留下孤儿。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // 先关传输层解除读线程阻塞；读线程看到 EOF 后会按“异常退出”上报 Exited(-1)。
            CleanupTransport();
            DisposeRunnerHandle(true, RunnerExitWaitMs);
        }

        private static async Task<RunnerSession> StartCoreAsync(LaunchRequest request, RunnerLaunchMode mode,
            string runnerPath, string baseDir, int connectTimeoutMs, CancellationToken ct)
        {
            string nonce = Guid.NewGuid().ToString("N");
            var spec = new LaunchSpec
            {
                V = 1,
                Nonce = nonce,
                Pipe = "gostgui." + nonce,
                Program = request.FileName ?? "",
                Arguments = request.Arguments ?? "",
                WorkingDirectory = request.WorkingDirectory ?? "",
                Kind = request.Kind,
                EnvOverrides = CopyOverrides(request.EnvOverrides)
            };

            // spec 的传输方式在建 spec 的同一处决定，管道服务器与临时文件的口径因此永远一致：
            // 降权路径的 lpCommandLine 上限只有 1024 字符，超过就必须走 --spec-file（见 NeedsTempSpecFile）。
            bool grantInteractiveUser = mode == RunnerLaunchMode.StandardUser;
            string tempSpecPath = null;
            string runnerArguments;
            if (NeedsTempSpecFile(spec, mode, runnerPath))
            {
                tempSpecPath = WriteSpecFile(spec, grantInteractiveUser);
                runnerArguments = "--spec-file \"" + tempSpecPath + "\"";
            }
            else
            {
                runnerArguments = "--spec " + LaunchSpecCodec.ToBase64(spec);
            }

            NamedPipeServerStream server = null;
            Process runner = null;
            StreamReader reader = null;
            Task connectionTask = null;
            try
            {
                server = CreatePipeServer(spec.Pipe, grantInteractiveUser);
                connectionTask = server.WaitForConnectionAsync(ct);

                // 跨权限路径的进程创建分别换成 ShellExecuteEx(runas) 与 explorer 令牌降权；
                // --spec / --spec-file 的装配由上面共用，管道服务器、握手、帧翻译与停止逻辑完全一致。
                runner = StartRunnerProcess(runnerPath, runnerArguments, baseDir, mode);

                await WaitForConnectionAsync(connectionTask, runner, ct, connectTimeoutMs).ConfigureAwait(false);

                reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, true);
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(HandshakeTimeoutMs);

                IpcMessage ready = await ReadFrameAsync(reader, RemainingMilliseconds(deadline), ct)
                    .ConfigureAwait(false);
                if (ready == null)
                {
                    throw new InvalidOperationException("中转进程在握手完成前断开连接。");
                }

                if (!string.Equals(ready.Type, "ready", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "中转进程握手失败：期望 ready 帧，实际收到「" + (ready.Type ?? "") + "」。");
                }

                if (!string.Equals(ready.Nonce, nonce, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("中转进程握手失败：nonce 不匹配。");
                }

                // started 之前可能先到 error 帧（例如环境规范化提示）或防御性地出现 out 帧：
                // 这些都不是失败，先缓冲下来，等会话建好后再按原顺序补发。
                var preStartOutput = new List<KeyValuePair<LogKind, string>>();
                int childPid = 0;
                while (childPid <= 0)
                {
                    IpcMessage frame = await ReadFrameAsync(reader, RemainingMilliseconds(deadline), ct)
                        .ConfigureAwait(false);
                    if (frame == null)
                    {
                        throw new InvalidOperationException(DescribePreStartFailure(preStartOutput));
                    }

                    if (string.Equals(frame.Type, "started", StringComparison.Ordinal))
                    {
                        childPid = frame.Pid;
                        continue;
                    }

                    if (string.Equals(frame.Type, "error", StringComparison.Ordinal))
                    {
                        preStartOutput.Add(new KeyValuePair<LogKind, string>(LogKind.System, frame.Data ?? ""));
                        continue;
                    }

                    if (string.Equals(frame.Type, "out", StringComparison.Ordinal))
                    {
                        preStartOutput.Add(new KeyValuePair<LogKind, string>(
                            MapOutputKind(frame.Stream), frame.Data ?? ""));
                        continue;
                    }

                    if (string.Equals(frame.Type, "exit", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "中转进程在报告子进程 PID 前就退出了（退出码 " + frame.ExitCode + "）。"
                            + DescribePreStartFailure(preStartOutput));
                    }

                    if (string.Equals(frame.Type, "ready", StringComparison.Ordinal))
                    {
                        // 重复的 ready 帧没有新信息，忽略即可。
                        continue;
                    }

                    throw new InvalidOperationException(
                        "中转进程握手失败：收到未知帧「" + (frame.Type ?? "") + "」。");
                }

                var session = new RunnerSession(server, runner, reader, childPid, mode);
                foreach (KeyValuePair<LogKind, string> line in preStartOutput)
                {
                    session.RaiseOutput(line.Key, line.Value);
                }

                session.StartReader();

                // 所有权移交给会话；catch 里不再清理这些资源。
                server = null;
                runner = null;
                reader = null;
                return session;
            }
            catch
            {
                // 连接等待可能因服务器被关闭而失败；先观察掉，避免未观察异常在 GC 时冒出来。
                if (connectionTask != null)
                {
                    ObserveFailure(connectionTask);
                }

                TryDispose(reader);
                TryDispose(server);
                KillAndDisposeRunner(runner);
                throw;
            }
            finally
            {
                // runner 读完 spec 才会连上管道；连接失败/握手失败时也由这里清理临时文件。
                TryDeleteFile(tempSpecPath);
            }
        }

        /// <summary>
        /// 决定这次 spec 走 <c>--spec &lt;base64&gt;</c> 还是 <c>--spec-file &lt;临时文件&gt;</c>。纯函数，供自检直接断言。
        /// <para>
        /// 1) Base64 超过 <see cref="LaunchSpecCodec.CommandLineLimit"/>（8000）时所有模式都必须走临时文件；
        /// 2) 降权路径（StandardUser）的上限更严：<c>CreateProcessWithTokenW</c> 的 lpCommandLine 文档上限是
        ///    1024 字符，超过 <see cref="StandardUserCommandLineLimit"/> 时会以 87 (ERROR_INVALID_PARAMETER)
        ///    失败，而 1314 的兜底不会救这种失败——所以必须提前改走临时文件。
        /// </para>
        /// </summary>
        internal static bool NeedsTempSpecFile(LaunchSpec spec, RunnerLaunchMode mode, string runnerPath)
        {
            if (LaunchSpecCodec.NeedsTempFile(spec))
            {
                return true;
            }

            if (mode != RunnerLaunchMode.StandardUser)
            {
                return false;
            }

            // 与 ElevationManager 拼命令行的方式保持一致：runner 路径按需加引号 + 空格 + "--spec " + Base64。
            string commandLine = ElevationManager.QuoteIfNeeded(runnerPath) + " --spec "
                                 + LaunchSpecCodec.ToBase64(spec);
            return commandLine.Length > StandardUserCommandLineLimit;
        }

        internal static NamedPipeServerStream CreatePipeServer(string pipeName, bool grantInteractiveUser)
        {
            if (CurrentUserSid() == null)
            {
                throw new InvalidOperationException("无法获取当前用户，不能创建中转进程命名管道。");
            }

            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, BuildPipeSecurity(grantInteractiveUser));
            try
            {
                // 命名管道必须显式把强制完整性标签降为“中等”：提权 UI（High IL）创建的管道默认自动带上
                // High 标签 + NO_WRITE_UP，而降权出来的 runner 是 Medium IL，读写打开会被拒绝（错误码 5），
                // runner 随后以退出码 4 结束——“提权 gostGUI + 非管理员项”就是坏在这条隐式标签上。
                // 反方向（Medium 服务器、High 客户端）默认就能写，所以历史规格只描述过那一种。
                // 这里只改强制标签，不动 DACL：DACL 仍由 BuildPipeSecurity 的主体授予，不因标签放宽任何访问。
                ApplyMediumIntegrityLabel(server.SafePipeHandle.DangerousGetHandle());
                return server;
            }
            catch
            {
                TryDispose(server);
                throw;
            }
        }

        /// <summary>
        /// 给命名管道对象设置 Medium 强制标签（SDDL 等价于 <c>S:(ML;;NW;;;ME)</c>）：
        /// 一个 <c>SYSTEM_MANDATORY_LABEL_ACE</c>，SID 为 <c>S-1-16-8192</c>，策略 <c>NO_WRITE_UP</c>。
        /// 通过 <c>SetSecurityInfo(..., LABEL_SECURITY_INFORMATION)</c> 写入 SACL 槽；每个启动模式都必须调用，
        /// 因为任何 runner（含同权限）都不低于 Medium，而 Medium 标签不会挡住 High 客户端写入。
        /// </summary>
        private static void ApplyMediumIntegrityLabel(IntPtr pipeHandle)
        {
            IntPtr sidBuffer = IntPtr.Zero;
            IntPtr aclBuffer = IntPtr.Zero;
            try
            {
                var mediumLabel = new SecurityIdentifier(MediumIntegritySid);
                byte[] sidBytes = new byte[mediumLabel.BinaryLength];
                mediumLabel.GetBinaryForm(sidBytes, 0);
                sidBuffer = Marshal.AllocHGlobal(sidBytes.Length);
                Marshal.Copy(sidBytes, 0, sidBuffer, sidBytes.Length);

                // ACL 头（8）+ ACE 头（4）+ 强制策略掩码（4）+ SID。
                int aclLength = 8 + 4 + 4 + sidBytes.Length;
                aclBuffer = Marshal.AllocHGlobal(aclLength);
                if (!NativeMethods.InitializeAcl(aclBuffer, (uint)aclLength, AclRevision))
                {
                    throw LabelFailure("初始化管道强制标签 ACL");
                }

                if (!NativeMethods.AddMandatoryAce(aclBuffer, AclRevision, 0, SystemMandatoryLabelNoWriteUp, sidBuffer))
                {
                    throw LabelFailure("写入管道强制标签 ACE");
                }

                uint result = NativeMethods.SetSecurityInfo(pipeHandle, SeKernelObject, LabelSecurityInformation,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, aclBuffer);
                if (result != 0)
                {
                    throw new InvalidOperationException(
                        "无法为命名管道设置中等完整性标签（Windows 错误码 " + result + "），降权中转进程将无法连接管道。");
                }
            }
            finally
            {
                if (aclBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(aclBuffer);
                }

                if (sidBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(sidBuffer);
                }
            }
        }

        private static InvalidOperationException LabelFailure(string operation)
        {
            return new InvalidOperationException(operation + "失败（Windows 错误码 "
                + Marshal.GetLastWin32Error() + "），降权中转进程将无法连接管道。");
        }

        /// <summary>决定一次中转会话“子进程基础环境”来源的日志文案（设计决策 13 / §6.4）。</summary>
        internal static string DescribeBaseEnvironment(RunnerLaunchMode mode)
        {
            switch (mode)
            {
                case RunnerLaunchMode.Elevated:
                    return "继承自提权令牌";
                case RunnerLaunchMode.StandardUser:
                    return "继承自标准用户令牌";
                default:
                    return "继承自当前进程";
            }
        }

        /// <summary>
        /// 尽力读取进程令牌的 TokenUser（域\用户名），供启动日志写“中转进程身份”。
        /// 跨权限句柄可能打不开、SID 也可能无法映射到账号：任何失败都返回 null，由调用方写“无法读取身份”。
        /// </summary>
        private static string ReadProcessUserName(Process process)
        {
            if (process == null)
            {
                return null;
            }

            IntPtr hProcess = IntPtr.Zero;
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                // OpenProcessToken 需要进程句柄带 PROCESS_QUERY_INFORMATION；实现上 Vista+ 也接受
                // PROCESS_QUERY_LIMITED_INFORMATION，但只开 LIMITED 在部分令牌/跨权限场景会被拒绝，
                // 身份就会静默落到“无法读取身份”。先按两者合一打开（同用户 runner 一定够权）；
                // 个别跨权限句柄只允许 LIMITED 时再退回它。
                hProcess = NativeMethods.OpenProcess(ProcessQueryInformation | ProcessQueryLimitedInformation, false, process.Id);
                if (hProcess == IntPtr.Zero)
                {
                    hProcess = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                }

                if (hProcess == IntPtr.Zero)
                {
                    return null;
                }

                if (!NativeMethods.OpenProcessToken(hProcess, TokenQuery, out token))
                {
                    return null;
                }

                uint needed;
                NativeMethods.GetTokenInformation(token, TokenUserInformation, IntPtr.Zero, 0, out needed);
                if (needed == 0)
                {
                    return null;
                }

                buffer = Marshal.AllocHGlobal((int)needed);
                if (!NativeMethods.GetTokenInformation(token, TokenUserInformation, buffer, needed, out needed))
                {
                    return null;
                }

                IntPtr sidPointer = Marshal.ReadIntPtr(buffer);
                if (sidPointer == IntPtr.Zero)
                {
                    return null;
                }

                var sid = new SecurityIdentifier(sidPointer);
                try
                {
                    return sid.Translate(typeof(NTAccount)).Value;
                }
                catch (Exception)
                {
                    // 账号已删除等无法映射的 SID：至少把 SID 本身写进日志。
                    return sid.Value;
                }
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
                    NativeMethods.CloseHandle(token);
                }

                if (hProcess != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(hProcess);
                }
            }
        }

        /// <summary>命名管道/临时 spec 文件共用的显式 ACL 主体（见 <see cref="BuildAclSids"/>）。</summary>
        internal static PipeSecurity BuildPipeSecurity(bool grantInteractiveUser)
        {
            var security = new PipeSecurity();
            foreach (SecurityIdentifier sid in BuildAclSids(grantInteractiveUser))
            {
                security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }

            return security;
        }

        /// <summary>
        /// 管道与临时 spec 文件的显式 ACL 主体：当前用户 + <c>BUILTIN\Administrators</c> + <c>SYSTEM</c>；
        /// 降权场景再加 <c>INTERACTIVE</c>（S-1-5-4）。
        /// <para>
        /// 为什么降权场景必须加 INTERACTIVE：跨账号提权（over-the-shoulder，用户在 UAC 里输入了另一个管理员账号）
        /// 时 gostGUI 以那个管理员账号运行，而降权出来的 runner 是**当前交互用户**的身份。默认 DACL 只覆盖创建者，
        /// 交互用户既读不到临时 spec 也连不上管道，runner 会起来后静默失败。授予 INTERACTIVE 后同账号场景
        /// 不受影响（那条规则本来就被“当前用户”覆盖），其它模式不加这条规则。
        /// </para>
        /// </summary>
        internal static List<SecurityIdentifier> BuildAclSids(bool grantInteractiveUser)
        {
            var sids = new List<SecurityIdentifier>();
            SecurityIdentifier currentUser = CurrentUserSid();
            if (currentUser != null)
            {
                sids.Add(currentUser);
            }

            sids.Add(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            sids.Add(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));

            if (grantInteractiveUser)
            {
                sids.Add(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null));
            }

            return sids;
        }

        /// <summary>当前用户 SID；拿不到时返回 null，由调用方给出中文错误或放弃 ACL。</summary>
        private static SecurityIdentifier CurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return identity.User;
            }
        }

        private static Process StartRunnerProcess(string runnerPath, string arguments, string baseDir,
            RunnerLaunchMode mode)
        {
            string workingDirectory = string.IsNullOrWhiteSpace(baseDir) ? Common.GetApplicationPath() : baseDir;

            if (mode == RunnerLaunchMode.Elevated)
            {
                // UAC 取消会在这里抛 Win32Exception(1223)，由 ProcessManager 映射成“已取消管理员授权”；
                // 其它失败同样带上原生错误码，绝不在这里降级成同权限启动。
                return ElevationManager.LaunchRunnerElevated(runnerPath, arguments, workingDirectory);
            }

            if (mode == RunnerLaunchMode.StandardUser)
            {
                // 降权路径：借 explorer.exe 的标准用户令牌创建 runner，子进程不继承本进程的管理员权限。
                // 失败抛带真实错误码的 Win32Exception（登记在案的预期失败码是 1314/5），同样绝不降级成同权限启动。
                return ElevationManager.LaunchRunnerAsStandardUser(runnerPath, arguments, workingDirectory);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = runnerPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            Process runner = Process.Start(startInfo);
            if (runner == null)
            {
                throw new InvalidOperationException("无法创建中转进程。");
            }

            return runner;
        }

        /// <summary>
        /// 等 runner 连上管道，同时轮询 runner 是否已经退出：死掉的中转进程不能伪装成 15/60 秒连接超时。
        /// 只能观察到 runner 的退出时立刻抛中文错误（带真实退出码）；活着或无法观察（跨权限句柄没有
        /// SYNCHRONIZE）的 runner 仍保持原有的“连接 vs 超时”语义。
        /// </summary>
        private static async Task WaitForConnectionAsync(Task connectionTask, Process runner,
            CancellationToken ct, int timeoutMs)
        {
            var stopWatching = new CancellationTokenSource();
            CancellationToken watchToken = stopWatching.Token;
            try
            {
                Task<bool> exitTask = WatchRunnerExitAsync(runner, watchToken);
                Task timeoutTask = Task.Delay(timeoutMs, ct);
                Task winner = await Task.WhenAny(connectionTask, exitTask, timeoutTask).ConfigureAwait(false);

                if (winner == connectionTask)
                {
                    await connectionTask.ConfigureAwait(false);
                    return;
                }

                if (winner == exitTask && exitTask.Result)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("中转进程已退出（错误码 " + GetRunnerExitCode(runner)
                        + "），未能连接管道。");
                }

                if (winner == exitTask)
                {
                    // 无法观察 runner（例如跨权限句柄打不开）：退回原语义，只等连接或超时。
                    winner = await Task.WhenAny(connectionTask, timeoutTask).ConfigureAwait(false);
                    if (winner == connectionTask)
                    {
                        await connectionTask.ConfigureAwait(false);
                        return;
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                throw new TimeoutException("中转进程在 " + (timeoutMs / 1000) + " 秒内未连上命名管道。");
            }
            finally
            {
                stopWatching.Cancel();
                stopWatching.Dispose();
            }
        }

        /// <summary>
        /// 轮询 runner 是否退出，作为连接等待的分支。返回 true 表示确认已退出；false 表示无法观察
        /// （跨权限句柄可能没有 SYNCHRONIZE）或已被取消。轮询而不是 WaitForExit 阻塞线程：
        /// 连接成功后要立刻停掉观察，不能让一个线程陪着子进程活到结束。
        /// </summary>
        private static Task<bool> WatchRunnerExitAsync(Process runner, CancellationToken stop)
        {
            return Task.Run(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (runner.HasExited)
                        {
                            return true;
                        }

                        Thread.Sleep(RunnerExitPollMs);
                    }
                }
                catch (Exception)
                {
                    // 已取消（令牌源已释放）或句柄失效：按“无法观察”收手，绝不把异常漏到线程池。
                }

                return false;
            });
        }

        private static int GetRunnerExitCode(Process runner)
        {
            try
            {
                return runner.ExitCode;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>有上限地读一帧；ReadLine 是阻塞调用，放进线程池并靠 WhenAny 加超时。</summary>
        private static async Task<IpcMessage> ReadFrameAsync(StreamReader reader, int timeoutMs,
            CancellationToken ct)
        {
            if (timeoutMs <= 0)
            {
                throw new TimeoutException("中转进程握手超时（" + (HandshakeTimeoutMs / 1000) + " 秒）。");
            }

            Task<IpcMessage> readTask = Task.Run(() => IpcFraming.Read(reader));
            Task delayTask = Task.Delay(timeoutMs, ct);
            Task winner = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
            if (winner != readTask)
            {
                // 超时后读任务仍会阻塞在管道上；收尾关闭管道时它会抛异常，这里先观察掉，避免未观察异常。
                ObserveFailure(readTask);

                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                throw new TimeoutException("中转进程握手超时（" + (HandshakeTimeoutMs / 1000) + " 秒）。");
            }

            return await readTask.ConfigureAwait(false);
        }

        private static void ObserveFailure(Task task)
        {
            task.ContinueWith(t =>
            {
                // 先取 Exception（标记为已观察），再记一条调试输出：泵任务没有 await 点，
                // 逃逸的故障只能靠这里留痕，否则未观察异常会在 GC 时被静默忽略。
                AggregateException error = t.Exception;
                Debug.WriteLine("后台任务异常：" + error);
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private static int RemainingMilliseconds(DateTime deadline)
        {
            double remaining = (deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0)
            {
                return 0;
            }

            return remaining >= int.MaxValue ? int.MaxValue : (int)remaining;
        }

        private static string DescribePreStartFailure(List<KeyValuePair<LogKind, string>> output)
        {
            var messages = new List<string>();
            foreach (KeyValuePair<LogKind, string> line in output)
            {
                if (!string.IsNullOrEmpty(line.Value))
                {
                    messages.Add(line.Value.Trim());
                }
            }

            if (messages.Count == 0)
            {
                return "中转进程在报告子进程 PID 前退出了。";
            }

            return "中转进程启动子进程失败：" + string.Join("；", messages.ToArray());
        }

        private static Dictionary<string, string> CopyOverrides(Dictionary<string, string> source)
        {
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return copy;
            }

            foreach (KeyValuePair<string, string> pair in source)
            {
                copy[pair.Key] = pair.Value ?? "";
            }

            return copy;
        }

        private static string WriteSpecFile(LaunchSpec spec, bool grantInteractiveUser)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "gostgui-spec-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, LaunchSpecCodec.ToJson(spec), new UTF8Encoding(false));
            TryApplySpecFileAcl(path, grantInteractiveUser);
            return path;
        }

        /// <summary>
        /// 给临时 spec 文件补显式 ACL（与管道 DACL 同一份主体，见 <see cref="BuildAclSids"/>）。
        /// 同权限路径不需要它，但跨权限路径需要 runner 那一方能读到文件；降权场景还要覆盖 INTERACTIVE
        /// （over-the-shoulder 时 runner 是交互用户身份）。失败只影响跨权限场景，不影响当前用户本次启动，因此尽力而为。
        /// </summary>
        private static void TryApplySpecFileAcl(string path, bool grantInteractiveUser)
        {
            try
            {
                FileSecurity security = File.GetAccessControl(path);
                foreach (SecurityIdentifier sid in BuildAclSids(grantInteractiveUser))
                {
                    AddFileFullControl(security, sid);
                }

                File.SetAccessControl(path, security);
            }
            catch (Exception)
            {
                // 非 NTFS 临时目录或 ACL 被策略禁用时无法设置；当前用户仍能正常使用该文件。
            }
        }

        private static void AddFileFullControl(FileSecurity security, SecurityIdentifier sid)
        {
            if (sid == null)
            {
                return;
            }

            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        private void StartReader()
        {
            // ReadLoop 会在整个会话存续期内阻塞在管道读上，所以必须用 LongRunning 让
            // TaskScheduler.Default 为它创建一条专用线程：普通线程池任务会长期占住一个工作线程，
            // 并发会话一多就可能把线程池饿死。LongRunning 创建的线程是后台线程（与原来的
            // IsBackground = true 等价），不阻止进程退出；会话关闭管道后 ReadLoop 自行返回，
            // 这条泵线程随会话一起结束。
            // 泵任务没有 await 点：万一有异常逃出 ReadLoop 的 try/finally，必须由 ObserveFailure
            // 观测并记录，不能让它在 GC 时以未观察异常的形式静默消失。
            Task pump = Task.Factory.StartNew(
                ReadLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            ObserveFailure(pump);
        }

        /// <summary>
        /// 读帧线程：out/error 转 Output，exit 转 Exited；EOF/IO 失败且没有 exit 帧时，
        /// 只写一条“中转进程异常退出”并上报 Exited(-1)（R28：错误帧本身不是失败）。
        /// </summary>
        private void ReadLoop()
        {
            bool receivedExit = false;
            int exitCode = -1;
            try
            {
                while (true)
                {
                    IpcMessage message;
                    try
                    {
                        message = IpcFraming.Read(_reader);
                    }
                    catch (Exception)
                    {
                        // 管道被关闭 / 对端消失：按 EOF 处理。
                        break;
                    }

                    if (message == null)
                    {
                        break;
                    }

                    if (string.Equals(message.Type, "exit", StringComparison.Ordinal))
                    {
                        receivedExit = true;
                        exitCode = message.ExitCode;
                        break;
                    }

                    if (string.Equals(message.Type, "out", StringComparison.Ordinal))
                    {
                        RaiseOutput(MapOutputKind(message.Stream), message.Data ?? "");
                        continue;
                    }

                    if (string.Equals(message.Type, "error", StringComparison.Ordinal))
                    {
                        // R28：非致命提示走同一通道；只显示为 System 行，不改变退出语义。
                        RaiseOutput(LogKind.System, message.Data ?? "");
                    }

                    // ready/started/stop 等帧在会话阶段没有意义，忽略。
                }
            }
            finally
            {
                if (receivedExit)
                {
                    RaiseExited(exitCode);
                }
                else
                {
                    RaiseEofNotice();
                    RaiseExited(-1);
                }

                CleanupTransport();

                // 正常退出后 runner 应马上结束；给 2 秒，仍不退出则强杀，保证句柄与进程都不残留。
                DisposeRunnerHandle(true, receivedExit ? RunnerExitWaitMs : 0);
            }
        }

        private void RaiseOutput(LogKind kind, string data)
        {
            string text = data ?? "";
            Action<LogKind, string> handlers;
            lock (_outputEventGate)
            {
                if (!_outputReplayDone)
                {
                    _pendingOutput.Add(new KeyValuePair<LogKind, string>(kind, text));
                    return;
                }

                handlers = _outputHandlers;
            }

            InvokeOutputHandlers(handlers, new[] { new KeyValuePair<LogKind, string>(kind, text) });
        }

        private void RaiseEofNotice()
        {
            if (Interlocked.Exchange(ref _eofNoticeRaised, 1) == 0)
            {
                // 停止超时后主动杀 runner 也会让读线程看到 EOF：这是预期收尾，不能说成“异常退出”。
                RaiseOutput(LogKind.System,
                    Volatile.Read(ref _stopTimedOut) != 0
                        ? "停止超时，已强制结束中转进程"
                        : "中转进程异常退出");
            }
        }

        private void RaiseExited(int code)
        {
            if (Interlocked.Exchange(ref _exitRaised, 1) != 0)
            {
                return;
            }

            Action<int> handlers = null;
            bool buffered = false;
            lock (_exitEventGate)
            {
                if (_exitReplayDone)
                {
                    handlers = _exitHandlers;
                }
                else
                {
                    _pendingExit = true;
                    _pendingExitCode = code;
                    buffered = true;
                }
            }

            // 先送事件再完成内部等待：StopAsync 返回时，订阅者已经收到 Exited。
            if (!buffered)
            {
                InvokeExitHandlers(handlers, code);
            }

            _exitSource.TrySetResult(code);
        }

        private static void InvokeOutputHandlers(Action<LogKind, string> handlers,
            IEnumerable<KeyValuePair<LogKind, string>> lines)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate target in handlers.GetInvocationList())
            {
                var handler = (Action<LogKind, string>)target;
                foreach (KeyValuePair<LogKind, string> line in lines)
                {
                    try
                    {
                        handler(line.Key, line.Value);
                    }
                    catch (Exception)
                    {
                        // 订阅者自己的异常不能杀死读帧线程，否则后续 exit 帧会丢失。
                    }
                }
            }
        }

        private static void InvokeExitHandlers(Action<int> handlers, int code)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate target in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<int>)target)(code);
                }
                catch (Exception)
                {
                    // 同上：退出回调故障不影响 Exited 的“只触发一次”与内部等待。
                }
            }
        }

        private async Task StopCoreAsync(TimeSpan timeout)
        {
            try
            {
                if (_exitSource.Task.IsCompleted)
                {
                    return;
                }

                if (timeout < TimeSpan.Zero)
                {
                    timeout = TimeSpan.Zero;
                }
                else if (timeout > TimeSpan.FromMilliseconds(int.MaxValue))
                {
                    // Task.Delay 的上限是 int.MaxValue 毫秒；更长的等待按上限处理，避免参数异常被兜底成“已退出”。
                    timeout = TimeSpan.FromMilliseconds(int.MaxValue);
                }

                SendStop();

                Task winner = await Task.WhenAny(_exitSource.Task, Task.Delay(timeout)).ConfigureAwait(false);
                if (winner == _exitSource.Task)
                {
                    await _exitSource.Task.ConfigureAwait(false);
                    return;
                }

                // R27：没有等到 exit 就杀 runner；runner 的 Job（KILL_ON_JOB_CLOSE）会带走它的子进程树。
                // 先记下“这是停止超时的主动强杀”，读线程随后看到的 EOF 才不会写成“中转进程异常退出”。
                Interlocked.Exchange(ref _stopTimedOut, 1);
                KillRunnerNow();

                Task afterKill = await Task.WhenAny(_exitSource.Task, Task.Delay(ForceKillWaitMs))
                    .ConfigureAwait(false);
                if (afterKill != _exitSource.Task)
                {
                    // 读线程始终没醒：再兜底上报一次 -1，杜绝 StopProcess 永远等下去。
                    RaiseEofNotice();
                    RaiseExited(-1);
                }
            }
            catch (Exception)
            {
                // StopAsync 的契约是绝不抛：任何收尾异常都只能以 Exited(-1) 结束等待。
                RaiseExited(-1);
            }
        }

        private void SendStop()
        {
            lock (_writeGate)
            {
                try
                {
                    IpcFraming.Write(_pipe, new IpcMessage { Type = "stop" });
                }
                catch (Exception)
                {
                    // 管道已断：交给杀 runner 的兜底路径。
                }
            }
        }

        private void KillRunnerNow()
        {
            lock (_runnerGate)
            {
                if (_runnerHandleDisposed)
                {
                    return;
                }

                try
                {
                    if (!_runner.HasExited)
                    {
                        _runner.Kill();
                    }
                }
                catch (Exception)
                {
                    // 已经退出 / 句柄失效：尽力而为。
                }
            }
        }

        /// <summary>关闭管道与读端；幂等，任何线程都可调用。关闭会让阻塞中的读线程立即看到 EOF/异常。</summary>
        private void CleanupTransport()
        {
            lock (_transportGate)
            {
                if (_transportDisposed)
                {
                    return;
                }

                _transportDisposed = true;
            }

            TryDispose(_reader);
            TryDispose(_pipe);
        }

        /// <summary>结束并释放 runner 进程句柄；幂等，和 StopAsync 的 kill 路径用同一把锁互斥。</summary>
        private void DisposeRunnerHandle(bool killIfRunning, int waitMs)
        {
            Process runner;
            lock (_runnerGate)
            {
                if (_runnerHandleDisposed)
                {
                    return;
                }

                _runnerHandleDisposed = true;
                runner = _runner;
            }

            try
            {
                bool exited;
                try
                {
                    exited = runner.HasExited;
                }
                catch (Exception)
                {
                    exited = true;
                }

                if (!exited && killIfRunning)
                {
                    try
                    {
                        runner.Kill();
                    }
                    catch (Exception)
                    {
                    }
                }

                if (!exited && waitMs > 0)
                {
                    try
                    {
                        runner.WaitForExit(waitMs);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            finally
            {
                TryDispose(runner);
            }
        }

        private static LogKind MapOutputKind(string stream)
        {
            return string.Equals(stream, "e", StringComparison.Ordinal) ? LogKind.Stderr : LogKind.Stdout;
        }

        private static void KillAndDisposeRunner(Process runner)
        {
            if (runner == null)
            {
                return;
            }

            try
            {
                if (!runner.HasExited)
                {
                    runner.Kill();
                }

                runner.WaitForExit(RunnerExitWaitMs);
            }
            catch (Exception)
            {
                // 启动失败路径上的清理尽力而为。
            }
            finally
            {
                TryDispose(runner);
            }
        }

        private static void TryDispose(IDisposable disposable)
        {
            if (disposable == null)
            {
                return;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 删不掉临时文件只影响清理；runner 读完后也会自己删。
            }
        }

        // =====================================================================================
        // 强制完整性标签与 runner 身份查询的原生入口（只在 Windows 上运行，net48 没有托管替代）。
        // 声明已收口到 gostGUI.Native，这里只保留本文件用到的权限常量。
        // =====================================================================================

        private const int TokenUserInformation = 1;
        private const int SeKernelObject = 6;
        private const uint TokenQuery = 0x0008;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint LabelSecurityInformation = 0x00000010;
        private const uint SystemMandatoryLabelNoWriteUp = 0x00000001;
        private const uint AclRevision = 2;

    }
}
