namespace gostGUI
{
    /// <summary>输出行的类别：stdout / stderr / 进程管理器自身的系统消息（启动、停止、错误）。</summary>
    public enum LogKind
    {
        Stdout,
        Stderr,
        System
    }
}
