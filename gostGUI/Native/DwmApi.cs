using System;
using System.Runtime.InteropServices;

namespace gostGUI.Native
{
    /// <summary>
    /// dwmapi.dll 的原生入口（纯搬运；签名、<c>SetLastError</c> 保持原样）。
    /// </summary>
    internal static partial class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref int attributeValue,
            int attributeSize);
    }
}
