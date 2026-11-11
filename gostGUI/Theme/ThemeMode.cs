namespace gostGUI
{
    /// <summary>主题模式：跟随系统 / 浅色 / 深色。</summary>
    public enum ThemeMode
    {
        System,
        Light,
        Dark
    }

    /// <summary>ThemeMode 与 config.json 字符串（system / light / dark）互转；非法或空值一律回落 System。</summary>
    public static class ThemeModes
    {
        public static ThemeMode Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ThemeMode.System;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "light":
                    return ThemeMode.Light;
                case "dark":
                    return ThemeMode.Dark;
                default:
                    return ThemeMode.System;
            }
        }

        public static string ToConfigString(ThemeMode mode)
        {
            switch (mode)
            {
                case ThemeMode.Light:
                    return "light";
                case ThemeMode.Dark:
                    return "dark";
                default:
                    return "system";
            }
        }
    }
}
