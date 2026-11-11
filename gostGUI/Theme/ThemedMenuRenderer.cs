using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 托盘/菜单的深色适配渲染器：用调色板替换 ProfessionalColorTable，
    /// 关闭系统渐变与图片边距，并统一文字/箭头颜色。
    /// </summary>
    public sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer, IThemed
    {
        private readonly ThemedColorTable _table;
        private Palette _palette;

        public ThemedMenuRenderer()
            : this(Palette.Light)
        {
        }

        public ThemedMenuRenderer(Palette palette)
            : base(new ThemedColorTable(palette ?? Palette.Light))
        {
            _palette = palette ?? Palette.Light;
            _table = (ThemedColorTable)ColorTable;
            RoundedEdges = false;
        }

        public void ApplyTheme(Palette palette)
        {
            if (palette == null)
            {
                return;
            }

            _palette = palette;
            _table.Palette = palette;
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 自绘菜单不需要左侧图片边距的灰色渐变条。
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            if (e == null || e.Graphics == null || e.ToolStrip == null)
            {
                return;
            }

            Color background = e.ToolStrip is ToolStripDropDownMenu
                ? _palette.SurfaceAlt
                : _palette.Surface;

            using (var brush = new SolidBrush(background))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e == null || e.Graphics == null || e.ToolStrip == null ||
                !(e.ToolStrip is ToolStripDropDownMenu))
            {
                return;
            }

            Size size = e.ToolStrip.ClientSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            using (var pen = new Pen(_palette.Border, 1f))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e == null || e.Item == null)
            {
                base.OnRenderItemText(e);
                return;
            }

            if (!e.Item.Enabled)
            {
                e.TextColor = _palette.TextMuted;
            }
            else if (e.Item.Pressed)
            {
                // 按下态背景是 ButtonPress（浅色主题下很浅），用正文色保证可读。
                e.TextColor = _palette.TextPrimary;
            }
            else if (IsAccentBackedItem(e.Item))
            {
                // 只有 Accent 背景（下拉菜单项选中 / 未悬停的 checked 按钮）才用反白色。
                e.TextColor = _palette.AccentText;
            }
            else
            {
                e.TextColor = _palette.TextPrimary;
            }

            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            if (e == null || e.Item == null)
            {
                base.OnRenderArrow(e);
                return;
            }

            if (!e.Item.Enabled)
            {
                e.ArrowColor = _palette.TextMuted;
            }
            else if (e.Item.Pressed)
            {
                e.ArrowColor = _palette.TextPrimary;
            }
            else if (e.Item is ToolStripMenuItem && e.Item.Selected)
            {
                e.ArrowColor = _palette.AccentText;
            }
            else
            {
                e.ArrowColor = _palette.TextPrimary;
            }

            base.OnRenderArrow(e);
        }

        private static bool IsAccentBackedItem(ToolStripItem item)
        {
            // 下拉/顶级菜单项选中时背景是 Accent；ToolStripButton 悬停是 ButtonHover，
            // 只有 checked 且未悬停/未按下时才是 Accent 背景。
            if (item is ToolStripMenuItem)
            {
                return item.Selected;
            }

            var button = item as ToolStripButton;
            return button != null && button.Checked && !button.Selected && !button.Pressed;
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            if (e == null || e.Graphics == null || e.Item == null || !e.Item.Enabled)
            {
                base.OnRenderItemCheck(e);
                return;
            }

            Rectangle box = e.ImageRectangle;
            if (box.Width <= 2 || box.Height <= 2)
            {
                base.OnRenderItemCheck(e);
                return;
            }

            int radius = Math.Max(1, Math.Min(box.Width, box.Height) / 4);
            ThemePaint.FillRoundedBox(e.Graphics, box, radius, _palette.Accent, _palette.Accent);

            int quarterX = Math.Max(1, box.Width / 4);
            int quarterY = Math.Max(1, box.Height / 4);
            Point[] check =
            {
                new Point(box.Left + quarterX, box.Top + (box.Height / 2)),
                new Point(box.Left + (box.Width / 2), box.Bottom - quarterY),
                new Point(box.Right - quarterX, box.Top + quarterY)
            };

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(_palette.AccentText, Math.Max(1.5f, box.Width / 8f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                e.Graphics.DrawLines(pen, check);
            }
        }

        private sealed class ThemedColorTable : ProfessionalColorTable
        {
            private Palette _palette;

            public ThemedColorTable(Palette palette)
            {
                _palette = palette ?? Palette.Light;
                UseSystemColors = false;
            }

            public Palette Palette
            {
                get { return _palette; }
                set { _palette = value ?? Palette.Light; }
            }

            public override Color MenuBorder
            {
                get { return _palette.Border; }
            }

            public override Color MenuItemBorder
            {
                get { return _palette.Accent; }
            }

            public override Color MenuItemSelected
            {
                get { return _palette.Accent; }
            }

            public override Color MenuItemSelectedGradientBegin
            {
                get { return _palette.Accent; }
            }

            public override Color MenuItemSelectedGradientEnd
            {
                get { return _palette.Accent; }
            }

            public override Color MenuItemPressedGradientBegin
            {
                get { return _palette.ButtonPress; }
            }

            public override Color MenuItemPressedGradientMiddle
            {
                get { return _palette.ButtonPress; }
            }

            public override Color MenuItemPressedGradientEnd
            {
                get { return _palette.ButtonPress; }
            }

            public override Color MenuStripGradientBegin
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color MenuStripGradientEnd
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ToolStripDropDownBackground
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ToolStripGradientBegin
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ToolStripGradientMiddle
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ToolStripGradientEnd
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ToolStripBorder
            {
                get { return _palette.Border; }
            }

            public override Color ImageMarginGradientBegin
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ImageMarginGradientMiddle
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ImageMarginGradientEnd
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ImageMarginRevealedGradientBegin
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ImageMarginRevealedGradientMiddle
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color ImageMarginRevealedGradientEnd
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color SeparatorDark
            {
                get { return _palette.Border; }
            }

            public override Color SeparatorLight
            {
                get { return _palette.Border; }
            }

            public override Color ButtonSelectedHighlight
            {
                get { return _palette.ButtonHover; }
            }

            public override Color ButtonSelectedHighlightBorder
            {
                get { return _palette.Border; }
            }

            public override Color ButtonSelectedBorder
            {
                get { return _palette.Border; }
            }

            public override Color ButtonSelectedGradientBegin
            {
                get { return _palette.ButtonHover; }
            }

            public override Color ButtonSelectedGradientMiddle
            {
                get { return _palette.ButtonHover; }
            }

            public override Color ButtonSelectedGradientEnd
            {
                get { return _palette.ButtonHover; }
            }

            public override Color ButtonPressedHighlight
            {
                get { return _palette.ButtonPress; }
            }

            public override Color ButtonPressedHighlightBorder
            {
                get { return _palette.Border; }
            }

            public override Color ButtonPressedBorder
            {
                get { return _palette.Border; }
            }

            public override Color ButtonPressedGradientBegin
            {
                get { return _palette.ButtonPress; }
            }

            public override Color ButtonPressedGradientMiddle
            {
                get { return _palette.ButtonPress; }
            }

            public override Color ButtonPressedGradientEnd
            {
                get { return _palette.ButtonPress; }
            }

            public override Color ButtonCheckedHighlight
            {
                get { return _palette.Accent; }
            }

            public override Color ButtonCheckedHighlightBorder
            {
                get { return _palette.Accent; }
            }

            public override Color ButtonCheckedGradientBegin
            {
                get { return _palette.Accent; }
            }

            public override Color ButtonCheckedGradientMiddle
            {
                get { return _palette.Accent; }
            }

            public override Color ButtonCheckedGradientEnd
            {
                get { return _palette.Accent; }
            }

            public override Color CheckBackground
            {
                get { return _palette.Accent; }
            }

            public override Color CheckSelectedBackground
            {
                get { return _palette.Accent; }
            }

            public override Color CheckPressedBackground
            {
                get { return _palette.Accent; }
            }

            public override Color GripDark
            {
                get { return _palette.Border; }
            }

            public override Color GripLight
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color OverflowButtonGradientBegin
            {
                get { return _palette.ButtonBg; }
            }

            public override Color OverflowButtonGradientMiddle
            {
                get { return _palette.ButtonBg; }
            }

            public override Color OverflowButtonGradientEnd
            {
                get { return _palette.ButtonBg; }
            }

            public override Color StatusStripGradientBegin
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color StatusStripGradientEnd
            {
                get { return _palette.SurfaceAlt; }
            }

            public override Color RaftingContainerGradientBegin
            {
                get { return _palette.Surface; }
            }

            public override Color RaftingContainerGradientEnd
            {
                get { return _palette.Surface; }
            }

            public override Color ToolStripPanelGradientBegin
            {
                get { return _palette.Surface; }
            }

            public override Color ToolStripPanelGradientEnd
            {
                get { return _palette.Surface; }
            }

            public override Color ToolStripContentPanelGradientBegin
            {
                get { return _palette.Surface; }
            }

            public override Color ToolStripContentPanelGradientEnd
            {
                get { return _palette.Surface; }
            }
        }
    }
}
