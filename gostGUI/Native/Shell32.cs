using System;
using System.Runtime.InteropServices;

namespace gostGUI.Native
{
    /// <summary>
    /// shell32.dll 的原生入口与配套结构体。本文件是阶段二 P/Invoke 收口的纯搬运目标：
    /// 签名、<c>EntryPoint</c> 与结构体布局逐字保留。
    /// </summary>
    internal static partial class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ShellExecuteExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpVerb;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpParameters;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpDirectory;

        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpClass;

        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }
}
