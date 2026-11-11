using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace gostGUI
{
    static class Program
    {
        // WinExe 启动时没有自己的控制台；--selftest 时尽力接上父进程的控制台，让输出可见。
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            // --ui-preview / --ui-snapshot 的值必须是目录；紧跟的另一个开关不能被当作目录名。
            if (args.Any(a => a == "--ui-preview"))
            {
                string previewDirectory = GetOptionValue(args, "--ui-preview");
                if (!IsDirectoryArgument(previewDirectory))
                {
                    AttachConsole(AttachParentProcess);
                    Console.WriteLine("FAIL --ui-preview 需要一个输出目录参数，不能把另一个 -- 开关当作目录。");
                    return 1;
                }
            }

            if (args.Any(a => a == "--ui-snapshot"))
            {
                string snapshotDirectory = GetOptionValue(args, "--ui-snapshot");
                if (!IsDirectoryArgument(snapshotDirectory))
                {
                    AttachConsole(AttachParentProcess);
                    Console.WriteLine("FAIL --ui-snapshot 需要一个输出目录参数，不能把另一个 -- 开关当作目录。");
                    return 1;
                }
            }

            // 隐藏的自检开关：不初始化 WinForms，退出码与 selftest.log 才是权威结果。
            if (args.Any(a => a == "--selftest"))
            {
                // 接不上控制台就算了，此时只剩退出码与 selftest.log 可用。
                AttachConsole(AttachParentProcess);
                return SelfTest.RunAll();
            }

            // 隐藏的中转进程端到端探针：只跑 relay smoke，退出码沿用 0/1 约定。
            if (args.Any(a => a == "--runner-smoke"))
            {
                AttachConsole(AttachParentProcess);
                return SelfTest.RunRunnerSmoke();
            }

            // 隐藏的界面预览：不显示窗口，把两套主题各渲染成 PNG。
            // 必须赶在任何 WinForms 消息循环之前，且只初始化一次文本渲染模式。
            if (args.Any(a => a == "--ui-preview"))
            {
                AttachConsole(AttachParentProcess);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return UiPreview.Run(GetOptionValue(args, "--ui-preview"));
            }

            // R42 截图开关：构造真实的 FormMain（抑制启动时的自动恢复），
            // 用 DrawToBitmap 输出 form-light.png / form-dark.png，绝不 Show()。
            if (args.Any(a => a == "--ui-snapshot"))
            {
                AttachConsole(AttachParentProcess);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return FormMain.RunUiSnapshotCli(GetOptionValue(args, "--ui-snapshot"));
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) =>
            {
                MessageBox.Show($"Application Error: {e.Exception.Message}\n\n{e.Exception.StackTrace}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Exception ex = e.ExceptionObject as Exception;
                MessageBox.Show($"Fatal Error: {ex?.Message}\n\n{ex?.StackTrace}", "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            // auto start will set working dir c:/windows/system32
            // set it to .exe dir
            var fi = new FileInfo(Application.ExecutablePath);
            Directory.SetCurrentDirectory(fi.DirectoryName);

            Application.Run(new FormMain());
            return 0;
        }

        /// <summary>读取 --option value 形式的下一个参数；缺值返回空字符串。</summary>
        private static string GetOptionValue(string[] args, string option)
        {
            if (args == null || string.IsNullOrEmpty(option))
            {
                return string.Empty;
            }

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], option, StringComparison.Ordinal))
                {
                    return args[i + 1];
                }
            }

            return string.Empty;
        }

        /// <summary>--ui-preview 后面必须是普通目录参数，不能是另一个 -- 开关。</summary>
        private static bool IsDirectoryArgument(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return !value.TrimStart().StartsWith("--", StringComparison.Ordinal);
        }
    }
}
