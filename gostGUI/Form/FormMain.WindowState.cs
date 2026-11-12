// =====================================================================================
// FormMain.WindowState.cs —— 窗口状态：几何捕获/恢复、最小尺寸与最大化处理、DPI 变化，
// 以及系统设置变化（WM_SETTINGCHANGE）的窗口消息钩子。
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
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // 系统主题色变化时（“跟随系统”模式下）热更新调色板。
            const int WmSettingChange = 0x001A;
            if (m.Msg != WmSettingChange || _themeManager.Mode != ThemeMode.System)
            {
                return;
            }

            try
            {
                string area = m.LParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(m.LParam);
                if (string.Equals(area, "ImmersiveColorSet", StringComparison.Ordinal))
                {
                    // 只有解析出的调色板真的变了才重铺整棵树；WM_SETTINGCHANGE 也可能
                    // 由别的系统设置触发。
                    _themeManager.RefreshSystemPreference();
                }
            }
            catch (Exception)
            {
                // 主题热更新失败不影响主流程。
            }
        }

        /// <summary>
        /// 每显示器 DPI 变化（PerMonitorV2 下由系统发 WM_DPICHANGED）：
        /// 基类已经按新旧 DPI 比例缩放字体、边距和 TableLayoutPanel 的绝对宽高；
        /// 这里补上本窗口自己的状态（左栏折叠宽度、最小尺寸）并重铺主题/日志视图。
        /// </summary>
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);

            if (e == null)
            {
                return;
            }

            try
            {
                _layoutDpi = e.DeviceDpiNew > 0 ? e.DeviceDpiNew : _layoutDpi;
                RecomputeLeftColumnWidths();
                ApplyMinimumSize(_layoutDpi);
                ApplyTheme();
                LayoutLogViews();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DPI 变化后的重排失败：" + ex.Message);
            }
        }

        // =====================================================================================
        // 窗口状态（DIP）与 DPI 变化
        // =====================================================================================

        /// <summary>
        /// 保存/恢复使用的 DPI：自检与截图可用 StartupDpiOverride 注入；
        /// 生产路径优先用窗口真实 DeviceDpi（每显示器 DPI），拿不到时退回构造时的布局 DPI。
        /// </summary>
        internal int EffectiveDpi
        {
            get
            {
                if (StartupDpiOverride > 0)
                {
                    return StartupDpiOverride;
                }

                try
                {
                    int dpi = DeviceDpi;
                    if (dpi > 0)
                    {
                        return dpi;
                    }
                }
                catch (Exception)
                {
                    // 句柄尚不存在时退回构造时的布局 DPI。
                }

                return _layoutDpi > 0 ? _layoutDpi : 96;
            }
        }

        /// <summary>按保存的 DIP 窗口状态恢复尺寸/位置；屏幕放不下时夹到工作区。</summary>
        private void RestoreWindowState(bool force)
        {
            if (!force && _suppressStartupStart)
            {
                return;
            }

            if (_configManager == null)
            {
                return;
            }

            try
            {
                int dpi = _layoutDpi > 0 ? _layoutDpi : 96;
                ConfigWindow saved = _configManager.WindowState;

                var screenBounds = new List<Rectangle>();
                var workingAreas = new List<Rectangle>();
                // 保存的是客户区；夹取时要给外框（标题栏/边框）留出空间，否则右/下边框可能
                // 越过工作区。这里用与捕获相同的外框来源（窗口样式 → 非客户区尺寸）。
                Size chrome = GetWindowChromePixels();
                Size minimum = GetMinimumClientDip(dpi);
                Screen primary = Screen.PrimaryScreen;
                if (primary != null)
                {
                    screenBounds.Add(primary.Bounds);
                    workingAreas.Add(DeflateForChrome(primary.WorkingArea, chrome));
                }

                foreach (Screen screen in Screen.AllScreens)
                {
                    if (primary != null && string.Equals(screen.DeviceName, primary.DeviceName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    screenBounds.Add(screen.Bounds);
                    workingAreas.Add(DeflateForChrome(screen.WorkingArea, chrome));
                }

                Point location;
                WindowPlacement placement;
                Size clientPixels = WindowGeometry.Place(
                    saved,
                    dpi,
                    screenBounds,
                    workingAreas,
                    minimum.Width,
                    minimum.Height,
                    out location,
                    out placement);

                ApplyMinimumSize(dpi);
                ClientSize = new Size(clientPixels.Width, clientPixels.Height);

                if (placement == WindowPlacement.Restored)
                {
                    StartPosition = FormStartPosition.Manual;
                    Location = location;
                }
                else
                {
                    // 没保存过位置或位置已不在任何屏幕上：居中，尺寸按默认/夹取后的结果。
                    StartPosition = FormStartPosition.CenterScreen;
                }

                _restoreMaximized = WindowGeometry.Normalize(saved, minimum.Width, minimum.Height).Max;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("恢复窗口状态失败：" + ex.Message);
            }
        }

        /// <summary>设计最小尺寸按当前 DPI 换算；工作区比最小尺寸还小时以工作区为准。</summary>
        private void ApplyMinimumSize(int dpi)
        {
            int minWidth = WindowGeometry.DipToPixels(WindowGeometry.DesignMinimumWidthDip, dpi);
            int minHeight = WindowGeometry.DipToPixels(WindowGeometry.DesignMinimumHeightDip, dpi);
            Rectangle work = GetPreferredWorkingArea();
            if (work.Width > 0)
            {
                minWidth = Math.Min(minWidth, work.Width);
            }

            if (work.Height > 0)
            {
                minHeight = Math.Min(minHeight, work.Height);
            }

            MinimumSize = new Size(Math.Max(1, minWidth), Math.Max(1, minHeight));
        }

        /// <summary>
        /// 客户端可用的最小尺寸（DIP）：设计下限按工作区放宽后，再减去外框。
        /// 与 Capture/Normalize/Place 共用同一个下限来源，避免小桌面上保存的几何被误判为损坏。
        /// </summary>
        private Size GetMinimumClientDip(int dpi)
        {
            return WindowGeometry.MinimumDipFor(GetPreferredWorkingArea(), dpi, GetWindowChromePixels());
        }

        /// <summary>
        /// 当前窗口的非客户区尺寸（设备像素）。必须由窗口样式计算，
        /// 不能使用 Bounds − ClientSize：最小化时 ClientSize 可能是 0×0，Bounds 是任务栏图标。
        /// </summary>
        private Size GetWindowChromePixels()
        {
            try
            {
                Size reference = SizeFromClientSize(new Size(100, 100));
                return new Size(
                    Math.Max(0, reference.Width - 100),
                    Math.Max(0, reference.Height - 100));
            }
            catch (Exception)
            {
                // 正常态下的退路；最小化/最大化路径不应走到这里。
                return new Size(
                    Math.Max(0, Bounds.Width - ClientSize.Width),
                    Math.Max(0, Bounds.Height - ClientSize.Height));
            }
        }

        private Rectangle GetPreferredWorkingArea()
        {
            try
            {
                if (IsHandleCreated)
                {
                    return Screen.FromHandle(Handle).WorkingArea;
                }

                Screen primary = Screen.PrimaryScreen;
                if (primary != null)
                {
                    return primary.WorkingArea;
                }
            }
            catch (Exception)
            {
                // 屏幕信息不可用时用保守的默认工作区。
            }

            return new Rectangle(0, 0, 1024, 768);
        }

        private static Rectangle DeflateForChrome(Rectangle workingArea, Size chrome)
        {
            return new Rectangle(
                workingArea.Left,
                workingArea.Top,
                Math.Max(1, workingArea.Width - chrome.Width),
                Math.Max(1, workingArea.Height - chrome.Height));
        }

        /// <summary>DPI 变化后 TableLayoutPanel 的绝对列宽已被基类按比例缩放，重算折叠宽度。</summary>
        private void RecomputeLeftColumnWidths()
        {
            if (_contentLayout == null || _contentLayout.ColumnStyles.Count == 0)
            {
                return;
            }

            int expanded = (int)Math.Round(_contentLayout.ColumnStyles[0].Width);
            if (expanded > 0)
            {
                _leftExpandedColumnWidth = expanded;
                _collapsedColumnWidth = (int)Math.Round(expanded * 64.0 / 300.0);
            }

            if (_collapsed)
            {
                _contentLayout.ColumnStyles[0].Width = _collapsedColumnWidth;
            }
        }

        /// <summary>把当前窗口几何换算成 DIP；最大化/最小化时用 RestoreBounds 的普通几何。</summary>
        private ConfigWindow CaptureWindowState()
        {
            try
            {
                int dpi = EffectiveDpi;
                Size minimum = GetMinimumClientDip(dpi);
                return BuildWindowState(
                    Bounds,
                    ClientSize,
                    RestoreBounds,
                    GetWindowChromePixels(),
                    WindowState == FormWindowState.Maximized,
                    WindowState == FormWindowState.Minimized,
                    dpi,
                    minimum.Width,
                    minimum.Height);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("读取窗口状态失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 窗口状态换算的纯函数：最小化时 ClientSize 可能是 0×0、Bounds 是任务栏图标，
        /// 所以最大化/最小化一律用 RestoreBounds（普通外框）减去按样式算出的非客户区尺寸。
        /// 自检直接调用它覆盖这两种状态，避免无消息循环的用例必须 Show 窗口。
        /// </summary>
        internal static ConfigWindow BuildWindowState(
            Rectangle currentBounds,
            Size currentClientSize,
            Rectangle restoreBounds,
            Size chromePixels,
            bool maximized,
            bool minimized,
            int dpi,
            int minimumWidthDip,
            int minimumHeightDip)
        {
            Size clientPixels;
            Point locationPixels;

            if ((maximized || minimized) && !restoreBounds.IsEmpty)
            {
                clientPixels = new Size(
                    Math.Max(1, restoreBounds.Width - Math.Max(0, chromePixels.Width)),
                    Math.Max(1, restoreBounds.Height - Math.Max(0, chromePixels.Height)));
                locationPixels = restoreBounds.Location;
            }
            else
            {
                clientPixels = currentClientSize;
                locationPixels = currentBounds.Location;
            }

            return WindowGeometry.Capture(
                clientPixels,
                locationPixels,
                maximized,
                dpi,
                minimumWidthDip,
                minimumHeightDip);
        }

        /// <summary>保存窗口状态；失败时记录中文原因并显形（托盘气泡或退出前模态框）。</summary>
        /// <param name="exiting">true = 真退出路径（R45 模态框）；false = 隐藏到托盘（气泡/横幅）。</param>
        /// <param name="pendingCommitFailure">
        /// 退出前提交里已经升起的失败原因（R2-13）。非空即视为退出保存失败，即使本次窗口状态写入成功，
        /// 也要走 R45 模态框把它显形——否则隐藏窗口里的横幅会随退出一起消失。
        /// </param>
        private void PersistWindowState(bool exiting, string pendingCommitFailure = null)
        {
            string reason = pendingCommitFailure;
            try
            {
                ConfigWindow state = CaptureWindowState();
                string saveFailure = null;
                if (state == null)
                {
                    saveFailure = "无法读取当前窗口大小/位置。";
                }
                else if (_configManager == null)
                {
                    saveFailure = "配置管理器不可用。";
                }
                else
                {
                    // 退出路径只保留 R45 的模态框；隐藏到托盘时横幅仍可见，继续保留现有信号。
                    bool previousSuppress = _suppressSaveFailedBanner;
                    if (exiting)
                    {
                        _suppressSaveFailedBanner = true;
                    }

                    try
                    {
                        if (!_configManager.SaveWindowState(state))
                        {
                            saveFailure = _configManager.SaveError;
                            if (string.IsNullOrEmpty(saveFailure))
                            {
                                saveFailure = Common.LastSaveError;
                            }

                            if (string.IsNullOrEmpty(saveFailure))
                            {
                                saveFailure = "保存配置文件失败。";
                            }
                        }
                    }
                    finally
                    {
                        _suppressSaveFailedBanner = previousSuppress;
                    }
                }

                reason = CombineSaveFailures(reason, saveFailure);
            }
            catch (Exception ex)
            {
                reason = CombineSaveFailures(reason, "保存窗口状态失败：" + ex.Message);
            }

            if (string.IsNullOrEmpty(reason))
            {
                _lastWindowSaveFailure = "";
                return;
            }

            _lastWindowSaveFailure = reason;
            ReportWindowStateSaveFailure(reason, exiting);
        }

        /// <summary>把“退出前提交的横幅原因”与“本次窗口状态保存失败原因”并成一条可见提示。</summary>
        private static string CombineSaveFailures(string first, string second)
        {
            if (string.IsNullOrEmpty(first))
            {
                return second;
            }

            if (string.IsNullOrEmpty(second))
            {
                return first;
            }

            return first + "；" + second;
        }

        /// <summary>
        /// R45：退出/隐藏前保存失败必须可见。隐藏到托盘时用气泡（应用还在跑），
        /// 真正退出时用一个小模态框（托盘马上就要消失，气泡可能来不及看到）。
        /// </summary>
        private void ReportWindowStateSaveFailure(string reason, bool exiting)
        {
            const string title = "保存窗口状态失败";
            string text = (exiting ? "退出前" : "隐藏窗口前") +
                          "保存窗口状态失败：" + reason +
                          "。刚才的修改可能没有写入磁盘。";

            if (SuppressDialogsForTest)
            {
                return;
            }

            if (exiting)
            {
                try
                {
                    // 从托盘退出时窗口是隐藏的：先恢复显示，保证模态框有一个可见的 owner，
                    // 不会被压在任务栏/其他窗口后面。
                    if (!Visible)
                    {
                        ShowInTaskbar = true;
                        Show();
                    }

                    Activate();
                }
                catch (Exception)
                {
                    // 窗口已不可显示时仍然尝试直接弹框。
                }

                try
                {
                    MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch (Exception)
                {
                    // 退出过程中弹框失败也只能继续退出。
                }

                return;
            }

            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = true;
                    _notifyIcon.BalloonTipTitle = title;
                    _notifyIcon.BalloonTipText = text;
                    _notifyIcon.BalloonTipIcon = ToolTipIcon.Error;
                    _notifyIcon.ShowBalloonTip(5000);
                    return;
                }
            }
            catch (Exception)
            {
                // 气泡不可用时退回模态框。
            }

            try
            {
                MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception)
            {
                // 连弹框都失败时至少已经写进 _lastWindowSaveFailure，自检可见。
            }
        }

        // ---- Task 13 自检缝：走与生产代码相同的恢复/捕获/DIP 变化路径 ----

        internal string LastWindowSaveFailureForTest => _lastWindowSaveFailure;

        internal void RestoreWindowStateForTest()
        {
            RestoreWindowState(true);
        }

        internal ConfigWindow CaptureWindowStateForTest()
        {
            return CaptureWindowState();
        }

        internal Size WindowChromeForTest()
        {
            return GetWindowChromePixels();
        }

        internal Size MinimumClientDipForTest(int dpi)
        {
            return GetMinimumClientDip(dpi);
        }

        internal bool BannerVisibleForTest => _bannerShown;

        internal bool ExitRequestedForTest => _exitRequested;

        internal void HandleDpiChangedForTest(DpiChangedEventArgs args)
        {
            OnDpiChanged(args);
        }

        internal void PersistWindowStateForTest(bool exiting)
        {
            PersistWindowState(exiting);
        }

        internal void SetWindowGeometryForTest(Size clientSize, Point location)
        {
            StartPosition = FormStartPosition.Manual;
            ClientSize = clientSize;
            Location = location;
        }

        internal int LayoutDpiForTest => _layoutDpi;

        internal Rectangle ContentLayoutBoundsForTest =>
            _contentLayout == null ? Rectangle.Empty : _contentLayout.Bounds;

        internal Rectangle LeftPanelBoundsForTest =>
            _leftPanel == null ? Rectangle.Empty : _leftPanel.Bounds;

        internal Rectangle RightColumnBoundsForTest =>
            _rightColumn == null ? Rectangle.Empty : _rightColumn.Bounds;

        internal Rectangle LaunchCardBoundsForTest =>
            _launchCard == null ? Rectangle.Empty : _launchCard.Bounds;

        internal Rectangle OutputCardBoundsForTest =>
            _outputCard == null ? Rectangle.Empty : _outputCard.Bounds;

        internal int ConfigListItemHeightForTest => _configList == null ? 0 : _configList.ItemHeight;

        internal int NewButtonHeightForTest => _newButton == null ? 0 : _newButton.Height;

        internal int LeftColumnWidthForTest =>
            _contentLayout == null || _contentLayout.ColumnStyles.Count == 0
                ? 0
                : (int)Math.Round(_contentLayout.ColumnStyles[0].Width);
    }
}
