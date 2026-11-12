using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace gostGUI
{
    /// <summary>
    /// config.json 的读写与增删改。数据层不弹任何 UI：加载错误见 <see cref="LoadError"/>，
    /// 保存错误见 <see cref="SaveError"/> 与 <see cref="SaveFailed"/>，由界面层统一提示。
    /// <para>
    /// 加载失败后第一次保存前必须先把原文件复制成 config.json.bak（只备份一次、覆盖旧备份），
    /// 绝不静默覆盖用户那份解析不了的配置。
    /// </para>
    /// </summary>
    public class ConfigurationManager
    {
        private const string ConfigFileName = "config.json";
        private const string BackupSuffix = ".bak";

        private readonly string _configFilePath;
        private ConfigData _configData;

        /// <summary>加载失败且磁盘上还有原文件时为 true；第一次成功备份后清零。</summary>
        private bool _backupPending;

        public IReadOnlyList<ConfigItem> Items => _configData.Items.AsReadOnly();

        /// <summary>上次加载失败的原因（中文）；加载成功时为空字符串。</summary>
        public string LoadError { get; private set; }

        /// <summary>上次保存失败的原因（中文）；保存成功时为空字符串。</summary>
        public string SaveError { get; private set; }

        /// <summary>配置文件全路径。</summary>
        public string ConfigFilePath => _configFilePath;

        /// <summary>备份文件全路径（config.json.bak）。</summary>
        public string BackupFilePath => _configFilePath + BackupSuffix;

        /// <summary>全局 UI 主题（跟随系统 / 浅色 / 深色）。</summary>
        public ThemeMode Theme => _configData.Theme;

        /// <summary>窗口大小/位置/最大化状态（DIP）；始终返回非 null 对象。</summary>
        public ConfigWindow WindowState
        {
            get
            {
                if (_configData.Window == null)
                {
                    _configData.Window = new ConfigWindow();
                }

                return _configData.Window;
            }
            set
            {
                if (value != null)
                {
                    _configData.Window = value;
                }
            }
        }

        /// <summary>
        /// 写入窗口状态并立即落盘。返回是否保存成功；失败原因见 <see cref="SaveError"/>
        /// （退出路径据此给出可见提示，而不是静默丢掉刚提交的修改）。
        /// 调用方负责按当前工作区/DPI 规范化几何（FormMain.CaptureWindowState）。
        /// </summary>
        public bool SaveWindowState(ConfigWindow state)
        {
            if (state == null)
            {
                return false;
            }

            WindowState = state;
            return Save();
        }

        /// <summary>保存失败时把中文原因抛给界面层（横幅 / 弹框），绝不静默吞掉。</summary>
        public event Action<string> SaveFailed;

        public event Action<ConfigItem> ItemAdded;
        public event Action<ConfigItem, string> ItemRenamed;
        public event Action<ConfigItem> ItemDeleted;
        public event Action<ConfigItem> ItemUpdated;

        public ConfigurationManager()
            : this(Path.Combine(Common.GetApplicationPath(), ConfigFileName))
        {
        }

        /// <summary>指定配置文件路径（自检用；正式路径由无参构造给出）。</summary>
        internal ConfigurationManager(string configFilePath)
        {
            if (string.IsNullOrWhiteSpace(configFilePath))
            {
                throw new ArgumentException("配置文件路径不能为空。", nameof(configFilePath));
            }

            _configFilePath = configFilePath;
            SaveError = "";
            Load();
        }

        private void Load()
        {
            ConfigLoadResult result = Common.LoadFrom(_configFilePath);
            _configData = result.Data ?? new ConfigData();
            LoadError = result.Ok ? "" : (result.Error ?? "");

            // R7/R8：解析失败时磁盘上的原文件还是用户的数据，第一次保存前必须先备份。
            _backupPending = !result.Ok && File.Exists(_configFilePath);
        }

        /// <summary>整表保存；返回是否成功。失败原因写进 SaveError 并触发 SaveFailed。</summary>
        public bool Save()
        {
            if (_backupPending)
            {
                string backupError;
                if (!TryCreateBackup(out backupError))
                {
                    // 备份不了就别覆盖：用户那份文件还可能能手工修好。
                    Fail(backupError);
                    return false;
                }

                _backupPending = false;
            }

            if (Common.SaveTo(_configData, _configFilePath))
            {
                SaveError = "";
                return true;
            }

            Fail(string.IsNullOrEmpty(Common.LastSaveError) ? "保存配置文件失败。" : Common.LastSaveError);
            return false;
        }

        /// <summary>把当前配置文件复制成 config.json.bak；只保留一份，每次覆盖。</summary>
        private bool TryCreateBackup(out string error)
        {
            error = null;
            try
            {
                File.Copy(_configFilePath, BackupFilePath, true);
                return true;
            }
            catch (Exception ex)
            {
                error = "无法把损坏的配置文件备份为 " + Path.GetFileName(BackupFilePath) + "，已取消保存：" + ex.Message;
                return false;
            }
        }

        private void Fail(string message)
        {
            SaveError = message;
            Action<string> handler = SaveFailed;
            if (handler != null)
            {
                handler(message);
            }
        }

        /// <summary>持久化主题选择（system / light / dark）；值没变时不写盘。</summary>
        public void SetTheme(ThemeMode mode)
        {
            if (_configData.Theme == mode)
            {
                return;
            }

            _configData.Theme = mode;
            Save();
        }

        public ConfigItem AddNewItem()
        {
            string baseName = "new item";
            string newItemName = baseName;
            int counter = 1;
            while (_configData.Items.Exists(item => item.Name == newItemName))
            {
                newItemName = $"{baseName} {counter++}";
            }

            var newItem = new ConfigItem
            {
                Name = newItemName,
                Enable = false, // Start disabled by default
                Program = "gost.exe",
                Args = ""
            };

            _configData.Items.Add(newItem);
            Save();
            ItemAdded?.Invoke(newItem);
            return newItem;
        }

        /// <summary>
        /// 复制一个配置项：新名字为“原名 副本”，重名时依次追加序号；插到源项后面，顺序即列表顺序。
        /// 副本不继承“上次运行状态”（Enable=false），避免复制完就被自动拉起。
        /// </summary>
        public ConfigItem DuplicateItem(ConfigItem source)
        {
            if (source == null)
            {
                return null;
            }

            string baseName = (source.Name ?? "未命名") + " 副本";
            string name = baseName;
            int counter = 2;
            while (_configData.Items.Exists(item => string.Equals(item.Name, name, StringComparison.Ordinal)))
            {
                name = baseName + " " + counter++;
            }

            var copy = new ConfigItem
            {
                Name = name,
                Enable = false,
                Program = source.Program,
                Args = source.Args,
                WorkDir = source.WorkDir,
                RunAsAdmin = source.RunAsAdmin,
                Env = source.Env == null ? null : new Dictionary<string, string>(source.Env)
            };

            int index = _configData.Items.IndexOf(source);
            if (index < 0)
            {
                _configData.Items.Add(copy);
            }
            else
            {
                _configData.Items.Insert(index + 1, copy);
            }

            Save();
            ItemAdded?.Invoke(copy);
            return copy;
        }

        /// <summary>在配置项列表内移动 delta 个位置（-1 上移 / +1 下移）；越界返回 false，顺序写入磁盘。</summary>
        public bool MoveItem(ConfigItem item, int delta)
        {
            if (item == null || delta == 0)
            {
                return false;
            }

            int index = _configData.Items.IndexOf(item);
            if (index < 0)
            {
                return false;
            }

            int target = index + delta;
            if (target < 0 || target >= _configData.Items.Count)
            {
                return false;
            }

            _configData.Items.RemoveAt(index);
            _configData.Items.Insert(target, item);
            Save();
            ItemUpdated?.Invoke(item);
            return true;
        }

        public void DeleteItem(ConfigItem item)
        {
            if (item == null) return;
            _configData.Items.Remove(item);
            Save();
            ItemDeleted?.Invoke(item);
        }

        /// <summary>改名；重名、空名或未变化都返回 false，具体原因见 error（中文，界面层负责提示）。</summary>
        public bool RenameItem(ConfigItem item, string newName, out string error)
        {
            error = null;

            if (item == null)
            {
                error = "配置项不存在。";
                return false;
            }

            if (string.IsNullOrEmpty(newName))
            {
                error = "名称不能为空。";
                return false;
            }

            if (item.Name == newName)
            {
                error = "名称没有变化。";
                return false;
            }

            if (_configData.Items.Exists(i => !ReferenceEquals(i, item) && i.Name == newName))
            {
                error = "已存在同名配置项，请换一个名称。";
                return false;
            }

            item.Name = newName;
            Save();
            ItemRenamed?.Invoke(item, newName);
            return true;
        }

        /// <summary>兼容旧签名：不返回原因（界面层请改用带 out error 的重载）。</summary>
        public bool RenameItem(ConfigItem item, string newName)
        {
            string error;
            return RenameItem(item, newName, out error);
        }

        public void UpdateItemProgram(ConfigItem item, string programPath)
        {
            if (item != null && item.Program != programPath)
            {
                item.Program = programPath;
                Save();
                ItemUpdated?.Invoke(item);
            }
        }

        public void UpdateItem(ConfigItem item)
        {
            if (item != null)
            {
                Save();
                ItemUpdated?.Invoke(item);
            }
        }
    }
}
