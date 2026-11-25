// =====================================================================================
// FormMain.Tray.cs —— 托盘与退出：通知图标、还原并抢前台、托盘菜单、关闭即隐藏、真退出。
// 本文件是 FormMain 的 partial；成员最初从 FormMain.cs 原样搬来，退出路径随后在阶段二收尾中
// 增加/修正了 R2-13/R45 的提交-保存-显形逻辑，因此不再是纯搬运。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using gostGUI.Native;
using gostGUI.Shared;
using Microsoft.Win32;

namespace gostGUI
{
    public partial class FormMain : Form
    {
        /// <summary>走与真实窗口关闭完全相同的事件处理器（UserClosing → 提交 + 隐藏）。</summary>
        internal void TriggerFormClosingForTest()
        {
            FormMain_FormClosing(this, new FormClosingEventArgs(CloseReason.UserClosing, false));
        }

        /// <summary>自检缝：走与“系统关机/任务管理器关闭”完全相同的 FormClosing 分支。</summary>
        internal void TriggerSystemCloseForTest()
        {
            FormMain_FormClosing(this, new FormClosingEventArgs(CloseReason.WindowsShutDown, false));
        }

        /// <summary>走与托盘“退出”完全相同的退出前提交路径（不真的 Application.Exit）。</summary>
        internal void CommitPendingEditsForTest()
        {
            CommitPendingDetailEdits();
        }

        // =====================================================================================
        // 托盘与退出
        // =====================================================================================

        private void NotifyIcon_MouseClick(object sender, MouseEventArgs e)
        {
            // 右键菜单由 NotifyIcon.ContextMenuStrip 自己弹出（只挂一处，避免重复弹两次菜单）；
            // 这里只处理“左键单击还原窗口”。
            if (e.Button == MouseButtons.Left)
            {
                RestoreAndBringToFront();
            }
        }

        private void NotifyIcon_DoubleClick(object sender, EventArgs e)
        {
            RestoreAndBringToFront();
        }

        private void RestoreTrayVisibility()
        {
            if (!Visible)
            {
                Visible = true;
            }

            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
        }

        /// <summary>还原窗口并抢到前台：保留 v1.8 的 TopMost + Alt 击键方案（v1.9 用 SendInput 实现）。</summary>
        private void RestoreAndBringToFront()
        {
            RestoreTrayVisibility();
            NativeMethods.ShowWindow(Handle, SwRestore);

            // 未最小化但被遮挡时，用 TopMost 快速置顶再还原，确保冲破遮挡。
            TopMost = true;
            BringToFront();
            Activate();
            TopMost = false;

            // 模拟一次 Alt 击键以取得 SetForegroundWindow 的前台切换权限（避免 AttachThreadInput 跨线程死锁）。
            INPUT[] inputs = NativeMethods.BuildAltKeyInputs();
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            NativeMethods.SetForegroundWindow(Handle);
        }

        /// <summary>关闭窗口 = 隐藏到托盘；真正退出只有托盘/按钮的“退出”。</summary>
        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_isExiting)
            {
                // 正常退出路径已经在 ExitApplication 里保存过；这里只兜底一次（同样走 R2-13 的提交 + 保存序列）。
                SaveExitWindowState();
                return;
            }

            if (e.CloseReason == CloseReason.UserClosing)
            {
                // 窗口可能在最后一次失焦之前就被关闭：先把详情编辑器里待提交的字段落盘。
                CommitPendingDetailEdits();

                // 隐藏到托盘前记住窗口几何；应用还会继续跑，失败用气泡提示。
                PersistWindowState(false);
                e.Cancel = true;
                // Hide 已会移除任务栏按钮；切换 ShowInTaskbar 会重建窗口句柄，导致闪烁。
                Hide();
                return;
            }

            // 系统关机/任务管理器等：保存但不阻塞关机。R2-13：这里同样可能有待提交编辑，
            // 提交升起的横幅会随进程消失；走与托盘退出相同的捕获/合并路径，但不弹模态框
            // （会阻塞/延长关机，且交互桌面可能已不可用），改为托盘气泡 + 系统日志显形。
            SaveShutdownWindowState();
        }

        private void FormMain_FormClosed(object sender, FormClosedEventArgs e)
        {
            _isExiting = true;
            DisposeProcessManagerQuietly();
        }

        private void OnApplicationExit(object sender, EventArgs e)
        {
            _isExiting = true;
            DisposeProcessManagerQuietly();
        }

        /// <summary>
        /// 退出前的最后一次提交 + 窗口状态保存。R2-13：提交里升起的横幅（保存失败/环境变量无效）
        /// 在已隐藏的窗口里没人看得见，必须并入 R45 的退出前模态框；窗口状态保存成功也不能把它掩盖。
        /// </summary>
        private void SaveExitWindowState()
        {
            if (_exitWindowStateAttempted)
            {
                return;
            }

            _exitWindowStateAttempted = true;

            string commitBanner = CommitPendingDetailEditsForExit();
            PersistWindowState(true, commitBanner);
        }

        /// <summary>自检缝：走与退出路径完全相同的“提交 + 窗口状态保存”序列（不执行 Application.Exit）。</summary>
        internal void SaveExitWindowStateForTest()
        {
            SaveExitWindowState();
        }

        /// <summary>
        /// 系统关机/任务管理器关闭时的保存：与托盘退出一样捕获提交横幅并合并进失败原因（R2-13），
        /// 但不弹模态框——Windows 关机时模态框会阻塞/延长关机，交互桌面也可能已不可用。
        /// 失败原因改由托盘气泡 + 当前项系统日志显形。
        /// </summary>
        private void SaveShutdownWindowState()
        {
            if (_exitWindowStateAttempted)
            {
                return;
            }

            _exitWindowStateAttempted = true;

            string commitBanner = CommitPendingDetailEditsForExit();
            PersistWindowState(true, commitBanner, reportWithModal: false);
        }

        /// <summary>
        /// 真退出：先停掉所有进程（可能阻塞，放线程池），再关 Job、关托盘、Application.Exit。
        /// _isExiting 语义保留：退出过程中的 ProcessExited/Stopped 不再回写 Enable。
        /// </summary>
        private void ExitApplication()
        {
            if (_exitRequested)
            {
                return;
            }

            // 先置 _exitRequested：退出前的保存可能弹出模态框，模态框会泵消息，
            // 第二次“退出”点击必须被这里挡住，不能重复停进程/Application.Exit。
            _exitRequested = true;

            // 退出前把字段与窗口状态落盘；失败必须显形，不能把刚提交的编辑静默丢掉。
            SaveExitWindowState();

            _isExiting = true;

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
            }

            ProcessManager manager = _processManager;
            Task.Run(() =>
            {
                try
                {
                    manager?.StopAllProcesses();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("退出时停止进程失败：" + ex.Message);
                }

                DisposeProcessManagerQuietly();

                Action exitAction = () =>
                {
                    try
                    {
                        _notifyIcon?.Dispose();
                    }
                    catch (Exception)
                    {
                        // 托盘图标已经释放过就忽略。
                    }

                    Application.Exit();
                };

                // 正常路径：回到 UI 线程退出；UI 线程已经不可投递（窗体和句柄都没了）时
                // 直接执行退出动作，绝不留下“没有界面但进程还在”的僵尸。
                if (!TryPostUi(exitAction))
                {
                    try
                    {
                        exitAction();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("兜底退出失败：" + ex.Message);
                        try
                        {
                            Application.Exit();
                        }
                        catch (Exception)
                        {
                            // 走到这里只能交给进程自然结束。
                        }
                    }
                }
            });
        }

        private const int SwRestore = 9;
    }
}
