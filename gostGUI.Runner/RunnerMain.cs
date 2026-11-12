using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using gostGUI.Shared;

namespace gostGUI.Runner
{
    /// <summary>
    /// 中转进程入口：把 UI 传来的 <see cref="LaunchSpec"/> 变成真正的子进程，替它持有 stdout/stderr，
    /// 用 NDJSON 帧把输出、PID 与退出码转发回 UI。自身只做“建进程 + 转发 + Job 兜底清理”，
    /// 不引用 System.Windows.Forms，也不写任何控制台输出。
    /// </summary>
    internal static class RunnerMain
    {
        /// <summary>参数不受支持（没有 spec 参数或混入未知参数）时的退出码，保持 Task 1 的探针契约。</summary>
        internal const int UnsupportedArgumentsExitCode = 2;

        /// <summary>spec 缺失、重复、无法解析或内容不可用时的退出码。</summary>
        internal const int InvalidSpecExitCode = 3;

        /// <summary>连不上 UI 命名管道（含 15s 连接超时）时的退出码。</summary>
        internal const int ConnectFailedExitCode = 4;

        /// <summary>子进程无法启动（Win32 错误、环境变量被拒绝等）时的退出码。</summary>
        internal const int ChildStartFailedExitCode = 5;

        /// <summary>等待 UI 管道连接的时长。</summary>
        private const int ConnectTimeoutMs = 15000;

        /// <summary>子进程启动后等待其收尾的兜底上限，只用于错误路径，避免退出前残留一个活进程。</summary>
        private const int ChildKillTimeoutMs = 5000;

        internal static int Main(string[] args)
        {
            try
            {
                return RunMain(args);
            }
            catch (Exception)
            {
                // 最后一道兜底：WinExe 的未处理异常会弹出系统崩溃对话框，且退出码不可控。
                return ChildStartFailedExitCode;
            }
        }

        private static int RunMain(string[] args)
        {
            // --selftest 只作为「runner 能启动」的探针；此时没有任何转发工作可做。
            if (args.Any(a => a == "--selftest"))
            {
                return 0;
            }

            LaunchSpec spec;
            int loadCode = TryLoadSpec(args, out spec);
            if (loadCode != 0)
            {
                return loadCode;
            }

            if (!IsSpecUsable(spec))
            {
                return InvalidSpecExitCode;
            }

            return Run(spec);
        }

        /// <summary>
        /// 连接 UI 的命名管道；连上后先发 ready（回带 nonce）完成握手，再进入子进程转发阶段。
        /// </summary>
        private static int Run(LaunchSpec spec)
        {
            NamedPipeClientStream pipe = null;
            StreamReader commands = null;
            try
            {
                // 客户端也必须用 PipeOptions.Asynchronous：net48 的同步管道上，一个线程里阻塞中的读
                // 会卡住同一句柄上的写，而本进程必须“边转发输出边等 stop/EOF”。异步句柄让阻塞读与写互不干扰
                // （UI 侧的服务端本来就是 PipeOptions.Asynchronous）。
                try
                {
                    pipe = new NamedPipeClientStream(".", spec.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                }
                catch (Exception)
                {
                    // 非法管道名等：spec 虽能解析，但这条管道永远连不上。
                    return ConnectFailedExitCode;
                }

                try
                {
                    pipe.Connect(ConnectTimeoutMs);
                }
                catch (Exception)
                {
                    // 管道不存在 / 15s 内没等到 / 访问被拒：没有 UI 就没有任何可转发的东西。
                    return ConnectFailedExitCode;
                }

                commands = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                var sender = new IpcSender(pipe);
                if (!sender.SendReady(spec.Nonce))
                {
                    // 握手帧都写不出去，说明管道在连接后立刻断了。
                    return ConnectFailedExitCode;
                }

                return new Session(spec, sender, commands).Run();
            }
            finally
            {
                // 先关读端再关管道；任何一次释放失败都不能把 WinExe 变成未处理异常。
                TryDispose(commands);
                TryDispose(pipe);
            }
        }

        /// <summary>
        /// 解析命令行。成功返回 0 并给出 spec；参数组合不受支持返回 2；spec 本身无法使用返回 3。
        /// </summary>
        private static int TryLoadSpec(string[] args, out LaunchSpec spec)
        {
            spec = null;
            string base64 = null;
            string specFile = null;
            bool sawSpecFlag = false;
            bool sawUnknown = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (arg == "--spec" || arg == "--spec-file")
                {
                    sawSpecFlag = true;
                    if (i + 1 >= args.Length)
                    {
                        // 开关后面没有值。
                        return InvalidSpecExitCode;
                    }

                    string value = args[++i];
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return InvalidSpecExitCode;
                    }

                    if (arg == "--spec")
                    {
                        if (base64 != null)
                        {
                            return InvalidSpecExitCode;
                        }

                        base64 = value;
                    }
                    else
                    {
                        if (specFile != null)
                        {
                            return InvalidSpecExitCode;
                        }

                        specFile = value;
                    }
                }
                else
                {
                    sawUnknown = true;
                }
            }

            if (!sawSpecFlag || sawUnknown)
            {
                // 没有任何 spec 参数（或混入未知参数）：沿用 Task 1 的“不支持的调用”退出码。
                return UnsupportedArgumentsExitCode;
            }

            if ((base64 == null) == (specFile == null))
            {
                // 两个来源都给 / 都不给：spec 只能有一种来源。
                return InvalidSpecExitCode;
            }

            try
            {
                spec = specFile != null
                    ? ReadSpecFile(specFile)
                    : LaunchSpecCodec.FromBase64(base64);
            }
            catch (Exception)
            {
                // 坏 base64 / 坏 JSON / 读文件失败：一律按无效 spec 处理，不把异常漏出 WinExe。
                return InvalidSpecExitCode;
            }

            return 0;
        }

        /// <summary>读 --spec-file 的 UTF-8 JSON，读完立即删除临时文件（无论解析是否成功）。</summary>
        private static LaunchSpec ReadSpecFile(string path)
        {
            string json;
            try
            {
                // 严格 UTF-8：非法字节必须报错，不能被替换字符悄悄吞掉。
                json = File.ReadAllText(path, new UTF8Encoding(false, true));
            }
            finally
            {
                TryDeleteFile(path);
            }

            return LaunchSpecCodec.FromJson(json);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 删不掉临时文件只影响清理，不影响本次启动结论。
            }
        }

        /// <summary>spec 的最低可用条件：管道名非空、program 是非空的现存文件。</summary>
        private static bool IsSpecUsable(LaunchSpec spec)
        {
            return spec != null
                   && !string.IsNullOrWhiteSpace(spec.Pipe)
                   && !string.IsNullOrWhiteSpace(spec.Program)
                   && File.Exists(spec.Program);
        }

        private static ProcessStartInfo CreateStartInfo(LaunchSpec spec)
        {
            return new ProcessStartInfo
            {
                FileName = spec.Program,
                Arguments = spec.Arguments ?? "",
                WorkingDirectory = spec.WorkingDirectory ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        /// <summary>Win32 错误码 → 中文说明；740 在 runner 这里表示“目标程序自己要求管理员”，无法替它提权。</summary>
        private static string DescribeWin32Error(int code)
        {
            switch (code)
            {
                case 740:
                    return "需要管理员权限（目标程序要求提权，runner 无法代为提权）";
                case 1223:
                    return "已取消管理员授权";
                case 1314:
                    return "权限不足";
                case 2:
                    return "文件不存在";
                case 5:
                    return "拒绝访问";
                case 193:
                    return "不是有效的可执行文件";
                default:
                    return "未知错误";
            }
        }

        private static void TryKill(Process process)
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
                }
            }
            catch (Exception)
            {
                // 进程已经退出 / 句柄失效：终止是尽力而为。
            }
        }

        private static void WaitForExitQuietly(Process process, int timeoutMs)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                process.WaitForExit(timeoutMs);
            }
            catch (Exception)
            {
                // 错误路径上的等待只用于收尾，失败不影响退出码。
            }
        }

        /// <summary>
        /// 等待子进程退出，但绝不调用无参 <c>WaitForExit()</c>：它等价于 <c>WaitForExit(-1)</c>，
        /// 会在进程退出后无超时地等 <c>AsyncStreamReader.WaitUtilEOF()</c>。子进程的孙进程若继承
        /// 重定向 stdout/stderr 写端，EOF 要等整棵树结束才来，relay 会一直卡住。这里只轮询进程句柄
        /// （<c>HasExited</c>），子进程活多久就等多久，但不会被管道 EOF 拖住。
        /// </summary>
        private static void WaitForExitWithoutDrain(Process process)
        {
            if (process == null)
            {
                return;
            }

            while (true)
            {
                try
                {
                    if (process.HasExited)
                    {
                        return;
                    }
                }
                catch (Exception)
                {
                    // 句柄失效按“已退出”处理：退出码由 GetExitCodeSafe 兜底成 -1。
                    return;
                }

                Thread.Sleep(20);
            }
        }

        /// <summary>
        /// 取消异步输出后续的事件派发；未发起过 BeginOutputReadLine/BeginErrorReadLine 或已经结束时
        /// 静默跳过。注意：这不会让阻塞在管道读上的读者线程立即返回，也不是收尾有界的原因
        /// （那来自 <see cref="WaitForExitWithoutDrain"/> 的句柄轮询）；它只是停止尾部输出派发。
        /// </summary>
        private static void CancelAsyncReaders(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                process.CancelOutputRead();
            }
            catch (Exception)
            {
                // 没有进行中的异步读，或读已经结束：无需处理。
            }

            try
            {
                process.CancelErrorRead();
            }
            catch (Exception)
            {
                // 同上。
            }
        }

        private static int GetProcessIdSafe(Process process)
        {
            try
            {
                return process.Id;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static int GetExitCodeSafe(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch (Exception)
            {
                return -1;
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
                // 释放失败不能影响退出码。
            }
        }

        /// <summary>所有 runner → UI 的写入口：串行化整帧写入，管道一断就记下来并终止子进程。</summary>
        private sealed class IpcSender
        {
            private readonly Stream _stream;
            private readonly object _gate = new object();
            private readonly object _jobGate = new object();
            private JobObjectManager _job;
            private bool _broken;

            public IpcSender(Stream stream)
            {
                _stream = stream;
            }

            public bool IsBroken
            {
                get
                {
                    lock (_gate)
                    {
                        return _broken;
                    }
                }
            }

            public void AttachJob(JobObjectManager job)
            {
                lock (_jobGate)
                {
                    _job = job;
                }
            }

            public bool SendReady(string nonce)
            {
                return Send(new IpcMessage { Type = "ready", Nonce = nonce });
            }

            public bool SendStarted(int pid)
            {
                return Send(new IpcMessage { Type = "started", Pid = pid });
            }

            public bool SendOutput(string stream, string data)
            {
                return Send(new IpcMessage { Type = "out", Stream = stream, Data = data });
            }

            public bool SendExit(int exitCode)
            {
                return Send(new IpcMessage { Type = "exit", ExitCode = exitCode });
            }

            public bool SendError(string data)
            {
                return Send(new IpcMessage { Type = "error", Data = data });
            }

            /// <summary>写一帧；失败时返回 false，并把管道标记为断开（之后所有写直接短路）。</summary>
            public bool Send(IpcMessage message)
            {
                bool brokenNow = false;
                lock (_gate)
                {
                    if (_broken)
                    {
                        return false;
                    }

                    try
                    {
                        IpcFraming.Write(_stream, message);
                        return true;
                    }
                    catch (Exception)
                    {
                        _broken = true;
                        brokenNow = true;
                    }
                }

                // 管道已断（UI 崩溃 / 关闭）：子进程没人要了，必须终止，否则 runner 会为一个空管道活着。
                if (brokenNow)
                {
                    TerminateChild();
                }

                return false;
            }

            /// <summary>终止 Job 内的整棵子进程树；幂等，可在任意线程调用。</summary>
            public void TerminateChild()
            {
                JobObjectManager job;
                lock (_jobGate)
                {
                    // 只取一次引用：这里刻意不拿写锁 _gate——输出写阻塞在管道上时，
                    // stop/EOF 线程仍必须能终止子进程，否则会为了一个没人读的管道把自己也锁死。
                    job = _job;
                }

                if (job != null)
                {
                    try
                    {
                        job.Terminate();
                    }
                    catch (Exception)
                    {
                        // 终止失败只能等 Job 句柄关闭时的 KILL_ON_JOB_CLOSE 兜底。
                    }
                }
            }
        }

        /// <summary>
        /// 单次中转会话：启动子进程、加入 Job、异步转发 stdout/stderr，并在 UI 发 stop / 管道消失时收尾。
        /// 字段由主线程与管道读线程共享，写操作一律用 volatile / 锁保护。
        /// </summary>
        private sealed class Session
        {
            private readonly LaunchSpec _spec;
            private readonly IpcSender _sender;
            private readonly StreamReader _commands;

            private Process _process;
            private JobObjectManager _job;

            /// <summary>正常收尾标记：为 true 时管道读线程看到 EOF 不再抢跑 Environment.Exit。</summary>
            private volatile bool _completed;

            public Session(LaunchSpec spec, IpcSender sender, StreamReader commands)
            {
                _spec = spec;
                _sender = sender;
                _commands = commands;
            }

            public int Run()
            {
                try
                {
                    return RunCore();
                }
                catch (Exception ex)
                {
                    // WinExe 的未处理异常会弹出系统崩溃对话框；这里统一转成 error 帧 + 退出码。
                    _sender.SendError("中转进程内部错误：" + ex.Message);
                    _sender.TerminateChild();
                    return ChildStartFailedExitCode;
                }
                finally
                {
                    // 收尾：置完成标记阻止读线程再 Exit；关 Job（KILL_ON_JOB_CLOSE 兜底杀整树）并释放进程句柄。
                    _completed = true;
                    TryDispose(_job);
                    _job = null;
                    TryDispose(_process);
                    _process = null;
                }
            }

            private int RunCore()
            {
                _process = new Process { StartInfo = CreateStartInfo(_spec) };

                // 先挂输出回调再启动：避免子进程一开始输出就被 BeginOutputReadLine 抢跑却没有处理器。
                _process.OutputDataReceived += OnOutputDataReceived;
                _process.ErrorDataReceived += OnErrorDataReceived;

                // 环境覆盖与 Start 必须在 ChildEnvironment 的同一把锁内完成（否则零覆盖项的启动会
                // 在锁外从活环境捕获子进程环境块，重新打开 Task 8a 修掉的那个污染窗口）。
                bool started;
                string environmentError;
                try
                {
                    started = ChildEnvironment.ApplyAndStart(
                        _process.StartInfo,
                        _spec.EnvOverrides,
                        msg => _sender.SendError(msg),
                        () => _process.Start(),
                        out environmentError);
                }
                catch (Win32Exception ex)
                {
                    _sender.SendError("启动子进程失败：" + DescribeWin32Error(ex.NativeErrorCode)
                                      + "（Windows 错误码 " + ex.NativeErrorCode + "）");
                    return ChildStartFailedExitCode;
                }
                catch (Exception ex)
                {
                    _sender.SendError("启动子进程失败：" + ex.Message);
                    return ChildStartFailedExitCode;
                }

                if (!string.IsNullOrEmpty(environmentError))
                {
                    _sender.SendError("启动子进程失败：" + environmentError);
                    return ChildStartFailedExitCode;
                }

                if (!started)
                {
                    _sender.SendError("启动子进程失败：无法创建子进程。");
                    return ChildStartFailedExitCode;
                }

                if (_sender.IsBroken)
                {
                    // UI 在子进程启动的同一刻消失了：不能为了一个没人读的管道继续养着它。
                    TryKill(_process);
                    return 0;
                }

                try
                {
                    _job = new JobObjectManager();
                }
                catch (Exception ex)
                {
                    _sender.SendError("创建 Job Object 失败，无法保证子进程树随中转进程退出：" + ex.Message);
                    TryKill(_process);
                    WaitForExitQuietly(_process, ChildKillTimeoutMs);
                    return ChildStartFailedExitCode;
                }

                _sender.AttachJob(_job);
                try
                {
                    // 子进程可能刚好已退出（或已失效）；加入失败只失去兜底清理，不影响已经开始的转发。
                    _job.AddProcess(_process);
                }
                catch (Exception)
                {
                }

                int childPid = GetProcessIdSafe(_process);
                if (childPid <= 0)
                {
                    _sender.SendError("启动子进程失败：拿不到子进程 PID。");
                    _sender.TerminateChild();
                    return ChildStartFailedExitCode;
                }

                if (!_sender.SendStarted(childPid))
                {
                    // 管道已断；IpcSender 已终止子进程，这里等它收尾后安静退出。
                    _sender.TerminateChild();
                    WaitForExitQuietly(_process, ChildKillTimeoutMs);
                    return 0;
                }

                // started 之后再开始异步读，保证 UI 看到的顺序是 ready → started → out。
                try
                {
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    _sender.SendError("无法读取子进程输出：" + ex.Message);
                }

                StartCommandReader();

                // 只等“子进程退出”，不等异步输出排空：无参 WaitForExit() 等价于 WaitForExit(-1)，
                // 会在进程退出后无超时地等 AsyncStreamReader.WaitUtilEOF()。子进程的孙进程若继承
                // 重定向 stdout/stderr 写端，EOF 要等整棵树结束才来，relay 就会一直卡在这里
                // （子进程已退出、UI 却仍显示运行中）。HasExited 是纯句柄查询，不会被管道拖住。
                WaitForExitWithoutDrain(_process);
                int exitCode = GetExitCodeSafe(_process);

                // 收尾阶段不再派发尚未送达的尾部输出（README 已知限制）；取消读流的事件派发，
                // 而不是去等 EOF——等待有界性来自上面的句柄轮询。
                CancelAsyncReaders(_process);

                // 先置完成标记再发 exit：即使 UI 读完立刻关管道，读线程也不会在 exit 写出去之前抢跑 Exit。
                _completed = true;
                _sender.SendExit(exitCode);

                // 先发 exit，再关 Job：退出帧已 flush，Job 关闭负责把残留的孙进程一并终结。
                TryDispose(_job);
                _job = null;
                return 0;
            }

            /// <summary>后台线程读 UI 命令：stop → 终止整树；EOF/异常（UI 消失）→ 终止整树后退出。</summary>
            private void StartCommandReader()
            {
                // 命令泵会在子进程存活期间一直阻塞在管道读上，必须用 LongRunning 拿到
                // TaskScheduler.Default 的专用线程；丢进线程池会长期占住一个工作线程。
                // LongRunning 线程是后台线程（等价于原来的 IsBackground = true），
                // UI 关闭管道后 ReadCommands 返回，泵线程随 runner 一起结束。
                // 泵任务没有 await 点：异常若逃出 ReadCommands 的 try/finally，必须被观测并记录。
                Task pump = Task.Factory.StartNew(
                    ReadCommands,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                ObserveFailure(pump);
            }

            /// <summary>
            /// 观测 fire-and-forget 后台任务的逃逸异常并记一条调试输出（与 RunnerClient 的同名习惯一致）。
            /// 先取 Exception 标记为已观察，避免未观察异常在 GC 时冒出来。
            /// </summary>
            private static void ObserveFailure(Task task)
            {
                task.ContinueWith(t =>
                {
                    AggregateException error = t.Exception;
                    Debug.WriteLine("后台任务异常：" + error);
                }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }

            private void ReadCommands()
            {
                try
                {
                    while (true)
                    {
                        IpcMessage message;
                        try
                        {
                            message = IpcFraming.Read(_commands);
                        }
                        catch (Exception)
                        {
                            break;
                        }

                        if (message == null)
                        {
                            break;
                        }

                        if (string.Equals(message.Type, "stop", StringComparison.Ordinal))
                        {
                            // UI 明确要求停止：终止 Job 内的整棵进程树，主线程随后照常发 exit。
                            _sender.TerminateChild();
                        }
                    }
                }
                finally
                {
                    // 正常收尾时 _completed 已置位，这里只结束线程；
                    // 否则说明 UI 走了（管道关闭/进程崩溃），必须终止子进程树并让 runner 立即退出。
                    if (!_completed)
                    {
                        _sender.TerminateChild();
                        Environment.Exit(0);
                    }
                }
            }

            private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    _sender.SendOutput("o", e.Data);
                }
            }

            private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    _sender.SendOutput("e", e.Data);
                }
            }
        }
    }
}
