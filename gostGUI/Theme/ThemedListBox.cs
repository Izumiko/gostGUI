using System;
using System.Drawing;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 配置列表：44px 固定行高，owner-draw 状态点 + 名称 + 弱化副标题，
    /// 右侧可显示运行时长等状态文本。不画系统焦点矩形，避免选中闪烁。
    /// </summary>
    public sealed class ThemedListBox : ListBox, IThemed
    {
        private Palette _palette = Palette.Light;
        private int _defaultItemHeight;

        public ThemedListBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DrawMode = DrawMode.OwnerDrawFixed;
            _defaultItemHeight = ThemePaint.Scale(this, 44);
            ItemHeight = _defaultItemHeight;
            IntegralHeight = false;
            BorderStyle = BorderStyle.None;
            ApplyTheme(_palette);
        }

        public Func<object, bool> IsRunningSelector { get; set; }

        /// <summary>副标题选择器；任务简报里的旧名 RowSubtitleSelector 已废弃，统一使用本属性。</summary>
        public Func<object, string> SubtitleSelector { get; set; }

        public Func<object, string> StatusTextSelector { get; set; }

        public void ApplyTheme(Palette palette)
        {
            if (palette == null)
            {
                return;
            }

            _palette = palette;
            BackColor = ResolveSurfaceColor();
            ForeColor = palette.TextPrimary;
            Invalidate();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            BackColor = ResolveSurfaceColor();
            Invalidate();
        }

        protected override void OnParentBackColorChanged(EventArgs e)
        {
            base.OnParentBackColorChanged(e);
            BackColor = ResolveSurfaceColor();
            Invalidate();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            UpdateDpiMetrics();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateDpiMetrics();
        }

        private Color ResolveSurfaceColor()
        {
            return ThemePaint.ParentSurface(this, _palette);
        }

        /// <summary>DPI/字体变化后重算 44px 基线行高；调用方自定义的 ItemHeight 不会被覆盖。</summary>
        private void UpdateDpiMetrics()
        {
            int nextItemHeight = ThemePaint.Scale(this, 44);
            if (ItemHeight == _defaultItemHeight)
            {
                ItemHeight = nextItemHeight;
            }

            _defaultItemHeight = nextItemHeight;
            Invalidate();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e == null || e.Index < 0 || e.Index >= Items.Count)
            {
                return;
            }

            Graphics graphics = e.Graphics;
            Rectangle bounds = e.Bounds;
            if (graphics == null || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            object item = Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Palette palette = _palette;

            Color background = selected ? palette.Accent : palette.SurfaceAlt;
            Color nameColor = selected ? palette.AccentText : (Enabled ? palette.TextPrimary : palette.TextMuted);
            Color subtitleColor = selected
                ? ThemePaint.Blend(palette.AccentText, palette.Accent, 0.28f)
                : palette.TextMuted;
            Color statusColor = subtitleColor;

            using (var backgroundBrush = new SolidBrush(background))
            {
                graphics.FillRectangle(backgroundBrush, bounds);
            }

            int padding = ThemePaint.Scale(this, 10);
            int dotSize = ThemePaint.Scale(this, 10);
            int gap = ThemePaint.Scale(this, 10);
            int rowHeight = bounds.Height;
            int nameHeight = Math.Max(1, rowHeight / 2);

            string statusText = StatusTextSelector != null ? StatusTextSelector(item) : null;
            int statusWidth = 0;
            if (!string.IsNullOrEmpty(statusText))
            {
                Size statusSize = TextRenderer.MeasureText(statusText, Font);
                statusWidth = Math.Min(
                    statusSize.Width + ThemePaint.Scale(this, 2),
                    Math.Max(1, bounds.Width / 3));
            }

            int contentRight = bounds.Right - padding - (statusWidth > 0 ? statusWidth + gap : 0);
            if (contentRight < bounds.Left)
            {
                contentRight = bounds.Left;
            }

            int dotLeft = bounds.Left + padding;
            int dotTop = bounds.Top + Math.Max(0, (nameHeight - dotSize) / 2);
            bool running = IsRunningSelector != null && IsRunningSelector(item);
            Color dotColor = running ? palette.Success : palette.TextMuted;
            if (!Enabled)
            {
                dotColor = ThemePaint.Blend(dotColor, palette.SurfaceAlt, 0.5f);
            }

            using (var dotBrush = new SolidBrush(dotColor))
            {
                graphics.FillEllipse(dotBrush, new Rectangle(dotLeft, dotTop, dotSize, dotSize));
            }

            int textLeft = dotLeft + dotSize + gap;
            int textWidth = contentRight - textLeft;
            if (textWidth > 0)
            {
                var nameBounds = new Rectangle(
                    textLeft,
                    bounds.Top + ThemePaint.Scale(this, 3),
                    textWidth,
                    Math.Max(1, nameHeight - ThemePaint.Scale(this, 3)));
                const TextFormatFlags nameFlags =
                    TextFormatFlags.Left |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(graphics, item == null ? string.Empty : item.ToString(), Font, nameBounds, nameColor, nameFlags);

                string subtitle = SubtitleSelector != null ? SubtitleSelector(item) : null;
                if (!string.IsNullOrEmpty(subtitle))
                {
                    var subtitleBounds = new Rectangle(
                        textLeft,
                        bounds.Top + nameHeight,
                        textWidth,
                        Math.Max(1, rowHeight - nameHeight - ThemePaint.Scale(this, 1)));
                    const TextFormatFlags subtitleFlags =
                        TextFormatFlags.Left |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine |
                        TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix;
                    TextRenderer.DrawText(graphics, subtitle, Font, subtitleBounds, subtitleColor, subtitleFlags);
                }
            }

            if (statusWidth > 0)
            {
                var statusBounds = new Rectangle(
                    bounds.Right - padding - statusWidth,
                    bounds.Top + ThemePaint.Scale(this, 3),
                    statusWidth,
                    Math.Max(1, nameHeight - ThemePaint.Scale(this, 3)));
                const TextFormatFlags statusFlags =
                    TextFormatFlags.Right |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(graphics, statusText, Font, statusBounds, statusColor, statusFlags);
            }

            base.OnDrawItem(e);
        }
    }
}
