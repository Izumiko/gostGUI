// =====================================================================================
// SelfTest.Native.cs —— 测试专用 P/Invoke 探针（TokenProbe / PipeLabelProbe）与 INPUT 结构断言。
// 本文件是 SelfTest 的 partial：成员由 SelfTest.cs 原样搬来（用例名/断言/注册顺序未改）。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using gostGUI.Native;
using gostGUI.Shared;
using Microsoft.Win32.SafeHandles;

namespace gostGUI
{
    internal static partial class SelfTest
    {
        // 测试专用 interop：本文件里的 TokenProbe / PipeLabelProbe / ConsoleOutput.CreateFile 探针
        // 刻意留在自检套件内、不从 Native\ 暴露，避免生产程序集提供任何测试钩子。
        /// <summary>用例专用的令牌探针：与 ElevationManager 的实现各自独立声明同一组 P/Invoke。</summary>
        private static class TokenProbe
        {
            private const int TokenElevationClass = 20;
            private const int TokenPrivilegesClass = 3;
            private const uint TokenQuery = 0x0008;
            private const uint ProcessQueryLimitedInformation = 0x1000;

            /// <summary>SE_PRIVILEGE_ENABLED：特权已启用。</summary>
            private const uint SePrivilegeEnabled = 0x00000002;

            [StructLayout(LayoutKind.Sequential)]
            private struct TokenElevation
            {
                public int TokenIsElevated;
            }

            public static bool IsProcessElevated()
            {
                return ReadTokenElevation(GetCurrentProcess()) ?? false;
            }

            /// <summary>
            /// 查询指定进程的令牌是否提权。进程已退出、拒绝访问或查询失败时返回 null——
            /// 调用方必须把 null 当作“证据不足”，不能当成“未提权”。
            /// </summary>
            public static bool? TryIsProcessElevated(int pid)
            {
                IntPtr process = IntPtr.Zero;
                try
                {
                    process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                    if (process == IntPtr.Zero)
                    {
                        return null;
                    }

                    return ReadTokenElevation(process);
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    if (process != IntPtr.Zero)
                    {
                        CloseHandle(process);
                    }
                }
            }

            /// <summary>读令牌的 TokenElevation；查询失败返回 null。不关闭传入的进程句柄。</summary>
            private static bool? ReadTokenElevation(IntPtr process)
            {
                IntPtr token = IntPtr.Zero;
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    if (!OpenProcessToken(process, TokenQuery, out token))
                    {
                        return null;
                    }

                    int size = Marshal.SizeOf(typeof(TokenElevation));
                    buffer = Marshal.AllocHGlobal(size);
                    uint returned;
                    if (!GetTokenInformation(token, TokenElevationClass, buffer, (uint)size, out returned))
                    {
                        return null;
                    }

                    var elevation = (TokenElevation)Marshal.PtrToStructure(buffer, typeof(TokenElevation));
                    return elevation.TokenIsElevated != 0;
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    if (token != IntPtr.Zero)
                    {
                        CloseHandle(token);
                    }
                }
            }

            /// <summary>
            /// 读本进程令牌里某个特权当前是否**已启用**（SE_PRIVILEGE_ENABLED）。查询失败返回 false。
            /// 用于核查降权路径“临时启用特权后必须恢复”的纪律。
            /// </summary>
            public static bool IsPrivilegeEnabled(string privilegeName)
            {
                long targetLuid;
                if (string.IsNullOrEmpty(privilegeName) || !LookupPrivilegeValue(null, privilegeName, out targetLuid))
                {
                    return false;
                }

                IntPtr token = IntPtr.Zero;
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                    {
                        return false;
                    }

                    uint needed;
                    GetTokenInformation(token, TokenPrivilegesClass, IntPtr.Zero, 0, out needed);
                    if (needed == 0)
                    {
                        return false;
                    }

                    buffer = Marshal.AllocHGlobal((int)needed);
                    if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, needed, out needed))
                    {
                        return false;
                    }

                    // TOKEN_PRIVILEGES：DWORD PrivilegeCount，随后是 PrivilegeCount 个 LUID_AND_ATTRIBUTES（12 字节）。
                    int count = Marshal.ReadInt32(buffer);
                    for (int i = 0; i < count; i++)
                    {
                        int offset = 4 + i * 12;
                        long luid = Marshal.ReadInt64(buffer, offset);
                        uint attributes = (uint)Marshal.ReadInt32(buffer, offset + 8);
                        if (luid == targetLuid && (attributes & SePrivilegeEnabled) != 0)
                        {
                            return true;
                        }
                    }

                    return false;
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    if (token != IntPtr.Zero)
                    {
                        CloseHandle(token);
                    }
                }
            }

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr OpenProcess(uint desiredAccess,
                [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information,
                uint informationLength, out uint returnLength);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool LookupPrivilegeValue(string systemName, string name, out long luid);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);
        }

        /// <summary>
        /// 用例专用的管道强制标签读回探针：与生产写入路径各自独立声明 P/Invoke，
        /// 用 GetSecurityInfo(LABEL_SECURITY_INFORMATION) + SDDL 转换证明标签真的落在管道对象上。
        /// </summary>
        private static class PipeLabelProbe
        {
            private const int SeKernelObject = 6;
            private const uint LabelSecurityInformation = 0x00000010;
            private const uint SddlRevision1 = 1;

            public static string ReadLabelSddl(IntPtr pipeHandle)
            {
                IntPtr owner = IntPtr.Zero;
                IntPtr group = IntPtr.Zero;
                IntPtr dacl = IntPtr.Zero;
                IntPtr sacl = IntPtr.Zero;
                IntPtr descriptor = IntPtr.Zero;
                IntPtr text = IntPtr.Zero;
                try
                {
                    uint result = GetSecurityInfo(pipeHandle, SeKernelObject, LabelSecurityInformation,
                        out owner, out group, out dacl, out sacl, out descriptor);
                    if (result != 0 || descriptor == IntPtr.Zero)
                    {
                        return null;
                    }

                    uint textLength;
                    if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, SddlRevision1,
                            LabelSecurityInformation, out text, out textLength) || text == IntPtr.Zero)
                    {
                        return null;
                    }

                    return Marshal.PtrToStringUni(text);
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    if (text != IntPtr.Zero)
                    {
                        LocalFree(text);
                    }

                    if (descriptor != IntPtr.Zero)
                    {
                        LocalFree(descriptor);
                    }
                }
            }

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
                out IntPtr psidOwner, out IntPtr psidGroup, out IntPtr pDacl, out IntPtr pSacl,
                out IntPtr ppSecurityDescriptor);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
                IntPtr securityDescriptor, uint stringSecurityDescriptorRevision, uint securityInformation,
                out IntPtr stringSecurityDescriptor, out uint stringSecurityDescriptorLength);

            [DllImport("kernel32.dll")]
            private static extern IntPtr LocalFree(IntPtr handle);
        }

        /// <summary>INPUT 在 x64 必须是 40 字节、x86 必须是 28 字节；布局错了 SendInput 会拒绝整批输入。</summary>
        private static void NativeInputStructHasExpectedSize()
        {
            int expected = IntPtr.Size == 8 ? 40 : 28;
            AssertEqual(expected, Marshal.SizeOf<INPUT>());
        }

        /// <summary>
        /// 构造器返回 Alt down + Alt up 两条键盘记录，且未用字段（扫描码/时间戳/附加信息）必须清零：
        /// 只有 wVk 与 dwFlags 允许不同。只读数组，不注入输入。
        /// </summary>
        private static void NativeBuildAltKeyInputsReturnsAltDownThenAltUp()
        {
            INPUT[] inputs = NativeMethods.BuildAltKeyInputs();
            AssertEqual(2, inputs.Length);

            AssertEqual(NativeMethods.InputKeyboard, inputs[0].type);
            AssertEqual((ushort)NativeMethods.VkMenu, inputs[0].u.ki.wVk);
            AssertEqual(0u, inputs[0].u.ki.dwFlags);
            AssertEqual((ushort)0, inputs[0].u.ki.wScan);
            AssertEqual(0u, inputs[0].u.ki.time);
            AssertEqual(IntPtr.Zero, inputs[0].u.ki.dwExtraInfo);

            AssertEqual(NativeMethods.InputKeyboard, inputs[1].type);
            AssertEqual((ushort)NativeMethods.VkMenu, inputs[1].u.ki.wVk);
            AssertEqual(NativeMethods.KeyeventfKeyUp, inputs[1].u.ki.dwFlags);
            AssertEqual((ushort)0, inputs[1].u.ki.wScan);
            AssertEqual(0u, inputs[1].u.ki.time);
            AssertEqual(IntPtr.Zero, inputs[1].u.ki.dwExtraInfo);
        }
    }
}
