using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 主题语义色。控件只允许从这里取色，不再各自写死颜色值。
    /// 浅色/深色两套由静态工厂提供，强调色在两套里保持一致。
    /// </summary>
    public sealed class Palette
    {
        private static readonly Palette LightInstance = CreateLight();
        private static readonly Palette DarkInstance = CreateDark();

        private Palette(
            Color surface,
            Color surfaceAlt,
            Color border,
            Color textPrimary,
            Color textMuted,
            Color accent,
            Color accentText,
            Color buttonBg,
            Color buttonHover,
            Color buttonPress,
            Color success,
            Color danger,
            Color logBg,
            Color logStdout,
            Color logStderr,
            Color logSystem)
        {
            Surface = surface;
            SurfaceAlt = surfaceAlt;
            Border = border;
            TextPrimary = textPrimary;
            TextMuted = textMuted;
            Accent = accent;
            AccentText = accentText;
            ButtonBg = buttonBg;
            ButtonHover = buttonHover;
            ButtonPress = buttonPress;
            Success = success;
            Danger = danger;
            LogBg = logBg;
            LogStdout = logStdout;
            LogStderr = logStderr;
            LogSystem = logSystem;
        }

        public Color Surface { get; }

        public Color SurfaceAlt { get; }

        public Color Border { get; }

        public Color TextPrimary { get; }

        public Color TextMuted { get; }

        public Color Accent { get; }

        public Color AccentText { get; }

        public Color ButtonBg { get; }

        public Color ButtonHover { get; }

        public Color ButtonPress { get; }

        public Color Success { get; }

        public Color Danger { get; }

        public Color LogBg { get; }

        public Color LogStdout { get; }

        public Color LogStderr { get; }

        public Color LogSystem { get; }

        /// <summary>浅色主题：接近白底、深灰正文，强调色固定为 #3B82F6。</summary>
        public static Palette Light
        {
            get { return LightInstance; }
        }

        /// <summary>深色主题：近黑底、浅灰正文，强调色与浅色主题相同。</summary>
        public static Palette Dark
        {
            get { return DarkInstance; }
        }

        private static Palette CreateLight()
        {
            return new Palette(
                surface: Color.FromArgb(0xF5, 0xF6, 0xF8),
                surfaceAlt: Color.FromArgb(0xFF, 0xFF, 0xFF),
                border: Color.FromArgb(0xD9, 0xDC, 0xE1),
                textPrimary: Color.FromArgb(0x1B, 0x1F, 0x24),
                textMuted: Color.FromArgb(0x6B, 0x72, 0x80),
                accent: Color.FromArgb(0x3B, 0x82, 0xF6),
                accentText: Color.FromArgb(0xFF, 0xFF, 0xFF),
                buttonBg: Color.FromArgb(0xFF, 0xFF, 0xFF),
                buttonHover: Color.FromArgb(0xF0, 0xF3, 0xF7),
                buttonPress: Color.FromArgb(0xE5, 0xEA, 0xF0),
                success: Color.FromArgb(0x16, 0xA3, 0x4A),
                danger: Color.FromArgb(0xDC, 0x26, 0x26),
                logBg: Color.FromArgb(0xF8, 0xFA, 0xFC),
                logStdout: Color.FromArgb(0x1B, 0x1F, 0x24),
                logStderr: Color.FromArgb(0xB9, 0x1C, 0x1C),
                logSystem: Color.FromArgb(0x25, 0x63, 0xEB));
        }

        private static Palette CreateDark()
        {
            return new Palette(
                surface: Color.FromArgb(0x15, 0x18, 0x1C),
                surfaceAlt: Color.FromArgb(0x1D, 0x21, 0x27),
                border: Color.FromArgb(0x2C, 0x32, 0x3A),
                textPrimary: Color.FromArgb(0xE6, 0xE9, 0xEE),
                textMuted: Color.FromArgb(0x9A, 0xA4, 0xB2),
                accent: Color.FromArgb(0x3B, 0x82, 0xF6),
                accentText: Color.FromArgb(0xFF, 0xFF, 0xFF),
                buttonBg: Color.FromArgb(0x24, 0x2A, 0x31),
                buttonHover: Color.FromArgb(0x2C, 0x33, 0x3C),
                buttonPress: Color.FromArgb(0x35, 0x3D, 0x48),
                success: Color.FromArgb(0x22, 0xC5, 0x5E),
                danger: Color.FromArgb(0xEF, 0x44, 0x44),
                logBg: Color.FromArgb(0x0B, 0x0D, 0x10),
                logStdout: Color.FromArgb(0xD7, 0xDC, 0xE3),
                logStderr: Color.FromArgb(0xF8, 0x71, 0x71),
                logSystem: Color.FromArgb(0x60, 0xA5, 0xFA));
        }
    }

    /// <summary>
    /// 自绘控件共用的绘制辅助：DPI 缩放、颜色混合与圆角路径。
    /// 所有尺寸以 96 DPI 为基线，按 Control.DeviceDpi 等比放大。
    /// </summary>
    internal static class ThemePaint
    {
        public const int BaselineDpi = 96;

        /// <summary>
        /// 自检/截图用的 DPI 覆盖：非 0 时所有自绘尺寸按该 DPI 缩放。
        /// 生产路径始终是 0，按控件真实 DeviceDpi 缩放。
        /// </summary>
        internal static int DpiOverride { get; set; }

        /// <summary>把 96 DPI 下的逻辑像素换算成当前控件的设备像素。</summary>
        public static int Scale(Control control, int value)
        {
            float dpi = BaselineDpi;
            if (DpiOverride > 0)
            {
                dpi = DpiOverride;
            }
            else if (control != null)
            {
                try
                {
                    dpi = control.DeviceDpi;
                }
                catch (Exception)
                {
                    dpi = BaselineDpi;
                }
            }

            if (dpi <= 0f)
            {
                dpi = BaselineDpi;
            }

            return Math.Max(0, (int)Math.Round(value * dpi / BaselineDpi, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// 控件圆角/边缘应透出的底色：优先父控件当前背景，父控件不存在或透明时回落到主题 Surface。
        /// </summary>
        public static Color ParentSurface(Control control, Palette palette)
        {
            if (control != null && control.Parent != null)
            {
                Color parent = control.Parent.BackColor;
                if (parent.A > 0)
                {
                    return parent;
                }
            }

            return palette != null ? palette.Surface : Color.Transparent;
        }

        /// <summary>按比例混合两个调色板颜色；amount=0 取 from，amount=1 取 to。</summary>
        public static Color Blend(Color from, Color to, float amount)
        {
            if (amount <= 0f)
            {
                return from;
            }

            if (amount >= 1f)
            {
                return to;
            }

            int alpha = (int)Math.Round(from.A + ((to.A - from.A) * amount));
            int red = (int)Math.Round(from.R + ((to.R - from.R) * amount));
            int green = (int)Math.Round(from.G + ((to.G - from.G) * amount));
            int blue = (int)Math.Round(from.B + ((to.B - from.B) * amount));
            return Color.FromArgb(ClampByte(alpha), ClampByte(red), ClampByte(green), ClampByte(blue));
        }

        /// <summary>生成圆角矩形路径；半径会按控件短边自动收紧，宽高为 0 时也能安全返回。</summary>
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            if (diameter <= 1)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        /// <summary>填充圆角矩形并画 1px 描边；描边为透明时只填充。</summary>
        public static void FillRoundedBox(
            Graphics graphics,
            Rectangle bounds,
            int radius,
            Color fill,
            Color border)
        {
            if (graphics == null || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = RoundedRect(bounds, radius))
            {
                if (fill.A > 0)
                {
                    using (var brush = new SolidBrush(fill))
                    {
                        graphics.FillPath(brush, path);
                    }
                }

                if (border.A > 0)
                {
                    using (var pen = new Pen(border, 1f))
                    {
                        graphics.DrawPath(pen, path);
                    }
                }
            }
        }

        private static int ClampByte(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 255)
            {
                return 255;
            }

            return value;
        }
    }
}
