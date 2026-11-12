using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using gostGUI.Shared;

namespace gostGUI
{
    /// <summary>
    /// 同权限直连启动的进程管理器：由 LaunchPipeline 装配启动参数，应用工作目录与环境变量覆盖，
    /// 把 stdout / stderr / 系统消息分别上报，并用 Job Object 在工具退出或崩溃时清理子进程树。
    /// <para>
    /// 调试开关 GOSTGUI_FORCE_RUNNER=1 时全部改走中转进程（RunnerSession），事件与状态语义保持一致；
    /// 默认路径不变，仍然在 UI 进程内直接启动子进程。
    /// </para>
    /// </summary>
    public class ProcessManager : IDisposable
    {
        /// <summary>与 gostGUI.exe 放在同一目录的中转进程文件名。</summary>
        private const string RunnerFileName = "gostGUI.Runner.exe";

        /// <summary>停止中转会话的上限：超时后 RunnerSession 会杀掉 runner，由其 Job 带走子进程树。</summary>
        private static readonly TimeSpan RunnerStopTimeout = TimeSpan.FromSeconds(8);

        private readonly Dictionary<ConfigItem, Process> _processes = new Dictionary<ConfigItem, Process>();
        private readonly Dictionary<ConfigItem, RunnerSession> _sessions =
            new Dictionary<ConfigItem, RunnerSession>();
        private readonly object _lock = new object();
        private readonly JobObjectManager _jobManager;

        public event Action<ConfigItem, LogKind, string> OutputReceived;
        public event Action<ConfigItem, int> ProcessExited;
        public event Action<ConfigItem> ProcessStarted;
        public event Action<ConfigItem> ProcessStopped;

        /// <summary>最近一次启动失败是否源于“需要管理员权限”（Win32 740）；供 Task 9 的提权路径决策。</summary>
        public bool LastStartErrorIsElevationRequired { get; private set; }

        public ProcessManager()
        {
            try
            {
                _jobManager = new JobObjectManager();
            }
            catch (Exception ex)
            {
                // Propagate the exception to be handled by the UI layer.
                throw new InvalidOperationException("Failed to create Job Object for process management.", ex);
            }
        }

        public bool IsProcessRunning(ConfigItem item)
        {
            if (item == null) return false;
            lock (_lock)
            {
                // 中转会话在 Exited 回调里从字典移除，登记期间就代表“运行中”。
                if (_sessions.ContainsKey(item))
                {
                    return true;
                }

                if (_processes.TryGetValue(item, out Process p) && p != null)
                {
                    try
                    {
                        return !p.HasExited;
                    }
                    catch
                    {
                        return false;
                    }
                }
                return false;
            }
        }

        /// <summary>返回正在运行的子进程 PID；未运行、已退出或句柄失效时返回 0，绝不抛异常。</summary>
        public int GetChildPid(ConfigItem item)
        {
            if (item == null) return 0;
            lock (_lock)
            {
                if (_sessions.TryGetValue(item, out RunnerSession session) && session != null)
                {
                    // started 握手已经保证 ChildPid > 0；这里仍按“拿不到就返回 0”的既有契约收口。
                    return session.ChildPid > 0 ? session.ChildPid : 0;
                }

                if (!_processes.TryGetValue(item, out Process p) || p == null)
                {
                    return 0;
                }

                try
                {
                    if (p.HasExited)
                    {
                        return 0;
                    }

                    int pid = p.Id;

                    // Id 与 HasExited 之间进程可能刚好退出：取到 PID 后再确认一次，避免返回“已死”的 PID。
                    return p.HasExited ? 0 : pid;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// 按权限矩阵启动一个配置项：权限匹配时在 UI 进程内直连，权限不匹配时改走中转进程（提权 / 降权）。
        /// R17：整段逻辑包在兜底里，任何异常都只记一条 System 日志并返回 false，绝不漏给界面线程。
        /// </summary>
        public bool StartProcess(ConfigItem configItem)
        {
            if (configItem == null)
            {
                return false;
            }

            LastStartErrorIsElevationRequired = false;
            try
            {
                return StartProcessCore(configItem);
            }
            catch (Exception ex)
            {
                try
                {
                    LogSystem(configItem, "启动失败：" + ex.Message);
                }
                catch (Exception)
                {
                    // 连日志回调都抛异常时只能吞掉：StartProcess 的契约是绝不抛。
                }

                return false;
            }
        }

        private bool StartProcessCore(ConfigItem configItem)
        {
            lock (_lock)
            {
                if (IsProcessRunning(configItem))
                {
                    // Already running, no action needed.
                    return true;
                }
            }

            Process p = null;
            RunnerSession session = null;
            StartSequence sequence = null;
            string startupError = null;
            bool elevationRequired = false;
            RunnerLaunchMode? launchMode = null;

            // 父环境快照由 ChildEnvironment 在它自己的静态锁内完成：并发启动时，
            // 快照绝不会看到另一个项临时写进本进程环境的覆盖值。
            // LaunchPipeline 只消费这份快照（COMSPEC 也从快照派生的有效环境取），Build 本身不再读活环境；
            // 随后 ApplyAndStart 把环境应用与 p.Start() 放进同一把锁，子进程的环境捕获不会落在别的项的临时覆盖窗口里。
            // 固定顺序（有效环境 → 解析 program → 解析 workDir）由 LaunchPipeline 负责；
            // 解析失败只写一条 System 日志并保持“未运行”状态，绝不把异常抛给调用方。
            LaunchRequest request = LaunchPipeline.Build(
                configItem, Common.GetApplicationPath(), ChildEnvironment.SnapshotParentEnvironment());

            if (!string.IsNullOrEmpty(request.Error))
            {
                startupError = "启动失败：" + request.Error;
            }
            else
            {
                // 调试开关优先：GOSTGUI_FORCE_RUNNER=1 时全部走同权限中转，用于在没有 UAC 的情况下
                // 验证管道与 runner 全链路。其余情况严格按权限矩阵选路，没有“权限不匹配也直连”的回落。
                launchMode = IsForceRunnerEnabled()
                    ? RunnerLaunchMode.SameIntegrity
                    : DecideMode(ElevationManager.IsProcessElevated(), configItem.RunAsAdmin);
            }

            if (!string.IsNullOrEmpty(startupError))
            {
                // 管线已给出中文失败原因：跳过启动，直接进入下面的失败收尾。
            }
            else if (launchMode.HasValue)
            {
                try
                {
                    sequence = new StartSequence();
                    string runnerPath = Path.Combine(Common.GetApplicationPath(), RunnerFileName);

                    if (launchMode.Value == RunnerLaunchMode.Elevated)
                    {
                        // 记下这是一次 UAC 提权请求：取消/失败时日志里能看出启动卡在授权环节。
                        TryLogSystem(configItem, "正在请求管理员授权（UAC），请在弹窗中确认。");
                    }

                    session = RunnerSession.StartAsync(request, launchMode.Value,
                            runnerPath, Common.GetApplicationPath(), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (session == null)
                    {
                        startupError = "启动失败：中转进程没有返回有效会话。";
                    }
                    else
                    {
                        // 设计决策 13 / §6.4：每个中转会话都记一行真实身份与基础环境来源。
                        // runner 可能被 UAC 换成了另一个管理员账号，所以写 runner 的实际令牌身份；
                        // 基础环境只保证“本项 env 覆盖一定生效”，不保证与 UI 进程逐字相同。
                        TryLogSystem(configItem,
                            "中转进程身份：" + session.RunnerIdentity
                            + "；子进程基础环境：" + session.BaseEnvironmentSource + "。");
                        if (launchMode.Value == RunnerLaunchMode.Elevated)
                        {
                            TryLogSystem(configItem,
                                "已以管理员身份启动中转进程（若在 UAC 中输入了其他管理员账号，子进程以该账号身份运行）。");
                        }
                    }
                }
                catch (Win32Exception ex)
                {
                    elevationRequired = ex.NativeErrorCode == 740;
                    if (launchMode.Value == RunnerLaunchMode.StandardUser)
                    {
                        // 设计 §6.4：降权失败必须明确写出“请改用标准权限运行 gostGUI”，
                        // 并且绝不回落到继承管理员权限再启动。
                        startupError = "无法以标准权限启动：请改用标准权限运行 gostGUI（"
                                       + ElevationManager.DescribeWin32Error(ex.NativeErrorCode)
                                       + "，Windows 错误码 " + ex.NativeErrorCode + "）";
                    }
                    else if (ex.NativeErrorCode == 1223)
                    {
                        // UAC 取消就是单独一行“已取消管理员授权”，不加任何前后缀（设计 §6.4）。
                        startupError = ElevationManager.DescribeWin32Error(1223);
                    }
                    else
                    {
                        startupError = $"启动失败：{ElevationManager.DescribeWin32Error(ex.NativeErrorCode)}（Windows 错误码 {ex.NativeErrorCode}）";
                    }
                }
                catch (Exception ex)
                {
                    // 中转进程启动/握手失败（含降权路径上非 Win32 的失败）都在这里落成 System 日志。
                    startupError = "启动失败：" + ex.Message;
                }
            }
            else
            {
                try
                {
                    ProcessStartInfo startInfo = CreateStartInfo(request);
                    p = new Process
                    {
                        StartInfo = startInfo,
                        EnableRaisingEvents = true
                    };
                    sequence = new StartSequence();
                    AttachProcessHandlers(p, configItem, sequence);

                    // 覆盖项（含重名规范化/临时环境兜底与 32767 上限守卫）与 p.Start() 由同一把锁覆盖：
                    // 零覆盖项时 Process.Start 也会从活环境捕获子进程环境块，必须与别的项的临时覆盖窗口互斥。
                    // start 的异常（Win32Exception 等）由 ChildEnvironment 原样抛回，交给下面的 catch 识别 740。
                    bool started = ChildEnvironment.ApplyAndStart(
                        startInfo,
                        request.EnvOverrides,
                        msg => LogSystem(configItem, msg),
                        () => p.Start(),
                        out string envError);

                    if (!string.IsNullOrEmpty(envError))
                    {
                        // 放弃启动：不登记进程，管理器状态保持“未运行”。
                        startupError = "启动失败：" + envError;
                    }
                    else if (!started)
                    {
                        startupError = "启动失败：无法创建子进程。";
                    }
                }
                catch (Win32Exception ex)
                {
                    elevationRequired = ex.NativeErrorCode == 740;
                    startupError = $"启动失败：{ElevationManager.DescribeWin32Error(ex.NativeErrorCode)}（Windows 错误码 {ex.NativeErrorCode}）";
                }
                catch (InvalidOperationException ex)
                {
                    startupError = "启动失败：" + ex.Message;
                }
                catch (Exception ex)
                {
                    // 兜底：StartProcess 的公开契约是返回 bool，任何启动期异常都不能漏给界面线程。
                    startupError = "启动失败：" + ex.Message;
                }
            }

            if (!string.IsNullOrEmpty(startupError))
            {
                LastStartErrorIsElevationRequired = elevationRequired;
                if (p != null)
                {
                    SafeDispose(p);
                }

                if (session != null)
                {
                    try
                    {
                        session.Dispose();
                    }
                    catch
                    {
                        // 启动失败路径上的清理尽力而为。
                    }
                }

                try
                {
                    LogSystem(configItem, startupError);
                }
                catch
                {
                    // 同上：日志回调故障不能改变“启动失败”的返回值。
                }

                return false;
            }

            // 启动成功后，登记 / 异步读 / 事件阶段的异常都只能记日志：
            // 子进程确实已经在跑，不能再把 StartProcess 变成抛异常或返回失败。
            if (session != null)
            {
                try
                {
                    RegisterRunnerSession(configItem, session, sequence);
                }
                catch (Exception ex)
                {
                    try
                    {
                        LogSystem(configItem, "登记中转会话失败：" + ex.Message);
                    }
                    catch { }
                }
            }
            else
            {
                try
                {
                    _jobManager.AddProcess(p);
                }
                catch (Exception)
                {
                    // 子进程可能已退出（句柄失效）或已在别的 Job 里；加入 Job 失败不影响本次启动结论。
                }

                try
                {
                    // 在锁内完成“检查未退出 + 登记”，与 Exited 回调里的“锁内移除”互斥：
                    // 快速退出的子进程不会在字典里留下“已退出但仍登记为运行中”的记录。
                    lock (_lock)
                    {
                        if (!HasExitedSafe(p))
                        {
                            _processes[configItem] = p;
                        }
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        LogSystem(configItem, "登记子进程失败：" + ex.Message);
                    }
                    catch { }
                }

                // 先启动异步读，再触发 started；子进程已退出时读线程可能起不来，退出事件仍会照常上报。
                try
                {
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    try
                    {
                        LogSystem(configItem, "无法读取子进程输出：" + ex.Message);
                    }
                    catch { }
                }
            }

            // 无论登记/读线程是否出过问题，started 事件都必须尝试发出，否则退出事件会永远滞留在记账状态。
            try
            {
                sequence.NotifyStarted(
                    () => ProcessStarted?.Invoke(configItem),
                    code => ProcessExited?.Invoke(configItem, code));
            }
            catch (Exception ex)
            {
                try
                {
                    LogSystem(configItem, "启动事件处理失败：" + ex.Message);
                }
                catch
                {
                    // 连日志回调都抛异常时只能吞掉：StartProcess 已承诺不抛。
                }
            }

            return true;
        }

        /// <summary>
        /// 同步停止入口（行为不变）：现有调用点（FormMain 的 Task.Run 回调、<see cref="Dispose"/>）继续使用它。
        /// 真正的工作由 <see cref="StopProcessAsync"/> 完成；这里的阻塞只等待从不向 UI 线程投递续体的任务，
        /// 因此即使调用线程是 UI 线程也不会死锁。
        /// </summary>
        public void StopProcess(ConfigItem configItem)
        {
            // 保持既有 8 秒上限：旧实现只对中转会话使用 RunnerStopTimeout；直连子进程现在的
            // kill 后等待同样受这个上限约束（Kill 成功时进程会立刻退出，实际等待只有毫秒级）。
            StopProcessAsync(configItem, RunnerStopTimeout).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 异步停止一个配置项：中转会话走 <see cref="RunnerSession.StopAsync(TimeSpan)"/>，直连子进程
        /// kill 后等待退出（最多 <paramref name="timeout"/>）。登记项的移出仍在锁内同步完成，
        /// 与 <see cref="IsProcessRunning"/> / 启动路径的“先查后改”保持互斥；公开事件一律在锁外触发。
        /// <para>
        /// 死锁说明：本方法（以及 <see cref="StopAllProcessesAsync"/>、<see cref="RunnerSession.StopAsync"/>）
        /// 的每个 await 都带 ConfigureAwait(false)，且没有任何续体需要 UI 线程；同步包装用
        /// GetAwaiter().GetResult() 阻塞的调用线程不会被等待中的续体回投，因此不会 sync-over-async 死锁。
        /// </para>
        /// </summary>
        public async Task StopProcessAsync(ConfigItem configItem, TimeSpan timeout)
        {
            if (configItem == null) return;

            Process p = null;
            RunnerSession session = null;
            lock (_lock)
            {
                if (_processes.TryGetValue(configItem, out p))
                {
                    _processes.Remove(configItem);
                }

                if (_sessions.TryGetValue(configItem, out session))
                {
                    _sessions.Remove(configItem);
                }
            }

            if (session != null)
            {
                // R27：先礼后兵——发 stop 等 exit，超时后 RunnerSession 会杀 runner，由 runner 的 Job 带走子进程树。
                try
                {
                    await session.StopAsync(timeout).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogSystem(configItem, "停止中转进程失败：" + ex.Message);
                }
                finally
                {
                    try
                    {
                        session.Dispose();
                    }
                    catch { }
                    ProcessStopped?.Invoke(configItem);
                }

                return;
            }

            if (p != null)
            {
                await StopDirectProcessAsync(configItem, p, timeout).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 直连子进程的异步停止：kill 后等待退出，最多 <paramref name="timeout"/>。
        /// <para>
        /// 事件顺序保持不变：旧实现是 Kill 后立即 Dispose，本次构建上 20 次采样中进程退出回调均抢不过
        /// Dispose，订阅者没有观测到 ProcessExited（直连停止只报 ProcessStopped；这是实测结果，不是普遍保证，
        /// 回调时序仍取决于运行时）。这里在 kill 前显式关闭退出事件的观察者，再等待退出，因此不会多出一条
        /// “进程已退出”的 UI 日志；kill 或等待失败的路径仍与旧实现一样只记一条日志并立刻收尾，不会白等 timeout。
        /// </para>
        /// </summary>
        private async Task StopDirectProcessAsync(ConfigItem configItem, Process process, TimeSpan timeout)
        {
            try
            {
                if (!process.HasExited)
                {
                    // 保留“主动停止不报 ProcessExited”的既有语义（见方法注释）。
                    process.EnableRaisingEvents = false;
                    process.Kill();
                }

                // WaitForExit 也可能抛（句柄失效/等待被中断）：与 Kill 同理，记一条日志后继续 teardown。
                await Task.Run(() => process.WaitForExit(TimeoutMilliseconds(timeout)))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogSystem(configItem, "停止进程失败：" + ex.Message);
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch { }
                ProcessStopped?.Invoke(configItem);
            }
        }

        /// <summary>把 TimeSpan 收敛成 WaitForExit 可接受的上限；非正值表示不等待（与 RunnerSession.StopAsync 一致）。</summary>
        private static int TimeoutMilliseconds(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                return 0;
            }

            return timeout.TotalMilliseconds >= int.MaxValue ? int.MaxValue : (int)timeout.TotalMilliseconds;
        }

        /// <summary>同步停止入口（行为不变）：逐项调用 <see cref="StopProcessAsync"/>。</summary>
        public void StopAllProcesses()
        {
            StopAllProcessesAsync(RunnerStopTimeout).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 依次停止当前登记的全部项（顺序与旧实现一致）。每一项获得完整 timeout，与旧实现
        /// “每个 StopProcess 各自等 8 秒”的语义相同；任何 await 都不回投 UI 线程。
        /// </summary>
        public async Task StopAllProcessesAsync(TimeSpan timeout)
        {
            List<ConfigItem> runningItems;
            lock (_lock)
            {
                // Create a copy of keys to avoid modification during iteration
                var running = new HashSet<ConfigItem>(_processes.Keys);
                running.UnionWith(_sessions.Keys);
                runningItems = new List<ConfigItem>(running);
            }

            foreach (ConfigItem item in runningItems)
            {
                await StopProcessAsync(item, timeout).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            StopAllProcesses();
            _jobManager?.Dispose();
        }

        /// <summary>
        /// 权限矩阵选路（纯函数）：返回 null 表示在 UI 进程内直连启动；否则返回中转进程的启动模式。
        /// 标准进程的管理员项必须走 UAC 提权中转；管理员进程的普通项必须走降权中转，绝不回落到继承当前权限。
        /// </summary>
        internal static RunnerLaunchMode? DecideMode(bool launcherElevated, bool runAsAdmin)
        {
            if (runAsAdmin)
            {
                // 已经提权：直连即可；未提权：必须让 runner 穿过 UAC 拿管理员令牌。
                return launcherElevated ? (RunnerLaunchMode?)null : RunnerLaunchMode.Elevated;
            }

            // 普通项在管理员进程里必须降权（任务 10 的 explorer 令牌路径）；标准进程里本来就同权限，直连。
            return launcherElevated ? RunnerLaunchMode.StandardUser : (RunnerLaunchMode?)null;
        }

        /// <summary>调试开关：只有精确等于 "1" 才强制走 runner；未设置或其它值保持直连行为。</summary>
        private static bool IsForceRunnerEnabled()
        {
            return string.Equals(Environment.GetEnvironmentVariable("GOSTGUI_FORCE_RUNNER"), "1",
                StringComparison.Ordinal);
        }

        /// <summary>
        /// 登记中转会话：会话先行登记再挂事件，Exited 回调按“只移除自己这一条”的规则清理字典；
        /// 会话在挂事件前就退出时，RunnerSession 会补发缓冲的退出事件，sequence 负责 started 先于 exit 的时序。
        /// </summary>
        private void RegisterRunnerSession(ConfigItem configItem, RunnerSession session, StartSequence sequence)
        {
            lock (_lock)
            {
                _sessions[configItem] = session;
            }

            session.Output += (kind, text) =>
            {
                OutputReceived?.Invoke(configItem, kind, text + Environment.NewLine);
            };

            session.Exited += code =>
            {
                lock (_lock)
                {
                    // 只移除自己这一条：停止后立刻重启同一个配置项时，旧会话的退出回调
                    // 不能把刚登记的新会话从字典里删掉。
                    if (_sessions.TryGetValue(configItem, out RunnerSession current) &&
                        ReferenceEquals(current, session))
                    {
                        _sessions.Remove(configItem);
                    }
                }

                sequence.NotifyExit(code, c => ProcessExited?.Invoke(configItem, c));
            };
        }

        /// <summary>管理器自身产生的消息（启动/停止/错误）一律走 System 类别。</summary>
        private void LogSystem(ConfigItem item, string message)
        {
            OutputReceived?.Invoke(item, LogKind.System, message + Environment.NewLine);
        }

        /// <summary>信息性 System 日志：订阅者的异常不能反过来影响本次启动的结论。</summary>
        private void TryLogSystem(ConfigItem item, string message)
        {
            try
            {
                LogSystem(item, message);
            }
            catch (Exception)
            {
                // 日志回调故障只丢这一行提示，启动流程照常继续。
            }
        }

        private static ProcessStartInfo CreateStartInfo(LaunchRequest request)
        {
            return new ProcessStartInfo
            {
                FileName = request.FileName,
                Arguments = request.Arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = request.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
        }

        private void AttachProcessHandlers(Process p, ConfigItem configItem, StartSequence sequence)
        {
            p.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    OutputReceived?.Invoke(configItem, LogKind.Stdout, e.Data + Environment.NewLine);
                }
            };
            p.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    OutputReceived?.Invoke(configItem, LogKind.Stderr, e.Data + Environment.NewLine);
                }
            };
            p.Exited += (sender, e) =>
            {
                int exitCode = GetExitCodeSafe(p);
                lock (_lock)
                {
                    // 只移除自己这一条：停止后立刻重启同一个配置项时，旧进程的退出回调
                    // 不能把刚登记的新进程从字典里删掉。
                    if (_processes.TryGetValue(configItem, out Process current) && ReferenceEquals(current, p))
                    {
                        _processes.Remove(configItem);
                    }
                }

                sequence.NotifyExit(exitCode, code => ProcessExited?.Invoke(configItem, code));
            };
        }

        private static int GetExitCodeSafe(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }

        private static bool HasExitedSafe(Process process)
        {
            if (process == null)
            {
                return true;
            }

            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static void SafeDispose(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                process.Dispose();
            }
            catch
            {
                // 启动失败后的清理尽力而为，不掩盖真正的失败原因。
            }
        }

        /// <summary>
        /// 单次启动的事件时序闩：ProcessStarted 只会在 Start() 成功后触发，且一定先于 ProcessExited；
        /// 子进程在 started 回调之前就退出时，退出码先记账，等 started 发完再补发（既不会丢也不会重复）。
        /// 公开事件一律在内部锁外触发。
        /// </summary>
        private sealed class StartSequence
        {
            private readonly object _gate = new object();
            private bool _startInProgress;
            private bool _started;
            private bool _exitRaised;
            private bool _exitPending;
            private int _pendingExitCode = -1;

            public void NotifyStarted(Action raiseStarted, Action<int> raiseExit)
            {
                bool hasPendingExit;
                int pendingExitCode;
                lock (_gate)
                {
                    // 先进入“正在发 started”状态：这期间到达的退出事件只记账，不插队。
                    _startInProgress = true;
                    hasPendingExit = _exitPending;
                    _exitPending = false;
                    pendingExitCode = _pendingExitCode;
                }

                Exception startedError = null;
                try
                {
                    raiseStarted();
                }
                catch (Exception ex)
                {
                    // 公开事件处理器抛异常不能破坏“退出事件一定按序送达”的状态机；
                    // 记下异常，完成状态收尾后再抛给 StartProcess 的兜底日志。
                    startedError = ex;
                }

                lock (_gate)
                {
                    _startInProgress = false;
                    _started = true;

                    if (_exitPending)
                    {
                        _exitPending = false;
                        if (!hasPendingExit)
                        {
                            hasPendingExit = true;
                            pendingExitCode = _pendingExitCode;
                        }
                    }

                    if (hasPendingExit)
                    {
                        _exitRaised = true;
                    }
                }

                if (hasPendingExit)
                {
                    try
                    {
                        raiseExit(pendingExitCode);
                    }
                    catch (Exception)
                    {
                        // 退出回调的异常只影响它自己，启动结论不受影响。
                    }
                }

                if (startedError != null)
                {
                    throw startedError;
                }
            }

            public void NotifyExit(int exitCode, Action<int> raiseExit)
            {
                lock (_gate)
                {
                    if (_startInProgress || !_started)
                    {
                        // started 还没发完：先记账，NotifyStarted 会按顺序补发。
                        if (!_exitPending)
                        {
                            _exitPending = true;
                            _pendingExitCode = exitCode;
                        }
                        return;
                    }

                    if (_exitRaised)
                    {
                        return;
                    }

                    _exitRaised = true;
                }

                raiseExit(exitCode);
            }
        }
    }
}
