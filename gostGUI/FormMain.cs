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
    /// <summary>
    /// 主窗口：左边配置列表与操作按钮，右边“启动设置”详情编辑器与主题化日志面板。
    /// <para>
    /// 两个右栏区域各自可独立工作：详情编辑器整组回填/逐字段保存，日志面板按项切换视图、
    /// 批量着色排空（工作线程只入队）。
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

        /// <summary>
        /// R36：启动时的自动恢复/开机自启跳过管理员项时，写进该项日志的唯一一行。
        /// 必须是精确文案，不得添加任何前缀（验收会逐字断言）。
        /// </summary>
        internal const string AutoRestoreSkipMessage = "已跳过：需要管理员权限，请手动启动";

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

        /// <summary>构造时的设计基线 DPI（--ui-snapshot / 自检可通过 StartupDpiOverride 覆盖）。</summary>
        private int _layoutDpi;

        /// <summary>恢复的最大化状态：窗口 Show 之后才能安全设置。</summary>
        private bool _restoreMaximized;

        /// <summary>真实退出路径只保存一次窗口状态，避免 FormClosing 重复弹提示。</summary>
        private bool _exitWindowStateAttempted;

        /// <summary>退出路径保存窗口状态时抑制 SaveFailed 横幅，只保留 R45 的模态框/气泡。</summary>
        private bool _suppressSaveFailedBanner;

        /// <summary>横幅是否处于“已显示”状态（用于自检；隐藏窗体的 Visible 会因子控件不可见而为 false）。</summary>
        private bool _bannerShown;

        /// <summary>最近一次窗口状态保存失败的中文原因（空字符串表示成功或还没保存过）。</summary>
        private string _lastWindowSaveFailure = "";

        /// <summary>自检用：非 0 时替代系统 DPI 作为布局基线（生产路径永远是 0）。</summary>
        internal static int StartupDpiOverride { get; set; }

        /// <summary>自检用：抑制退出路径的托盘气泡/模态框，避免阻塞无消息循环的用例。</summary>
        internal bool SuppressDialogsForTest { get; set; }

        /// <summary>详情编辑器当前回填/保存的目标项（null 表示没有选中项）。只在 UI 线程读写。</summary>
        private ConfigItem _detailItem;

        /// <summary>回填字段期间为 true：抑制 TextChanged/Leave/CheckedChanged 触发的保存。</summary>
        private bool _detailLoading;

        /// <summary>程序解析结果缓存：键 = 程序 + 有效环境签名 + 应用目录；主题切换/重复提交不再扫文件系统。</summary>
        private string _programHintCacheKey;
        private ResolvedProgram _programHintCacheResult;

        /// <summary>最近一次已经提示过的环境变量解析警告，避免每次失焦重复刷同一条系统日志。</summary>
        private string _lastEnvWarningSignature;

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

            LoadDetailEditor(GetCurrentConfigItem());
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄出现后补一次深色标题栏与积压的 UI 回调：
            // 构造函数阶段没有句柄，不能让任何回调/主题调用抢先建句柄。
            ThemeManager.ApplyDarkTitleBar(this, ReferenceEquals(_themeManager.Current, Palette.Dark));

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
            _detailScroll.BackColor = palette.Surface;
            _detailLayout.BackColor = palette.Surface;
            _programRow.BackColor = palette.Surface;
            _workDirRow.BackColor = palette.Surface;
            _runAsAdminRow.BackColor = palette.Surface;
            _bannerPanel.BackColor = palette.SurfaceAlt;

            _titleLabel.ForeColor = palette.TextPrimary;
            _listHeaderLabel.ForeColor = palette.TextPrimary;
            _launchTitleLabel.ForeColor = palette.TextPrimary;
            _outputTitleLabel.ForeColor = palette.TextPrimary;
            _detailSummaryLabel.ForeColor = palette.TextMuted;
            _programLabel.ForeColor = palette.TextMuted;
            _argsLabel.ForeColor = palette.TextMuted;
            _workDirLabel.ForeColor = palette.TextMuted;
            _envLabel.ForeColor = palette.TextMuted;
            _workDirHint.ForeColor = palette.TextMuted;
            _envHint.ForeColor = palette.TextMuted;
            _bannerLabel.ForeColor = palette.Danger;

            _logHost.BackColor = palette.LogBg;
            _logViewManager?.ApplyTheme(palette);
            if (_detailItem == null)
            {
                _programHint.ForeColor = palette.TextMuted;
            }
            else
            {
                UpdateProgramHint();
            }

            // 原生滚动条（多行文本框 / RichTextBox / 列表）在深色下也要变深。
            WindowTheme.ApplyScrollbarsRecursive(this, ReferenceEquals(palette, Palette.Dark));

            if (_renameBox != null)
            {
                _renameBox.BackColor = palette.SurfaceAlt;
                _renameBox.ForeColor = palette.TextPrimary;
            }

            var renderer = new ThemedMenuRenderer(palette);
            _listMenu.Renderer = renderer;
            _trayMenu.Renderer = renderer;
            _themeMenu.Renderer = renderer;

            if (IsHandleCreated)
            {
                // 浅色时也要显式写 0：否则从深色切回来标题栏会一直是黑的。
                ThemeManager.ApplyDarkTitleBar(this, ReferenceEquals(palette, Palette.Dark));
            }

            UpdateThemeSelector();
            ApplyPrivilegeBadge();
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

        /// <summary>当前选中的配置项（详情编辑器/日志面板都以它为准）。</summary>
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

        // ---- Task 12b 自检缝：走与生产代码相同的回填 / 提交 / 解析路径 ----

        internal void SetProgramFieldForTest(string program)
        {
            if (_programBox == null)
            {
                return;
            }

            _programHintTimer?.Stop();
            _programBox.Text = program ?? string.Empty;
            CommitProgramField();
            UpdateProgramHint();
        }

        internal string ProgramHintTextForTest => _programHint == null ? string.Empty : _programHint.Text;

        internal Color ProgramHintColorForTest => _programHint == null ? Color.Empty : _programHint.ForeColor;

        internal string EnvFieldTextForTest => _envBox == null ? string.Empty : _envBox.Text;

        internal string ProgramFieldTextForTest => _programBox == null ? string.Empty : _programBox.Text;

        internal string ArgsFieldTextForTest => _argsBox == null ? string.Empty : _argsBox.Text;

        internal string WorkDirFieldTextForTest => _workDirBox == null ? string.Empty : _workDirBox.Text;

        internal bool RunAsAdminFieldForTest => _runAsAdminToggle != null && _runAsAdminToggle.Checked;

        internal void SelectItemForTest(ConfigItem item)
        {
            SelectItem(item);
        }

        internal void CommitEnvFieldForTest()
        {
            CommitEnvField();
        }

        /// <summary>只改程序框文字、不提交（用于验证 200ms 防抖：提示不应同步刷新）。</summary>
        internal void SetProgramTextOnlyForTest(string program)
        {
            if (_programBox == null)
            {
                return;
            }

            _programHintTimer?.Stop();
            _programBox.Text = program ?? string.Empty;
        }

        /// <summary>触发一次防抖到期（模拟 200ms Timer Tick）。</summary>
        internal void FireProgramHintDebounceForTest()
        {
            _programHintTimer?.Stop();
            ProgramHintTimer_Tick(null, EventArgs.Empty);
        }

        internal void SetArgsTextForTest(string args)
        {
            if (_argsBox != null)
            {
                _argsBox.Text = args ?? string.Empty;
            }
        }

        internal void SetEnvTextForTest(string env)
        {
            if (_envBox != null)
            {
                _envBox.Text = env ?? string.Empty;
            }
        }

        /// <summary>走与真实窗口关闭完全相同的事件处理器（UserClosing → 提交 + 隐藏）。</summary>
        internal void TriggerFormClosingForTest()
        {
            FormMain_FormClosing(this, new FormClosingEventArgs(CloseReason.UserClosing, false));
        }

        /// <summary>走与托盘“退出”完全相同的退出前提交路径（不真的 Application.Exit）。</summary>
        internal void CommitPendingEditsForTest()
        {
            CommitPendingDetailEdits();
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
                string state = IsItemRunning(item) ? "运行中" : "已停止";
                _detailSummaryLabel.Text = "已选：" + (item.Name ?? string.Empty) + " · " + state;
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

        // =====================================================================================
        // 进程事件（可能来自工作线程）
        // =====================================================================================

        private void OnProcessOutputReceived(ConfigItem item, LogKind kind, string data)
        {
            // LogViewManager 只入队：绝不在工作线程上碰 UI 控件。
            _logViewManager?.AppendText(item, kind, data);
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

        /// <summary>写一行系统日志（启动/停止/退出码/错误），按 LogKind.System 着色。</summary>
        internal void AppendSystemLine(ConfigItem item, string message)
        {
            if (item == null || string.IsNullOrEmpty(message))
            {
                return;
            }

            _logViewManager?.AppendText(item, LogKind.System, message + Environment.NewLine);
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
                    LoadDetailEditor(null);
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
            if (_suppressSaveFailedBanner)
            {
                // 退出路径由 PersistWindowState 的模态框/气泡负责显形，横幅在退出后看不见。
                return;
            }

            PostToUi(() => ShowBanner(message, true));
        }

        // =====================================================================================
        // 列表交互
        // =====================================================================================

        private void ConfigList_SelectedIndexChanged(object sender, EventArgs e)
        {
            // 先把旧项上还没触发 Leave 的编辑提交掉，再回填新项：
            // 否则“先改选中项、后触发 Leave”的事件顺序会把旧项的文字写丢。
            CommitDetailFields();

            ConfigItem item = GetCurrentConfigItem();
            if (item != null)
            {
                // 每个配置项各自一份日志视图，切项即切视图。
                _logViewManager?.ShowLogView(item);
                LayoutLogViews();
            }

            // 详情编辑器整组重新回填：绝不能把上一项的文字留在新选中的项上。
            LoadDetailEditor(item);
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
            CommitDetailFields();
            ConfigItem newItem = _configManager.AddNewItem();
            if (newItem != null)
            {
                SelectItem(newItem);
            }
        }

        private void DuplicateButton_Click(object sender, EventArgs e)
        {
            EndListBoxEdit(false);
            CommitDetailFields();
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
            CommitDetailFields();
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

            // review minor (a)：删除正在运行的项时，先把它标成“停止中…”再进入停止往返。
            // 停止可能要等中转/UAC，期间任何启停入口都会因 _stopping 而拒绝重新启动这一项。
            if (IsItemRunning(item))
            {
                _stopping.Add(item);
            }

            ProcessManager manager = _processManager;
            RefreshStatus(null);

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
            CommitDetailFields();
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

            LoadDetailEditor(item);
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
        // 拖放设置程序（程序输入框 + 浏览…共用 ApplyProgramToItem）
        // =====================================================================================

        /// <summary>把程序输入框接成拖放目标（内层 TextBox 允许拖放 + 两个处理器）。</summary>
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

        /// <summary>把路径写进指定配置项的 Program（“浏览…”与拖放都复用这里）。</summary>
        public void ApplyProgramToItem(ConfigItem item, string path)
        {
            if (item == null || string.IsNullOrEmpty(path))
            {
                return;
            }

            string trimmed = path.Trim();
            _configManager.UpdateItemProgram(item, trimmed);

            // 程序框正看着这一项：把新路径同步回输入框（拖放/浏览都是“用户已触碰”的路径）。
            if (ReferenceEquals(item, _detailItem) && _programBox != null &&
                !string.Equals(_programBox.Text, trimmed, StringComparison.Ordinal))
            {
                _programHintTimer?.Stop();
                _detailLoading = true;
                try
                {
                    _programBox.Text = trimmed;
                }
                finally
                {
                    _detailLoading = false;
                }
            }

            UpdateProgramHint();
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
        // 详情编辑器：程序 / 参数 / 工作目录 / 环境变量 / 以管理员身份运行
        // 规则：切项整组回填；字段失焦/提交即保存，只改用户碰过的那一个字段。
        // =====================================================================================

        /// <summary>把指定项的值整组回填进编辑器；item 为 null 时清空并禁用整组字段。</summary>
        private void LoadDetailEditor(ConfigItem item)
        {
            if (_programBox == null)
            {
                return;
            }

            _detailItem = item;
            bool hasItem = item != null;

            // 回填期间禁止任何“字段变化即保存”的副作用，避免把新项的值写进旧项。
            _programHintTimer?.Stop();
            _lastEnvWarningSignature = null;
            _detailLoading = true;
            try
            {
                _programBox.Enabled = hasItem;
                _programBrowseButton.Enabled = hasItem;
                _argsBox.Enabled = hasItem;
                _workDirBox.Enabled = hasItem;
                _workDirBrowseButton.Enabled = hasItem;
                _workDirFromProgramButton.Enabled = hasItem;
                _envBox.Enabled = hasItem;
                _runAsAdminToggle.Enabled = hasItem;

                _programBox.Text = hasItem ? (item.Program ?? string.Empty) : string.Empty;
                _argsBox.Text = hasItem ? (item.Args ?? string.Empty) : string.Empty;
                _workDirBox.Text = hasItem ? (item.WorkDir ?? string.Empty) : string.Empty;
                _envBox.Text = hasItem ? FormatEnvForEditor(item) : string.Empty;
                _runAsAdminToggle.Checked = hasItem && item.RunAsAdmin;
            }
            finally
            {
                _detailLoading = false;
            }

            UpdateProgramHint();
        }

        /// <summary>
        /// 提交当前编辑器里所有已改动字段（切项前调用）。每个 Commit* 内部都与项当前值比较，
        /// 没改过的字段不会被重写；_detailLoading 期间（正在回填）整体跳过。
        /// </summary>
        private void CommitDetailFields()
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            CommitProgramField();
            ArgsBox_Leave(null, EventArgs.Empty);
            CommitWorkDirField();
            CommitEnvField();

            if (_detailItem.RunAsAdmin != _runAsAdminToggle.Checked)
            {
                _detailItem.RunAsAdmin = _runAsAdminToggle.Checked;
                _configManager.UpdateItem(_detailItem);
            }
        }

        /// <summary>
        /// 退出/隐藏前的最后一道提交：窗口可能在没有触发任何 Leave 的情况下直接关闭，
        /// 用户刚输入但还没失焦的字段必须在这里落盘，否则会被静默丢弃。
        /// 提交失败只记调试输出，绝不能拦住退出流程。
        /// </summary>
        private void CommitPendingDetailEdits()
        {
            try
            {
                CommitDetailFields();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("退出前提交详情编辑失败：" + ex.Message);
            }
        }

        private void ProgramBox_TextChanged(object sender, EventArgs e)
        {
            if (_detailLoading || _programHintTimer == null)
            {
                return;
            }

            // 防抖 ~200ms：停止输入后再解析一次，避免每敲一键都扫 PATH。
            _programHintTimer.Stop();
            _programHintTimer.Start();
        }

        private void ProgramHintTimer_Tick(object sender, EventArgs e)
        {
            _programHintTimer.Stop();
            UpdateProgramHint();
        }

        private void ProgramBox_Leave(object sender, EventArgs e)
        {
            CommitProgramField();
        }

        private void ProgramBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CommitProgramField();
                UpdateProgramHint();
            }
        }

        private void CommitProgramField()
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            string value = (_programBox.Text ?? string.Empty).Trim();
            if (string.Equals(_detailItem.Program ?? string.Empty, value, StringComparison.Ordinal))
            {
                return;
            }

            _configManager.UpdateItemProgram(_detailItem, value);
            UpdateProgramHint();
        }

        /// <summary>
        /// 解析程序框里的当前文本（有效环境 = 父环境 + 本项 env），把结果写进提示行：
        /// 命中显示 → 绝对路径（成功色），失败显示中文错误（危险色）。
        /// </summary>
        private void UpdateProgramHint()
        {
            if (_programHint == null)
            {
                return;
            }

            if (_detailItem == null)
            {
                _programHint.Text = string.Empty;
                _programHint.ForeColor = _themeManager.Current.TextMuted;
                return;
            }

            string program = _programBox == null ? _detailItem.Program : _programBox.Text;
            string hint;
            bool found;
            ResolvedProgram resolved = GetProgramResolution(_detailItem, program);
            if (resolved != null && resolved.Found)
            {
                found = true;
                hint = "→ " + resolved.FullPath;
            }
            else
            {
                found = false;
                hint = resolved != null && !string.IsNullOrEmpty(resolved.Error)
                    ? resolved.Error
                    : "无法解析程序，请检查程序名或完整路径。";
            }

            _programHint.Text = hint;
            _programHint.ForeColor = found ? _themeManager.Current.Success : _themeManager.Current.Danger;
        }

        /// <summary>
        /// 带缓存地解析程序：键 = 程序 + 有效环境签名 + 应用目录。
        /// 同一份输入（例如主题切换、Leave 后再 Enter）不会重复扫 PATH；env/程序一改，键就变。
        /// </summary>
        private ResolvedProgram GetProgramResolution(ConfigItem item, string program)
        {
            string key = BuildProgramResolutionKey(item, program);
            if (_programHintCacheResult != null &&
                string.Equals(_programHintCacheKey, key, StringComparison.Ordinal))
            {
                return _programHintCacheResult;
            }

            ResolvedProgram result = ResolveProgramForEditor(item, program);
            _programHintCacheKey = key;
            _programHintCacheResult = result;
            return result;
        }

        private static string BuildProgramResolutionKey(ConfigItem item, string program)
        {
            var builder = new StringBuilder();
            builder.Append(Common.GetApplicationPath()).Append('\u0000');
            builder.Append(program ?? string.Empty).Append('\u0000');

            if (item != null && item.Env != null)
            {
                // 按键名排序，避免字典枚举顺序不同导致本可命中的键变成未命中。
                var pairs = new List<KeyValuePair<string, string>>(item.Env);
                pairs.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
                foreach (KeyValuePair<string, string> pair in pairs)
                {
                    builder.Append(pair.Key).Append('=').Append(pair.Value ?? string.Empty).Append('\u0001');
                }
            }

            return builder.ToString();
        }

        /// <summary>解析程序名；畸形环境/路径只回落成中文错误，不把异常抖给界面。</summary>
        private static ResolvedProgram ResolveProgramForEditor(ConfigItem item, string program)
        {
            try
            {
                Dictionary<string, string> effective;
                string envError;
                if (!TryBuildEditorEnvironment(item, out effective, out envError))
                {
                    // 环境变量本身无效（例如合并后超 32K）：直接把环境错误作为提示，而不是继续搜程序。
                    return new ResolvedProgram
                    {
                        Found = false,
                        FullPath = string.Empty,
                        Error = envError
                    };
                }

                return ProgramResolver.Resolve(program, Common.GetApplicationPath(), effective);
            }
            catch (Exception ex)
            {
                return new ResolvedProgram
                {
                    Found = false,
                    FullPath = string.Empty,
                    Error = "无法解析程序：" + ex.Message
                };
            }
        }

        /// <summary>构建本项的有效环境（父环境 + env 覆盖）；envError 非空表示环境不可用于启动。</summary>
        private static bool TryBuildEditorEnvironment(
            ConfigItem item,
            out Dictionary<string, string> effective,
            out string error)
        {
            effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = string.Empty;
            try
            {
                List<KeyValuePair<string, string>> parent = ChildEnvironment.SnapshotParentEnvironment();
                effective = EnvBlock.BuildEffective(parent, item == null ? null : item.Env, out error);
                return string.IsNullOrEmpty(error);
            }
            catch (Exception ex)
            {
                error = "构建有效环境失败：" + ex.Message;
                return false;
            }
        }

        private void ArgsBox_Leave(object sender, EventArgs e)
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            string value = _argsBox.Text ?? string.Empty;
            if (string.Equals(_detailItem.Args ?? string.Empty, value, StringComparison.Ordinal))
            {
                return;
            }

            _detailItem.Args = value;
            _configManager.UpdateItem(_detailItem);
        }

        private void WorkDirBox_Leave(object sender, EventArgs e)
        {
            CommitWorkDirField();
        }

        private void CommitWorkDirField()
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            string value = _workDirBox.Text ?? string.Empty;
            if (string.Equals(_detailItem.WorkDir ?? string.Empty, value, StringComparison.Ordinal))
            {
                return;
            }

            _detailItem.WorkDir = value;
            _configManager.UpdateItem(_detailItem);
        }

        private void WorkDirBrowseButton_Click(object sender, EventArgs e)
        {
            if (_detailItem == null)
            {
                return;
            }

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择工作目录";
                dialog.ShowNewFolderButton = true;
                string current = (_workDirBox.Text ?? string.Empty).Trim();
                if (current.Length > 0 && Directory.Exists(current))
                {
                    dialog.SelectedPath = current;
                }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                _workDirBox.Text = dialog.SelectedPath;
                CommitWorkDirField();
            }
        }

        /// <summary>“取程序目录”：按有效环境解析当前程序，把解析到的目录填进工作目录并立即保存。</summary>
        private void WorkDirFromProgramButton_Click(object sender, EventArgs e)
        {
            if (_detailItem == null)
            {
                return;
            }

            ResolvedProgram resolved = GetProgramResolution(_detailItem, _programBox.Text);
            if (resolved == null || !resolved.Found || string.IsNullOrEmpty(resolved.FullPath))
            {
                ShowBanner(
                    "无法确定程序目录：" +
                    (resolved != null && !string.IsNullOrEmpty(resolved.Error)
                        ? resolved.Error
                        : "请先在程序框里填写可解析的程序。"),
                    true);
                return;
            }

            string directory;
            try
            {
                directory = Path.GetDirectoryName(resolved.FullPath);
            }
            catch (Exception ex)
            {
                ShowBanner("无法确定程序目录：" + ex.Message, true);
                return;
            }

            if (string.IsNullOrEmpty(directory))
            {
                ShowBanner("无法确定程序目录：解析结果没有父目录。", true);
                return;
            }

            _workDirBox.Text = directory;
            CommitWorkDirField();
        }

        private void EnvBox_Leave(object sender, EventArgs e)
        {
            CommitEnvField();
        }

        private void CommitEnvField()
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            EnvParseResult parsed = EnvBlock.ParseLines(_envBox.Text);

            // 无效行（缺 '=' / 空键）仍留在输入框里，必须让用户知道哪几行被忽略；
            // 同一组警告只提示一次，避免每次失焦都重复刷系统日志。
            SurfaceEnvWarnings(parsed.Warnings);

            bool changed = !EnvEquals(_detailItem.Env, parsed.Overrides);
            if (changed)
            {
                // 空表存 null：配置里保持“不覆盖”的语义（与旧配置兼容）。
                _detailItem.Env = parsed.Overrides.Count == 0 ? null : parsed.Overrides;
                _configManager.UpdateItem(_detailItem);
            }

            // 环境变量整体无效（合并后超过 Windows 32K 上限）时必须显形，不能静默忽略。
            Dictionary<string, string> effective;
            string envError;
            if (!TryBuildEditorEnvironment(_detailItem, out effective, out envError))
            {
                ShowBanner("环境变量无效：" + envError, true);
            }

            // 环境变了（尤其是 PATH）必须重算程序提示：旧的红色“未找到”不能继续留在屏幕上。
            // 环境没变时走缓存，不会重复扫 PATH。
            if (changed)
            {
                UpdateProgramHint();
            }
        }

        /// <summary>把 ParseLines 的警告写成当前项的系统日志（同一组警告只提示一次）。</summary>
        private void SurfaceEnvWarnings(List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0)
            {
                return;
            }

            string signature = string.Join("\n", warnings);
            if (string.Equals(signature, _lastEnvWarningSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastEnvWarningSignature = signature;
            AppendSystemLine(_detailItem, "环境变量有未保存的行：" + string.Join("；", warnings));
        }

        /// <summary>比较环境变量覆盖表（键忽略大小写、值逐字比较；null 与空表等价）。</summary>
        private static bool EnvEquals(Dictionary<string, string> left, Dictionary<string, string> right)
        {
            int leftCount = left == null ? 0 : left.Count;
            int rightCount = right == null ? 0 : right.Count;
            if (leftCount != rightCount)
            {
                return false;
            }

            if (leftCount == 0)
            {
                return true;
            }

            foreach (KeyValuePair<string, string> pair in left)
            {
                string value;
                if (!right.TryGetValue(pair.Key, out value))
                {
                    return false;
                }

                if (!string.Equals(value, pair.Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private void RunAsAdminToggle_CheckedChanged(object sender, EventArgs e)
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            if (_detailItem.RunAsAdmin == _runAsAdminToggle.Checked)
            {
                return;
            }

            _detailItem.RunAsAdmin = _runAsAdminToggle.Checked;
            _configManager.UpdateItem(_detailItem);
        }

        private void ProgramBrowseButton_Click(object sender, EventArgs e)
        {
            if (_detailItem == null)
            {
                return;
            }

            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择程序";
                dialog.CheckFileExists = true;
                dialog.Filter = "程序文件 (*.exe;*.com;*.bat;*.cmd)|*.exe;*.com;*.bat;*.cmd|所有文件 (*.*)|*.*";
                string current = (_programBox.Text ?? string.Empty).Trim();
                if (current.Length > 0 && File.Exists(current))
                {
                    dialog.FileName = current;
                }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                ApplyProgramToItem(_detailItem, dialog.FileName);
                UpdateProgramHint();
            }
        }

        /// <summary>
        /// R11：环境变量文本格式无法表达 CR/LF/TAB，渲染时把值里的连续换行/制表符折叠成一个空格，
        /// 键顺序原样保留。格式化本身复用 <see cref="EnvBlock.FormatLines"/>（单一实现），
        /// 这里只负责在交给它之前消毒值。
        /// </summary>
        private static string FormatEnvForEditor(ConfigItem item)
        {
            if (item == null || item.Env == null || item.Env.Count == 0)
            {
                return string.Empty;
            }

            var sanitized = new List<KeyValuePair<string, string>>(item.Env.Count);
            foreach (KeyValuePair<string, string> pair in item.Env)
            {
                sanitized.Add(new KeyValuePair<string, string>(pair.Key, SanitizeEnvValue(pair.Value)));
            }

            return EnvBlock.FormatLines(sanitized);
        }

        private static string SanitizeEnvValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            bool lastWasControl = false;
            foreach (char c in value)
            {
                if (c == '\r' || c == '\n' || c == '\t')
                {
                    if (!lastWasControl)
                    {
                        builder.Append(' ');
                    }
                    lastWasControl = true;
                    continue;
                }

                lastWasControl = false;
                builder.Append(c);
            }

            return builder.ToString();
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

        /// <summary>还原窗口并抢到前台：保留 v1.8 的 TopMost + Alt 击键方案（v1.9 用 SendInput 实现）。</summary>
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
            // 窗口可能在最后一次失焦之前就被关闭：先把详情编辑器里待提交的字段落盘。
            CommitPendingDetailEdits();

            if (_isExiting)
            {
                // 正常退出路径已经在 ExitApplication 里保存过；这里只兜底一次。
                if (!_exitWindowStateAttempted)
                {
                    _exitWindowStateAttempted = true;
                    PersistWindowState(true);
                }

                return;
            }

            if (e.CloseReason == CloseReason.UserClosing)
            {
                // 隐藏到托盘前记住窗口几何；应用还会继续跑，失败用气泡提示。
                PersistWindowState(false);
                e.Cancel = true;
                ShowInTaskbar = false;
                Hide();
                return;
            }

            // 系统关机/任务管理器等：保存但不阻塞关机。
            _exitWindowStateAttempted = true;
            PersistWindowState(false);
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

            // 托盘/按钮“退出”同样可能发生在最后一次失焦之前：在置 _isExiting 之前提交。
            CommitPendingDetailEdits();

            // 先置 _exitRequested：退出前的保存可能弹出模态框，模态框会泵消息，
            // 第二次“退出”点击必须被这里挡住，不能重复停进程/Application.Exit。
            _exitRequested = true;

            // 退出前把窗口状态落盘；失败必须显形，不能把刚提交的编辑静默丢掉。
            if (!_exitWindowStateAttempted)
            {
                _exitWindowStateAttempted = true;
                PersistWindowState(true);
            }

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
        private void PersistWindowState(bool exiting)
        {
            string reason = null;
            try
            {
                ConfigWindow state = CaptureWindowState();
                if (state == null)
                {
                    reason = "无法读取当前窗口大小/位置。";
                }
                else if (_configManager == null)
                {
                    reason = "配置管理器不可用。";
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
                            reason = _configManager.SaveError;
                            if (string.IsNullOrEmpty(reason))
                            {
                                reason = Common.LastSaveError;
                            }

                            if (string.IsNullOrEmpty(reason))
                            {
                                reason = "保存配置文件失败。";
                            }
                        }
                    }
                    finally
                    {
                        _suppressSaveFailedBanner = previousSuppress;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "保存窗口状态失败：" + ex.Message;
            }

            if (string.IsNullOrEmpty(reason))
            {
                _lastWindowSaveFailure = "";
                return;
            }

            _lastWindowSaveFailure = reason;
            ReportWindowStateSaveFailure(reason, exiting);
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
                _logViewManager?.Dispose();
            }
            catch (Exception)
            {
                // 日志视图可能已随宿主控件释放。
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

            // 主题监听同样不能把窗体留在一个比它活得更久的对象上。
            try
            {
                _themeManager.Changed -= OnThemeChanged;
            }
            catch (Exception)
            {
                // 同上。
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

        /// <summary>启动时的系统 DPI（线程 DPI 上下文相关；--ui-snapshot 会临时切成 96）。</summary>
        private static int ResolveStartupDpi()
        {
            if (StartupDpiOverride > 0)
            {
                return StartupDpiOverride;
            }

            return HostSystemDpi();
        }

        /// <summary>
        /// 宿主真实系统 DPI（不受 <see cref="StartupDpiOverride"/> 影响）。
        /// R49：只有宿主真的是 192 DPI 时，才允许产出“真实 200% 上下文”的那对快照。
        /// </summary>
        internal static int HostSystemDpi()
        {
            try
            {
                uint dpi = NativeMethods.GetDpiForSystem();
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
                    Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-150-light.png"));
                    Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-150-dark.png"));

                    // R49：宿主不是 192 DPI 时不会产出这一对（TryRenderSnapshots 已给出 NOTICE）。
                    if (HostSystemDpi() == 192)
                    {
                        Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-200-light.png"));
                        Console.WriteLine("PASS ui-snapshot：" + Path.Combine(full, "form-200-dark.png"));
                    }
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
        /// 渲染 96 DPI（设计基线）、144 DPI（150% 模拟）各一对浅/深 PNG；
        /// 宿主真的是 192 DPI（200%）时再渲染一对真实 192 DPI 上下文快照（R49）。
        /// 绝不 Show()/Visible=true：只 CreateControl + DrawToBitmap。
        /// 96/144 在“DPI 不感知”的 96 DPI 绘制上下文里用布局/自绘/字体比例模拟；
        /// 192 用真实 DPI 上下文，文本度量按目标 DPI 显式钉住。
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

                if (!TryRenderSnapshotPair(fullDirectory, 96, "form-light.png", "form-dark.png", false, out error))
                {
                    return false;
                }

                if (!TryRenderSnapshotPair(fullDirectory, 144, "form-150-light.png", "form-150-dark.png", false, out error))
                {
                    return false;
                }

                // 开发机的真实缩放是 200%：用真实的 192 DPI 上下文（不模拟）输出一对，
                // 验收真实出货比例。R49：宿主不是 192 DPI 时这对没有意义（真实上下文就是
                // 宿主 DPI），必须跳过并明确通知，而不是悄悄产出一张“假的 200%”。
                if (HostSystemDpi() == 192)
                {
                    if (!TryRenderSnapshotPair(fullDirectory, 192, "form-200-light.png", "form-200-dark.png", true, out error))
                    {
                        return false;
                    }
                }
                else
                {
                    WriteSnapshotNotice(
                        "宿主系统 DPI 为 " + HostSystemDpi() + "（不是 192），已跳过 200% 真实 DPI 快照对。");
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>渲染一对（浅色/深色）指定 DPI 的快照；任何一个文件不合法都返回 false。</summary>
        private static bool TryRenderSnapshotPair(
            string fullDirectory,
            int dpi,
            string lightName,
            string darkName,
            bool nativeDpi,
            out string error)
        {
            error = null;
            string lightPath = Path.Combine(fullDirectory, lightName);
            string darkPath = Path.Combine(fullDirectory, darkName);

            // nativeDpi=true 用真实 DPI 上下文（开发机 192 DPI）；否则在 96 DPI 绘制上下文里
            // 用布局/自绘/字体比例模拟目标 DPI。
            // R48：文本度量必须跟随“绘制上下文”，而不是被模拟的 DPI。模拟对在 96 DPI 上下文里
            // 渲染（字体已按 1.5× 放大），按 144 度量会把文字宽度算大 1.5 倍，布局随之失真；
            // 只有真实 192 DPI 上下文才按目标 DPI 度量（同时避免 96 DPI 的测定缓存污染）。
            int measureDpi = nativeDpi ? dpi : ThemePaint.BaselineDpi;
            int previousMeasureDpi = ThemeText.MeasureDpiOverride;
            ThemeText.MeasureDpiOverride = measureDpi;
            try
            {
                using (nativeDpi ? new SnapshotDpiScope(0) : new SnapshotDpiScope(dpi))
                {
                    FormMain form;
                    try
                    {
                        form = new FormMain(true);
                    }
                    catch (Exception ex)
                    {
                        error = "无法创建窗体（" + dpi + " DPI）：" + ex.GetType().Name + ": " + ex.Message;
                        return false;
                    }

                    using (form)
                    {
                        try
                        {
                            int width = SnapshotPixels(dpi, SnapshotWidth);
                            int height = SnapshotPixels(dpi, SnapshotHeight);
                            form.PrepareSnapshotScene(width, height);
                            form.RenderSnapshot(Palette.Light, lightPath, width, height);
                            form.RenderSnapshot(Palette.Dark, darkPath, width, height);
                        }
                        catch (Exception ex)
                        {
                            error = "渲染快照失败（" + dpi + " DPI）：" + ex.GetType().Name + ": " + ex.Message;
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
            finally
            {
                ThemeText.MeasureDpiOverride = previousMeasureDpi;
            }
        }

        /// <summary>把 96 DPI 基线像素换算成指定 DPI 的位图尺寸。</summary>
        private static int SnapshotPixels(int dpi, int baselinePixels)
        {
            return WindowGeometry.DipToPixels(baselinePixels, dpi);
        }

        /// <summary>快照场景准备：固定客户区尺寸、灌演示数据、建句柄、排一次布局，绝不显示窗口。</summary>
        private void PrepareSnapshotScene(int clientWidth, int clientHeight)
        {
            AutoScaleMode = AutoScaleMode.None;
            // DrawToBitmap 对 Form 会连非客户区（标题栏）一起画；截图只保留客户区，
            // 这样位图就是设计与验收基线，不会因为标题栏挤掉底部一行。
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(clientWidth, clientHeight);
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

            // 演示日志：让“输出”槽位不是空白。日志走队列 + UI 定时器，快照没有消息循环，
            // 必须手动排空一次，PNG 里才看得到内容。
            IList<ConfigItem> items = ListItems();
            foreach (ConfigItem item in items)
            {
                AppendSystemLine(item, "系统：演示数据，未启动任何进程。");
            }

            _logViewManager?.DrainForTest();
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
                    // 演示“解析成功”的绿色提示：cmd.exe 在 PATH 里真实存在。
                    Program = "cmd.exe",
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

        private void RenderSnapshot(Palette palette, string path, int width, int height)
        {
            if (palette == null)
            {
                throw new ArgumentNullException(nameof(palette));
            }

            _themeManager.Mode = ReferenceEquals(palette, Palette.Dark) ? ThemeMode.Dark : ThemeMode.Light;
            ApplyTheme();
            PerformLayout();
            LayoutLogViews();
            _logViewManager?.DrainForTest();

            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
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

        /// <summary>快照流程里的“跳过/降级”通知：控制台可用就打印，--ui-snapshot 的退出码仍是权威。</summary>
        private static void WriteSnapshotNotice(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            try
            {
                Console.WriteLine("NOTICE ui-snapshot：" + message);
            }
            catch (Exception)
            {
                // WinExe 没有控制台时忽略。
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
        /// 快照作用域：把线程切到 96 DPI（DPI 不感知）上下文，让 DrawToBitmap 的字体
        /// 以 96 DPI 光栅化；同时用 StartupDpiOverride/ThemePaint.DpiOverride 模拟目标 DPI
        /// 的布局与自绘尺寸。离开作用域时全部恢复。拿不到 API（老系统）时静默照常渲染。
        /// </summary>
        private sealed class SnapshotDpiScope : IDisposable
        {
            private const int DpiAwarenessContextUnaware = -1;

            private readonly IntPtr _previous;
            private readonly bool _applied;
            private readonly int _requestedDpi;
            private readonly int _previousStartupDpi;
            private readonly int _previousPaintDpi;

            /// <summary>dpi &gt; 0：在 96 DPI 绘制上下文里模拟该 DPI；dpi &lt;= 0：保持真实 DPI 上下文。</summary>
            public SnapshotDpiScope(int dpi)
            {
                _requestedDpi = dpi;
                if (dpi <= 0)
                {
                    return;
                }

                _previousStartupDpi = StartupDpiOverride;
                _previousPaintDpi = ThemePaint.DpiOverride;
                StartupDpiOverride = dpi;
                ThemePaint.DpiOverride = dpi;

                try
                {
                    _previous = NativeMethods.GetThreadDpiAwarenessContext();
                    _applied = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(DpiAwarenessContextUnaware));
                }
                catch (Exception)
                {
                    _applied = false;
                }
            }

            public void Dispose()
            {
                if (_requestedDpi <= 0)
                {
                    return;
                }

                StartupDpiOverride = _previousStartupDpi;
                ThemePaint.DpiOverride = _previousPaintDpi;

                if (!_applied)
                {
                    return;
                }

                try
                {
                    NativeMethods.SetThreadDpiAwarenessContext(_previous);
                }
                catch (Exception)
                {
                    // 恢复失败也没有可补救的动作。
                }
            }
        }

        private const int SwRestore = 9;
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
