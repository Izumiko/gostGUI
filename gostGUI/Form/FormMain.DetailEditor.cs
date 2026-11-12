// =====================================================================================
// FormMain.DetailEditor.cs —— 详情编辑器：程序/参数/工作目录/环境变量的回填与提交、
// 程序解析提示与防抖、拖放与“浏览…”入口。
// 本文件是 FormMain 的 partial，成员从 FormMain.cs 原样搬来（签名/可见性/行为未改）。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using gostGUI.Native;
using gostGUI.Shared;
using Microsoft.Win32;

namespace gostGUI
{
    public partial class FormMain : Form
    {
        // ---- Task 12b 自检缝：走与生产代码相同的回填 / 提交 / 解析路径 ----

        internal void SetProgramFieldForTest(string program)
        {
            if (_programBox == null)
            {
                return;
            }

            _programHintTimer?.Stop();
            _programBox.Text = program ?? string.Empty;
            CommitProgramField();
            UpdateProgramHint();
        }

        internal string ProgramHintTextForTest => _programHint == null ? string.Empty : _programHint.Text;

        internal Color ProgramHintColorForTest => _programHint == null ? Color.Empty : _programHint.ForeColor;

        internal string EnvFieldTextForTest => _envBox == null ? string.Empty : _envBox.Text;

        internal string ProgramFieldTextForTest => _programBox == null ? string.Empty : _programBox.Text;

        internal string ArgsFieldTextForTest => _argsBox == null ? string.Empty : _argsBox.Text;

        internal string WorkDirFieldTextForTest => _workDirBox == null ? string.Empty : _workDirBox.Text;

        internal bool RunAsAdminFieldForTest => _runAsAdminToggle != null && _runAsAdminToggle.Checked;

        internal void CommitEnvFieldForTest()
        {
            CommitEnvField();
        }

        /// <summary>只改程序框文字、不提交（用于验证 200ms 防抖：提示不应同步刷新）。</summary>
        internal void SetProgramTextOnlyForTest(string program)
        {
            if (_programBox == null)
            {
                return;
            }

            _programHintTimer?.Stop();
            _programBox.Text = program ?? string.Empty;
        }

        /// <summary>触发一次防抖到期（模拟 200ms Timer Tick）。</summary>
        internal void FireProgramHintDebounceForTest()
        {
            _programHintTimer?.Stop();
            ProgramHintTimer_Tick(null, EventArgs.Empty);
        }

        internal void SetArgsTextForTest(string args)
        {
            if (_argsBox != null)
            {
                _argsBox.Text = args ?? string.Empty;
            }
        }

        internal void SetEnvTextForTest(string env)
        {
            if (_envBox != null)
            {
                _envBox.Text = env ?? string.Empty;
            }
        }

        // =====================================================================================
        // 拖放设置程序（程序输入框 + 浏览…共用 ApplyProgramToItem）
        // =====================================================================================

        /// <summary>把程序输入框接成拖放目标（内层 TextBox 允许拖放 + 两个处理器）。</summary>
        public void AttachProgramDropTarget(ThemedTextBox box)
        {
            if (box == null || box.Inner == null)
            {
                return;
            }

            box.Inner.AllowDrop = true;
            box.Inner.DragEnter += ProgramField_DragEnter;
            box.Inner.DragDrop += ProgramField_DragDrop;
        }

        /// <summary>程序输入框的 DragEnter：只接受文件拖入。</summary>
        public void ProgramField_DragEnter(object sender, DragEventArgs e)
        {
            if (e == null || e.Data == null)
            {
                return;
            }

            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        }

        /// <summary>程序输入框的 DragDrop：把拖入的第一个文件写进当前配置项的 Program 并保存。</summary>
        public void ProgramField_DragDrop(object sender, DragEventArgs e)
        {
            string path = GetFirstDroppedFile(e);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            ConfigItem item = GetCurrentConfigItem();
            if (item == null)
            {
                MessageBox.Show(
                    this,
                    "请先在配置列表中选择一个配置项，再拖入程序文件。",
                    "拖放程序",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            ApplyProgramToItem(item, path);
        }

        /// <summary>把路径写进指定配置项的 Program（“浏览…”与拖放都复用这里）。</summary>
        public void ApplyProgramToItem(ConfigItem item, string path)
        {
            if (item == null || string.IsNullOrEmpty(path))
            {
                return;
            }

            string trimmed = path.Trim();
            _configManager.UpdateItemProgram(item, trimmed);

            // 程序框正看着这一项：把新路径同步回输入框（拖放/浏览都是“用户已触碰”的路径）。
            if (ReferenceEquals(item, _detailItem) && _programBox != null &&
                !string.Equals(_programBox.Text, trimmed, StringComparison.Ordinal))
            {
                _programHintTimer?.Stop();
                _detailLoading = true;
                try
                {
                    _programBox.Text = trimmed;
                }
                finally
                {
                    _detailLoading = false;
                }
            }

            UpdateProgramHint();
            UpdateDetailSummary();
            RefreshStatus(item);
        }

        private static string GetFirstDroppedFile(DragEventArgs e)
        {
            if (e == null || e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return null;
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0)
            {
                return null;
            }

            return files[0];
        }

        // =====================================================================================
        // 详情编辑器：程序 / 参数 / 工作目录 / 环境变量 / 以管理员身份运行
        // 规则：切项整组回填；字段失焦/提交即保存，只改用户碰过的那一个字段。
        // =====================================================================================

        /// <summary>把指定项的值整组回填进编辑器；item 为 null 时清空并禁用整组字段。</summary>
        private void LoadDetailEditor(ConfigItem item)
        {
            if (_programBox == null)
            {
                return;
            }

            _detailItem = item;
            bool hasItem = item != null;

            // 回填期间禁止任何“字段变化即保存”的副作用，避免把新项的值写进旧项。
            _programHintTimer?.Stop();
            _lastEnvWarningSignature = null;
            _detailLoading = true;
            try
            {
                _programBox.Enabled = hasItem;
                _programBrowseButton.Enabled = hasItem;
                _argsBox.Enabled = hasItem;
                _workDirBox.Enabled = hasItem;
                _workDirBrowseButton.Enabled = hasItem;
                _workDirFromProgramButton.Enabled = hasItem;
                _envBox.Enabled = hasItem;
                _runAsAdminToggle.Enabled = hasItem;

                _programBox.Text = hasItem ? (item.Program ?? string.Empty) : string.Empty;
                _argsBox.Text = hasItem ? (item.Args ?? string.Empty) : string.Empty;
                _workDirBox.Text = hasItem ? (item.WorkDir ?? string.Empty) : string.Empty;
                _envBox.Text = hasItem ? FormatEnvForEditor(item) : string.Empty;
                _runAsAdminToggle.Checked = hasItem && item.RunAsAdmin;
            }
            finally
            {
                _detailLoading = false;
            }

            UpdateProgramHint();
        }

        /// <summary>
        /// 提交当前编辑器里所有已改动字段（切项前调用）。每个 Commit* 内部都与项当前值比较，
        /// 没改过的字段不会被重写；_detailLoading 期间（正在回填）整体跳过。
        /// </summary>
        private void CommitDetailFields()
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            CommitProgramField();
            ArgsBox_Leave(null, EventArgs.Empty);
            CommitWorkDirField();
            CommitEnvField();

            if (_detailItem.RunAsAdmin != _runAsAdminToggle.Checked)
            {
                _detailItem.RunAsAdmin = _runAsAdminToggle.Checked;
                _configManager.UpdateItem(_detailItem);
            }
        }

        private void ProgramBox_TextChanged(object sender, EventArgs e)
        {
            if (_detailLoading || _programHintTimer == null)
            {
                return;
            }

            // 防抖 ~200ms：停止输入后再解析一次，避免每敲一键都扫 PATH。
            _programHintTimer.Stop();
            _programHintTimer.Start();
        }

        private void ProgramHintTimer_Tick(object sender, EventArgs e)
        {
            _programHintTimer.Stop();
            UpdateProgramHint();
        }

        private void ProgramBox_Leave(object sender, EventArgs e)
        {
            CommitProgramField();
        }

        private void ProgramBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CommitProgramField();
                UpdateProgramHint();
            }
        }

        private void CommitProgramField()
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            string value = (_programBox.Text ?? string.Empty).Trim();
            if (string.Equals(_detailItem.Program ?? string.Empty, value, StringComparison.Ordinal))
            {
                return;
            }

            _configManager.UpdateItemProgram(_detailItem, value);
            UpdateProgramHint();
        }

        /// <summary>
        /// 解析程序框里的当前文本（有效环境 = 父环境 + 本项 env），把结果写进提示行：
        /// 命中显示 → 绝对路径（成功色），失败显示中文错误（危险色）。
        /// </summary>
        private void UpdateProgramHint()
        {
            if (_programHint == null)
            {
                return;
            }

            if (_detailItem == null)
            {
                _programHint.Text = string.Empty;
                _programHint.ForeColor = _themeManager.Current.TextMuted;
                return;
            }

            string program = _programBox == null ? _detailItem.Program : _programBox.Text;
            string hint;
            bool found;
            ResolvedProgram resolved = GetProgramResolution(_detailItem, program);
            if (resolved != null && resolved.Found)
            {
                found = true;
                hint = "→ " + resolved.FullPath;
            }
            else
            {
                found = false;
                hint = resolved != null && !string.IsNullOrEmpty(resolved.Error)
                    ? resolved.Error
                    : "无法解析程序，请检查程序名或完整路径。";
            }

            _programHint.Text = hint;
            _programHint.ForeColor = found ? _themeManager.Current.Success : _themeManager.Current.Danger;
        }

        /// <summary>
        /// 带缓存地解析程序：键 = 程序 + 有效环境签名 + 应用目录。
        /// 同一份输入（例如主题切换、Leave 后再 Enter）不会重复扫 PATH；env/程序一改，键就变。
        /// </summary>
        private ResolvedProgram GetProgramResolution(ConfigItem item, string program)
        {
            string key = BuildProgramResolutionKey(item, program);
            if (_programHintCacheResult != null &&
                string.Equals(_programHintCacheKey, key, StringComparison.Ordinal))
            {
                return _programHintCacheResult;
            }

            ResolvedProgram result = ResolveProgramForEditor(item, program);
            _programHintCacheKey = key;
            _programHintCacheResult = result;
            return result;
        }

        private static string BuildProgramResolutionKey(ConfigItem item, string program)
        {
            var builder = new StringBuilder();
            builder.Append(Common.GetApplicationPath()).Append('\u0000');
            builder.Append(program ?? string.Empty).Append('\u0000');

            if (item != null && item.Env != null)
            {
                // 按键名排序，避免字典枚举顺序不同导致本可命中的键变成未命中。
                var pairs = new List<KeyValuePair<string, string>>(item.Env);
                pairs.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
                foreach (KeyValuePair<string, string> pair in pairs)
                {
                    builder.Append(pair.Key).Append('=').Append(pair.Value ?? string.Empty).Append('\u0001');
                }
            }

            return builder.ToString();
        }

        /// <summary>解析程序名；畸形环境/路径只回落成中文错误，不把异常抖给界面。</summary>
        private static ResolvedProgram ResolveProgramForEditor(ConfigItem item, string program)
        {
            try
            {
                Dictionary<string, string> effective;
                string envError;
                if (!TryBuildEditorEnvironment(item, out effective, out envError))
                {
                    // 环境变量本身无效（例如合并后超 32K）：直接把环境错误作为提示，而不是继续搜程序。
                    return new ResolvedProgram
                    {
                        Found = false,
                        FullPath = string.Empty,
                        Error = envError
                    };
                }

                return ProgramResolver.Resolve(program, Common.GetApplicationPath(), effective);
            }
            catch (Exception ex)
            {
                return new ResolvedProgram
                {
                    Found = false,
                    FullPath = string.Empty,
                    Error = "无法解析程序：" + ex.Message
                };
            }
        }

        /// <summary>构建本项的有效环境（父环境 + env 覆盖）；envError 非空表示环境不可用于启动。</summary>
        private static bool TryBuildEditorEnvironment(
            ConfigItem item,
            out Dictionary<string, string> effective,
            out string error)
        {
            effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = string.Empty;
            try
            {
                List<KeyValuePair<string, string>> parent = ChildEnvironment.SnapshotParentEnvironment();
                effective = EnvBlock.BuildEffective(parent, item == null ? null : item.Env, out error);
                return string.IsNullOrEmpty(error);
            }
            catch (Exception ex)
            {
                error = "构建有效环境失败：" + ex.Message;
                return false;
            }
        }

        private void ArgsBox_Leave(object sender, EventArgs e)
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            string value = _argsBox.Text ?? string.Empty;
            if (string.Equals(_detailItem.Args ?? string.Empty, value, StringComparison.Ordinal))
            {
                return;
            }

            _detailItem.Args = value;
            _configManager.UpdateItem(_detailItem);
        }

        private void WorkDirBox_Leave(object sender, EventArgs e)
        {
            CommitWorkDirField();
        }

        private void CommitWorkDirField()
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            string value = _workDirBox.Text ?? string.Empty;
            if (string.Equals(_detailItem.WorkDir ?? string.Empty, value, StringComparison.Ordinal))
            {
                return;
            }

            _detailItem.WorkDir = value;
            _configManager.UpdateItem(_detailItem);
        }

        private void WorkDirBrowseButton_Click(object sender, EventArgs e)
        {
            if (_detailItem == null)
            {
                return;
            }

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择工作目录";
                dialog.ShowNewFolderButton = true;
                string current = (_workDirBox.Text ?? string.Empty).Trim();
                if (current.Length > 0 && Directory.Exists(current))
                {
                    dialog.SelectedPath = current;
                }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                _workDirBox.Text = dialog.SelectedPath;
                CommitWorkDirField();
            }
        }

        /// <summary>“取程序目录”：按有效环境解析当前程序，把解析到的目录填进工作目录并立即保存。</summary>
        private void WorkDirFromProgramButton_Click(object sender, EventArgs e)
        {
            if (_detailItem == null)
            {
                return;
            }

            ResolvedProgram resolved = GetProgramResolution(_detailItem, _programBox.Text);
            if (resolved == null || !resolved.Found || string.IsNullOrEmpty(resolved.FullPath))
            {
                ShowBanner(
                    "无法确定程序目录：" +
                    (resolved != null && !string.IsNullOrEmpty(resolved.Error)
                        ? resolved.Error
                        : "请先在程序框里填写可解析的程序。"),
                    true);
                return;
            }

            string directory;
            try
            {
                directory = Path.GetDirectoryName(resolved.FullPath);
            }
            catch (Exception ex)
            {
                ShowBanner("无法确定程序目录：" + ex.Message, true);
                return;
            }

            if (string.IsNullOrEmpty(directory))
            {
                ShowBanner("无法确定程序目录：解析结果没有父目录。", true);
                return;
            }

            _workDirBox.Text = directory;
            CommitWorkDirField();
        }

        private void EnvBox_Leave(object sender, EventArgs e)
        {
            CommitEnvField();
        }

        private void CommitEnvField()
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            EnvParseResult parsed = EnvBlock.ParseLines(_envBox.Text);

            // 无效行（缺 '=' / 空键）仍留在输入框里，必须让用户知道哪几行被忽略；
            // 同一组警告只提示一次，避免每次失焦都重复刷系统日志。
            SurfaceEnvWarnings(parsed.Warnings);

            bool changed = !EnvEquals(_detailItem.Env, parsed.Overrides);
            if (changed)
            {
                // 空表存 null：配置里保持“不覆盖”的语义（与旧配置兼容）。
                _detailItem.Env = parsed.Overrides.Count == 0 ? null : parsed.Overrides;
                _configManager.UpdateItem(_detailItem);
            }

            // 环境变量整体无效（合并后超过 Windows 32K 上限）时必须显形，不能静默忽略。
            Dictionary<string, string> effective;
            string envError;
            if (!TryBuildEditorEnvironment(_detailItem, out effective, out envError))
            {
                ShowBanner("环境变量无效：" + envError, true);
            }

            // 环境变了（尤其是 PATH）必须重算程序提示：旧的红色“未找到”不能继续留在屏幕上。
            // 环境没变时走缓存，不会重复扫 PATH。
            if (changed)
            {
                UpdateProgramHint();
            }
        }

        /// <summary>把 ParseLines 的警告写成当前项的系统日志（同一组警告只提示一次）。</summary>
        private void SurfaceEnvWarnings(List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0)
            {
                return;
            }

            string signature = string.Join("\n", warnings);
            if (string.Equals(signature, _lastEnvWarningSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastEnvWarningSignature = signature;
            AppendSystemLine(_detailItem, "环境变量有未保存的行：" + string.Join("；", warnings));
        }

        /// <summary>比较环境变量覆盖表（键忽略大小写、值逐字比较；null 与空表等价）。</summary>
        private static bool EnvEquals(Dictionary<string, string> left, Dictionary<string, string> right)
        {
            int leftCount = left == null ? 0 : left.Count;
            int rightCount = right == null ? 0 : right.Count;
            if (leftCount != rightCount)
            {
                return false;
            }

            if (leftCount == 0)
            {
                return true;
            }

            foreach (KeyValuePair<string, string> pair in left)
            {
                string value;
                if (!right.TryGetValue(pair.Key, out value))
                {
                    return false;
                }

                if (!string.Equals(value, pair.Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private void RunAsAdminToggle_CheckedChanged(object sender, EventArgs e)
        {
            if (_detailLoading || _detailItem == null)
            {
                return;
            }

            if (_detailItem.RunAsAdmin == _runAsAdminToggle.Checked)
            {
                return;
            }

            _detailItem.RunAsAdmin = _runAsAdminToggle.Checked;
            _configManager.UpdateItem(_detailItem);
        }

        private void ProgramBrowseButton_Click(object sender, EventArgs e)
        {
            if (_detailItem == null)
            {
                return;
            }

            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择程序";
                dialog.CheckFileExists = true;
                dialog.Filter = "程序文件 (*.exe;*.com;*.bat;*.cmd)|*.exe;*.com;*.bat;*.cmd|所有文件 (*.*)|*.*";
                string current = (_programBox.Text ?? string.Empty).Trim();
                if (current.Length > 0 && File.Exists(current))
                {
                    dialog.FileName = current;
                }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                ApplyProgramToItem(_detailItem, dialog.FileName);
                UpdateProgramHint();
            }
        }

        /// <summary>
        /// R11：环境变量文本格式无法表达 CR/LF/TAB，渲染时把值里的连续换行/制表符折叠成一个空格，
        /// 键顺序原样保留。格式化本身复用 <see cref="EnvBlock.FormatLines"/>（单一实现），
        /// 这里只负责在交给它之前消毒值。
        /// </summary>
        private static string FormatEnvForEditor(ConfigItem item)
        {
            if (item == null || item.Env == null || item.Env.Count == 0)
            {
                return string.Empty;
            }

            var sanitized = new List<KeyValuePair<string, string>>(item.Env.Count);
            foreach (KeyValuePair<string, string> pair in item.Env)
            {
                sanitized.Add(new KeyValuePair<string, string>(pair.Key, SanitizeEnvValue(pair.Value)));
            }

            return EnvBlock.FormatLines(sanitized);
        }

        private static string SanitizeEnvValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            bool lastWasControl = false;
            foreach (char c in value)
            {
                if (c == '\r' || c == '\n' || c == '\t')
                {
                    if (!lastWasControl)
                    {
                        builder.Append(' ');
                    }
                    lastWasControl = true;
                    continue;
                }

                lastWasControl = false;
                builder.Append(c);
            }

            return builder.ToString();
        }
    }
}
