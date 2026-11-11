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
    }
}
