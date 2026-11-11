namespace gostGUI
{
    /// <summary>
    /// 主题感知控件契约。ThemeManager 递归遍历控件树时只调用这个接口，
    /// 控件自己决定哪些属性从 Palette 的哪个语义色取值。
    /// </summary>
    public interface IThemed
    {
        /// <summary>应用一套调色板；实现必须可重复调用且不改变布局/选中状态。</summary>
        void ApplyTheme(Palette palette);
    }
}
