namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// net48 的编译器不提供 init 访问器所需的 IsExternalInit 标记类型，
    /// 这里补一个空实现，供 record / init 等较新语言特性使用。
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
