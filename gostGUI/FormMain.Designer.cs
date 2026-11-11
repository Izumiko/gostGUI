using System;
using System.Drawing;
using System.Windows.Forms;

namespace gostGUI
{
    partial class FormMain
    {
        /// <summary>
        /// 必需的设计器变量。
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        // ---------------- 顶部条 ----------------
        private TableLayoutPanel _topBar;
        private Label _titleLabel;
        private CardPanel _badge;
        private Label _badgeLabel;
        private Panel _themeHost;
        private FlatButton _themeButton;
        private ContextMenuStrip _themeMenu;
        private ToolStripMenuItem _themeMenuSystem;
        private ToolStripMenuItem _themeMenuLight;
        private ToolStripMenuItem _themeMenuDark;

        // ---------------- 载入/保存错误横幅 ----------------
        private Panel _bannerPanel;
        private Label _bannerLabel;
        private FlatButton _bannerCloseButton;

        // ---------------- 主体两栏 ----------------
        private TableLayoutPanel _contentLayout;

        // 左栏
        private Panel _leftPanel;
        private Panel _leftHeader;
        private Label _listHeaderLabel;
        private FlatButton _collapseButton;
        private CardPanel _listCard;
        private ThemedListBox _configList;
        private ConfigStatusStrip _collapsedStrip;
        private Panel _leftButtons;
        private TableLayoutPanel _itemButtonRow;
        private TableLayoutPanel _allButtonRow;
        private Panel _autoStartRow;
        private FlatButton _newButton;
        private FlatButton _duplicateButton;
        private FlatButton _deleteButton;
        private FlatButton _moveUpButton;
        private FlatButton _moveDownButton;
        private FlatButton _startAllButton;
        private FlatButton _stopAllButton;
        private ToggleSwitch _autoStartToggle;
        private FlatButton _exitButton;
        private ContextMenuStrip _listMenu;
        private ToolStripMenuItem _menuStart;
        private ToolStripMenuItem _menuStop;
        private ToolStripMenuItem _menuRename;
        private ToolStripMenuItem _menuNew;
        private ToolStripMenuItem _menuDelete;
        private TextBox _renameBox;

        // 右栏
        private TableLayoutPanel _rightColumn;
        private CardPanel _launchCard;
        private Panel _launchHeader;
        private Label _launchTitleLabel;
        private Panel _slotPanel;
        private Label _slotLabel;
        private Label _detailSummaryLabel;
        private TableLayoutPanel _launchButtonRow;
        private FlatButton _startButton;
        private FlatButton _stopButton;
        private CardPanel _outputCard;
        private Panel _outputHeader;
        private Label _outputTitleLabel;
        private FlatButton _clearLogButton;
        private FlatButton _copyLogButton;
        private Panel _logHost;
        private TextBox _logTemplate;

        // 托盘
        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _trayMenu;
        private ToolStripMenuItem _trayStartAll;
        private ToolStripMenuItem _trayStopAll;
        private ToolStripMenuItem _trayExit;

        // 资源
        private Timer _elapsedTimer;
        private Font _baseFont;
        private Font _titleFont;
        private Font _sectionFont;
        private Font _monoFont;
        private Icon _appIcon;

        /// <summary>
        /// 清理所有正在使用的资源。
        /// </summary>
        /// <param name="disposing">如果应释放托管资源，为 true；否则为 false。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (components != null)
                {
                    components.Dispose();
                }

                DisposeManagedExtras();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// 布局全部由代码搭建：TableLayoutPanel + Dock/Anchor，不使用 resx 坐标。
        /// 所有尺寸以 96 DPI 为基线，按启动时的系统 DPI 等比换算（Dip）。
        /// </summary>
        private void InitializeComponent()
        {
            int dpi = ResolveStartupDpi();
            int Dip(int value)
            {
                return (int)Math.Round(value * dpi / 96.0, MidpointRounding.AwayFromZero);
            }

            this.components = new System.ComponentModel.Container();
            this.SuspendLayout();

            // ---------------- 窗体 ----------------
            this.Text = "gostGUI v1.9";
            this.Name = "FormMain";
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(Dip(1024), Dip(640));
            this.MinimumSize = new Size(Dip(880), Dip(560));
            this.StartPosition = FormStartPosition.CenterScreen;
            this.DoubleBuffered = true;
            this.BackColor = Palette.Light.Surface;
            this.ForeColor = Palette.Light.TextPrimary;

            _baseFont = CreateBaseFont();
            this.Font = _baseFont;
            _titleFont = new Font(_baseFont.FontFamily, 12f, FontStyle.Bold, GraphicsUnit.Point);
            _sectionFont = new Font(_baseFont.FontFamily, 10f, FontStyle.Bold, GraphicsUnit.Point);
            _monoFont = CreateMonoFont(9.5f);
            _appIcon = LoadApplicationIcon();
            if (_appIcon != null)
            {
                this.Icon = _appIcon;
            }

            // ---------------- 顶部条：标题 / 权限徽标 / 主题三态 ----------------
            _topBar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = Dip(52),
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Palette.Light.Surface,
                Margin = new Padding(0)
            };
            _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(210)));
            _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(150)));
            _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _topBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _titleLabel = new Label
            {
                Text = "gostGUI v1.9",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = _titleFont,
                Margin = new Padding(Dip(16), 0, 0, 0)
            };

            _badge = new CardPanel
            {
                Dock = DockStyle.Fill,
                BorderRadius = Dip(10),
                Padding = new Padding(Dip(10), Dip(2), Dip(10), Dip(2)),
                Margin = new Padding(0, Dip(12), Dip(8), Dip(12))
            };
            _badgeLabel = new Label
            {
                Text = "权限：标准",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = _baseFont
            };
            _badge.Controls.Add(_badgeLabel);

            _themeHost = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, Dip(10), Dip(16), Dip(10)),
                Margin = new Padding(0)
            };
            _themeButton = new FlatButton
            {
                Dock = DockStyle.Right,
                Width = Dip(140),
                Text = "跟随系统 ▼",
                Variant = ButtonVariant.Secondary,
                Font = _baseFont
            };
            _themeButton.Click += ThemeButton_Click;
            _themeHost.Controls.Add(_themeButton);

            _themeMenuSystem = new ToolStripMenuItem("跟随系统");
            _themeMenuSystem.Click += (sender, e) => SetThemeMode(ThemeMode.System);
            _themeMenuLight = new ToolStripMenuItem("浅色");
            _themeMenuLight.Click += (sender, e) => SetThemeMode(ThemeMode.Light);
            _themeMenuDark = new ToolStripMenuItem("深色");
            _themeMenuDark.Click += (sender, e) => SetThemeMode(ThemeMode.Dark);
            _themeMenu = new ContextMenuStrip(this.components);
            _themeMenu.Items.AddRange(new ToolStripItem[]
            {
                _themeMenuSystem, _themeMenuLight, _themeMenuDark
            });

            _topBar.Controls.Add(_titleLabel, 0, 0);
            _topBar.Controls.Add(_badge, 1, 0);
            _topBar.Controls.Add(_themeHost, 2, 0);

            // ---------------- 载入/保存错误横幅（默认隐藏） ----------------
            _bannerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = Dip(44),
                Visible = false,
                Padding = new Padding(Dip(12), Dip(4), Dip(8), Dip(4)),
                BackColor = Palette.Light.SurfaceAlt
            };
            _bannerLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Palette.Light.Danger
            };
            _bannerCloseButton = new FlatButton
            {
                Dock = DockStyle.Right,
                Width = Dip(72),
                Text = "关闭",
                Variant = ButtonVariant.Ghost,
                Font = _baseFont
            };
            _bannerCloseButton.Click += (sender, e) => HideBanner();
            _bannerPanel.Controls.Add(_bannerLabel);
            _bannerPanel.Controls.Add(_bannerCloseButton);

            // ---------------- 主体两栏 ----------------
            _contentLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(Dip(12), Dip(12), Dip(12), Dip(12)),
                BackColor = Palette.Light.Surface
            };
            _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(300)));
            _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // ---------------- 左栏 ----------------
            _leftPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, Dip(8), 0),
                BackColor = Palette.Light.Surface
            };

            _leftHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = Dip(30),
                BackColor = Palette.Light.Surface
            };
            _listHeaderLabel = new Label
            {
                Text = "配置 (0)",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = _sectionFont
            };
            _collapseButton = new FlatButton
            {
                Dock = DockStyle.Right,
                Width = Dip(30),
                Text = "«",
                Variant = ButtonVariant.Ghost,
                Font = _baseFont
            };
            _collapseButton.Click += CollapseButton_Click;
            _leftHeader.Controls.Add(_listHeaderLabel);
            _leftHeader.Controls.Add(_collapseButton);

            _listCard = new CardPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(Dip(6), Dip(6), Dip(6), Dip(6)),
                Margin = new Padding(0)
            };
            _configList = new ThemedListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false
            };
            _configList.IsRunningSelector = item => IsItemRunning(item as ConfigItem);
            _configList.SubtitleSelector = item => SubtitleFor(item as ConfigItem);
            _configList.StatusTextSelector = item => StatusTextFor(item as ConfigItem);
            _configList.SelectedIndexChanged += ConfigList_SelectedIndexChanged;
            _configList.MouseClick += ConfigList_MouseClick;
            _configList.MouseDoubleClick += ConfigList_MouseDoubleClick;
            _configList.MouseDown += ConfigList_MouseDown;
            _listCard.Controls.Add(_configList);

            _collapsedStrip = new ConfigStatusStrip
            {
                Dock = DockStyle.Fill,
                Visible = false,
                Margin = new Padding(0)
            };
            _collapsedStrip.ItemProvider = ListItems;
            _collapsedStrip.RunningSelector = item => IsItemRunning(item);
            _collapsedStrip.StatusSelector = item => StatusTextFor(item);
            _collapsedStrip.MouseClick += CollapsedStrip_MouseClick;

            // 左栏底部三行按钮：新建/复制/删除/上移/下移、全部启动/全部停止、开机自启 + 退出
            _leftButtons = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = Dip(120),
                BackColor = Palette.Light.Surface
            };

            _itemButtonRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = Dip(34),
                ColumnCount = 5,
                RowCount = 1,
                Margin = new Padding(0),
                BackColor = Palette.Light.Surface
            };
            for (int i = 0; i < 5; i++)
            {
                _itemButtonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            }

            _itemButtonRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _newButton = CreateRowButton("新建", ButtonVariant.Primary, Dip(2));
            _duplicateButton = CreateRowButton("复制", ButtonVariant.Secondary, Dip(2));
            _deleteButton = CreateRowButton("删除", ButtonVariant.Danger, Dip(2));
            _moveUpButton = CreateRowButton("↑", ButtonVariant.Secondary, Dip(2));
            _moveDownButton = CreateRowButton("↓", ButtonVariant.Secondary, Dip(2));
            _newButton.Click += NewButton_Click;
            _duplicateButton.Click += DuplicateButton_Click;
            _deleteButton.Click += DeleteButton_Click;
            _moveUpButton.Click += MoveUpButton_Click;
            _moveDownButton.Click += MoveDownButton_Click;
            _itemButtonRow.Controls.Add(_newButton, 0, 0);
            _itemButtonRow.Controls.Add(_duplicateButton, 1, 0);
            _itemButtonRow.Controls.Add(_deleteButton, 2, 0);
            _itemButtonRow.Controls.Add(_moveUpButton, 3, 0);
            _itemButtonRow.Controls.Add(_moveDownButton, 4, 0);

            _allButtonRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = Dip(40),
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0, Dip(6), 0, 0),
                Margin = new Padding(0),
                BackColor = Palette.Light.Surface
            };
            _allButtonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _allButtonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _allButtonRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _startAllButton = CreateRowButton("全部启动", ButtonVariant.Primary, Dip(2));
            _stopAllButton = CreateRowButton("全部停止", ButtonVariant.Secondary, Dip(2));
            _startAllButton.Click += (sender, e) => StartAll(true);
            _stopAllButton.Click += (sender, e) => StopAll();
            _allButtonRow.Controls.Add(_startAllButton, 0, 0);
            _allButtonRow.Controls.Add(_stopAllButton, 1, 0);

            _autoStartRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = Dip(40),
                Padding = new Padding(0, Dip(6), 0, 0),
                Margin = new Padding(0),
                BackColor = Palette.Light.Surface
            };
            _autoStartToggle = new ToggleSwitch
            {
                Text = "开机自启",
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = _baseFont
            };
            _autoStartToggle.CheckedChanged += AutoStartToggle_CheckedChanged;
            _exitButton = new FlatButton
            {
                Dock = DockStyle.Right,
                Width = Dip(80),
                Text = "退出",
                Variant = ButtonVariant.Ghost,
                Font = _baseFont
            };
            _exitButton.Click += (sender, e) => ExitApplication();
            _autoStartRow.Controls.Add(_autoStartToggle);
            _autoStartRow.Controls.Add(_exitButton);

            // Dock 顺序：后加入的先贴边
            _leftButtons.Controls.Add(_autoStartRow);
            _leftButtons.Controls.Add(_allButtonRow);
            _leftButtons.Controls.Add(_itemButtonRow);

            _leftPanel.Controls.Add(_listCard);
            _leftPanel.Controls.Add(_collapsedStrip);
            _leftPanel.Controls.Add(_leftButtons);
            _leftPanel.Controls.Add(_leftHeader);

            // 列表右键菜单：启动 / 停止 / 重命名 / 新建 / 删除
            _menuStart = new ToolStripMenuItem("启动");
            _menuStart.Click += (sender, e) => ToggleOrStart(SelectedItem);
            _menuStop = new ToolStripMenuItem("停止");
            _menuStop.Click += (sender, e) => StopItems(SelectedItemAsList());
            _menuRename = new ToolStripMenuItem("重命名");
            _menuRename.Click += (sender, e) => BeginRename(SelectedItem);
            _menuNew = new ToolStripMenuItem("新建");
            _menuNew.Click += NewButton_Click;
            _menuDelete = new ToolStripMenuItem("删除");
            _menuDelete.Click += DeleteButton_Click;
            _listMenu = new ContextMenuStrip(this.components);
            _listMenu.Items.AddRange(new ToolStripItem[]
            {
                _menuStart, _menuStop, new ToolStripSeparator(), _menuRename, _menuNew, _menuDelete
            });
            _listMenu.Opening += ListMenu_Opening;
            _configList.ContextMenuStrip = _listMenu;

            _renameBox = new TextBox
            {
                Visible = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = _baseFont
            };
            _renameBox.KeyDown += RenameBox_KeyDown;
            _renameBox.Leave += RenameBox_Leave;

            // ---------------- 右栏 ----------------
            _rightColumn = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(Dip(8), 0, 0, 0),
                BackColor = Palette.Light.Surface
            };
            _rightColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _rightColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 56f));
            _rightColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 44f));

            // 上：启动设置卡片（12b 在此填字段）
            _launchCard = new CardPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, Dip(8))
            };
            _launchHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = Dip(30),
                BackColor = Palette.Light.SurfaceAlt
            };
            _launchTitleLabel = new Label
            {
                Text = "启动设置",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = _sectionFont
            };
            _launchHeader.Controls.Add(_launchTitleLabel);

            _slotPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(Dip(10), Dip(10), Dip(10), Dip(10)),
                BackColor = Palette.Light.Surface
            };
            _slotLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "（任务 12b 在此填充：程序 / 参数 / 工作目录 / 环境变量 / 以管理员身份运行）",
                ForeColor = Palette.Light.TextMuted
            };
            _slotPanel.Controls.Add(_slotLabel);

            _detailSummaryLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = Dip(26),
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "当前没有选中的配置项。"
            };

            _launchButtonRow = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = Dip(40),
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0),
                BackColor = Palette.Light.SurfaceAlt
            };
            _launchButtonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _launchButtonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(96)));
            _launchButtonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(96)));
            _launchButtonRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _stopButton = new FlatButton
            {
                Dock = DockStyle.Fill,
                Text = "停止",
                Variant = ButtonVariant.Secondary,
                Font = _baseFont,
                Margin = new Padding(Dip(2), Dip(2), Dip(2), Dip(2))
            };
            _stopButton.Click += (sender, e) => StopItems(SelectedItemAsList());
            _startButton = new FlatButton
            {
                Dock = DockStyle.Fill,
                Text = "启动",
                Variant = ButtonVariant.Primary,
                Font = _baseFont,
                Margin = new Padding(Dip(2), Dip(2), Dip(2), Dip(2))
            };
            _startButton.Click += (sender, e) => ToggleOrStart(SelectedItem);
            // 设计稿顺序：[启动] [停止]（左→右）
            _launchButtonRow.Controls.Add(_startButton, 1, 0);
            _launchButtonRow.Controls.Add(_stopButton, 2, 0);

            _launchCard.Controls.Add(_slotPanel);
            _launchCard.Controls.Add(_detailSummaryLabel);
            _launchCard.Controls.Add(_launchButtonRow);
            _launchCard.Controls.Add(_launchHeader);

            // 下：输出卡片（12b 在此放带颜色的日志控件）
            _outputCard = new CardPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, Dip(8), 0, 0)
            };
            _outputHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = Dip(30),
                BackColor = Palette.Light.SurfaceAlt
            };
            _outputTitleLabel = new Label
            {
                Text = "输出",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = _sectionFont
            };
            _clearLogButton = new FlatButton
            {
                Dock = DockStyle.Right,
                Width = Dip(72),
                Text = "清空",
                Variant = ButtonVariant.Ghost,
                Font = _baseFont
            };
            _clearLogButton.Click += ClearLogButton_Click;
            _copyLogButton = new FlatButton
            {
                Dock = DockStyle.Right,
                Width = Dip(72),
                Text = "复制",
                Variant = ButtonVariant.Ghost,
                Font = _baseFont
            };
            _copyLogButton.Click += CopyLogButton_Click;
            _outputHeader.Controls.Add(_outputTitleLabel);
            _outputHeader.Controls.Add(_clearLogButton);
            _outputHeader.Controls.Add(_copyLogButton);

            _logHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Light.LogBg
            };
            _logTemplate = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                Visible = false,
                Font = _monoFont,
                BackColor = Palette.Light.LogBg,
                ForeColor = Palette.Light.LogStdout
            };
            _logHost.Controls.Add(_logTemplate);
            _logHost.Resize += LogHost_Resize;

            _outputCard.Controls.Add(_logHost);
            _outputCard.Controls.Add(_outputHeader);

            _rightColumn.Controls.Add(_launchCard, 0, 0);
            _rightColumn.Controls.Add(_outputCard, 0, 1);

            _contentLayout.Controls.Add(_leftPanel, 0, 0);
            _contentLayout.Controls.Add(_rightColumn, 1, 0);

            // ---------------- 托盘 ----------------
            _trayStartAll = new ToolStripMenuItem("启动全部");
            _trayStartAll.Click += (sender, e) => StartAll(true);
            _trayStopAll = new ToolStripMenuItem("停止全部");
            _trayStopAll.Click += (sender, e) => StopAll();
            _trayExit = new ToolStripMenuItem("退出");
            _trayExit.Click += (sender, e) => ExitApplication();
            _trayMenu = new ContextMenuStrip(this.components);
            _trayMenu.Items.AddRange(new ToolStripItem[]
            {
                _trayStartAll, _trayStopAll, new ToolStripSeparator(), _trayExit
            });

            _notifyIcon = new NotifyIcon(this.components)
            {
                Text = "gostGUI v1.9",
                ContextMenuStrip = _trayMenu,
                Icon = _appIcon,
                Visible = false
            };
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;
            _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

            // ---------------- 计时器：刷新运行时长 ----------------
            _elapsedTimer = new Timer(this.components)
            {
                Interval = 1000
            };
            _elapsedTimer.Tick += ElapsedTimer_Tick;

            // ---------------- 窗体级事件 ----------------
            this.FormClosing += FormMain_FormClosing;
            this.FormClosed += FormMain_FormClosed;

            // Dock 顺序：后加入的先贴边（Fill 在最前面加入）
            this.Controls.Add(_contentLayout);
            this.Controls.Add(_bannerPanel);
            this.Controls.Add(_topBar);

            this.ResumeLayout(false);
            this.PerformLayout();
        }

        /// <summary>左栏按钮行的统一外观（20% 等分格子里的扁平按钮）。</summary>
        private FlatButton CreateRowButton(string text, ButtonVariant variant, int horizontalMargin)
        {
            return new FlatButton
            {
                Dock = DockStyle.Fill,
                Text = text,
                Variant = variant,
                Font = _baseFont,
                Margin = new Padding(horizontalMargin, 0, horizontalMargin, 0)
            };
        }
    }
}
