using System;
using System.Runtime.InteropServices;
using System.Text;

namespace gostGUI.Native
{
    /// <summary>
    /// advapi32.dll 的原生入口与配套结构体。本文件是阶段二 P/Invoke 收口的纯搬运目标：
    /// 签名、<c>CharSet</c>、<c>SetLastError</c>、<c>EntryPoint</c> 与结构体布局逐字保留。
    /// </summary>
    internal static partial class NativeMethods
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information,
            uint informationLength, out uint returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess,
            IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(string systemName, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(IntPtr token,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState,
            uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags,
            string applicationName, StringBuilder commandLine, uint creationFlags, IntPtr environment,
            string currentDirectory, ref StartupInfo startupInfo, out ProcessInfo processInformation);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUser(IntPtr token, string applicationName,
            StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
            string currentDirectory, ref StartupInfo startupInfo, out ProcessInfo processInformation);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool InitializeAcl(IntPtr pAcl, uint nAclLength, uint dwAclRevision);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool AddMandatoryAce(IntPtr pAcl, uint dwAceRevision, uint aceFlags,
            uint mandatoryPolicy, IntPtr pLabelSid);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern uint SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
            IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);
    }

    /// <summary>TOKEN_PRIVILEGES（一元素版本）。LUID 是 8 字节，必须按 4 字节对齐打包。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct TokenPrivileges
    {
        public int PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenElevation
    {
        public int TokenIsElevated;
    }
}
