// =====================================================================================
// SelfTest.Runner.cs —— 中转用例：runner 进程、RunnerSession、smoke、管道强制标签与身份日志。
// 本文件是自检套件的 partial：原单体 SelfTest.cs 已按领域拆分（该文件已不存在），用例名/断言/注册顺序未改。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using gostGUI.Shared;

namespace gostGUI
{
    internal static partial class SelfTest
    {
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
    }
}
