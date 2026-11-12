// =====================================================================================
// FormMain.Theme.cs —— 主题应用与主题选择器：调色板铺设、三态主题切换、当前调色板与权限徽标重着色。
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
    }
}
