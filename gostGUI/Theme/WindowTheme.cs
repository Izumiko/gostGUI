using System;
using System.Windows.Forms;
using gostGUI.Native;

namespace gostGUI
{
    /// <summary>
    /// 原生滚动条的深色主题开关（R44）。
    /// uxtheme!SetWindowTheme 只在句柄已存在时调用；句柄晚于主题应用创建时，
    /// 由各控件自己的 HandleCreated 回调补一次。
    /// </summary>
    internal static class WindowTheme
    {
        private const string DarkScrollbarTheme = "DarkMode_Explorer";

        /// <summary>
        /// 按浅/深色给控件自己的原生滚动条套用系统主题。
        /// 句柄不存在、控件已释放或 API 失败时静默跳过（返回 false）。
        /// </summary>
        public static bool ApplyScrollbars(Control control, bool dark)
        {
            if (control == null || control.IsDisposed || !control.IsHandleCreated)
            {
                return false;
            }

            try
            {
                IntPtr handle = control.Handle;
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                return NativeMethods.SetWindowTheme(handle, dark ? DarkScrollbarTheme : null, null) == 0;
            }
            catch (Exception)
            {
                // 老系统没有 uxtheme 的深色主题名：保持系统默认滚动条即可。
                return false;
            }
        }

        /// <summary>对整棵树套用滚动条主题；句柄尚未创建的控件会被跳过。</summary>
        public static void ApplyScrollbarsRecursive(Control root, bool dark)
        {
            if (root == null || root.IsDisposed)
            {
                return;
            }

            ApplyScrollbars(root, dark);
            foreach (Control child in root.Controls)
            {
                ApplyScrollbarsRecursive(child, dark);
            }
        }
    }
}
