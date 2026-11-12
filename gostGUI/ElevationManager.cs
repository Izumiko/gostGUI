using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using gostGUI.Native;

namespace gostGUI
{
    /// <summary>
    /// 权限相关的 Windows 原生操作都在这里收口：判断本进程是否提权、把 Win32 错误码翻译成中文、
    /// 读取当前账号名、用 ShellExecuteEx(runas) 拉起需要管理员权限的中转进程，以及借当前会话
    /// explorer.exe 的标准用户令牌**降权**拉起中转进程。
    /// <para>
    /// 提权/降权失败一律抛异常交给调用方记日志，绝不回落到“继承父进程权限”再启动目标程序——
    /// 那也是本设计的反目标（权限模型必须严格按配置项来）。
    /// </para>
    /// </summary>
    public static class ElevationManager
    {
        /// <summary>TOKEN_INFORMATION_CLASS.TokenElevation。</summary>
        private const int TokenElevationClass = 20;

        /// <summary>TOKEN_QUERY：只读令牌信息所需的最小权限。</summary>
        private const uint TokenQuery = 0x0008;

        /// <summary>TOKEN_DUPLICATE：复制令牌所需。</summary>
        private const uint TokenDuplicate = 0x0002;

        /// <summary>TOKEN_ADJUST_PRIVILEGES：启用调用方自身令牌里的特权。</summary>
        private const uint TokenAdjustPrivileges = 0x0020;

        /// <summary>TOKEN_ALL_ACCESS：复制主令牌时请求的最大访问权。</summary>
        private const uint TokenAllAccess = 0x000F01FF;

        /// <summary>PROCESS_QUERY_LIMITED_INFORMATION：跨进程只读查询所需的最小访问权。</summary>
        private const uint ProcessQueryLimitedInformation = 0x1000;

        /// <summary>SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation。</summary>
        private const int SecurityImpersonation = 2;

        /// <summary>TOKEN_TYPE.TokenPrimary：可以被 CreateProcess 系列直接使用的令牌类型。</summary>
        private const int TokenPrimary = 1;

        /// <summary>STARTF_USESHOWWINDOW：让 STARTUPINFO.wShowWindow 生效。</summary>
        private const int StartfUseShowWindow = 0x00000001;

        /// <summary>SE_PRIVILEGE_ENABLED：启用特权。</summary>
        private const uint SePrivilegeEnabled = 0x00000002;

        /// <summary>SEM_FAILCRITICALERRORS：创建进程失败时不弹硬错误对话框。</summary>
        private const uint SemFailCriticalErrors = 0x0001;

        /// <summary>SEM_NOOPENFILEERRORBOX：找不到文件时也不弹框。</summary>
        private const uint SemNoOpenFileErrorBox = 0x8000;

        /// <summary>ERROR_PRIVILEGE_NOT_HELD：调用方令牌里没有所需特权。</summary>
        private const int ErrorPrivilegeNotHeld = 1314;

        /// <summary>ERROR_NOT_ALL_ASSIGNED：AdjustTokenPrivileges 返回 TRUE 但一个特权都没有真的启用。</summary>
        private const int ErrorNotAllAssigned = 1300;

        /// <summary>ERROR_ACCESS_DENIED：没有访问权（拿不到 explorer 等结构性失败也用它报告）。</summary>
        private const int ErrorAccessDenied = 5;

        /// <summary>降权兜底（CreateProcessAsUser）需要的两个特权。</summary>
        private static readonly string[] PrivilegesForCreateProcessAsUser =
        {
            "SeAssignPrimaryTokenPrivilege",
            "SeIncreaseQuotaPrivilege"
        };

        /// <summary>SEE_MASK_NOCLOSEPROCESS：让 ShellExecuteEx 返回进程句柄。</summary>
        private const uint SeeMaskNoCloseProcess = 0x00000040;

        /// <summary>SEE_MASK_FLAG_NO_UI：失败时不弹 shell 自己的错误框（UAC 同意框不受影响）。</summary>
        private const uint SeeMaskFlagNoUI = 0x00000100;

        /// <summary>SW_HIDE：中转进程没有窗口，隐藏启动。</summary>
        private const int SwHide = 0;

        /// <summary>
        /// 本进程是否以管理员权限运行：读当前进程令牌的 TokenElevation（UAC 过滤后的真实状态，
        /// 而不是“是否属于 Administrators 组”）。
        /// <para>
        /// R33：判断失败必须**大声失败**（中文 <see cref="InvalidOperationException"/>），不再默认按“未提权”处理。
        /// 默认 false 会让提权启动器把 runAsAdmin=false 的项送去直连，子进程就继承了管理员权限——
        /// 这正是设计禁止的那个方向。调用方的启动兜底（R17）会记日志并放弃本次启动。
        /// </para>
        /// </summary>
        public static bool IsProcessElevated()
        {
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), TokenQuery, out token))
                {
                    throw CannotDetermineElevation("打开当前进程令牌失败", Marshal.GetLastWin32Error());
                }

                int size = Marshal.SizeOf(typeof(TokenElevation));
                buffer = Marshal.AllocHGlobal(size);
                uint returned;
                if (!NativeMethods.GetTokenInformation(token, TokenElevationClass, buffer, (uint)size, out returned))
                {
                    throw CannotDetermineElevation("读取 TokenElevation 失败", Marshal.GetLastWin32Error());
                }

                var elevation = (TokenElevation)Marshal.PtrToStructure(buffer, typeof(TokenElevation));
                return elevation.TokenIsElevated != 0;
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
            }
        }

        /// <summary>权限级别判断失败时的中文异常：宁可放弃启动，也不能猜成“未提权”。</summary>
        private static InvalidOperationException CannotDetermineElevation(string stage, int errorCode)
        {
            return new InvalidOperationException(
                "无法判断当前进程的权限级别（" + stage + "，Windows 错误码 " + errorCode
                + "）。为避免子进程误继承管理员权限，已放弃本次启动。");
        }

        /// <summary>
        /// Win32 错误码 → 中文说明。740/1223/1314/2/5/193 各有独立文案，
        /// 与设计文档 §6.4 的错误映射表逐条对应；未知错误码回落到“未知错误”。
        /// </summary>
        public static string DescribeWin32Error(int code)
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

        /// <summary>当前登录账号名（DOMAIN\\user）；读取失败返回空字符串。</summary>
        public static string CurrentUserName()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return identity == null ? "" : (identity.Name ?? "");
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// 以管理员身份启动中转进程：ShellExecuteEx + verb runas。用户在 UAC 里点“否”时抛
        /// <see cref="Win32Exception"/>（错误码 1223 = ERROR_CANCELLED），调用方据此写“已取消管理员授权”。
        /// <para>
        /// 返回值是<b>用 PID 重新打开的 Process 对象</b>：ShellExecuteEx 返回的 hProcess 只用来取 PID，
        /// 取到后立刻关闭。这样做的代价是：标准权限进程通常无法用这个 Process 去 Kill 提权后的 runner
        /// （跨完整性级别会被拒绝访问），所以停止提权会话必须依赖管道上的 stop 帧让 runner 自己关 Job，
        /// 以及断开管道让 runner 在 EOF 时自杀；这两条路都在 <see cref="RunnerSession"/> 里实现。
        /// </para>
        /// </summary>
        public static Process LaunchRunnerElevated(string runnerPath, string arguments, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(runnerPath))
            {
                throw new ArgumentException("中转进程路径为空，无法提权启动。", nameof(runnerPath));
            }

            var info = new ShellExecuteInfo
            {
                cbSize = Marshal.SizeOf(typeof(ShellExecuteInfo)),
                fMask = SeeMaskNoCloseProcess | SeeMaskFlagNoUI,
                hwnd = IntPtr.Zero,
                lpVerb = "runas",
                lpFile = runnerPath,
                lpParameters = arguments ?? "",
                lpDirectory = string.IsNullOrWhiteSpace(baseDir) ? Common.GetApplicationPath() : baseDir,
                nShow = SwHide,
                hInstApp = IntPtr.Zero,
                lpIDList = IntPtr.Zero,
                lpClass = null,
                hkeyClass = IntPtr.Zero,
                dwHotKey = 0,
                hIconOrMonitor = IntPtr.Zero,
                hProcess = IntPtr.Zero
            };

            IntPtr processHandle = IntPtr.Zero;
            try
            {
                if (!NativeMethods.ShellExecuteEx(ref info))
                {
                    // 1223 就是 UAC 取消，必须原样上抛错误码，由调用方映射成中文。
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                processHandle = info.hProcess;
                if (processHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("ShellExecuteEx 成功但未返回中转进程句柄。");
                }

                uint processId = NativeMethods.GetProcessId(processHandle);
                if (processId == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    return Process.GetProcessById((int)processId);
                }
                catch (ArgumentException)
                {
                    // 提权后的 runner 如果立刻退出（例如 spec 无效），这里已拿不到进程对象；
                    // 管线随后会在连接/握手阶段拿到更具体的失败原因。
                    throw new InvalidOperationException("中转进程在提权启动后立即退出。");
                }
            }
            finally
            {
                // R34：失败路径也要关闭句柄。ShellExecuteEx 返回 FALSE 时仍可能已经把 hProcess 填好，
                // 只按“成功后才赋值”的局部变量关闭会漏掉它；这里统一以 info.hProcess 为准，
                // 也避免成功路径上同一个句柄被关闭两次。
                // 返回值是 Process.GetProcessById 重新打开的对象，不依赖这个原句柄。
                if (info.hProcess != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(info.hProcess);
                }
            }
        }

        /// <summary>
        /// 以**标准用户权限**启动中转进程：借当前会话 explorer.exe 的主令牌（中等完整性级别）
        /// 复制出一个主令牌，再用 <c>CreateProcessWithTokenW</c> 拉起 runner；1314（调用方缺少
        /// SeImpersonatePrivilege）时启用 <c>SeAssignPrimaryTokenPrivilege</c> /
        /// <c>SeIncreaseQuotaPrivilege</c> 后重试 <c>CreateProcessAsUser</c>。
        /// <para>
        /// 它设计上服务于“gostGUI 自己已提权”的场景。标准权限调用方按文档预期拿不到降权令牌，
        /// 1314（权限不足）或 5（拒绝访问）是那里的预期失败；本机实测补正：本机的标准权限进程是
        /// <b>过滤管理员</b>（中等 IL），CreateProcessAsUser 的令牌分配检查会放行，所以这里也可能真的成功——
        /// 无论成败，这条路径都**绝不**回落到继承调用方的管理员权限（失败抛
        /// <see cref="Win32Exception"/> 带真实错误码，调用方映射成中文）。
        /// </para>
        /// <para>
        /// 返回值是按 PID 重新打开的 <see cref="Process"/>（供 R27 的停止兜底），所有原生句柄
        /// （explorer 进程/令牌、复制出的主令牌、pi.hProcess/hThread）都在 finally 里关闭。
        /// </para>
        /// </summary>
        public static Process LaunchRunnerAsStandardUser(string runnerPath, string arguments, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(runnerPath))
            {
                throw new ArgumentException("中转进程路径为空，无法降权启动。", nameof(runnerPath));
            }

            string workingDirectory = string.IsNullOrWhiteSpace(baseDir) ? Common.GetApplicationPath() : baseDir;

            // explorer.exe 的主令牌就是“当前会话的标准用户”该有的样子（中等 IL、桌面可交互）；
            // 这里只借它的身份，不借它的窗口/句柄。
            Process explorer = FindSessionExplorer();
            IntPtr explorerProcess = IntPtr.Zero;
            IntPtr explorerToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;
            var started = new ProcessInfo();
            bool created = false;
            try
            {
                if (explorer == null)
                {
                    throw NativeFailure(ErrorAccessDenied,
                        "找不到当前会话的标准权限 explorer.exe 进程，无法降权启动；请改用标准权限运行 gostGUI");
                }

                explorerProcess = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, explorer.Id);
                if (explorerProcess == IntPtr.Zero)
                {
                    throw NativeFailure(Marshal.GetLastWin32Error(),
                        "打开 explorer.exe 进程失败，无法降权启动；请改用标准权限运行 gostGUI");
                }

                if (!NativeMethods.OpenProcessToken(explorerProcess, TokenQuery | TokenDuplicate, out explorerToken))
                {
                    throw NativeFailure(Marshal.GetLastWin32Error(),
                        "打开 explorer.exe 的令牌失败，无法降权启动；请改用标准权限运行 gostGUI");
                }

                bool tokenElevated;
                int queryError = ReadTokenElevation(explorerToken, out tokenElevated);
                if (queryError != 0)
                {
                    throw NativeFailure(queryError, "读取 explorer.exe 令牌的提权状态失败，无法确认它是标准权限");
                }

                if (tokenElevated)
                {
                    // 整机 UAC 关闭或 explorer 以管理员身份运行时没有可借的标准令牌；
                    // 这种情况必须明确报错，绝不能退化成“用当前（管理员）令牌启动”。
                    throw NativeFailure(ErrorPrivilegeNotHeld,
                        "explorer.exe 的令牌本身就是管理员权限，没有可借的标准用户令牌；请改用标准权限运行 gostGUI");
                }

                if (!NativeMethods.DuplicateTokenEx(explorerToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation,
                        TokenPrimary, out primaryToken))
                {
                    throw NativeFailure(Marshal.GetLastWin32Error(), "复制 explorer.exe 的主令牌失败，无法降权启动");
                }

                int createError;
                created = TryCreateWithToken(primaryToken, runnerPath, arguments ?? "", workingDirectory,
                    ref started, out createError);

                string createFailureReason = "以标准用户身份创建中转进程失败；请改用标准权限运行 gostGUI";
                if (!created && createError == ErrorPrivilegeNotHeld)
                {
                    // CreateProcessWithTokenW 需要调用方的 SeImpersonatePrivilege；某些加固环境把它拿掉了。
                    // 按设计改用 CreateProcessAsUser，先启用它需要的两个特权，**用完必须恢复原状**
                    // （不能把特权留在 UI 进程令牌上）。
                    //
                    // 注意：启用失败不等于这条兜底一定失败——过滤管理员令牌即使不持有这两个特权，
                    // CreateProcessAsUser 的“分配主令牌”检查也可能放行（本机实测）。所以把中文原因记下来、
                    // 仍然尝试一次；真正失败时再把原因拼进错误，避免用户只看到一个笼统的 1314。
                    EnabledPrivileges privileges;
                    string privilegeError;
                    bool privilegesEnabled = TryEnablePrivileges(PrivilegesForCreateProcessAsUser,
                        out privileges, out privilegeError);
                    try
                    {
                        created = TryCreateAsUser(primaryToken, runnerPath, arguments ?? "", workingDirectory,
                            ref started, out createError);
                    }
                    finally
                    {
                        if (privileges != null)
                        {
                            // 无论 CreateProcessAsUser 成败，都恢复启用前的特权状态。
                            privileges.Dispose();
                        }
                    }

                    if (!created && !privilegesEnabled)
                    {
                        createFailureReason = "以标准用户身份创建中转进程失败，且令牌特权启用也未成功（"
                                              + privilegeError + "）；请改用标准权限运行 gostGUI";
                    }
                }

                if (!created)
                {
                    throw NativeFailure(createError, createFailureReason);
                }

                if (started.ProcessId <= 0)
                {
                    throw NativeFailure(ErrorAccessDenied, "降权启动成功但拿不到中转进程 PID");
                }

                try
                {
                    return Process.GetProcessById(started.ProcessId);
                }
                catch (ArgumentException)
                {
                    // 与提权路径一致：runner 起来就退出（例如 spec 无效）时这里已拿不到进程对象，
                    // 管线随后会在连接/握手阶段拿到更具体的失败原因。
                    throw new InvalidOperationException("中转进程在降权启动后立即退出。");
                }
            }
            finally
            {
                // 所有原生句柄都在这里关闭：成功路径返回的 Process 自己按 PID 重新打开句柄。
                // pi 的两个句柄在失败路径上也可能已被填好，按“非零就关”处理，且只关一次。
                CloseIfValid(started.Process);
                CloseIfValid(started.Thread);
                CloseIfValid(primaryToken);
                CloseIfValid(explorerToken);
                CloseIfValid(explorerProcess);
                explorer?.Dispose();
            }
        }

        /// <summary>
        /// 在当前会话里找 explorer.exe。找不到/枚举失败返回 null（由调用方报 5 类错误），
        /// 非当前会话的候选进程一律释放，不留句柄。
        /// </summary>
        private static Process FindSessionExplorer()
        {
            int sessionId;
            try
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    sessionId = self.SessionId;
                }
            }
            catch (Exception)
            {
                // 连自己的会话号都读不到：没有可靠的降权来源。
                return null;
            }

            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName("explorer");
            }
            catch (Exception)
            {
                return null;
            }

            Process found = null;
            try
            {
                foreach (Process candidate in candidates)
                {
                    try
                    {
                        // 同一会话通常只有一个 explorer；多个时取第一个，其余在 finally 里统一释放。
                        if (candidate.SessionId == sessionId && found == null)
                        {
                            found = candidate;
                        }
                    }
                    catch (Exception)
                    {
                        // 进程刚好退出（拒绝访问/已结束）：跳过它，finally 仍会释放句柄。
                    }
                }
            }
            finally
            {
                foreach (Process candidate in candidates)
                {
                    if (!ReferenceEquals(candidate, found))
                    {
                        candidate.Dispose();
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// CreateProcessWithTokenW 的一次尝试。失败返回 false 并给出真实错误码；
        /// 命令行必须在每次调用前重建：Unicode 版本可能会就地修改这块缓冲区。
        /// </summary>
        private static bool TryCreateWithToken(IntPtr token, string runnerPath, string arguments,
            string workingDirectory, ref ProcessInfo started, out int errorCode)
        {
            var startup = CreateStartupInfo();
            var commandLine = new StringBuilder(QuoteIfNeeded(runnerPath) + " " + arguments);

            // 每次尝试都先清零：失败的 P/Invoke 不保证写这块内存，残留上一次尝试的句柄/PID 会被 finally 误关。
            started = new ProcessInfo();

            uint previousErrorMode = 0;
            bool modeChanged = SuppressHardErrorDialogs(out previousErrorMode);
            try
            {
                bool created = NativeMethods.CreateProcessWithTokenW(token, 0, runnerPath, commandLine, 0, IntPtr.Zero,
                    workingDirectory, ref startup, out started);
                errorCode = created ? 0 : Marshal.GetLastWin32Error();
                return created;
            }
            finally
            {
                RestoreHardErrorDialogs(modeChanged, previousErrorMode);
            }
        }

        /// <summary>CreateProcessAsUser 的一次尝试（1314 的兜底路径），语义同上。</summary>
        private static bool TryCreateAsUser(IntPtr token, string runnerPath, string arguments,
            string workingDirectory, ref ProcessInfo started, out int errorCode)
        {
            var startup = CreateStartupInfo();
            var commandLine = new StringBuilder(QuoteIfNeeded(runnerPath) + " " + arguments);

            // 同上：两次尝试共用一份 PROCESS_INFORMATION，调用前必须清零。
            started = new ProcessInfo();

            uint previousErrorMode = 0;
            bool modeChanged = SuppressHardErrorDialogs(out previousErrorMode);
            try
            {
                bool created = NativeMethods.CreateProcessAsUser(token, runnerPath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    0, IntPtr.Zero, workingDirectory, ref startup, out started);
                errorCode = created ? 0 : Marshal.GetLastWin32Error();
                return created;
            }
            finally
            {
                RestoreHardErrorDialogs(modeChanged, previousErrorMode);
            }
        }

        /// <summary>
        /// 创建进程期间压掉系统的“硬错误框”（SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX）。
        /// <para>
        /// gostGUI 是 <b>GUI 子系统</b>进程：创建进程失败时 Windows 默认弹“不是有效的 Win32 应用程序”
        /// 这类模态框，把调用线程（UI 线程）钉死在那里——自检时实测到降权重试（CreateProcessAsUser）
        /// 撞上无效镜像会永久挂起。降权路径的失败必须只用错误码报告，所以每次 CreateProcess* 前临时设置、
        /// 结束后恢复线程原来的错误模式（线程级，不影响其它线程）。
        /// </para>
        /// </summary>
        private static bool SuppressHardErrorDialogs(out uint previousErrorMode)
        {
            previousErrorMode = 0;
            try
            {
                return NativeMethods.SetThreadErrorMode(SemFailCriticalErrors | SemNoOpenFileErrorBox, out previousErrorMode);
            }
            catch (Exception)
            {
                // 极老系统没有 SetThreadErrorMode：放弃抑制，让创建失败照常返回错误码。
                return false;
            }
        }

        /// <summary>恢复线程错误模式；设置失败过就什么也不做。</summary>
        private static void RestoreHardErrorDialogs(bool changed, uint previousErrorMode)
        {
            if (!changed)
            {
                return;
            }

            try
            {
                uint ignored;
                NativeMethods.SetThreadErrorMode(previousErrorMode, out ignored);
            }
            catch (Exception)
            {
                // 恢复失败只影响后续错误框的抑制，不影响本次启动结论。
            }
        }

        /// <summary>
        /// 降权路径的 STARTUPINFO：桌面固定为 winsta0\default（runner 要在当前交互桌面里安静运行），
        /// 隐藏窗口，且**不设** STARTF_USESTDHANDLES——输出走命名管道，不走继承句柄。
        /// </summary>
        private static StartupInfo CreateStartupInfo()
        {
            return new StartupInfo
            {
                cb = Marshal.SizeOf(typeof(StartupInfo)),
                lpDesktop = "winsta0\\default",
                dwFlags = StartfUseShowWindow,
                wShowWindow = SwHide
            };
        }

        /// <summary>路径含空格时加引号（干净路径不加，免得白白多两个字符影响命令行观感）。</summary>
        internal static string QuoteIfNeeded(string path)
        {
            return path.IndexOf(' ') >= 0 ? "\"" + path + "\"" : path;
        }

        /// <summary>
        /// 在调用方令牌上启用指定特权，并交出“恢复原状”的句柄（见 <see cref="EnabledPrivileges"/>）。
        /// <para>
        /// 返回值表示**是否全部真的启用成功**：任何一步失败（打开令牌、查找 LUID、AdjustTokenPrivileges 返回 FALSE、
        /// 或返回 TRUE 但带 <c>ERROR_NOT_ALL_ASSIGNED</c>——即令牌根本不持有该特权）都会把中文原因写进
        /// <paramref name="privilegeError"/>，并先把已经启用的部分恢复、再返回 false。
        /// 成功时 <paramref name="enabled"/> 非空，调用方必须在 finally 里 Dispose 它。
        /// </para>
        /// </summary>
        private static bool TryEnablePrivileges(string[] privilegeNames, out EnabledPrivileges enabled,
            out string privilegeError)
        {
            enabled = null;
            privilegeError = "";

            IntPtr token = IntPtr.Zero;
            var previousStates = new List<KeyValuePair<long, uint>>();
            bool allEnabled = true;
            try
            {
                if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                {
                    privilegeError = "打开当前进程令牌失败（Windows 错误码 " + Marshal.GetLastWin32Error() + "）";
                    return false;
                }

                foreach (string name in privilegeNames)
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    long luid;
                    if (!NativeMethods.LookupPrivilegeValue(null, name, out luid))
                    {
                        privilegeError = "找不到特权 " + name + "（Windows 错误码 "
                                         + Marshal.GetLastWin32Error() + "）";
                        allEnabled = false;
                        continue;
                    }

                    var newState = new TokenPrivileges
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = SePrivilegeEnabled
                    };

                    // PreviousState 必须给缓冲区：恢复时要按启用前的属性写回，不能猜。
                    int stateSize = Marshal.SizeOf(typeof(TokenPrivileges));
                    IntPtr previousBuffer = Marshal.AllocHGlobal(stateSize);
                    try
                    {
                        bool adjusted = NativeMethods.AdjustTokenPrivileges(token, false, ref newState, (uint)stateSize,
                            previousBuffer, IntPtr.Zero);
                        int lastError = Marshal.GetLastWin32Error();
                        if (!adjusted)
                        {
                            privilegeError = "启用特权 " + name + " 失败（Windows 错误码 " + lastError + "）";
                            allEnabled = false;
                            continue;
                        }

                        if (lastError == ErrorNotAllAssigned)
                        {
                            // 返回 TRUE 也可能“一个都没启用”：当前令牌不持有这个特权。
                            privilegeError = "当前进程令牌不持有特权 " + name + "（Windows 错误码 "
                                             + lastError + "）";
                            allEnabled = false;
                            continue;
                        }

                        var previousState = (TokenPrivileges)Marshal.PtrToStructure(previousBuffer,
                            typeof(TokenPrivileges));
                        previousStates.Add(new KeyValuePair<long, uint>(luid, previousState.Attributes));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(previousBuffer);
                    }
                }

                if (allEnabled)
                {
                    // 句柄所有权移交给守卫：它负责恢复特权并关闭句柄。
                    enabled = new EnabledPrivileges(token, previousStates);
                    token = IntPtr.Zero;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                privilegeError = ex.Message;
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero)
                {
                    // 失败/异常路径：先把已经启用的特权恢复，再关闭句柄，绝不留下进程级特权残留。
                    RestorePrivileges(token, previousStates);
                    CloseIfValid(token);
                }
            }
        }

        /// <summary>把一个令牌上的特权恢复成给定属性（通常是启用前抓到的那一份）。清理路径尽力而为。</summary>
        private static void RestorePrivileges(IntPtr token, List<KeyValuePair<long, uint>> previousStates)
        {
            foreach (KeyValuePair<long, uint> pair in previousStates)
            {
                var state = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = pair.Key,
                    Attributes = pair.Value
                };

                try
                {
                    NativeMethods.AdjustTokenPrivileges(token, false, ref state,
                        (uint)Marshal.SizeOf(typeof(TokenPrivileges)), IntPtr.Zero, IntPtr.Zero);
                }
                catch (Exception)
                {
                    // 恢复失败只影响“特权残留”这一项清理，不能掩盖真正的启动失败原因。
                }
            }
        }

        /// <summary>
        /// 已启用特权的一次性快照：记录每个特权启用前的属性，Dispose 时逐条恢复并关闭自己持有的令牌句柄。
        /// 这是“降权失败不留进程级特权残留”这条纪律的唯一收口点。
        /// </summary>
        private sealed class EnabledPrivileges : IDisposable
        {
            private readonly IntPtr _token;
            private readonly List<KeyValuePair<long, uint>> _previousStates;
            private bool _disposed;

            internal EnabledPrivileges(IntPtr token, List<KeyValuePair<long, uint>> previousStates)
            {
                _token = token;
                _previousStates = previousStates;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    RestorePrivileges(_token, _previousStates);
                }
                finally
                {
                    CloseIfValid(_token);
                }
            }
        }

        /// <summary>读令牌的提权状态；成功返回 0，失败返回真实 Win32 错误码。</summary>
        private static int ReadTokenElevation(IntPtr token, out bool elevated)
        {
            elevated = false;
            int size = Marshal.SizeOf(typeof(TokenElevation));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint returned;
                if (!NativeMethods.GetTokenInformation(token, TokenElevationClass, buffer, (uint)size, out returned))
                {
                    return Marshal.GetLastWin32Error();
                }

                elevated = ((TokenElevation)Marshal.PtrToStructure(buffer, typeof(TokenElevation)))
                    .TokenIsElevated != 0;
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>带真实错误码、中文说明的失败异常；NativeErrorCode 由调用方映射成中文文案。</summary>
        private static Win32Exception NativeFailure(int errorCode, string reason)
        {
            return new Win32Exception(errorCode, reason + "（Windows 错误码 " + errorCode + "）。");
        }

        /// <summary>关闭非零句柄；失败忽略（清理路径不能掩盖真正的失败原因）。</summary>
        private static void CloseIfValid(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }

    }
}
