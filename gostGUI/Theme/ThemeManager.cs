using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace gostGUI
{
    /// <summary>
    /// 主题管理器：持有当前模式/调色板，递归把主题应用到实现了 IThemed 的控件。
    /// System 模式读取 HKCU 的 AppsUseLightTheme；测试可通过 systemPrefersDarkOverride 注入探针。
    /// </summary>
    public sealed class ThemeManager
    {
        private const string PersonalizeKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private const string AppsUseLightThemeValue = "AppsUseLightTheme";

        private ThemeMode _mode = ThemeMode.System;
        private Palette _current;

        /// <summary>测试注入点：非 null 时替代注册表读取（返回 true 表示系统偏好深色）。</summary>
        internal Func<bool> systemPrefersDarkOverride;

        public ThemeManager()
        {
            _current = ResolvePalette();
        }

        /// <summary>主题模式；每次赋值都立即重新解析并触发 Changed。</summary>
        public ThemeMode Mode
        {
            get { return _mode; }
            set
            {
                _mode = value;
                _current = ResolvePalette();
                Changed?.Invoke();
            }
        }

        /// <summary>当前生效的调色板（System 模式下解析自系统偏好）。</summary>
        public Palette Current
        {
            get { return _current; }
        }

        /// <summary>主题变化通知；订阅者负责对自己的窗口调用 ApplyTo。</summary>
        public event Action Changed;

        /// <summary>递归应用当前主题；同一棵树可安全重复调用。</summary>
        public void ApplyTo(Control root)
        {
            if (root == null)
            {
                return;
            }

            if (root.IsDisposed)
            {
                return;
            }

            var themed = root as IThemed;
            if (themed != null)
            {
                themed.ApplyTheme(_current);
            }

            foreach (Control child in root.Controls)
            {
                ApplyTo(child);
            }
        }

        /// <summary>
        /// 读取 HKCU\...\Themes\Personalize\AppsUseLightTheme：0 表示深色，1 表示浅色。
        /// 值缺失、类型异常或读取失败一律按浅色处理。
        /// </summary>
        public static bool SystemPrefersDark()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    object value = key.GetValue(AppsUseLightThemeValue);
                    if (value == null)
                    {
                        return false;
                    }

                    int appsUseLightTheme;
                    if (!int.TryParse(Convert.ToString(value), out appsUseLightTheme))
                    {
                        return false;
                    }

                    return appsUseLightTheme == 0;
                }
            }
            catch (Exception)
            {
                // 旧系统 / 注册表不可读：静默按浅色处理。
                return false;
            }
        }

        /// <summary>
        /// 让系统标题栏跟随深色（Win10 1809+ 的 DWMWA_USE_IMMERSIVE_DARK_MODE=20）。
        /// 老系统返回失败码也无妨，这里静默忽略。
        /// </summary>
        public static void ApplyDarkTitleBar(Form form)
        {
            if (form == null)
            {
                return;
            }

            try
            {
                IntPtr handle = form.Handle;
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                int enabled = 1;
                DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            }
            catch (Exception)
            {
                // 旧版 Windows 没有这个属性，或窗口尚未创建句柄。
            }
        }

        private Palette ResolvePalette()
        {
            switch (_mode)
            {
                case ThemeMode.Light:
                    return Palette.Light;
                case ThemeMode.Dark:
                    return Palette.Dark;
                default:
                    Func<bool> probe = systemPrefersDarkOverride;
                    bool prefersDark = probe != null ? probe() : SystemPrefersDark();
                    return prefersDark ? Palette.Dark : Palette.Light;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref int attributeValue,
            int attributeSize);
    }
}
