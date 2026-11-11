using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace gostGUI
{
    /// <summary>
    /// 主窗口：代码搭建的两栏骨架（左：配置列表 + 操作按钮；右：启动设置槽位 + 输出槽位）。
    /// <para>
    /// Task 12a 只做骨架与 v1.8 行为全量对齐：详情编辑器字段和带颜色的日志控件由 12b 填进
    /// <see cref="LaunchSlotPanel"/> 与 <see cref="LogHost"/> 这两个明确标注的槽位。
    /// </para>
    /// <para>
    /// R30 纪律：StartProcess / StopProcess 可能因中转 + UAC 阻塞数十秒，一律不落 UI 线程；
    /// 状态变化通过 PostToUi（BeginInvoke）回到界面线程。
    /// </para>
    /// </summary>
    public partial class FormMain : Form
    {
        private const string AppRegistryKey = "gostGUI";
        private const string RunRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";

        /// <summary>--ui-snapshot 固定输出尺寸（1024×640 DIP 设计基线）。</summary>
        private const int SnapshotWidth = 1024;
        private const int SnapshotHeight = 640;

        private ProcessManager _processManager;
        private ConfigurationManager _configManager;
        private LogViewManager _logViewManager;
        private readonly ThemeManager _themeManager = new ThemeManager();

        /// <summary>哪些项正在运行（含启动时刻，用于列表右侧的“运行中 8m12s”）。只在 UI 线程读写。</summary>
        private readonly Dictionary<ConfigItem, DateTime> _runStarted = new Dictionary<ConfigItem, DateTime>();

        /// <summary>正在启动/停止的项：按钮置灰并显示“启动中…”/“停止中…”。只在 UI 线程读写。</summary>
        private readonly HashSet<ConfigItem> _starting = new HashSet<ConfigItem>();
        private readonly HashSet<ConfigItem> _stopping = new HashSet<ConfigItem>();

        private readonly bool _suppressStartupStart;

        /// <summary>UI 线程 id：PostToUi 用它判断“真的在 UI 线程上”，不再依赖会撒谎的 InvokeRequired。</summary>
        private readonly int _uiThreadId;

        /// <summary>句柄还没建好时先排队的 UI 回调；句柄一建好（OnHandleCreated）就按顺序排空。</summary>
        private readonly object _pendingUiLock = new object();
        private readonly Queue<Action> _pendingUiActions = new Queue<Action>();

        /// <summary>自检/快照用的外部配置管理器（null 表示用应用目录的 config.json）。</summary>
        private readonly ConfigurationManager _injectedConfigManager;

        private bool _startupRestoreDone;
        private bool _isExiting;
        private bool _exitRequested;
        private bool _processManagerDisposed;
        private bool _disposeCompleted;
        private bool _syncingAutoStart;
        private bool _collapsed;
        private bool _endingRename;
        private int _leftExpandedColumnWidth;
        private int _collapsedColumnWidth;
        private ConfigItem _currentEditingItem;

        /// <summary>正常的启动路径：从配置恢复上次运行状态，并显示窗口与托盘图标。</summary>
        public FormMain()
            : this(false)
        {
        }

        /// <summary>
        /// 内部构造：suppressStartupStart=true 时不执行启动时的自动恢复（--ui-snapshot 与自检用），
        /// 公共默认行为不受影响。
        /// </summary>
        internal FormMain(bool suppressStartupStart)
            : this(suppressStartupStart, null)
        {
        }

        /// <summary>自检用：注入临时目录里的 ConfigurationManager，避免碰到用户那份 config.json。</summary>
        internal FormMain(bool suppressStartupStart, ConfigurationManager configurationManager)
        {
            // 必须先记住 UI 线程：PostToUi 的内联判定完全依赖它（Control.InvokeRequired
            // 在句柄没建好时对任何线程都返回 false，不能用来判断线程归属）。
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;
            _suppressStartupStart = suppressStartupStart;
            _injectedConfigManager = configurationManager;
            InitializeComponent();
            InitializeManagers();
        }

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

            _logViewManager = new LogViewManager(_logHost, _logTemplate);

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

            LoadItemsIntoList();
            _elapsedTimer.Start();

            Application.ApplicationExit += OnApplicationExit;
        }

        /// <summary>把 ConfigurationManager.Items 按顺序灌进列表（不触发配置写入）。</summary>
        private void LoadItemsIntoList()
        {
            _configList.BeginUpdate();
            try
            {
                _configList.Items.Clear();
                foreach (ConfigItem item in _configManager.Items)
                {
                    _logViewManager?.AddLogView(item);
                    _configList.Items.Add(item);
                }
            }
            finally
            {
                _configList.EndUpdate();
            }

            UpdateCountLabel();

            if (_configList.Items.Count > 0)
            {
                _configList.SelectedIndex = 0;
            }
            else
            {
                UpdateDetailSummary();
                _logViewManager?.ShowLogView(null);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄出现后补一次深色标题栏与积压的 UI 回调：
            // 构造函数阶段没有句柄，不能让任何回调/主题调用抢先建句柄。
            if (ReferenceEquals(_themeManager.Current, Palette.Dark))
            {
                ThemeManager.ApplyDarkTitleBar(this);
            }

            DrainPendingUiActions();
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
                    _themeManager.Mode = ThemeMode.System;
                }
            }
            catch (Exception)
            {
                // 主题热更新失败不影响主流程。
            }
        }

        // =====================================================================================
        // 主题与徽标
        // =====================================================================================

        private void OnThemeChanged()
        {
            ApplyTheme();
        }

        internal Palette CurrentPalette => _themeManager.Current;

        /// <summary>把当前调色板铺到所有控件；非 IThemed 的容器/标签在这里单独上色。</summary>
        private void ApplyTheme()
        {
            Palette palette = _themeManager.Current;
            BackColor = palette.Surface;
            ForeColor = palette.TextPrimary;

            _themeManager.ApplyTo(this);

            _topBar.BackColor = palette.Surface;
            _themeHost.BackColor = palette.Surface;
            _leftPanel.BackColor = palette.Surface;
            _leftHeader.BackColor = palette.Surface;
            _leftButtons.BackColor = palette.Surface;
            _itemButtonRow.BackColor = palette.Surface;
            _allButtonRow.BackColor = palette.Surface;
            _autoStartRow.BackColor = palette.Surface;
            _rightColumn.BackColor = palette.Surface;
            _contentLayout.BackColor = palette.Surface;
            _launchHeader.BackColor = palette.SurfaceAlt;
            _launchButtonRow.BackColor = palette.SurfaceAlt;
            _outputHeader.BackColor = palette.SurfaceAlt;
            _slotPanel.BackColor = palette.Surface;
            _bannerPanel.BackColor = palette.SurfaceAlt;

            _titleLabel.ForeColor = palette.TextPrimary;
            _listHeaderLabel.ForeColor = palette.TextPrimary;
            _launchTitleLabel.ForeColor = palette.TextPrimary;
            _outputTitleLabel.ForeColor = palette.TextPrimary;
            _detailSummaryLabel.ForeColor = palette.TextPrimary;
            _slotLabel.ForeColor = palette.TextMuted;
            _bannerLabel.ForeColor = palette.Danger;

            _logHost.BackColor = palette.LogBg;
            ApplyLogViewColors(palette);
            if (_renameBox != null)
            {
                _renameBox.BackColor = palette.SurfaceAlt;
                _renameBox.ForeColor = palette.TextPrimary;
            }

            var renderer = new ThemedMenuRenderer(palette);
            _listMenu.Renderer = renderer;
            _trayMenu.Renderer = renderer;
            _themeMenu.Renderer = renderer;

            if (ReferenceEquals(palette, Palette.Dark) && IsHandleCreated)
            {
                // 只在句柄已存在时调：构造函数阶段（尤其是 --ui-snapshot / 自检）
                // 不能因为设置深色标题栏而提前把窗体句柄建出来。
                ThemeManager.ApplyDarkTitleBar(this);
            }

            UpdateThemeSelector();
            ApplyPrivilegeBadge();
        }

        private void ApplyLogViewColors(Palette palette)
        {
            if (_logHost == null)
            {
                return;
            }

            _logTemplate.BackColor = palette.LogBg;
            _logTemplate.ForeColor = palette.LogStdout;
            foreach (Control child in _logHost.Controls)
            {
                var box = child as TextBox;
                if (box == null || ReferenceEquals(box, _logTemplate))
                {
                    continue;
                }

                box.BackColor = palette.LogBg;
                box.ForeColor = palette.LogStdout;
            }
        }

        private void UpdateThemeSelector()
        {
            if (_themeButton == null)
            {
                return;
            }

            ThemeMode mode = _themeManager.Mode;
            _themeButton.Text = ThemeModeLabel(mode) + " ▼";
            _themeMenuSystem.Checked = mode == ThemeMode.System;
            _themeMenuLight.Checked = mode == ThemeMode.Light;
            _themeMenuDark.Checked = mode == ThemeMode.Dark;
        }

        private static string ThemeModeLabel(ThemeMode mode)
        {
            switch (mode)
            {
                case ThemeMode.Light:
                    return "浅色";
                case ThemeMode.Dark:
                    return "深色";
                default:
                    return "跟随系统";
            }
        }

        private void ThemeButton_Click(object sender, EventArgs e)
        {
            if (_themeMenu == null)
            {
                return;
            }

            _themeMenu.Show(_themeButton, new Point(0, _themeButton.Height));
        }

        /// <summary>三态主题切换：先更新内存中的调色板，再把选择持久化到 config.json。</summary>
        private void SetThemeMode(ThemeMode mode)
        {
            _themeManager.Mode = mode;
            _configManager?.SetTheme(mode);
        }

        private void ApplyPrivilegeBadge()
        {
            if (_badgeLabel == null)
            {
                return;
            }

            bool elevated;
            try
            {
                elevated = ElevationManager.IsProcessElevated();
            }
            catch (Exception)
            {
                elevated = false;
            }

            Palette palette = _themeManager.Current;
            _badgeLabel.Text = elevated ? "权限：管理员" : "权限：标准";
            _badgeLabel.ForeColor = elevated ? palette.Accent : palette.TextMuted;
        }

        // =====================================================================================
        // 列表状态
        // =====================================================================================

        private ConfigItem GetCurrentConfigItem()
        {
            return _configList?.SelectedItem as ConfigItem;
        }

        /// <summary>当前选中的配置项（12b 的详情编辑器会用它回填字段）。</summary>
        public ConfigItem SelectedItem => GetCurrentConfigItem();

        private IList<ConfigItem> SelectedItemAsList()
        {
            ConfigItem item = GetCurrentConfigItem();
            if (item == null)
            {
                return new List<ConfigItem>();
            }

            return new List<ConfigItem> { item };
        }

        private IList<ConfigItem> ListItems()
        {
            var items = new List<ConfigItem>(_configList.Items.Count);
            foreach (object entry in _configList.Items)
            {
                var item = entry as ConfigItem;
                if (item != null)
                {
                    items.Add(item);
                }
            }

            return items;
        }

        private bool IsItemRunning(ConfigItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (_runStarted.ContainsKey(item))
            {
                return true;
            }

            return _processManager != null && _processManager.IsProcessRunning(item);
        }

        /// <summary>配置里还有这一项吗（删除与启动/停止并发时用来判断“行还在不在”）。</summary>
        private bool IsItemInConfig(ConfigItem item)
        {
            return item != null && IndexOfConfigItem(item) >= 0;
        }

        private int IndexOfConfigItem(ConfigItem item)
        {
            if (item == null || _configManager == null)
            {
                return -1;
            }

            IReadOnlyList<ConfigItem> items = _configManager.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (ReferenceEquals(items[i], item))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>自检缝：当前列表里的项（按界面顺序）。</summary>
        internal IList<ConfigItem> ListItemsForTest()
        {
            return ListItems();
        }

        /// <summary>列表右侧的状态文本：运行时长 / 启动中 / 停止中 / 已停止。</summary>
        private string StatusTextFor(ConfigItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            if (_starting.Contains(item))
            {
                return "启动中…";
            }

            if (_stopping.Contains(item))
            {
                return "停止中…";
            }

            DateTime startedAt;
            if (_runStarted.TryGetValue(item, out startedAt))
            {
                return "运行中 " + FormatElapsed(DateTime.UtcNow - startedAt);
            }

            return "已停止";
        }

        private static string FormatElapsed(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            if (span.TotalHours >= 1)
            {
                return string.Format("{0}h{1}m", (int)span.TotalHours, span.Minutes);
            }

            if (span.TotalMinutes >= 1)
            {
                return string.Format("{0}m{1}s", span.Minutes, span.Seconds);
            }

            return ((int)span.TotalSeconds) + "s";
        }

        /// <summary>行副标题：优先显示参数，没有参数时显示程序。</summary>
        private static string SubtitleFor(ConfigItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            string args = (item.Args ?? string.Empty).Trim();
            if (args.Length > 0)
            {
                return args.Replace('\r', ' ').Replace('\n', ' ');
            }

            string program = (item.Program ?? string.Empty).Trim();
            return program.Length > 0 ? program : "（未设置程序）";
        }

        private void UpdateCountLabel()
        {
            if (_listHeaderLabel != null)
            {
                _listHeaderLabel.Text = "配置 (" + (_configList?.Items.Count ?? 0) + ")";
            }
        }

        private void UpdateDetailSummary()
        {
            if (_detailSummaryLabel == null)
            {
                return;
            }

            ConfigItem item = GetCurrentConfigItem();
            if (item == null)
            {
                _detailSummaryLabel.Text = "当前没有选中的配置项。";
            }
            else
            {
                string program = string.IsNullOrWhiteSpace(item.Program) ? "（未设置）" : item.Program.Trim();
                string args = string.IsNullOrWhiteSpace(item.Args) ? "（空）" : item.Args.Trim().Replace('\r', ' ').Replace('\n', ' ');
                string state = IsItemRunning(item) ? "运行中" : "已停止";
                _detailSummaryLabel.Text =
                    "已选配置：" + (item.Name ?? string.Empty) + "　程序：" + program + "　参数：" + args + "　状态：" + state;
            }

            UpdateActionButtons();
        }

        private void UpdateActionButtons()
        {
            if (_startButton == null)
            {
                return;
            }

            ConfigItem item = GetCurrentConfigItem();
            bool hasSelection = item != null;
            bool running = hasSelection && IsItemRunning(item);
            bool busy = hasSelection && (_starting.Contains(item) || _stopping.Contains(item));

            _startButton.Enabled = hasSelection && !running && !busy;
            _stopButton.Enabled = hasSelection && running && !busy;
            _duplicateButton.Enabled = hasSelection;
            // 删除在“启动中…/停止中…”期间禁用：StartProcess 可能卡在 UAC/中转上几十秒，
            // 这期间删行会让进程在完成后失去行与日志视图（见 StartItems 的收尾检查）。
            _deleteButton.Enabled = hasSelection && !busy;
            _moveUpButton.Enabled = hasSelection && _configList.SelectedIndex > 0;
            _moveDownButton.Enabled = hasSelection && _configList.SelectedIndex < _configList.Items.Count - 1;
        }

        /// <summary>列表/状态条重绘 + 按钮可用性刷新；item 参数只为可读性保留。</summary>
        private void RefreshStatus(ConfigItem item)
        {
            if (_configList != null && !_configList.IsDisposed)
            {
                _configList.Invalidate();
            }

            if (_collapsedStrip != null && !_collapsedStrip.IsDisposed)
            {
                _collapsedStrip.Invalidate();
            }

            UpdateActionButtons();
            UpdateDetailSummary();
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

                if (item.Enable && !IsItemRunning(item))
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

        // =====================================================================================
        // 进程事件（可能来自工作线程）
        // =====================================================================================

        private void OnProcessOutputReceived(ConfigItem item, LogKind kind, string data)
        {
            // 12b 会按 LogKind 着色；当前 LogViewManager 仍是 v1.8 的纯文本实现。
            AppendLog(item, data);
        }

        private void OnProcessStarted(ConfigItem item)
        {
            if (item != null)
            {
                item.Enable = true;
                _configManager?.UpdateItem(item);
            }

            PostToUi(() =>
            {
                if (item == null)
                {
                    return;
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
            if (!_isExiting && item != null)
            {
                item.Enable = false;
                _configManager?.UpdateItem(item);
            }

            PostToUi(() =>
            {
                if (item == null)
                {
                    return;
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
            if (!_isExiting && item != null)
            {
                item.Enable = false;
                _configManager?.UpdateItem(item);
            }

            PostToUi(() =>
            {
                if (item == null)
                {
                    return;
                }

                _runStarted.Remove(item);
                _starting.Remove(item);
                _stopping.Remove(item);
                AppendSystemLine(item, "已停止进程。");
                RefreshStatus(item);
            });
        }

        private void AppendLog(ConfigItem item, string text)
        {
            if (item == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            _logViewManager?.AppendText(item, text);
        }

        /// <summary>写一行系统日志（启动/停止/退出码/错误），12b 会按 LogKind.System 着色。</summary>
        internal void AppendSystemLine(ConfigItem item, string message)
        {
            if (item == null || string.IsNullOrEmpty(message))
            {
                return;
            }

            _logViewManager?.AppendText(item, message + Environment.NewLine);
        }

        /// <summary>
        /// 把动作排到 UI 线程。
        /// <para>
        /// 线程归属只认构造函数里记下的 <see cref="_uiThreadId"/>：<c>Control.InvokeRequired</c>
        /// 在窗体/祖先都没有句柄时对<b>任何</b>线程都返回 false，拿它当“我在 UI 线程”会把
        /// “窗口还没显示就先自动恢复配置项”这条启动路径上的进程事件直接跑在工作线程里。
        /// </para>
        /// <para>
        /// 句柄没建好时先入队，句柄一建好（OnHandleCreated/OnShown）按入队顺序在 UI 线程排空。
        /// </para>
        /// </summary>
        private void PostToUi(Action action)
        {
            TryPostUi(action);
        }

        /// <summary>自检缝：走与生产代码完全相同的 PostToUi 路径（用于验证句柄建立前的跨线程投递）。</summary>
        internal void PostToUiForTest(Action action)
        {
            PostToUi(action);
        }

        /// <summary>PostToUi 的“能投递吗”版本：UI 线程已不可用（已释放）时返回 false，供退出流程兜底。</summary>
        private bool TryPostUi(Action action)
        {
            if (action == null)
            {
                return true;
            }

            if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
            {
                RunUiAction(action);
                return true;
            }

            if (IsDisposed || Disposing)
            {
                return false;
            }

            lock (_pendingUiLock)
            {
                _pendingUiActions.Enqueue(action);
            }

            if (!IsHandleCreated)
            {
                // 句柄还没建好：留在队列里，等 OnHandleCreated 统一排空。
                return true;
            }

            return TryScheduleUiDrain();
        }

        private bool TryScheduleUiDrain()
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated)
                {
                    return false;
                }

                BeginInvoke(new Action(DrainPendingUiActions));
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("PostToUi 投递失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>在 UI 线程上按入队顺序执行积压的 UI 回调；在别的线程调用会被忽略。</summary>
        private void DrainPendingUiActions()
        {
            if (Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                return;
            }

            while (true)
            {
                Action next;
                lock (_pendingUiLock)
                {
                    if (_pendingUiActions.Count == 0)
                    {
                        return;
                    }

                    next = _pendingUiActions.Dequeue();
                }

                RunUiAction(next);
            }
        }

        private static void RunUiAction(Action action)
        {
            if (action == null)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("PostToUi 回调失败：" + ex.Message);
            }
        }

        // =====================================================================================
        // ConfigurationManager 事件
        // =====================================================================================

        private void OnConfigItemAdded(ConfigItem item)
        {
            PostToUi(() =>
            {
                if (item == null)
                {
                    return;
                }

                _logViewManager?.AddLogView(item);
                if (!_configList.Items.Contains(item))
                {
                    // 按 ConfigurationManager.Items 里的真实下标插入：复制会把副本紧插在源项后面，
                    // 不能一律 Add 到底部（否则界面顺序与 config.json 顺序、↑/↓ 的语义就对不上）。
                    int index = IndexOfConfigItem(item);
                    if (index >= 0 && index <= _configList.Items.Count)
                    {
                        _configList.Items.Insert(index, item);
                    }
                    else
                    {
                        _configList.Items.Add(item);
                    }
                }

                UpdateCountLabel();
                RefreshStatus(item);
            });
        }

        private void OnConfigItemRenamed(ConfigItem item, string newName)
        {
            PostToUi(() =>
            {
                _configList.Invalidate();
                UpdateDetailSummary();
            });
        }

        private void OnConfigItemDeleted(ConfigItem item)
        {
            PostToUi(() =>
            {
                if (item == null)
                {
                    return;
                }

                _logViewManager?.RemoveLogView(item);

                int index = _configList.Items.IndexOf(item);
                if (index >= 0)
                {
                    _configList.Items.RemoveAt(index);
                }

                if (_configList.Items.Count > 0 && _configList.SelectedIndex < 0)
                {
                    _configList.SelectedIndex = Math.Min(Math.Max(index, 0), _configList.Items.Count - 1);
                }

                UpdateCountLabel();
                if (_configList.SelectedItem == null)
                {
                    UpdateDetailSummary();
                    _logViewManager?.ShowLogView(null);
                }

                RefreshStatus(item);
                LayoutLogViews();
            });
        }

        private void OnConfigItemUpdated(ConfigItem item)
        {
            PostToUi(UpdateDetailSummary);
        }

        private void OnConfigSaveFailed(string message)
        {
            PostToUi(() => ShowBanner(message, true));
        }

        // =====================================================================================
        // 列表交互
        // =====================================================================================

        private void ConfigList_SelectedIndexChanged(object sender, EventArgs e)
        {
            ConfigItem item = GetCurrentConfigItem();
            if (item != null)
            {
                // 每个配置项各自一份日志视图，切项即切视图。
                _logViewManager?.ShowLogView(item);
                LayoutLogViews();
            }

            UpdateDetailSummary();
            RefreshStatus(item);
        }

        private void ConfigList_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            int index = _configList.IndexFromPoint(e.Location);
            if (index != ListBox.NoMatches && index >= 0)
            {
                _configList.SelectedIndex = index;
            }
        }

        private void ConfigList_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            int index = _configList.IndexFromPoint(e.Location);
            if (index < 0 || index >= _configList.Items.Count)
            {
                return;
            }

            Rectangle itemBounds = _configList.GetItemRectangle(index);
            if (StatusDotBounds(itemBounds, _configList).Contains(e.Location))
            {
                ToggleItem(_configList.Items[index] as ConfigItem);
            }
        }

        private void ConfigList_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            int index = _configList.IndexFromPoint(e.Location);
            if (index < 0 || index >= _configList.Items.Count)
            {
                return;
            }

            ToggleItem(_configList.Items[index] as ConfigItem);
        }

        /// <summary>与 ThemedListBox 的绘制逻辑保持一致的状态点矩形（并放大成点击热区）。</summary>
        private static Rectangle StatusDotBounds(Rectangle itemBounds, Control owner)
        {
            int padding = ThemePaint.Scale(owner, 10);
            int dotSize = ThemePaint.Scale(owner, 10);
            int nameHeight = Math.Max(1, itemBounds.Height / 2);
            int top = itemBounds.Top + Math.Max(0, (nameHeight - dotSize) / 2);
            var dot = new Rectangle(itemBounds.Left + padding, top, dotSize, dotSize);
            return Rectangle.Inflate(dot, ThemePaint.Scale(owner, 5), ThemePaint.Scale(owner, 8));
        }

        private void ListMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            EndListBoxEdit(false);

            ConfigItem item = GetCurrentConfigItem();
            bool hasSelection = item != null;
            bool running = hasSelection && IsItemRunning(item);
            bool busy = hasSelection && (_starting.Contains(item) || _stopping.Contains(item));

            _menuNew.Enabled = true;
            // 启动中/停止中不允许改名与删除：这两步都可能要等几十秒（UAC/中转），
            // 期间改配置会让完成回调对一个已经变形的项收尾。
            _menuRename.Enabled = hasSelection && !busy;
            _menuDelete.Enabled = hasSelection && !busy;
            _menuStart.Enabled = hasSelection && !running && !busy;
            _menuStop.Enabled = hasSelection && running && !busy;
        }

        private void CollapsedStrip_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            IList<ConfigItem> items = _collapsedStrip.GetItems();
            int index = _collapsedStrip.HitTest(e.Location);
            if (items == null || index < 0 || index >= items.Count)
            {
                return;
            }

            ToggleItem(items[index]);
        }

        private void CollapseButton_Click(object sender, EventArgs e)
        {
            SetLeftColumnCollapsed(!_collapsed);
        }

        /// <summary>
        /// 折叠决策：不重建列表，直接把左栏收窄到约 56 DIP，隐藏列表与按钮区，
        /// 改用一条自绘的状态窄条（状态点 + 名称前两字，点击仍可启停）。展开恢复原宽度与列表。
        /// </summary>
        private void SetLeftColumnCollapsed(bool collapsed)
        {
            EndListBoxEdit(false);

            _collapsed = collapsed;
            _contentLayout.ColumnStyles[0].Width = collapsed ? _collapsedColumnWidth : _leftExpandedColumnWidth;
            _listCard.Visible = !collapsed;
            _leftButtons.Visible = !collapsed;
            _listHeaderLabel.Visible = !collapsed;
            _collapsedStrip.Visible = collapsed;
            _collapseButton.Text = collapsed ? "»" : "«";
            _collapseButton.Width = collapsed
                ? Math.Max(_collapseButton.Width, _collapsedColumnWidth - ThemePaint.Scale(_collapseButton, 6))
                : ThemePaint.Scale(_collapseButton, 30);
            _contentLayout.PerformLayout();
            RefreshStatus(null);
        }

        // =====================================================================================
        // 行内重命名
        // =====================================================================================

        private void BeginRename(ConfigItem item)
        {
            if (item == null)
            {
                return;
            }

            int index = _configList.Items.IndexOf(item);
            if (index < 0)
            {
                return;
            }

            _configList.SelectedIndex = index;
            Rectangle itemBounds = _configList.GetItemRectangle(index);
            if (itemBounds.Height <= 0)
            {
                return;
            }

            // 与 ThemedListBox.OnDrawItem 的绘制几何严格对齐：
            // 状态点占 [padding, padding+dotSize]，名称从 dotSize+gap 之后开始画，
            // 名称文本盒是 bounds.Top + 3 DIP 起、高 nameHeight - 3。
            // 之前在 12 DIP 起画编辑框，既压住了状态点又比文字高一截。
            int padding = ThemePaint.Scale(_configList, 10);
            int dotSize = ThemePaint.Scale(_configList, 10);
            int gap = ThemePaint.Scale(_configList, 10);
            int nameHeight = Math.Max(1, itemBounds.Height / 2);
            int textTopInset = ThemePaint.Scale(_configList, 3);
            int textLeft = itemBounds.Left + padding + dotSize + gap;

            _currentEditingItem = item;
            _renameBox.Parent = _configList;
            _renameBox.Font = _configList.Font;
            _renameBox.BackColor = _themeManager.Current.SurfaceAlt;
            _renameBox.ForeColor = _themeManager.Current.TextPrimary;
            _renameBox.Bounds = new Rectangle(
                textLeft,
                itemBounds.Top + textTopInset,
                Math.Max(ThemePaint.Scale(_configList, 60), itemBounds.Right - padding - textLeft),
                Math.Max(ThemePaint.Scale(_configList, 18), nameHeight - textTopInset));
            _renameBox.Text = item.Name ?? string.Empty;
            _renameBox.Visible = true;
            _renameBox.BringToFront();
            _renameBox.Focus();
            _renameBox.SelectAll();
        }

        private void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                EndListBoxEdit(true);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                EndListBoxEdit(false);
            }
        }

        private void RenameBox_Leave(object sender, EventArgs e)
        {
            // 失焦提交（与 v1.8 行为一致）。
            EndListBoxEdit(true);
        }

        /// <summary>结束行内重命名：Enter/失焦提交，Esc 取消；重名时提示中文错误并保持编辑态。</summary>
        private void EndListBoxEdit(bool commitChanges)
        {
            if (_endingRename)
            {
                return;
            }

            _endingRename = true;
            try
            {
                if (_renameBox == null || !_renameBox.Visible)
                {
                    _currentEditingItem = null;
                    return;
                }

                ConfigItem editing = _currentEditingItem;
                string newName = (_renameBox.Text ?? string.Empty).Trim();
                string oldName = editing == null ? string.Empty : (editing.Name ?? string.Empty);

                if (!commitChanges || editing == null || string.IsNullOrEmpty(newName) || newName == oldName)
                {
                    _currentEditingItem = null;
                    _renameBox.Visible = false;
                    _configList.Invalidate();
                    return;
                }

                string error;
                if (_configManager.RenameItem(editing, newName, out error))
                {
                    _currentEditingItem = null;
                    _renameBox.Visible = false;
                    _configList.Invalidate();
                    UpdateCountLabel();
                    UpdateDetailSummary();
                    return;
                }

                MessageBox.Show(
                    this,
                    error ?? "重命名失败。",
                    "无法重命名",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                if (_renameBox.Visible)
                {
                    _renameBox.Focus();
                    _renameBox.SelectAll();
                }
            }
            finally
            {
                _endingRename = false;
            }
        }

        // =====================================================================================
        // 新建 / 复制 / 删除 / 排序
        // =====================================================================================

        private void NewButton_Click(object sender, EventArgs e)
        {
            EndListBoxEdit(false);
            ConfigItem newItem = _configManager.AddNewItem();
            if (newItem != null)
            {
                SelectItem(newItem);
            }
        }

        private void DuplicateButton_Click(object sender, EventArgs e)
        {
            EndListBoxEdit(false);
            ConfigItem source = GetCurrentConfigItem();
            if (source == null)
            {
                return;
            }

            ConfigItem copy = _configManager.DuplicateItem(source);
            if (copy != null)
            {
                // 副本在 ConfigurationManager 里紧插在源项后面，界面顺序必须按管理器重建一次，
                // 否则列表底部会多一行、而 config.json 的顺序却在源项后面（↑/↓ 会作用到错的行）。
                RebuildList(copy);
            }
        }

        /// <summary>自检缝：走与“复制”按钮完全相同的路径。</summary>
        internal void DuplicateForTest(ConfigItem source)
        {
            if (source == null)
            {
                return;
            }

            SelectItem(source);
            DuplicateButton_Click(null, EventArgs.Empty);
        }

        /// <summary>自检缝：建句柄并排一次布局（隐藏窗体上不 Show 也能拿到行矩形）。</summary>
        internal void PrepareHandlesForTest()
        {
            CreateControl();
            ForceHandles(this);
            PerformLayout();
        }

        /// <summary>自检缝：走与右键“重命名”完全相同的路径。</summary>
        internal void BeginRenameForTest(ConfigItem item)
        {
            BeginRename(item);
        }

        /// <summary>自检缝：行内编辑框当前的位置。</summary>
        internal Rectangle RenameEditorBoundsForTest => _renameBox.Bounds;

        /// <summary>自检缝：列表第 index 行的矩形（未命中返回空矩形）。</summary>
        internal Rectangle ListRowBoundsForTest(int index)
        {
            if (index < 0 || index >= _configList.Items.Count)
            {
                return Rectangle.Empty;
            }

            return _configList.GetItemRectangle(index);
        }

        private void DeleteButton_Click(object sender, EventArgs e)
        {
            EndListBoxEdit(false);
            ConfigItem item = GetCurrentConfigItem();
            if (item == null)
            {
                return;
            }

            // 启动中/停止中不允许删（按钮与菜单已经置灰，这里是第二道保险）：
            // StartProcess 可能卡在 UAC/中转上几十秒，等到它返回时行已经没了，进程就成了孤儿。
            if (_starting.Contains(item) || _stopping.Contains(item))
            {
                ShowBanner("该配置项正在启动或停止，请等当前操作结束后再删除。", true);
                return;
            }

            // 与 v1.8 一致：删除不弹确认框，先停进程再删配置。
            _starting.Remove(item);
            _stopping.Remove(item);
            ProcessManager manager = _processManager;

            // 两步都可能阻塞（停止要等中转/进程退出），全部放到线程池。
            Task.Run(() =>
            {
                try
                {
                    manager?.StopProcess(item);
                }
                catch (Exception ex)
                {
                    PostToUi(() => AppendSystemLine(item, "停止失败：" + ex.Message));
                }
                finally
                {
                    PostToUi(() =>
                    {
                        _runStarted.Remove(item);
                        _stopping.Remove(item);
                        _configManager.DeleteItem(item);
                    });
                }
            });
        }

        private void MoveUpButton_Click(object sender, EventArgs e)
        {
            MoveSelected(-1);
        }

        private void MoveDownButton_Click(object sender, EventArgs e)
        {
            MoveSelected(1);
        }

        private void MoveSelected(int delta)
        {
            EndListBoxEdit(false);
            ConfigItem item = GetCurrentConfigItem();
            if (item == null)
            {
                return;
            }

            if (!_configManager.MoveItem(item, delta))
            {
                return;
            }

            RebuildList(item);
        }

        /// <summary>排序后按 ConfigurationManager.Items 的顺序重建列表，并保持选中项。</summary>
        private void RebuildList(ConfigItem selected)
        {
            _configList.BeginUpdate();
            try
            {
                _configList.Items.Clear();
                foreach (ConfigItem item in _configManager.Items)
                {
                    _configList.Items.Add(item);
                }
            }
            finally
            {
                _configList.EndUpdate();
            }

            SelectItem(selected);
            UpdateCountLabel();
            RefreshStatus(selected);
        }

        private void SelectItem(ConfigItem item)
        {
            if (item == null)
            {
                return;
            }

            int index = _configList.Items.IndexOf(item);
            if (index >= 0)
            {
                _configList.SelectedIndex = index;
                _configList.Invalidate();
                LayoutLogViews();
            }

            UpdateDetailSummary();
            RefreshStatus(item);
        }

        // =====================================================================================
        // 日志区
        // =====================================================================================

        private void LogHost_Resize(object sender, EventArgs e)
        {
            LayoutLogViews();
        }

        /// <summary>把模板与各配置项的日志控件铺满日志槽位（LogViewManager 会复制模板的 Location/Size）。</summary>
        private void LayoutLogViews()
        {
            if (_logHost == null || _logTemplate == null || _logHost.IsDisposed)
            {
                return;
            }

            Size size = _logHost.ClientSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            var bounds = new Rectangle(0, 0, size.Width, size.Height);
            _logTemplate.Bounds = bounds;

            foreach (Control child in _logHost.Controls)
            {
                var box = child as TextBox;
                if (box == null || ReferenceEquals(box, _logTemplate))
                {
                    continue;
                }

                box.Bounds = bounds;
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
            if (_logHost == null)
            {
                return string.Empty;
            }

            foreach (Control child in _logHost.Controls)
            {
                var box = child as TextBox;
                if (box != null && !ReferenceEquals(box, _logTemplate) && box.Visible)
                {
                    return box.Text;
                }
            }

            return string.Empty;
        }

        // =====================================================================================
        // 横幅
        // =====================================================================================

        private void ShowBanner(string message, bool isError)
        {
            if (_bannerPanel == null || _bannerLabel == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(message))
            {
                HideBanner();
                return;
            }

            _bannerLabel.Text = message;
            _bannerLabel.ForeColor = isError ? _themeManager.Current.Danger : _themeManager.Current.TextPrimary;
            _bannerPanel.Visible = true;
        }

        private void HideBanner()
        {
            if (_bannerPanel != null)
            {
                _bannerPanel.Visible = false;
            }
        }

        // =====================================================================================
        // 拖放设置程序（12b 会把这两个处理器挂到程序输入框上）
        // =====================================================================================

        /// <summary>12b 用：把程序输入框接成拖放目标（内层 TextBox 允许拖放 + 两个处理器）。</summary>
        public void AttachProgramDropTarget(ThemedTextBox box)
        {
            if (box == null || box.Inner == null)
            {
                return;
            }

            box.Inner.AllowDrop = true;
            box.Inner.DragEnter += ProgramField_DragEnter;
            box.Inner.DragDrop += ProgramField_DragDrop;
        }

        /// <summary>程序输入框的 DragEnter：只接受文件拖入。</summary>
        public void ProgramField_DragEnter(object sender, DragEventArgs e)
        {
            if (e == null || e.Data == null)
            {
                return;
            }

            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        }

        /// <summary>程序输入框的 DragDrop：把拖入的第一个文件写进当前配置项的 Program 并保存。</summary>
        public void ProgramField_DragDrop(object sender, DragEventArgs e)
        {
            string path = GetFirstDroppedFile(e);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            ConfigItem item = GetCurrentConfigItem();
            if (item == null)
            {
                MessageBox.Show(
                    this,
                    "请先在配置列表中选择一个配置项，再拖入程序文件。",
                    "拖放程序",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            ApplyProgramToItem(item, path);
        }

        /// <summary>把路径写进指定配置项的 Program（12b 的“浏览…”与自身拖放都复用这里）。</summary>
        public void ApplyProgramToItem(ConfigItem item, string path)
        {
            if (item == null || string.IsNullOrEmpty(path))
            {
                return;
            }

            string trimmed = path.Trim();
            if (string.Equals(item.Program, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            _configManager.UpdateItemProgram(item, trimmed);
            UpdateDetailSummary();
            RefreshStatus(item);
        }

        private static string GetFirstDroppedFile(DragEventArgs e)
        {
            if (e == null || e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return null;
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0)
            {
                return null;
            }

            return files[0];
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

        /// <summary>还原窗口并抢到前台：保留 v1.8 的 TopMost + Alt 击键方案（SendInput 属阶段二）。</summary>
        private void RestoreAndBringToFront()
        {
            if (!Visible)
            {
                Visible = true;
            }

            ShowInTaskbar = true;

            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            ShowWindow(Handle, SwRestore);

            // 未最小化但被遮挡时，用 TopMost 快速置顶再还原，确保冲破遮挡。
            TopMost = true;
            BringToFront();
            Activate();
            TopMost = false;

            // 模拟一次 Alt 击键以取得 SetForegroundWindow 的前台切换权限（避免 AttachThreadInput 跨线程死锁）。
            keybd_event(VkMenu, 0, 0, 0);
            keybd_event(VkMenu, 0, KeyeventfKeyUp, 0);
            SetForegroundWindow(Handle);
        }

        /// <summary>关闭窗口 = 隐藏到托盘；真正退出只有托盘/按钮的“退出”。</summary>
        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_isExiting)
            {
                return;
            }

            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                ShowInTaskbar = false;
                Hide();
            }
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
        /// 真退出：先停掉所有进程（可能阻塞，放线程池），再关 Job、关托盘、Application.Exit。
        /// _isExiting 语义保留：退出过程中的 ProcessExited/Stopped 不再回写 Enable。
        /// </summary>
        private void ExitApplication()
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
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

        private void DisposeProcessManagerQuietly()
        {
            ProcessManager manager = _processManager;
            if (manager == null || _processManagerDisposed)
            {
                return;
            }

            _processManagerDisposed = true;
            try
            {
                manager.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("释放进程管理器失败：" + ex.Message);
            }
        }

        private void DisposeManagedExtras()
        {
            if (_disposeCompleted)
            {
                return;
            }

            _disposeCompleted = true;

            try
            {
                _elapsedTimer?.Stop();
            }
            catch (Exception)
            {
                // 计时器已释放。
            }

            try
            {
                _notifyIcon?.Dispose();
            }
            catch (Exception)
            {
                // 同上。
            }

            // 静态事件会把窗体实例钉在内存里（自检每跑一次就多一个），释放时必须退订。
            try
            {
                Application.ApplicationExit -= OnApplicationExit;
            }
            catch (Exception)
            {
                // 退订失败不影响界面释放。
            }

            DisposeProcessManagerQuietly();

            try
            {
                _renameBox?.Dispose();
            }
            catch (Exception)
            {
                // 行内编辑框可能已随列表释放。
            }

            _baseFont?.Dispose();
            _titleFont?.Dispose();
            _sectionFont?.Dispose();
            _monoFont?.Dispose();

            if (_appIcon != null)
            {
                try
                {
                    Icon = null;
                    _appIcon.Dispose();
                }
                catch (Exception)
                {
                    // 图标可能被系统缓存，释放失败可忽略。
                }
            }
        }

        private void ElapsedTimer_Tick(object sender, EventArgs e)
        {
            if (_runStarted.Count == 0)
            {
                return;
            }

            _configList?.Invalidate();
            _collapsedStrip?.Invalidate();
        }

        // =====================================================================================
        // 字体 / 图标 / DPI
        // =====================================================================================

        /// <summary>界面基准字体：优先雅黑（中文清晰），没有则回落 Segoe UI / 通用无衬线。</summary>
        private static Font CreateBaseFont()
        {
            string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
            foreach (string name in candidates)
            {
                try
                {
                    using (var family = new FontFamily(name))
                    {
                        return new Font(family, 9f, FontStyle.Regular, GraphicsUnit.Point);
                    }
                }
                catch (Exception)
                {
                    // 该字体不存在，试下一个。
                }
            }

            return new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Regular, GraphicsUnit.Point);
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

        /// <summary>启动时的系统 DPI（线程 DPI 上下文相关；--ui-snapshot 会临时切成 96）。</summary>
        private static int ResolveStartupDpi()
        {
            try
            {
                uint dpi = GetDpiForSystem();
                if (dpi >= 48 && dpi <= 480)
                {
                    return (int)dpi;
                }
            }
            catch (Exception)
            {
                // Win7/8.1 没有 GetDpiForSystem，按 96 DPI 处理。
            }

            return 96;
        }

        // =====================================================================================
        // --ui-snapshot：不显示窗口，把两套主题各渲染成 1024×640 的 PNG
        // =====================================================================================

        /// <summary>--ui-snapshot 的命令行入口：成功返回 0，失败返回 1。</summary>
        internal static int RunUiSnapshotCli(string directory)
        {
            string error;
            bool ok = TryRenderSnapshots(directory, out error);
            try
            {
                if (ok)
                {
                    string full = Path.GetFullPath(directory.Trim());
                    Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-light.png"));
                    Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-dark.png"));
                }
                else
                {
                    Console.WriteLine("FAIL ui-snapshot：" + error);
                }
            }
            catch (Exception)
            {
                // WinExe 没有控制台时忽略，退出码才是权威结果。
            }

            return ok ? 0 : 1;
        }

        /// <summary>
        /// 渲染浅色/深色两张窗体快照。绝不 Show()/Visible=true：
        /// 只 CreateControl + DrawToBitmap，并把线程 DPI 上下文临时切成 96 DPI，
        /// 这样 1024×640 的位图就是设计基线上的真实排版。
        /// </summary>
        internal static bool TryRenderSnapshots(string directory, out string error)
        {
            error = null;

            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    error = "需要一个输出目录参数。";
                    return false;
                }

                string trimmed = directory.Trim();
                if (trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    error = "需要一个输出目录参数，不能把另一个 -- 开关当作目录。";
                    return false;
                }

                string fullDirectory = Path.GetFullPath(trimmed);
                Directory.CreateDirectory(fullDirectory);
                string lightPath = Path.Combine(fullDirectory, "form-light.png");
                string darkPath = Path.Combine(fullDirectory, "form-dark.png");

                using (new SnapshotDpiScope())
                {
                    FormMain form;
                    try
                    {
                        form = new FormMain(true);
                    }
                    catch (Exception ex)
                    {
                        error = "无法创建窗体：" + ex.GetType().Name + ": " + ex.Message;
                        return false;
                    }

                    using (form)
                    {
                        try
                        {
                            form.PrepareSnapshotScene();
                            form.RenderSnapshot(Palette.Light, lightPath);
                            form.RenderSnapshot(Palette.Dark, darkPath);
                        }
                        catch (Exception ex)
                        {
                            error = "渲染快照失败：" + ex.GetType().Name + ": " + ex.Message;
                            return false;
                        }
                    }
                }

                if (!IsNonEmptyPng(lightPath))
                {
                    error = "浅色快照未生成或不是有效 PNG：" + lightPath;
                    return false;
                }

                if (!IsNonEmptyPng(darkPath))
                {
                    error = "深色快照未生成或不是有效 PNG：" + darkPath;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>快照场景准备：固定 1024×640、灌演示数据、建句柄、排一次布局，绝不显示窗口。</summary>
        private void PrepareSnapshotScene()
        {
            AutoScaleMode = AutoScaleMode.None;
            // DrawToBitmap 对 Form 会连非客户区（标题栏）一起画；截图只保留客户区，
            // 这样 1024×640 的位图就是设计与验收基线，不会因为标题栏挤掉底部一行。
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(SnapshotWidth, SnapshotHeight);
            MinimumSize = Size.Empty;
            _elapsedTimer?.Stop();

            CreateControl();
            ForceHandles(this);
            PerformLayout();
            LayoutLogViews();

            SeedSnapshotDemoItems();

            CreateControl();
            ForceHandles(this);
            PerformLayout();
            LayoutLogViews();
            ApplyTheme();

            // 演示日志：让“输出”槽位不是空白（真实日志由 12b 的控件接管）。
            IList<ConfigItem> items = ListItems();
            foreach (ConfigItem item in items)
            {
                AppendSystemLine(item, "系统：演示数据，未启动任何进程。");
            }
        }

        /// <summary>快照用的演示配置项：只进列表，不写 config.json，也不启动任何进程。</summary>
        private void SeedSnapshotDemoItems()
        {
            _runStarted.Clear();
            _starting.Clear();
            _stopping.Clear();

            if (_logViewManager != null)
            {
                foreach (ConfigItem existing in _configManager.Items)
                {
                    _logViewManager.RemoveLogView(existing);
                }
            }

            _configList.Items.Clear();

            var demo = new[]
            {
                new ConfigItem
                {
                    Name = "默认节点",
                    Program = "gost.exe",
                    Args = "-L 127.0.0.1:8080 -F tls://abc.com:4000"
                },
                new ConfigItem
                {
                    Name = "测试节点",
                    Program = "gost.exe",
                    Args = "-L 127.0.0.1:8081 -F kcp://abc.com:4000"
                },
                new ConfigItem
                {
                    Name = "本地调试",
                    Program = "C:\\Tools\\gost.exe",
                    Args = "-L 127.0.0.1:9090"
                }
            };

            foreach (ConfigItem item in demo)
            {
                _logViewManager?.AddLogView(item);
                _configList.Items.Add(item);
            }

            // 第一项显示成“运行中”，验证运行时长列与状态点的高亮。
            _runStarted[demo[0]] = DateTime.UtcNow - TimeSpan.FromMinutes(8) - TimeSpan.FromSeconds(12);

            UpdateCountLabel();
            _configList.SelectedIndex = 0;
            RefreshStatus(demo[0]);
        }

        private void RenderSnapshot(Palette palette, string path)
        {
            if (palette == null)
            {
                throw new ArgumentNullException(nameof(palette));
            }

            _themeManager.Mode = ReferenceEquals(palette, Palette.Dark) ? ThemeMode.Dark : ThemeMode.Light;
            ApplyTheme();
            PerformLayout();
            LayoutLogViews();

            using (var bitmap = new Bitmap(SnapshotWidth, SnapshotHeight, PixelFormat.Format32bppArgb))
            {
                DrawToBitmap(bitmap, new Rectangle(0, 0, SnapshotWidth, SnapshotHeight));
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static void ForceHandles(Control control)
        {
            if (control == null)
            {
                return;
            }

            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls)
            {
                ForceHandles(child);
            }
        }

        private static bool IsNonEmptyPng(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                byte[] bytes = File.ReadAllBytes(path);
                byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                if (bytes.Length <= signature.Length)
                {
                    return false;
                }

                for (int i = 0; i < signature.Length; i++)
                {
                    if (bytes[i] != signature[i])
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 把当前线程切到 96 DPI（DPI 不感知）上下文，让窗体按设计基线排版；
        /// 离开作用域时恢复原上下文。拿不到 API（老系统）时静默照常渲染。
        /// </summary>
        private sealed class SnapshotDpiScope : IDisposable
        {
            private const int DpiAwarenessContextUnaware = -1;

            private readonly IntPtr _previous;
            private readonly bool _applied;

            public SnapshotDpiScope()
            {
                try
                {
                    _previous = GetThreadDpiAwarenessContext();
                    _applied = SetThreadDpiAwarenessContext(new IntPtr(DpiAwarenessContextUnaware));
                }
                catch (Exception)
                {
                    _applied = false;
                }
            }

            public void Dispose()
            {
                if (!_applied)
                {
                    return;
                }

                try
                {
                    SetThreadDpiAwarenessContext(_previous);
                }
                catch (Exception)
                {
                    // 恢复失败也没有可补救的动作。
                }
            }

            [DllImport("user32.dll")]
            private static extern IntPtr GetThreadDpiAwarenessContext();

            [DllImport("user32.dll")]
            private static extern bool SetThreadDpiAwarenessContext(IntPtr dpiContext);
        }

        // =====================================================================================
        // P/Invoke
        // =====================================================================================

        private const int SwRestore = 9;
        private const byte VkMenu = 0x12;
        private const uint KeyeventfKeyUp = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();
    }

    /// <summary>
    /// 左栏折叠后的窄状态条：每行一个状态点 + 名称前两个字，点击仍可启停。
    /// 折叠时不重建 ThemedListBox，避免 44px 行高在 56px 宽里挤成一团。
    /// </summary>
    internal sealed class ConfigStatusStrip : Control, IThemed
    {
        private Palette _palette = Palette.Light;
        private int _rowHeight;

        public ConfigStatusStrip()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);

            Cursor = Cursors.Hand;
            BackColor = _palette.Surface;
            _rowHeight = ThemePaint.Scale(this, 32);
        }

        /// <summary>当前列表项（按显示顺序）。</summary>
        public Func<IList<ConfigItem>> ItemProvider { get; set; }

        public Func<ConfigItem, bool> RunningSelector { get; set; }

        /// <summary>与列表右侧完全一样的状态文本（运行中 8m12s / 启动中… / 停止中… / 已停止）。</summary>
        public Func<ConfigItem, string> StatusSelector { get; set; }

        public IList<ConfigItem> GetItems()
        {
            if (ItemProvider == null)
            {
                return null;
            }

            try
            {
                return ItemProvider();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>点击位置命中的行号；未命中返回 -1。</summary>
        public int HitTest(Point location)
        {
            IList<ConfigItem> items = GetItems();
            if (items == null || items.Count == 0 || _rowHeight <= 0)
            {
                return -1;
            }

            int index = location.Y / _rowHeight;
            return index >= 0 && index < items.Count ? index : -1;
        }

        public void ApplyTheme(Palette palette)
        {
            if (palette == null)
            {
                return;
            }

            _palette = palette;
            BackColor = palette.Surface;
            ForeColor = palette.TextPrimary;
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            _rowHeight = ThemePaint.Scale(this, 32);
            Invalidate();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            _rowHeight = ThemePaint.Scale(this, 32);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (e == null || e.Graphics == null)
            {
                return;
            }

            Graphics graphics = e.Graphics;
            graphics.Clear(_palette.Surface);

            IList<ConfigItem> items = GetItems();
            if (items == null || items.Count == 0)
            {
                var emptyBounds = new Rectangle(0, ThemePaint.Scale(this, 8), ClientSize.Width, ThemePaint.Scale(this, 20));
                if (emptyBounds.Width > 0)
                {
                    TextRenderer.DrawText(
                        graphics,
                        "无配置",
                        Font,
                        emptyBounds,
                        _palette.TextMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                }

                return;
            }

            int dotSize = ThemePaint.Scale(this, 10);
            int padding = ThemePaint.Scale(this, 6);
            int gap = ThemePaint.Scale(this, 4);

            for (int i = 0; i < items.Count; i++)
            {
                int top = i * _rowHeight;
                if (top >= ClientSize.Height)
                {
                    break;
                }

                ConfigItem item = items[i];
                bool running = RunningSelector != null && RunningSelector(item);

                // 折叠态也要能看出“启动中…/停止中…”：这两态用强调色点和“启…/停…”代替名字前两字。
                string status = StatusSelector == null ? null : StatusSelector(item);
                bool starting = status != null && status.StartsWith("启动中", StringComparison.Ordinal);
                bool stopping = status != null && status.StartsWith("停止中", StringComparison.Ordinal);
                bool busy = starting || stopping;

                var row = new Rectangle(0, top, ClientSize.Width, _rowHeight);
                if (running || busy)
                {
                    using (var highlight = new SolidBrush(_palette.SurfaceAlt))
                    {
                        graphics.FillRectangle(highlight, row);
                    }
                }

                Color dotColor = busy
                    ? _palette.Accent
                    : (running ? _palette.Success : _palette.TextMuted);
                int dotTop = row.Top + Math.Max(0, (_rowHeight - dotSize) / 2);
                using (var dotBrush = new SolidBrush(dotColor))
                {
                    graphics.FillEllipse(dotBrush, new Rectangle(padding, dotTop, dotSize, dotSize));
                }

                int textLeft = padding + dotSize + gap;
                int textWidth = ClientSize.Width - textLeft - padding;
                if (textWidth <= 0)
                {
                    continue;
                }

                string label = starting ? "启…" : (stopping ? "停…" : ShortName(item));
                Color textColor = busy
                    ? _palette.Accent
                    : (running ? _palette.TextPrimary : _palette.TextMuted);

                var textBounds = new Rectangle(textLeft, row.Top, textWidth, _rowHeight);
                const TextFormatFlags flags =
                    TextFormatFlags.Left |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(graphics, label, Font, textBounds, textColor, flags);
            }
        }

        private static string ShortName(ConfigItem item)
        {
            string name = item == null ? string.Empty : (item.Name ?? string.Empty);
            return name.Length > 2 ? name.Substring(0, 2) : name;
        }
    }
}
