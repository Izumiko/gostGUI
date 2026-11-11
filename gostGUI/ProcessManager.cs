using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using gostGUI.Shared;

namespace gostGUI
{
    /// <summary>
    /// 同权限直连启动的进程管理器：由 LaunchPipeline 装配启动参数，应用工作目录与环境变量覆盖，
    /// 把 stdout / stderr / 系统消息分别上报，并用 Job Object 在工具退出或崩溃时清理子进程树。
    /// </summary>
    public class ProcessManager : IDisposable
    {
        private readonly Dictionary<ConfigItem, Process> _processes = new Dictionary<ConfigItem, Process>();
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

        public bool StartProcess(ConfigItem configItem)
        {
            if (configItem == null)
            {
                return false;
            }

            LastStartErrorIsElevationRequired = false;

            lock (_lock)
            {
                if (IsProcessRunning(configItem))
                {
                    // Already running, no action needed.
                    return true;
                }
            }

            Process p = null;
            StartSequence sequence = null;
            string startupError = null;
            bool elevationRequired = false;

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
                    startupError = $"启动失败：{DescribeWin32ErrorLocal(ex.NativeErrorCode)}（Windows 错误码 {ex.NativeErrorCode}）";
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

        public void StopProcess(ConfigItem configItem)
        {
            if (configItem == null) return;
            Process p = null;
            lock (_lock)
            {
                if (_processes.TryGetValue(configItem, out p))
                {
                    _processes.Remove(configItem);
                }
            }

            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                    }
                }
                catch (Exception ex)
                {
                    LogSystem(configItem, "停止进程失败：" + ex.Message);
                }
                finally
                {
                    try
                    {
                        p.Dispose();
                    }
                    catch { }
                    ProcessStopped?.Invoke(configItem);
                }
            }
        }

        public void StopAllProcesses()
        {
            List<ConfigItem> runningItems;
            lock (_lock)
            {
                // Create a copy of keys to avoid modification during iteration
                runningItems = new List<ConfigItem>(_processes.Keys);
            }

            foreach (ConfigItem item in runningItems)
            {
                StopProcess(item);
            }
        }

        public void Dispose()
        {
            StopAllProcesses();
            _jobManager?.Dispose();
        }

        /// <summary>管理器自身产生的消息（启动/停止/错误）一律走 System 类别。</summary>
        private void LogSystem(ConfigItem item, string message)
        {
            OutputReceived?.Invoke(item, LogKind.System, message + Environment.NewLine);
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

        /// <summary>
        /// Win32 错误码 → 中文说明。Task 6 的本地占位；Task 9 会换成 ElevationManager.DescribeWin32Error 并删除本方法。
        /// </summary>
        private static string DescribeWin32ErrorLocal(int code)
        {
            switch (code)
            {
                case 740:
                    return "需要管理员权限（请勾选“以管理员身份运行”）";
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
                    // 调用方会拼上“启动失败：”，这里不能再重复“启动进程失败”。
                    return "未知错误";
            }
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
