using System;
using System.Linq;

namespace gostGUI.Runner
{
    /// <summary>
    /// 中转进程入口。它不引用 System.Windows.Forms，只负责建子进程并转发输出。
    /// 目前只识别隐藏探针 --selftest，其余参数一律返回 2；真正的 --spec / --spec-file 在后续任务实现。
    /// </summary>
    internal static class RunnerMain
    {
        /// <summary>参数不受支持时的退出码。</summary>
        internal const int UnsupportedArgumentsExitCode = 2;

        internal static int Main(string[] args)
        {
            // --selftest 只作为「runner 能启动」的探针；此时没有任何转发工作可做。
            if (args.Any(a => a == "--selftest"))
            {
                return 0;
            }

            return UnsupportedArgumentsExitCode;
        }
    }
}
