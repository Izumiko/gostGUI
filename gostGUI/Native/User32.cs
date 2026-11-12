using System;
using System.Runtime.InteropServices;

namespace gostGUI.Native
{
    /// <summary>
    /// user32.dll 的原生入口与配套结构体。本文件是阶段二 P/Invoke 收口的纯搬运目标：
    /// 既有签名、<c>CharSet</c>、<c>SetLastError</c>、<c>EntryPoint</c> 与结构体布局逐字保留。
    /// 除搬运外新增了三类内容：<c>keybd_event</c> 的替代品 <c>SendInput</c>、它需要的
    /// <c>INPUT</c> 联合体与相关结构体/常量，以及纯构造函数
    /// <see cref="NativeMethods.BuildAltKeyInputs"/>（托盘还原路径用它们取得前台切换权限）。
    /// </summary>
    internal static partial class NativeMethods
    {
        /// <summary>VK_MENU（Alt）。</summary>
        internal const byte VkMenu = 0x12;

        /// <summary>KEYEVENTF_KEYUP。</summary>
        internal const uint KeyeventfKeyUp = 0x0002;

        /// <summary>INPUT_KEYBOARD。</summary>
        internal const uint InputKeyboard = 1;

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll")]
        internal static extern bool SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        internal static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInputStructure);

        /// <summary>
        /// 构造一次 Alt 击键（down + up）的 INPUT 记录。纯函数：不调用 SendInput、不产生副作用；
        /// 托盘还原路径用它取得 SetForegroundWindow 的前台切换权限。
        /// </summary>
        internal static INPUT[] BuildAltKeyInputs()
        {
            return new[]
            {
                new INPUT
                {
                    type = InputKeyboard,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = VkMenu,
                            wScan = 0,
                            dwFlags = 0,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                },
                new INPUT
                {
                    type = InputKeyboard,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = VkMenu,
                            wScan = 0,
                            dwFlags = KeyeventfKeyUp,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                }
            };
        }
    }

    /// <summary>INPUT：type 后跟一个显式联合体；x64 下 40 字节、x86 下 28 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    /// <summary>INPUT 的联合体：鼠标/键盘/硬件三种记录共享同一块内存。</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
