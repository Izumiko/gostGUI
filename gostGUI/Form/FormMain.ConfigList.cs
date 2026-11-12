// =====================================================================================
// FormMain.ConfigList.cs —— 配置列表：渲染与选中、行内重命名、右键菜单、新建/复制/删除/
// 排序、折叠后的状态窄条类型（ConfigStatusStrip）。
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

        internal void SelectItemForTest(ConfigItem item)
        {
            SelectItem(item);
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

        private void ElapsedTimer_Tick(object sender, EventArgs e)
        {
            if (_runStarted.Count == 0)
            {
                return;
            }

            _configList?.Invalidate();
            _collapsedStrip?.Invalidate();
        }
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
