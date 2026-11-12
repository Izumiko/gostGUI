// =====================================================================================
// SelfTest.Process.cs —— 直连启动用例：ProcessManager 的事件、退出码、环境、工作目录与子进程 PID。
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
    }
}
