using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>扁平按钮的语义变体。</summary>
    public enum ButtonVariant
    {
        Primary,
        Secondary,
        Ghost,
        Danger
    }

    /// <summary>
    /// 自绘扁平按钮：圆角 6px，支持 Primary/Secondary/Ghost/Danger 四种变体、
    /// hover/pressed/disabled 三态与键盘焦点框。文字统一走 TextRenderer（GDI）。
    /// </summary>
    public sealed class FlatButton : Button, IThemed
    {
        private Palette _palette = Palette.Light;
        private ButtonVariant _variant = ButtonVariant.Secondary;
        private bool _hovered;
        private bool _pressed;
        private int _defaultMinimumHeight;

        public FlatButton()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.Selectable,
                true);

            UseVisualStyleBackColor = false;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;

            // 96 DPI 下按钮高度不低于 30；调用方若把 MinimumSize 设得更大则保留更大值。
            _defaultMinimumHeight = ThemePaint.Scale(this, 30);
            MinimumSize = new Size(0, _defaultMinimumHeight);

            ApplyTheme(_palette);
        }

        public ButtonVariant Variant
        {
            get { return _variant; }
            set
            {
                if (_variant == value)
                {
                    return;
                }

                _variant = value;
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

            Color body;
            Color text;
            Color border;
            ResolveColors(out body, out text, out border);

            var bodyBounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            int radius = ThemePaint.Scale(this, 6);
            ThemePaint.FillRoundedBox(graphics, bodyBounds, radius, body, border);

            if (Focused && ShowFocusCues)
            {
                int inset = ThemePaint.Scale(this, 2);
                Rectangle ringBounds = Rectangle.Inflate(bodyBounds, -inset, -inset);
                if (ringBounds.Width > 2 && ringBounds.Height > 2)
                {
                    Color ring = (Variant == ButtonVariant.Primary || Variant == ButtonVariant.Danger)
                        ? _palette.AccentText
                        : _palette.Accent;
                    if (!Enabled)
                    {
                        ring = _palette.TextMuted;
                    }

                    using (GraphicsPath path = ThemePaint.RoundedRect(ringBounds, Math.Max(1, radius - inset)))
                    using (var pen = new Pen(ring, 1f))
                    {
                        graphics.DrawPath(pen, path);
                    }
                }
            }

            int horizontalPadding = ThemePaint.Scale(this, 10);
            int verticalPadding = ThemePaint.Scale(this, 2);
            Rectangle textBounds = Rectangle.Inflate(ClientRectangle, -horizontalPadding, -verticalPadding);
            if (textBounds.Width <= 0 || textBounds.Height <= 0)
            {
                return;
            }

            const TextFormatFlags flags =
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix;

            TextRenderer.DrawText(graphics, Text ?? string.Empty, Font, textBounds, text, flags);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent != null && mevent.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }

            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            if (_pressed)
            {
                _pressed = false;
                Invalidate();
            }

            base.OnMouseUp(mevent);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnLostFocus(e);
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

        /// <summary>DPI 或字体变化后重算 96-DPI 基线的默认最小高度；调用方自定义的更大值保持不变。</summary>
        private void UpdateDpiMetrics()
        {
            int nextMinimumHeight = ThemePaint.Scale(this, 30);
            if (MinimumSize.Height == _defaultMinimumHeight)
            {
                MinimumSize = new Size(MinimumSize.Width, nextMinimumHeight);
            }

            _defaultMinimumHeight = nextMinimumHeight;
            Invalidate();
        }

        private void ResolveColors(out Color body, out Color text, out Color border)
        {
            switch (Variant)
            {
                case ButtonVariant.Primary:
                    body = _hovered ? ThemePaint.Blend(_palette.Accent, _palette.AccentText, 0.12f) : _palette.Accent;
                    if (_pressed)
                    {
                        body = ThemePaint.Blend(_palette.Accent, _palette.AccentText, 0.22f);
                    }

                    text = _palette.AccentText;
                    border = body;
                    break;

                case ButtonVariant.Danger:
                    body = _hovered ? ThemePaint.Blend(_palette.Danger, _palette.AccentText, 0.12f) : _palette.Danger;
                    if (_pressed)
                    {
                        body = ThemePaint.Blend(_palette.Danger, _palette.AccentText, 0.22f);
                    }

                    text = _palette.AccentText;
                    border = body;
                    break;

                case ButtonVariant.Ghost:
                    body = BackColor;
                    if (_pressed)
                    {
                        body = _palette.ButtonPress;
                    }
                    else if (_hovered)
                    {
                        body = _palette.ButtonHover;
                    }

                    text = _palette.TextPrimary;
                    border = Color.Transparent;
                    break;

                default:
                    body = _pressed ? _palette.ButtonPress : (_hovered ? _palette.ButtonHover : _palette.ButtonBg);
                    text = _palette.TextPrimary;
                    border = _palette.Border;
                    break;
            }

            if (!Enabled)
            {
                body = ThemePaint.Blend(body, _palette.SurfaceAlt, 0.60f);
                text = _palette.TextMuted;
                border = border.A == 0 ? border : _palette.Border;
            }
        }
    }
}
