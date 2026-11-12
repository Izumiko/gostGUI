using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using gostGUI.Native;

namespace gostGUI.Shared
{
    /// <summary>
    /// Job Object 包装：把子进程（树）放进同一个 Job，Job 句柄关闭时内核强制结束 Job 内的全部进程
    /// （JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE），用于“工具崩溃也不留孤儿”的兜底清理。
    /// UI 进程用它管理直连子进程，runner 进程用它管理自己拉起的子进程树；两个 exe 共用这一份实现。
    /// </summary>
    public class JobObjectManager : IDisposable
    {
        /// <summary>同时保护句柄与释放标记：runner 的管道读线程可能并发调用 Terminate / Dispose。</summary>
        private readonly object _gate = new object();

        private IntPtr _jobHandle;
        private bool _disposed;

        public JobObjectManager()
        {
            // 创建匿名 Job；句柄一旦关闭，Job 内的进程全部被内核终止。
            _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "创建 Job Object 失败，Windows 错误码 " + Marshal.GetLastWin32Error());
            }

            // 设置“Job 句柄关闭即杀死 Job 内全部进程”的兜底策略。
            var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

                if (!NativeMethods.SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation,
                        extendedInfoPtr, (uint)length))
                {
                    int error = Marshal.GetLastWin32Error();
                    NativeMethods.CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                    throw new InvalidOperationException(
                        "设置 Job Object 信息失败，Windows 错误码 " + error);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr);
            }
        }

        /// <summary>
        /// 把进程加入 Job。进程已退出/尚未启动或 Job 已释放时返回 false，绝不抛异常
        /// （调用方通常在子进程刚启动后调用，失败只意味着兜底清理不可用，不应影响已经开始的转发）。
        /// </summary>
        public bool AddProcess(Process process)
        {
            if (process == null)
            {
                return false;
            }

            lock (_gate)
            {
                if (_disposed || _jobHandle == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr processHandle;
                try
                {
                    processHandle = process.Handle;
                }
                catch (Exception)
                {
                    // 进程对象尚未启动或句柄已被释放：没有可加入的目标。
                    return false;
                }

                if (processHandle == IntPtr.Zero)
                {
                    return false;
                }

                return NativeMethods.AssignProcessToJobObject(_jobHandle, processHandle);
            }
        }

        /// <summary>
        /// 立即终止 Job 内的全部进程（退出码 1），但不关闭 Job 句柄；已释放或创建失败时返回 false。
        /// runner 收到 stop 或发现 UI 管道消失时用它，比逐个子进程 Kill 更彻底。
        /// </summary>
        public bool Terminate()
        {
            lock (_gate)
            {
                if (_disposed || _jobHandle == IntPtr.Zero)
                {
                    return false;
                }

                return NativeMethods.TerminateJobObject(_jobHandle, 1);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_jobHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                }

                _disposed = true;
            }
        }

        ~JobObjectManager()
        {
            Dispose(false);
        }
    }
}
