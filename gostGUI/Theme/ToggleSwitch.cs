using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 开关控件：36×20 轨道 + 滑动圆点，文字在轨道右侧。
    /// 支持鼠标点击与 Space 键切换，替代 CheckBox 的勾选语义。
    /// </summary>
    public sealed class ToggleSwitch : Control, IThemed
    {
        private Palette _palette = Palette.Light;
        private bool _checked;

        public ToggleSwitch()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.Selectable,
                true);

            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
            Cursor = Cursors.Hand;
            AutoSize = true;
            Size = GetPreferredSize(Size.Empty);
            ApplyTheme(_palette);
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value)
                {
                    return;
                }

                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler CheckedChanged;

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

        public override Size GetPreferredSize(Size proposedSize)
        {
            int trackWidth = ThemePaint.Scale(this, 36);
            int trackHeight = ThemePaint.Scale(this, 20);
            string text = Text ?? string.Empty;
            int gap = text.Length == 0 ? 0 : ThemePaint.Scale(this, 8);
            Size textSize = text.Length == 0 ? Size.Empty : TextRenderer.MeasureText(text, Font);
            int width = trackWidth + gap + textSize.Width + ThemePaint.Scale(this, 2);
            int height = Math.Max(trackHeight, textSize.Height);
            return new Size(Math.Max(1, width), Math.Max(1, height));
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space)
            {
                return true;
            }

            return base.IsInputKey(keyData);
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

            int trackWidth = ThemePaint.Scale(this, 36);
            int trackHeight = ThemePaint.Scale(this, 20);
            int trackTop = Math.Max(0, (Height - trackHeight) / 2);
            var track = new Rectangle(0, trackTop, Math.Min(trackWidth, Width), trackHeight);

            Color trackFill = _checked ? _palette.Accent : _palette.ButtonPress;
            Color trackBorder = _checked ? _palette.Accent : _palette.Border;
            Color knob = _checked ? _palette.AccentText : _palette.TextMuted;

            if (!Enabled)
            {
                trackFill = ThemePaint.Blend(trackFill, _palette.SurfaceAlt, 0.55f);
                trackBorder = ThemePaint.Blend(trackBorder, _palette.SurfaceAlt, 0.55f);
                knob = ThemePaint.Blend(knob, _palette.SurfaceAlt, 0.35f);
            }

            ThemePaint.FillRoundedBox(graphics, track, trackHeight / 2, trackFill, trackBorder);

            int knobInset = Math.Max(1, ThemePaint.Scale(this, 2));
            int knobDiameter = Math.Max(2, trackHeight - (knobInset * 2));
            int knobLeft = _checked
                ? track.Right - knobInset - knobDiameter
                : track.Left + knobInset;
            var knobBounds = new Rectangle(knobLeft, track.Top + knobInset, knobDiameter, knobDiameter);
            using (var knobBrush = new SolidBrush(knob))
            {
                graphics.FillEllipse(knobBrush, knobBounds);
            }

            if (Focused && ShowFocusCues)
            {
                Rectangle focusBounds = Rectangle.Inflate(track, ThemePaint.Scale(this, 2), ThemePaint.Scale(this, 2));
                if (focusBounds.Width > 2 && focusBounds.Height > 2)
                {
                    using (GraphicsPath path = ThemePaint.RoundedRect(focusBounds, focusBounds.Height / 2))
                    using (var pen = new Pen(Enabled ? _palette.Accent : _palette.TextMuted, 1f))
                    {
                        graphics.DrawPath(pen, path);
                    }
                }
            }

            int textLeft = track.Right + ThemePaint.Scale(this, 8);
            int textWidth = Width - textLeft;
            if (textWidth <= 0 || string.IsNullOrEmpty(Text))
            {
                return;
            }

            var textBounds = new Rectangle(textLeft, 0, textWidth, Height);
            const TextFormatFlags flags =
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix;

            TextRenderer.DrawText(
                graphics,
                Text,
                Font,
                textBounds,
                Enabled ? _palette.TextPrimary : _palette.TextMuted,
                flags);
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled)
            {
                Checked = !Checked;
            }

            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e != null && e.KeyCode == Keys.Space)
            {
                if (Enabled)
                {
                    Checked = !Checked;
                }

                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (AutoSize)
            {
                Size = GetPreferredSize(Size.Empty);
            }

            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (AutoSize)
            {
                Size = GetPreferredSize(Size.Empty);
            }

            Invalidate();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            if (AutoSize)
            {
                Size = GetPreferredSize(Size.Empty);
            }

            Invalidate();
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
            Invalidate();
            base.OnLostFocus(e);
        }
    }
}
