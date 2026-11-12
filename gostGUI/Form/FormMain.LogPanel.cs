// =====================================================================================
// FormMain.LogPanel.cs —— 日志面板：日志宿主布局、清空/复制、LogKind 路由与系统日志行。
// 本文件是 FormMain 的 partial，成员从 FormMain.cs 原样搬来（签名/可见性/行为未改）。
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
        // =====================================================================================
        // 进程事件（可能来自工作线程）
        // =====================================================================================

        private void OnProcessOutputReceived(ConfigItem item, LogKind kind, string data)
        {
            // LogViewManager 只入队：绝不在工作线程上碰 UI 控件。
            _logViewManager?.AppendText(item, kind, data);
        }

        /// <summary>写一行系统日志（启动/停止/退出码/错误），按 LogKind.System 着色。</summary>
        internal void AppendSystemLine(ConfigItem item, string message)
        {
            if (item == null || string.IsNullOrEmpty(message))
            {
                return;
            }

            _logViewManager?.AppendText(item, LogKind.System, message + Environment.NewLine);
        }

        // =====================================================================================
        // 日志区
        // =====================================================================================

        private void LogHost_Resize(object sender, EventArgs e)
        {
            LayoutLogViews();
        }

        /// <summary>日志视图是 Dock=Fill 的 ThemedRichTextBox；这里只在尺寸变化时再兜一次底。</summary>
        private void LayoutLogViews()
        {
            if (_logHost == null || _logHost.IsDisposed)
            {
                return;
            }

            Size size = _logHost.ClientSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            var bounds = new Rectangle(0, 0, size.Width, size.Height);
            foreach (Control child in _logHost.Controls)
            {
                var box = child as ThemedRichTextBox;
                if (box != null)
                {
                    box.Bounds = bounds;
                }
            }
        }

        private void ClearLogButton_Click(object sender, EventArgs e)
        {
            ConfigItem item = GetCurrentConfigItem();
            if (item == null)
            {
                return;
            }

            _logViewManager?.ClearLog(item);
        }

        private void CopyLogButton_Click(object sender, EventArgs e)
        {
            string text = GetCurrentLogText();
            if (string.IsNullOrEmpty(text))
            {
                MessageBox.Show(this, "当前没有可复制的日志。", "复制日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制到剪贴板失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string GetCurrentLogText()
        {
            ConfigItem item = GetCurrentConfigItem();
            if (item == null || _logViewManager == null)
            {
                return string.Empty;
            }

            return _logViewManager.GetText(item);
        }
    }
}
