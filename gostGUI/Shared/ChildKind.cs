namespace gostGUI.Shared
{
    /// <summary>可启动程序的类型：.exe/.com 直接启动，.bat/.cmd 需要 cmd.exe 包装后再启动。</summary>
    /// <remarks>
    /// 放在 Shared\：UI 与 runner 两个工程都要编译它（<see cref="LaunchSpec.Kind"/> 用得到），
    /// 而 runner 不编译 UI 的 ProgramResolver.cs，所以不能把枚举留在 UI 工程里。
    /// </remarks>
    public enum ChildKind
    {
        Executable,
        Batch
    }
}
