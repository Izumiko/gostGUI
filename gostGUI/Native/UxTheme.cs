using System;
using System.Runtime.InteropServices;

namespace gostGUI.Native
{
    /// <summary>
    /// uxtheme.dll 的原生入口（纯搬运；签名、<c>CharSet</c>、<c>SetLastError</c> 保持原样）。
    /// </summary>
    internal static partial class NativeMethods
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList);
    }
}
