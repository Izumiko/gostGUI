using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using gostGUI.Shared;

namespace gostGUI
{
    /// <summary>
    /// 中转进程的启动模式。Task 8c 只实现 SameIntegrity；Elevated / StandardUser 由 Task 9/10 替换为真实分支。
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

        /// <summary>ready + started 两个握手帧共用的总上限。</summary>
        private const int HandshakeTimeoutMs = 15000;

        /// <summary>stop 超时并杀掉 runner 后，等待读线程报 EOF 的兜底上限。</summary>
        private const int ForceKillWaitMs = 3000;

        /// <summary>正常收到 exit 帧后，等待 runner 自己退出的上限。</summary>
        private const int RunnerExitWaitMs = 2000;

        private readonly NamedPipeServerStream _pipe;
        private readonly Process _runner;
        private readonly StreamReader _reader;
        private readonly TaskCompletionSource<int> _exitSource = new TaskCompletionSource<int>();
        private readonly Thread _readerThread;
        private readonly int _childPid;

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
        private int _disposed;
        private bool _transportDisposed;
        private bool _runnerHandleDisposed;
        private Task _stopTask;

        private RunnerSession(NamedPipeServerStream pipe, Process runner, StreamReader reader, int childPid)
        {
            _pipe = pipe;
            _runner = runner;
            _reader = reader;
            _childPid = childPid;
            _readerThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "gostgui-runner-client"
            };
        }

        /// <summary>runner 上报的子进程 PID；握手阶段拿不到正数 PID 时 StartAsync 会直接失败。</summary>
        public int ChildPid
        {
            get { return _childPid; }
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

            // 模式检查必须早于路径检查：Elevated / StandardUser 的“暂不支持”是模式本身的结论，
            // 不能因为 runner 路径暂时不在而报成另一种错误。
            switch (mode)
            {
                case RunnerLaunchMode.SameIntegrity:
                    break;
                case RunnerLaunchMode.Elevated:
                    throw new NotSupportedException("暂不支持以管理员身份启动中转进程（Task 9 实现）。");
                case RunnerLaunchMode.StandardUser:
                    throw new NotSupportedException("暂不支持以标准用户身份启动中转进程（Task 10 实现）。");
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
            return StartCoreAsync(request, runnerPath, baseDir, ct);
        }

        /// <summary>
        /// 发送 stop 并等待退出：超时后杀掉 runner（它自己的 Job 会带走子进程树），再兜底上报 Exited(-1)。
        /// 幂等，且按契约绝不抛异常；重复调用返回同一个停止任务。
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

        private static async Task<RunnerSession> StartCoreAsync(LaunchRequest request, string runnerPath,
            string baseDir, CancellationToken ct)
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

            string tempSpecPath = null;
            string runnerArguments;
            if (LaunchSpecCodec.NeedsTempFile(spec))
            {
                tempSpecPath = WriteSpecFile(spec);
                runnerArguments = "--spec-file \"" + tempSpecPath + "\"";
            }
            else
            {
                runnerArguments = "--spec " + LaunchSpecCodec.ToBase64(spec);
            }

            NamedPipeServerStream server = null;
            Process runner = null;
            StreamReader reader = null;
            try
            {
                server = CreatePipeServer(spec.Pipe);
                Task connectionTask = server.WaitForConnectionAsync(ct);

                runner = StartRunnerProcess(runnerPath, runnerArguments, baseDir);

                await WaitForConnectionAsync(connectionTask, ct).ConfigureAwait(false);

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

                var session = new RunnerSession(server, runner, reader, childPid);
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

        private static NamedPipeServerStream CreatePipeServer(string pipeName)
        {
            var security = new PipeSecurity();
            SecurityIdentifier currentUser = CurrentUserSid();
            if (currentUser == null)
            {
                throw new InvalidOperationException("无法获取当前用户，不能创建中转进程命名管道。");
            }

            // 显式 DACL：当前用户 + 管理员 + SYSTEM 全控。Task 9/10 跨权限拉起 runner 时，默认 DACL 会漏掉
            // UAC 里输入的另一管理员账号，导致 runner 连不上管道。
            security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
        }

        /// <summary>当前用户 SID；拿不到时返回 null，由调用方给出中文错误或放弃 ACL。</summary>
        private static SecurityIdentifier CurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return identity.User;
            }
        }

        private static Process StartRunnerProcess(string runnerPath, string arguments, string baseDir)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = runnerPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(baseDir) ? Common.GetApplicationPath() : baseDir
            };

            Process runner = Process.Start(startInfo);
            if (runner == null)
            {
                throw new InvalidOperationException("无法创建中转进程。");
            }

            return runner;
        }

        private static async Task WaitForConnectionAsync(Task connectionTask, CancellationToken ct)
        {
            Task timeoutTask = Task.Delay(ConnectTimeoutMs, ct);
            Task winner = await Task.WhenAny(connectionTask, timeoutTask).ConfigureAwait(false);
            if (winner != connectionTask)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                throw new TimeoutException("中转进程在 " + (ConnectTimeoutMs / 1000) + " 秒内未连上命名管道。");
            }

            await connectionTask.ConfigureAwait(false);
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
                _ = t.Exception;
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

        private static string WriteSpecFile(LaunchSpec spec)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "gostgui-spec-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, LaunchSpecCodec.ToJson(spec), new UTF8Encoding(false));
            TryApplySpecFileAcl(path);
            return path;
        }

        /// <summary>
        /// 给临时 spec 文件补显式 ACL（当前用户 + 管理员 + SYSTEM 全控），与管道 DACL 保持一致。
        /// 同权限路径不需要它，但 Task 9/10 的跨账号提权路径需要 runner 以外的那一方能读到文件。
        /// 失败只影响跨权限场景，不影响当前用户本次启动，因此尽力而为。
        /// </summary>
        private static void TryApplySpecFileAcl(string path)
        {
            try
            {
                FileSecurity security = File.GetAccessControl(path);
                AddFileFullControl(security, CurrentUserSid());
                AddFileFullControl(security,
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                AddFileFullControl(security,
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
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
            _readerThread.Start();
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
                RaiseOutput(LogKind.System, "中转进程异常退出");
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
    }
}
