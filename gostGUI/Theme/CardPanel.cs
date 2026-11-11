using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 卡片容器：圆角 6px、1px 描边，可选左上角标题（弱化色）。
    /// 子控件布局仍由 Panel 的 Padding/DisplayRectangle 负责。
    /// </summary>
    public sealed class CardPanel : Panel, IThemed
    {
        private Palette _palette = Palette.Light;
        private int _borderRadius = 6;
        private string _title = string.Empty;
        private Padding _defaultPadding = new Padding(0);

        public CardPanel()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor,
                true);

            _defaultPadding = CreateDefaultPadding();
            Padding = _defaultPadding;

            ApplyTheme(_palette);
        }

        /// <summary>圆角半径（96 DPI 逻辑像素），默认 6。</summary>
        public int BorderRadius
        {
            get { return _borderRadius; }
            set
            {
                int next = Math.Max(0, value);
                if (_borderRadius == next)
                {
                    return;
                }

                _borderRadius = next;
                Invalidate();
            }
        }

        /// <summary>可选的卡片标题；空字符串表示不画标题。</summary>
        public string Title
        {
            get { return _title; }
            set
            {
                string next = value ?? string.Empty;
                if (_title == next)
                {
                    return;
                }

                _title = next;
                Invalidate();
            }
        }

        public void ApplyTheme(Palette palette)
        {
            if (palette == null)
            {
                return;
            }

            _palette = palette;
            BackColor = palette.SurfaceAlt;
            ForeColor = palette.TextPrimary;
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

        private Padding CreateDefaultPadding()
        {
            return new Padding(
                ThemePaint.Scale(this, 16),
                ThemePaint.Scale(this, 12),
                ThemePaint.Scale(this, 16),
                ThemePaint.Scale(this, 12));
        }

        /// <summary>DPI/字体变化后重算默认 Padding；调用方自定义的 Padding 不会被覆盖。</summary>
        private void UpdateDpiMetrics()
        {
            Padding next = CreateDefaultPadding();
            if (Padding == _defaultPadding)
            {
                Padding = next;
            }

            _defaultPadding = next;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (e == null || e.Graphics == null)
            {
                return;
            }

            using (var brush = new SolidBrush(ThemePaint.ParentSurface(this, _palette)))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (e == null || e.Graphics == null || Width <= 0 || Height <= 0)
            {
                return;
            }

            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var bounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            ThemePaint.FillRoundedBox(
                graphics,
                bounds,
                ThemePaint.Scale(this, _borderRadius),
                _palette.SurfaceAlt,
                _palette.Border);

            if (string.IsNullOrEmpty(_title))
            {
                return;
            }

            int left = Padding.Left;
            int top = Padding.Top;
            int width = Width - Padding.Horizontal;
            if (width <= 0 || top < 0)
            {
                return;
            }

            var titleBounds = new Rectangle(left, top, width, Math.Max(1, Font.Height));
            const TextFormatFlags flags =
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix;

            TextRenderer.DrawText(graphics, _title, Font, titleBounds, _palette.TextMuted, flags);
        }
    }
}
