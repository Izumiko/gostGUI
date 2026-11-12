// =====================================================================================
// FormMain.Startup.cs —— 启动与启停：构造期初始化、显示后的自动恢复、startAll/stopAll、
// 进程事件回到 UI 的收尾、开机自启、顶部横幅与构造期字体/图标辅助。
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
        // 初始化
        // =====================================================================================

        private void InitializeManagers()
        {
            try
            {
                _processManager = new ProcessManager();
                _processManager.OutputReceived += OnProcessOutputReceived;
                _processManager.ProcessExited += OnProcessExited;
                _processManager.ProcessStarted += OnProcessStarted;
                _processManager.ProcessStopped += OnProcessStopped;
            }
            catch (Exception ex)
            {
                _processManager = null;
                string message =
                    "初始化进程管理器失败：" + ex.Message + "\r\n\r\n子进程可能无法在本工具退出时被自动清理。";

                // R42：快照/自检模式下绝不弹模态窗口，改成顶部横幅（同一句话仍然显形）。
                if (_suppressStartupStart)
                {
                    ShowBanner(message.Replace("\r\n", " "), true);
                }
                else
                {
                    MessageBox.Show(message, "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            _configManager = _injectedConfigManager ?? new ConfigurationManager();
            _configManager.ItemAdded += OnConfigItemAdded;
            _configManager.ItemRenamed += OnConfigItemRenamed;
            _configManager.ItemDeleted += OnConfigItemDeleted;
            _configManager.ItemUpdated += OnConfigItemUpdated;
            _configManager.SaveFailed += OnConfigSaveFailed;

            _logViewManager = new LogViewManager(_logHost, _monoFont, TryPostUi);

            _themeManager.Mode = _configManager.Theme;
            _themeManager.Changed += OnThemeChanged;
            ApplyTheme();

            ApplyPrivilegeBadge();
            CheckAutoStartStatus();

            if (!string.IsNullOrEmpty(_configManager.LoadError))
            {
                ShowBanner(
                    "配置文件加载失败：" + _configManager.LoadError +
                    "。原文件未被覆盖；下次保存前会先备份为 config.json.bak。",
                    true);
            }

            _leftExpandedColumnWidth = _contentLayout.ColumnStyles[0].Width > 0
                ? (int)_contentLayout.ColumnStyles[0].Width
                : 300;
            _collapsedColumnWidth = (int)Math.Round(_leftExpandedColumnWidth * 64.0 / 300.0);

            // Task 13：按上次保存的 DIP 尺寸/位置恢复；屏幕放不下时夹到工作区。
            ApplyMinimumSize(_layoutDpi);
            RestoreWindowState(false);

            LoadItemsIntoList();
            _elapsedTimer.Start();

            Application.ApplicationExit += OnApplicationExit;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            DrainPendingUiActions();

            // 托盘图标只在真正显示窗口之后出现：--ui-snapshot 与自检永远不会走到这里。
            if (_notifyIcon != null && !_notifyIcon.Visible)
            {
                _notifyIcon.Visible = true;
            }

            if (_restoreMaximized)
            {
                // 最大化必须等窗口真正显示之后再设置，否则 RestoreBounds 会被打乱。
                _restoreMaximized = false;
                WindowState = FormWindowState.Maximized;
            }

            LayoutLogViews();
            RefreshStatus(null);

            // v1.8 在构造函数里就 startAll()；这里推到窗口显示之后，
            // 保证进程事件回到的 UI 线程已经有句柄，同时用户观感不变。
            if (!_startupRestoreDone)
            {
                _startupRestoreDone = true;
                if (!_suppressStartupStart)
                {
                    StartAll(false);
                }
            }
        }

        // =====================================================================================
        // 启停（R30：一律在线程池里执行，进度用 BeginInvoke 回到 UI）
        // =====================================================================================

        /// <summary>双击行 / 点击状态点 / 折叠条的状态点：运行中就停，否则启动。</summary>
        private void ToggleItem(ConfigItem item)
        {
            if (item == null)
            {
                return;
            }

            if (_starting.Contains(item) || _stopping.Contains(item))
            {
                return;
            }

            if (IsItemRunning(item))
            {
                StopItems(new List<ConfigItem> { item });
            }
            else
            {
                StartItems(new List<ConfigItem> { item });
            }
        }

        private void ToggleOrStart(ConfigItem item)
        {
            ToggleItem(item);
        }

        private void StartItems(IList<ConfigItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            ProcessManager manager = _processManager;
            if (manager == null)
            {
                ShowBanner("进程管理器不可用，无法启动配置项。", true);
                return;
            }

            var pending = new List<ConfigItem>();
            foreach (ConfigItem item in items)
            {
                if (item == null || _starting.Contains(item) || _stopping.Contains(item) || IsItemRunning(item))
                {
                    continue;
                }

                pending.Add(item);
                _starting.Add(item);
            }

            RefreshStatus(null);
            if (pending.Count == 0)
            {
                return;
            }

            Task.Run(() =>
            {
                foreach (ConfigItem item in pending)
                {
                    ConfigItem current = item;
                    try
                    {
                        manager.StartProcess(current);
                    }
                    catch (Exception ex)
                    {
                        PostToUi(() => AppendSystemLine(current, "启动失败：" + ex.Message));
                    }
                    finally
                    {
                        PostToUi(() =>
                        {
                            _starting.Remove(current);

                            // 启动可能卡在 UAC/中转上几十秒；这期间行可能已经被删掉。
                            // 那种情况下进程没有行也没有日志视图可挂，必须收掉，不能留一个看不见的进程。
                            if (!IsItemInConfig(current))
                            {
                                _runStarted.Remove(current);
                                StopOrphanedProcess(current);
                                return;
                            }

                            RefreshStatus(current);
                        });
                    }
                }
            });
        }

        /// <summary>收掉“启动期间被删掉”的项留下的进程（幂等，失败只记调试输出）。</summary>
        private void StopOrphanedProcess(ConfigItem item)
        {
            ProcessManager manager = _processManager;
            if (item == null || manager == null)
            {
                return;
            }

            _runStarted.Remove(item);
            _stopping.Add(item);
            RefreshStatus(null);

            Task.Run(() =>
            {
                try
                {
                    manager.StopProcess(item);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("收尾停止孤儿进程失败：" + ex.Message);
                }
                finally
                {
                    PostToUi(() =>
                    {
                        _stopping.Remove(item);
                        _runStarted.Remove(item);
                        RefreshStatus(null);
                    });
                }
            });
        }

        private void StopItems(IList<ConfigItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            ProcessManager manager = _processManager;
            if (manager == null)
            {
                return;
            }

            var pending = new List<ConfigItem>();
            foreach (ConfigItem item in items)
            {
                if (item == null || _starting.Contains(item) || _stopping.Contains(item) || !IsItemRunning(item))
                {
                    continue;
                }

                pending.Add(item);
                _stopping.Add(item);
            }

            RefreshStatus(null);
            if (pending.Count == 0)
            {
                return;
            }

            Task.Run(() =>
            {
                foreach (ConfigItem item in pending)
                {
                    ConfigItem current = item;
                    try
                    {
                        manager.StopProcess(current);
                    }
                    catch (Exception ex)
                    {
                        PostToUi(() => AppendSystemLine(current, "停止失败：" + ex.Message));
                    }
                    finally
                    {
                        PostToUi(() =>
                        {
                            _stopping.Remove(current);
                            RefreshStatus(current);
                        });
                    }
                }
            });
        }

        /// <summary>
        /// R36（纯决策，自检直接断言）：启动时的自动恢复与开机自启只拉起
        /// <c>enable && !runAsAdmin</c> 的项。管理员项必须由用户显式点“启动”或“全部启动”
        /// 才走提权路径（每项一次 UAC），否则启动就会变成一串无法拒绝的弹窗。
        /// </summary>
        internal static bool ShouldAutoRestore(ConfigItem item)
        {
            return item != null && item.Enable && !item.RunAsAdmin;
        }

        /// <summary>与 v1.8 的 startAll(true/false) 等价：写 Enable 标志，然后启动需要运行的项。</summary>
        private void StartAll(bool startAllItems)
        {
            var toStart = new List<ConfigItem>();
            foreach (ConfigItem item in _configManager.Items)
            {
                if (startAllItems && !item.Enable)
                {
                    item.Enable = true;
                    _configManager.UpdateItem(item);
                }

                if (!item.Enable)
                {
                    continue;
                }

                if (!startAllItems && !ShouldAutoRestore(item))
                {
                    // R36：自动恢复/开机自启只记一行日志，不弹 UAC、不启动、也不清 Enable
                    //（用户下次点“全部启动”时它仍是启用状态）。
                    AppendSystemLine(item, AutoRestoreSkipMessage);
                    continue;
                }

                if (!IsItemRunning(item))
                {
                    toStart.Add(item);
                }
            }

            StartItems(toStart);
        }

        /// <summary>与 v1.8 的 stopAll 等价：先清 Enable 标志，再把所有在跑的进程停下来。</summary>
        private void StopAll()
        {
            foreach (ConfigItem item in _configManager.Items)
            {
                if (item.Enable)
                {
                    item.Enable = false;
                    _configManager.UpdateItem(item);
                }
            }

            ProcessManager manager = _processManager;
            if (manager == null)
            {
                RefreshStatus(null);
                return;
            }

            var running = new List<ConfigItem>();
            foreach (ConfigItem item in _configManager.Items)
            {
                if (IsItemRunning(item))
                {
                    running.Add(item);
                    _stopping.Add(item);
                }
            }

            RefreshStatus(null);

            Task.Run(() =>
            {
                try
                {
                    manager.StopAllProcesses();
                }
                catch (Exception ex)
                {
                    PostToUi(() => ShowBanner("停止全部进程失败：" + ex.Message, true));
                }
                finally
                {
                    PostToUi(() =>
                    {
                        foreach (ConfigItem item in running)
                        {
                            _stopping.Remove(item);
                            _runStarted.Remove(item);
                        }

                        RefreshStatus(null);
                    });
                }
            });
        }

        private void OnProcessStarted(ConfigItem item)
        {
            PostToUi(() =>
            {
                if (item == null)
                {
                    return;
                }

                // 进程事件可能来自管道/进程 IO 线程：Enable 与配置文件写入一律回到 UI 线程，
                // 避免在后台线程上跑 ConfigurationManager 的整表保存。
                if (!_isExiting)
                {
                    item.Enable = true;
                    _configManager?.UpdateItem(item);
                }

                _starting.Remove(item);

                // 启动期间这一项被删掉了：不要给一个已经不在配置里的项登记运行状态，
                // 收尾由 StartItems 的完成回调负责（它会把这个进程停掉）。
                if (!IsItemInConfig(item))
                {
                    _runStarted.Remove(item);
                    return;
                }

                _runStarted[item] = DateTime.UtcNow;
                _stopping.Remove(item);
                AppendSystemLine(item, "启动成功。");
                AppendSystemLine(item, "---------------------------------------------------------");
                RefreshStatus(item);
            });
        }

        private void OnProcessExited(ConfigItem item, int exitCode)
        {
            PostToUi(() =>
            {
                if (item == null)
                {
                    return;
                }

                if (!_isExiting)
                {
                    item.Enable = false;
                    _configManager?.UpdateItem(item);
                }

                _runStarted.Remove(item);
                _starting.Remove(item);
                _stopping.Remove(item);
                AppendSystemLine(item, "进程已退出，退出码 " + exitCode + "。");
                RefreshStatus(item);
            });
        }

        private void OnProcessStopped(ConfigItem item)
        {
            PostToUi(() =>
            {
                if (item == null)
                {
                    return;
                }

                if (!_isExiting)
                {
                    item.Enable = false;
                    _configManager?.UpdateItem(item);
                }

                _runStarted.Remove(item);
                _starting.Remove(item);
                _stopping.Remove(item);
                AppendSystemLine(item, "已停止进程。");
                RefreshStatus(item);
            });
        }

        // =====================================================================================
        // 横幅
        // =====================================================================================

        private void ShowBanner(string message, bool isError)
        {
            if (string.IsNullOrEmpty(message))
            {
                HideBanner();
                return;
            }

            if (_captureExitCommitBanner)
            {
                // R2-13：退出前提交时窗口可能已隐藏，横幅会随退出一起被销毁；只记原因，
                // 由退出保存失败（R45 模态框）统一显形。保留第一条，避免后一条覆盖真正的失败原因。
                if (string.IsNullOrEmpty(_exitCommitBannerReason))
                {
                    _exitCommitBannerReason = message;
                }

                return;
            }

            if (_bannerPanel == null || _bannerLabel == null)
            {
                return;
            }

            _bannerLabel.Text = message;
            _bannerLabel.ForeColor = isError ? _themeManager.Current.Danger : _themeManager.Current.TextPrimary;
            _bannerShown = true;
            _bannerPanel.Visible = true;
        }

        private void HideBanner()
        {
            _bannerShown = false;
            if (_bannerPanel != null)
            {
                _bannerPanel.Visible = false;
            }
        }

        // =====================================================================================
        // 开机自启（注册表语义与 v1.8 完全一致：HKCU\...\Run 的 gostGUI 值）
        // =====================================================================================

        private void CheckAutoStartStatus()
        {
            _syncingAutoStart = true;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false))
                {
                    object value = key?.GetValue(AppRegistryKey);
                    string text = value?.ToString();
                    _autoStartToggle.Checked = text != null && text == Application.ExecutablePath;
                }
            }
            catch (Exception)
            {
                // 注册表读不到就按“未启用”显示。
                _autoStartToggle.Checked = false;
            }
            finally
            {
                _syncingAutoStart = false;
            }
        }

        private void AutoStartToggle_CheckedChanged(object sender, EventArgs e)
        {
            if (_syncingAutoStart)
            {
                return;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true))
                {
                    if (key == null)
                    {
                        throw new InvalidOperationException("无法以可写方式打开开机自启注册表项。");
                    }

                    if (_autoStartToggle.Checked)
                    {
                        key.SetValue(AppRegistryKey, Application.ExecutablePath);
                        MessageBox.Show(this, "已启用开机自启。", "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        key.DeleteValue(AppRegistryKey, false);
                        MessageBox.Show(this, "已禁用开机自启。", "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "更新开机自启设置失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                CheckAutoStartStatus();
            }
        }

        // =====================================================================================
        // 字体 / 图标 / DPI
        // =====================================================================================

        /// <summary>界面基准字体：优先雅黑（中文清晰），没有则回落 Segoe UI / 通用无衬线。</summary>
        private static Font CreateBaseFont(float size = 9f)
        {
            string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
            foreach (string name in candidates)
            {
                try
                {
                    using (var family = new FontFamily(name))
                    {
                        return new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
                    }
                }
                catch (Exception)
                {
                    // 该字体不存在，试下一个。
                }
            }

            return new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        /// <summary>日志等宽字体：Cascadia Mono → Consolas → 通用等宽。</summary>
        private static Font CreateMonoFont(float size)
        {
            string[] candidates = { "Cascadia Mono", "Consolas", "Courier New" };
            foreach (string name in candidates)
            {
                try
                {
                    using (var family = new FontFamily(name))
                    {
                        return new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
                    }
                }
                catch (Exception)
                {
                    // 该字体不存在，试下一个。
                }
            }

            return new Font(FontFamily.GenericMonospace, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        /// <summary>窗体/托盘图标：从自身 exe 关联图标取（不再依赖 FormMain.resx 的内嵌图标）。</summary>
        private static Icon LoadApplicationIcon()
        {
            try
            {
                Icon extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null)
                {
                    return extracted;
                }
            }
            catch (Exception)
            {
                // 取不到就回落系统图标。
            }

            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
