using System;
using System.Drawing;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 主题化只读日志区：外层 Panel 无边框，内层 RichTextBox 使用等宽字体、
    /// 主题背景/正文色；AppendColored 逐段着色，并尽量保持用户当前的滚动位置。
    /// </summary>
    public sealed class ThemedRichTextBox : Panel, IThemed
    {
        private readonly RichTextBox _inner;
        private Palette _palette = Palette.Light;
        private Padding _defaultPadding = new Padding(0);
        private bool _hasContent;

        public ThemedRichTextBox()
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
            _inner = new RichTextBox
            {
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                HideSelection = false,
                ReadOnly = true,
                Font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point),
                Dock = DockStyle.Fill
            };
            _inner.TextChanged += InnerTextChanged;
            _inner.HandleCreated += (sender, args) =>
                WindowTheme.ApplyScrollbars(_inner, ReferenceEquals(_palette, Palette.Dark));
            Controls.Add(_inner);

            ApplyTheme(_palette);
        }

        public RichTextBox Inner
        {
            get { return _inner; }
        }

        public override string Text
        {
            get { return _inner.Text; }
            set { _inner.Text = value ?? string.Empty; }
        }

        public void ApplyTheme(Palette palette)
        {
            if (palette == null)
            {
                return;
            }

            _palette = palette;
            BackColor = palette.LogBg;
            ForeColor = palette.LogStdout;
            _inner.BackColor = palette.LogBg;
            WindowTheme.ApplyScrollbars(_inner, ReferenceEquals(palette, Palette.Dark));

            // 已有 run 的 SelectionColor 是权威颜色：主题切换时不能重设 ForeColor，
            // 否则 RichTextBox 会把所有 run 压成同一种颜色。空日志才需要写入主题默认色；
            // 已有内容的重新着色由宿主在主题切换时自行重渲染。
            if (!_hasContent)
            {
                _inner.ForeColor = palette.LogStdout;
            }

            Invalidate();
        }

        /// <summary>
        /// 在末尾追加一段带颜色的文本。用户在往上翻日志时不抢滚动条：
        /// 记录追加前的选区与首个可见字符，追加后恢复。
        /// </summary>
        public void AppendColored(string text, Color color)
        {
            if (string.IsNullOrEmpty(text) || _inner.IsDisposed)
            {
                return;
            }

            int savedSelectionStart = _inner.SelectionStart;
            int savedSelectionLength = _inner.SelectionLength;
            bool wasAtBottom = IsScrolledToBottom();
            int firstVisibleChar = GetFirstVisibleCharIndex();

            _inner.SelectionStart = _inner.TextLength;
            _inner.SelectionLength = 0;
            _inner.SelectionColor = color.IsEmpty ? _palette.LogStdout : color;
            _inner.AppendText(text);
            _inner.SelectionColor = _palette.LogStdout;

            if (wasAtBottom)
            {
                _inner.SelectionStart = _inner.TextLength;
                _inner.SelectionLength = 0;
                _inner.ScrollToCaret();
                return;
            }

            _inner.SelectionStart = savedSelectionStart;
            _inner.SelectionLength = savedSelectionLength;

            if (firstVisibleChar >= 0)
            {
                // 先滚回原来的首个可见字符，再把选区恢复到用户原位；两者都不再触发滚动。
                _inner.SelectionStart = firstVisibleChar;
                _inner.SelectionLength = 0;
                _inner.ScrollToCaret();
                _inner.SelectionStart = savedSelectionStart;
                _inner.SelectionLength = savedSelectionLength;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (e == null || e.Graphics == null)
            {
                return;
            }

            using (var brush = new SolidBrush(_palette.LogBg))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            UpdateDpiMetrics();
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateDpiMetrics();
            Invalidate();
        }

        private Padding CreateDefaultPadding()
        {
            return new Padding(
                ThemePaint.Scale(this, 6),
                ThemePaint.Scale(this, 6),
                ThemePaint.Scale(this, 6),
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

        private void InnerTextChanged(object sender, EventArgs e)
        {
            _hasContent = _inner.TextLength > 0;
            base.OnTextChanged(e);
        }

        private bool IsScrolledToBottom()
        {
            if (_inner.TextLength == 0)
            {
                return true;
            }

            try
            {
                Point lastChar = _inner.GetPositionFromCharIndex(_inner.TextLength - 1);
                return lastChar.Y + _inner.Font.Height <= _inner.ClientSize.Height;
            }
            catch (Exception)
            {
                // 句柄还没建好或 RichTextBox 正在布局：按“贴底”处理，保证新日志可见。
                return true;
            }
        }

        private int GetFirstVisibleCharIndex()
        {
            try
            {
                return _inner.GetCharIndexFromPosition(new Point(1, 1));
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
