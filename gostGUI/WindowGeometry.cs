using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>窗口状态的放置结果：正常恢复 / 居中（未保存位置）/ 越界回落居中默认尺寸。</summary>
    internal enum WindowPlacement
    {
        Restored,
        Centered,
        OffscreenReset
    }

    /// <summary>
    /// 窗口几何的纯函数集合：DIP ↔ 设备像素、非法值回落、以及按屏幕工作区夹取。
    /// 所有方法都不碰 UI，便于自检在不同 DPI 上做往返断言。
    /// </summary>
    internal static class WindowGeometry
    {
        /// <summary>历史/绝对可恢复下限（DIP）。实际下限会按工作区与最小窗口尺寸放宽，见 MinimumDipFor。</summary>
        public const int MinWidth = 640;

        public const int MinHeight = 480;

        /// <summary>设计最小窗口尺寸（DIP），与 FormMain 的 MinimumSize 使用同一来源。</summary>
        public const int DesignMinimumWidthDip = 880;

        public const int DesignMinimumHeightDip = 560;

        /// <summary>超过这个 DIP 尺寸视为损坏配置，回落默认值。</summary>
        public const int MaxSize = 16384;

        /// <summary>坐标的合理范围；超出视为损坏配置（多显示器允许负坐标）。</summary>
        public const int MaxAbsolutePosition = 32768;

        public const int DefaultDpi = 96;

        public const int MinDpi = 48;

        public const int MaxDpi = 480;

        /// <summary>把 DPI 夹到合理范围；损坏值按 96 处理（旧配置没有 dpi 字段）。</summary>
        public static int NormalizeDpi(int dpi)
        {
            return dpi >= MinDpi && dpi <= MaxDpi ? dpi : DefaultDpi;
        }

        /// <summary>把设备像素换算成 DIP（四舍五入，远离零）。</summary>
        public static int PixelsToDip(int pixels, int dpi)
        {
            int safeDpi = NormalizeDpi(dpi);
            return (int)Math.Round(pixels * 96.0 / safeDpi, MidpointRounding.AwayFromZero);
        }

        /// <summary>把 DIP 换算成设备像素（四舍五入，远离零）。</summary>
        public static int DipToPixels(int dip, int dpi)
        {
            int safeDpi = NormalizeDpi(dpi);
            return (int)Math.Round(dip * safeDpi / 96.0, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 当前工作区允许的最小客户区（DIP）：设计下限 880×560 先夹到工作区，
        /// 再减去外框得到客户区，最后与历史下限 640×480 取较小值。
        /// 这样窗口在很小的桌面上被放宽后，保存的几何不会被 Normalize 误判为损坏。
        /// </summary>
        public static Size MinimumDipFor(Rectangle workingAreaPixels, int dpi, Size chromePixels)
        {
            int safeDpi = NormalizeDpi(dpi);
            int workWidthDip = Math.Max(1, PixelsToDip(Math.Max(1, workingAreaPixels.Width), safeDpi));
            int workHeightDip = Math.Max(1, PixelsToDip(Math.Max(1, workingAreaPixels.Height), safeDpi));
            int chromeWidthDip = Math.Max(0, PixelsToDip(Math.Max(0, chromePixels.Width), safeDpi));
            int chromeHeightDip = Math.Max(0, PixelsToDip(Math.Max(0, chromePixels.Height), safeDpi));

            int outerMinimumWidth = Math.Min(DesignMinimumWidthDip, workWidthDip);
            int outerMinimumHeight = Math.Min(DesignMinimumHeightDip, workHeightDip);
            int clientMinimumWidth = Math.Max(1, outerMinimumWidth - chromeWidthDip);
            int clientMinimumHeight = Math.Max(1, outerMinimumHeight - chromeHeightDip);

            return new Size(
                Math.Min(MinWidth, clientMinimumWidth),
                Math.Min(MinHeight, clientMinimumHeight));
        }

        /// <summary>
        /// 校验并规范化保存的窗口状态：负数/零/超大尺寸、越界坐标、只保存了半对坐标
        /// 一律回落默认值；合法的多显示器负坐标原样保留。默认下限为 640×480 DIP。
        /// </summary>
        public static ConfigWindow Normalize(ConfigWindow source)
        {
            return Normalize(source, MinWidth, MinHeight);
        }

        /// <summary>用指定客户区下限（DIP）规范化保存的窗口状态。</summary>
        public static ConfigWindow Normalize(ConfigWindow source, int minimumWidthDip, int minimumHeightDip)
        {
            if (source == null)
            {
                return CreateDefault(DefaultDpi);
            }

            int dpi = NormalizeDpi(source.Dpi);
            int minWidth = Math.Max(1, minimumWidthDip);
            int minHeight = Math.Max(1, minimumHeightDip);

            bool invalidSize =
                source.W < minWidth ||
                source.H < minHeight ||
                source.W > MaxSize ||
                source.H > MaxSize;

            bool halfUnset = (source.X == ConfigWindow.DefaultX) != (source.Y == ConfigWindow.DefaultY);
            bool invalidPosition =
                halfUnset ||
                source.X < -MaxAbsolutePosition ||
                source.X > MaxAbsolutePosition ||
                source.Y < -MaxAbsolutePosition ||
                source.Y > MaxAbsolutePosition;

            if (invalidSize || invalidPosition)
            {
                return CreateDefault(dpi);
            }

            return new ConfigWindow
            {
                W = source.W,
                H = source.H,
                X = source.X,
                Y = source.Y,
                Max = source.Max,
                Dpi = dpi
            };
        }

        /// <summary>把设备像素几何换算成可持久化的 DIP 状态；非法几何回落默认值。</summary>
        public static ConfigWindow Capture(Size clientSizePixels, Point locationPixels, bool maximized, int dpi)
        {
            return Capture(clientSizePixels, locationPixels, maximized, dpi, MinWidth, MinHeight);
        }

        /// <summary>把设备像素几何换算成 DIP 状态，并记录捕获时的 DPI。</summary>
        public static ConfigWindow Capture(
            Size clientSizePixels,
            Point locationPixels,
            bool maximized,
            int dpi,
            int minimumWidthDip,
            int minimumHeightDip)
        {
            int safeDpi = NormalizeDpi(dpi);
            var state = new ConfigWindow
            {
                W = PixelsToDip(clientSizePixels.Width, safeDpi),
                H = PixelsToDip(clientSizePixels.Height, safeDpi),
                X = PixelsToDip(locationPixels.X, safeDpi),
                Y = PixelsToDip(locationPixels.Y, safeDpi),
                Max = maximized,
                Dpi = safeDpi
            };

            return Normalize(state, minimumWidthDip, minimumHeightDip);
        }

        /// <summary>
        /// 计算恢复时的客户区像素尺寸与窗口位置：
        /// 保存的位置与任何屏幕都不相交 → 居中 + 默认尺寸；只缺位置 → 居中但保留尺寸；
        /// 否则夹到目标屏幕的工作区内。保存的 DIP 用捕获时的 DPI 换算，
        /// 默认值用当前 DPI 换算。所有屏幕/工作区矩形均为设备像素。
        /// </summary>
        public static Size Place(
            ConfigWindow saved,
            int dpi,
            IReadOnlyList<Rectangle> screenBoundsPixels,
            IReadOnlyList<Rectangle> workingAreaPixels,
            out Point locationPixels,
            out WindowPlacement placement)
        {
            return Place(
                saved,
                dpi,
                screenBoundsPixels,
                workingAreaPixels,
                MinWidth,
                MinHeight,
                out locationPixels,
                out placement);
        }

        /// <summary>带客户区下限（DIP）的 Place 重载。</summary>
        public static Size Place(
            ConfigWindow saved,
            int dpi,
            IReadOnlyList<Rectangle> screenBoundsPixels,
            IReadOnlyList<Rectangle> workingAreaPixels,
            int minimumWidthDip,
            int minimumHeightDip,
            out Point locationPixels,
            out WindowPlacement placement)
        {
            ConfigWindow normalized = Normalize(saved, minimumWidthDip, minimumHeightDip);
            int currentDpi = NormalizeDpi(dpi);
            int captureDpi = NormalizeDpi(normalized.Dpi);

            Rectangle fallbackWork = FirstWorkArea(workingAreaPixels, screenBoundsPixels);
            Size defaultSize = new Size(
                DipToPixels(ConfigWindow.DefaultWidth, currentDpi),
                DipToPixels(ConfigWindow.DefaultHeight, currentDpi));
            Size requestedSize = new Size(
                DipToPixels(normalized.W, captureDpi),
                DipToPixels(normalized.H, captureDpi));

            if (normalized.X == ConfigWindow.DefaultX || normalized.Y == ConfigWindow.DefaultY)
            {
                Size centeredSize = ClampSize(requestedSize, fallbackWork);
                placement = WindowPlacement.Centered;
                locationPixels = CenterIn(fallbackWork, centeredSize);
                return centeredSize;
            }

            var requestedLocation = new Point(
                DipToPixels(normalized.X, captureDpi),
                DipToPixels(normalized.Y, captureDpi));
            var desired = new Rectangle(requestedLocation, requestedSize);

            int screenIndex = FindScreen(desired, screenBoundsPixels);
            if (screenIndex < 0)
            {
                Size resetSize = ClampSize(defaultSize, fallbackWork);
                placement = WindowPlacement.OffscreenReset;
                locationPixels = CenterIn(fallbackWork, resetSize);
                return resetSize;
            }

            Rectangle work = GetWorkingArea(workingAreaPixels, screenBoundsPixels, screenIndex);
            Size clampedSize = ClampSize(requestedSize, work);
            int x = Math.Max(work.Left, Math.Min(requestedLocation.X, work.Right - clampedSize.Width));
            int y = Math.Max(work.Top, Math.Min(requestedLocation.Y, work.Bottom - clampedSize.Height));

            placement = WindowPlacement.Restored;
            locationPixels = new Point(x, y);
            return clampedSize;
        }

        private static ConfigWindow CreateDefault(int dpi)
        {
            return new ConfigWindow
            {
                W = ConfigWindow.DefaultWidth,
                H = ConfigWindow.DefaultHeight,
                X = ConfigWindow.DefaultX,
                Y = ConfigWindow.DefaultY,
                Max = false,
                Dpi = NormalizeDpi(dpi)
            };
        }

        private static Rectangle FirstWorkArea(
            IReadOnlyList<Rectangle> workingAreaPixels,
            IReadOnlyList<Rectangle> screenBoundsPixels)
        {
            if (workingAreaPixels != null && workingAreaPixels.Count > 0)
            {
                return workingAreaPixels[0];
            }

            if (screenBoundsPixels != null && screenBoundsPixels.Count > 0)
            {
                return screenBoundsPixels[0];
            }

            return Screen.PrimaryScreen != null
                ? Screen.PrimaryScreen.WorkingArea
                : new Rectangle(0, 0, 1024, 768);
        }

        private static int FindScreen(Rectangle desired, IReadOnlyList<Rectangle> screenBoundsPixels)
        {
            if (screenBoundsPixels == null)
            {
                return -1;
            }

            for (int i = 0; i < screenBoundsPixels.Count; i++)
            {
                if (screenBoundsPixels[i].IntersectsWith(desired))
                {
                    return i;
                }
            }

            return -1;
        }

        private static Rectangle GetWorkingArea(
            IReadOnlyList<Rectangle> workingAreaPixels,
            IReadOnlyList<Rectangle> screenBoundsPixels,
            int screenIndex)
        {
            if (workingAreaPixels != null && screenIndex < workingAreaPixels.Count)
            {
                return workingAreaPixels[screenIndex];
            }

            if (screenBoundsPixels != null && screenIndex < screenBoundsPixels.Count)
            {
                return screenBoundsPixels[screenIndex];
            }

            return FirstWorkArea(workingAreaPixels, screenBoundsPixels);
        }

        private static Size ClampSize(Size requested, Rectangle work)
        {
            int width = Math.Max(1, Math.Min(requested.Width, Math.Max(1, work.Width)));
            int height = Math.Max(1, Math.Min(requested.Height, Math.Max(1, work.Height)));
            return new Size(width, height);
        }

        private static Point CenterIn(Rectangle work, Size size)
        {
            return new Point(
                work.Left + Math.Max(0, (work.Width - size.Width) / 2),
                work.Top + Math.Max(0, (work.Height - size.Height) / 2));
        }
    }
}
