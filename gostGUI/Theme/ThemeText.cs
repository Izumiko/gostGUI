using System;
using System.Drawing;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 文本测量的 DPI 缝。生产路径直接用 TextRenderer.MeasureText（与 TextRenderer.DrawText 同一套 GDI 度量）；
    /// 自检/截图会把同一进程里先渲染的 96 DPI 测定缓存带进 144/192 DPI 的模拟，因此快照模式
    /// 改用显式 DPI 的 GDI+ 度量，保证控件 AutoSize 与当前模拟 DPI 一致。
    /// </summary>
    internal static class ThemeText
    {
        /// <summary>非 0 时用该 DPI 做 GDI+ 度量（快照/自检）；0 表示生产路径的 TextRenderer。</summary>
        internal static int MeasureDpiOverride { get; set; }

        public static Size Measure(string text, Font font)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Size.Empty;
            }

            if (MeasureDpiOverride <= 0)
            {
                return TextRenderer.MeasureText(text, font);
            }

            return MeasureAtDpi(text, font, MeasureDpiOverride);
        }

        private static Size MeasureAtDpi(string text, Font font, int dpi)
        {
            try
            {
                using (var bitmap = new Bitmap(1, 1))
                {
                    bitmap.SetResolution(dpi, dpi);
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        SizeF measured = graphics.MeasureString(text, font);
                        return new Size(
                            Math.Max(1, (int)Math.Ceiling(measured.Width)),
                            Math.Max(1, (int)Math.Ceiling(measured.Height)));
                    }
                }
            }
            catch (Exception)
            {
                // GDI+ 不可用时退回 GDI 度量。
                return TextRenderer.MeasureText(text, font);
            }
        }
    }
}

