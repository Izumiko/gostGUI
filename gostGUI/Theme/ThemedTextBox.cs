using System;
using System.Drawing;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 主题化单行/多行文本框：外层 Panel 画 1px 边框与强调色焦点环，
    /// 内部 TextBox 去边框并铺满内容区。外部通过 Inner 访问原生 TextBox。
    /// </summary>
    public sealed class ThemedTextBox : Panel, IThemed
    {
        private readonly TextBox _inner;
        private Palette _palette = Palette.Light;
        private bool _focused;
        private Padding _defaultPadding = new Padding(0);

        public ThemedTextBox()
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

            _inner = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Multiline = false,
                ScrollBars = ScrollBars.None
            };
            _inner.GotFocus += InnerGotFocus;
            _inner.LostFocus += InnerLostFocus;
            _inner.TextChanged += InnerTextChanged;
            _inner.HandleCreated += (sender, args) =>
                WindowTheme.ApplyScrollbars(_inner, ReferenceEquals(_palette, Palette.Dark));
            Controls.Add(_inner);

            ApplyTheme(_palette);
            LayoutInner();
        }

        public TextBox Inner
        {
            get { return _inner; }
        }

        public override string Text
        {
            get { return _inner.Text; }
            set { _inner.Text = value ?? string.Empty; }
        }

        /// <summary>是否多行；多行时内层 TextBox 填满内容区。</summary>
        public bool Multiline
        {
            get { return _inner.Multiline; }
            set
            {
                if (_inner.Multiline == value)
                {
                    return;
                }

                _inner.Multiline = value;
                if (!value)
                {
                    _inner.ScrollBars = ScrollBars.None;
                }

                LayoutInner();
                Invalidate();
            }
        }

        /// <summary>滚动条设置；非 None 会自动打开 Multiline。</summary>
        public ScrollBars ScrollBars
        {
            get { return _inner.ScrollBars; }
            set
            {
                if (value != ScrollBars.None && !_inner.Multiline)
                {
                    _inner.Multiline = true;
                }

                _inner.ScrollBars = value;
                WindowTheme.ApplyScrollbars(_inner, ReferenceEquals(_palette, Palette.Dark));
                LayoutInner();
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
            _inner.BackColor = palette.SurfaceAlt;
            _inner.ForeColor = palette.TextPrimary;
            WindowTheme.ApplyScrollbars(_inner, ReferenceEquals(palette, Palette.Dark));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _inner.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutInner();
        }

        protected override void OnPaddingChanged(EventArgs e)
        {
            base.OnPaddingChanged(e);
            LayoutInner();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            UpdateDpiMetrics();
            LayoutInner();
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateDpiMetrics();
            LayoutInner();
            Invalidate();
        }

        private Padding CreateDefaultPadding()
        {
            return new Padding(
                ThemePaint.Scale(this, 8),
                ThemePaint.Scale(this, 6),
                ThemePaint.Scale(this, 8),
                ThemePaint.Scale(this, 6));
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

            // 焦点环在最外圈 1px，边框始终在向内 1px 处，内容再留 1px。
            if (_focused)
            {
                using (var ringPen = new Pen(_palette.Accent, 1f))
                {
                    graphics.DrawRectangle(ringPen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
                }
            }

            using (var borderPen = new Pen(_palette.Border, 1f))
            {
                graphics.DrawRectangle(borderPen, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
            }
        }

        private void InnerGotFocus(object sender, EventArgs e)
        {
            _focused = true;
            Invalidate();
        }

        private void InnerLostFocus(object sender, EventArgs e)
        {
            _focused = false;
            Invalidate();
        }

        private void InnerTextChanged(object sender, EventArgs e)
        {
            base.OnTextChanged(e);
        }

        private void LayoutInner()
        {
            if (_inner == null)
            {
                return;
            }

            int frame = ThemePaint.Scale(this, 2);
            int left = frame + Padding.Left;
            int top = frame + Padding.Top;
            int width = Width - (frame * 2) - Padding.Horizontal;
            int height = Height - (frame * 2) - Padding.Vertical;
            if (width <= 0 || height <= 0)
            {
                _inner.Bounds = new Rectangle(0, 0, 0, 0);
                return;
            }

            if (!_inner.Multiline)
            {
                int preferred = _inner.PreferredHeight;
                int lineHeight = Math.Min(height, Math.Max(1, preferred));
                int lineTop = top + Math.Max(0, (height - lineHeight) / 2);
                _inner.Bounds = new Rectangle(left, lineTop, width, lineHeight);
                return;
            }

            _inner.Bounds = new Rectangle(left, top, width, height);
        }
    }
}
