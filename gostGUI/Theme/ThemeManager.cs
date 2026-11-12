using System;
using System.Windows.Forms;
using gostGUI.Native;
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

        /// <summary>
        /// “跟随系统”模式下重新读取系统偏好。只有解析出的调色板真的变了才触发 Changed；
        /// WM_SETTINGCHANGE 可能因为任意系统设置变化而到达，不能每次都重铺整棵树。
        /// </summary>
        public bool RefreshSystemPreference()
        {
            if (_mode != ThemeMode.System)
            {
                return false;
            }

            Palette resolved = ResolvePalette();
            if (ReferenceEquals(resolved, _current))
            {
                return false;
            }

            _current = resolved;
            Changed?.Invoke();
            return true;
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
        /// 设置标题栏深色/浅色。切回浅色时必须显式写 0，否则标题栏会一直停在上次的深色状态。
        /// </summary>
        public static void ApplyDarkTitleBar(Form form, bool dark)
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

                int enabled = dark ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
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
    }
}
