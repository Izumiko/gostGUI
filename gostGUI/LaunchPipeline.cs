using System;
using System.Collections.Generic;
using System.IO;
using gostGUI.Shared;

namespace gostGUI
{
    /// <summary>
    /// 一次启动所需的全部参数。Error 非空表示不要启动；其余字段尽力填充，调用方可直接读取而不必判空。
    /// </summary>
    public sealed class LaunchRequest
    {
        /// <summary>要启动的文件：exe 是解析后的完整路径，批处理是 cmd.exe。</summary>
        public string FileName = "";

        /// <summary>命令行参数（不含 FileName）；批处理已按 cmd /d /s /c 规则包装。</summary>
        public string Arguments = "";

        /// <summary>工作目录（规范化后的绝对路径）。</summary>
        public string WorkingDirectory = "";

        /// <summary>本项的环境变量覆盖项（值已展开）；父环境不在这里，由启动方自行合并。</summary>
        public Dictionary<string, string> EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>启动类型：exe 直接启动，批处理需要 cmd.exe 包装。</summary>
        public ChildKind Kind;

        /// <summary>非空表示不要启动，内容为面向用户的中文失败原因。</summary>
        public string Error = "";

        public bool IsBatch => Kind == ChildKind.Batch;
    }

    /// <summary>
    /// 把配置项装配成一次启动的完整参数。固定顺序：建有效环境 → 解析 program → 解析 workDir → 得到启动参数。
    /// 纯逻辑层：不弹 UI、不启动进程、不写文件；任何失败都转成中文 Error，绝不让异常漏给启动方。
    /// </summary>
    public static class LaunchPipeline
    {
        /// <summary>ProgramResolver 裸名未命中时的错误开头；只有这种“找不到”才换成下面的措辞。</summary>
        private const string ResolverNotFoundPrefix = "在应用目录与有效 PATH 中均未找到程序";

        public static LaunchRequest Build(ConfigItem item, string baseDir,
            IEnumerable<KeyValuePair<string, string>> parentEnv)
        {
            var request = new LaunchRequest();
            if (item == null)
            {
                request.Error = "配置项为空，无法构建启动请求。";
                return request;
            }

            // 参数与配置无关，先原样放好；失败路径上也能看到用户填的内容。
            request.Arguments = item.Args ?? "";

            try
            {
                // 1. 有效环境：父环境 + 本项覆盖。PATH / PATHEXT 都从这里取，所以 PATH=%PATH%;...
                //    这种写法对程序解析同样生效。
                Dictionary<string, string> effective =
                    EnvBlock.BuildEffective(parentEnv, item.Env, out string envError);
                request.EnvOverrides = BuildOverrides(item.Env, effective);
                if (!string.IsNullOrEmpty(envError))
                {
                    request.Error = envError;
                    return request;
                }

                // 2. 程序：裸名按“应用目录 → 有效 PATH”查找，显式路径按 baseDir 解析。
                ResolvedProgram resolved = ProgramResolver.Resolve(item.Program, baseDir, effective);
                if (!resolved.Found)
                {
                    request.Error = DescribeProgramFailure(item.Program, resolved);
                    return request;
                }

                request.Kind = resolved.Kind;

                // 3. 工作目录：空 = baseDir；%VAR% 从有效环境展开；相对路径按 baseDir 解析。
                if (!TryResolveWorkingDirectory(item.WorkDir, baseDir, effective, out string workDir, out string workDirError))
                {
                    request.Error = workDirError;
                    return request;
                }

                request.WorkingDirectory = workDir;

                // 4. 启动参数：bat/cmd 需要 cmd.exe 包装，其余直接透传用户参数。
                // COMSPEC 从有效环境（父环境快照 + 覆盖项）取，不再读活进程环境。
                BuildCommandLine(request, resolved, item.Args ?? "", effective);
                return request;
            }
            catch (Exception ex)
            {
                // 兜底：管线不把异常抛给启动方，一律转成中文错误。
                request.Error = "构建启动请求失败：" + ex.Message;
                return request;
            }
        }

        /// <summary>
        /// 只取本项的覆盖项，值用有效环境里同名键的展开结果。
        /// 有效环境本身就是“父环境 + 按枚举顺序逐项展开后的覆盖项”，因此这里得到的正是
        /// 单次展开后的值，且父环境的其余变量不会混进来。
        /// </summary>
        private static Dictionary<string, string> BuildOverrides(Dictionary<string, string> overrides,
            Dictionary<string, string> effective)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (overrides == null)
            {
                return result;
            }

            foreach (KeyValuePair<string, string> pair in overrides)
            {
                string value;
                if (effective != null && effective.TryGetValue(pair.Key, out value))
                {
                    result[pair.Key] = value ?? "";
                }
                else
                {
                    // 理论上不会走到这里；保留原值也比丢键安全。
                    result[pair.Key] = pair.Value ?? "";
                }
            }

            return result;
        }

        /// <summary>
        /// 程序解析失败时的中文说明：裸名改成“应用目录与有效 PATH”的措辞并带上真实搜索目录数；
        /// 显式路径、空程序、不支持的类型沿用解析器原文（它已经点名了路径或类型）。
        /// </summary>
        private static string DescribeProgramFailure(string program, ResolvedProgram resolved)
        {
            string error = resolved != null ? (resolved.Error ?? "") : "";
            if (error.StartsWith(ResolverNotFoundPrefix, StringComparison.Ordinal))
            {
                int searched = resolved != null ? resolved.SearchedDirectories : 0;
                return $"在应用目录与有效 PATH 中均未找到 {NormalizeDisplay(program)}（共搜索 {searched} 个目录）";
            }

            return error.Length > 0 ? error : "未设置程序，请填写程序名或完整路径。";
        }

        /// <summary>错误信息里展示用户填的程序名：去首尾空白与成对引号，不展开变量。</summary>
        private static string NormalizeDisplay(string program)
        {
            string value = (program ?? "").Trim();
            if (value.Length > 0 && value[0] == '"')
            {
                value = value.Substring(1);
            }

            if (value.Length > 0 && value[value.Length - 1] == '"')
            {
                value = value.Substring(0, value.Length - 1);
            }

            return value.Trim();
        }

        /// <summary>
        /// 解析工作目录：空 → baseDir；先展开 %VAR%，相对路径按 baseDir 解析，再 GetFullPath 规范化。
        /// 不存在、畸形路径都返回中文错误而不是抛异常。baseDir 为空时回落到本工具所在目录。
        /// </summary>
        private static bool TryResolveWorkingDirectory(string workDir, string baseDir,
            IDictionary<string, string> effective, out string fullPath, out string error)
        {
            fullPath = "";
            error = "";

            string rawValue = (workDir ?? "").Trim();
            string baseDirectory = (baseDir ?? "").Trim();
            if (baseDirectory.Length == 0)
            {
                baseDirectory = Common.GetApplicationPath();
            }

            // 错误信息展示用户填的原文；空值时就展示实际使用的 baseDir。
            string display = rawValue.Length > 0 ? rawValue : baseDirectory;
            string value = rawValue.Length > 0 ? EnvBlock.ExpandPercent(rawValue, effective).Trim() : "";

            try
            {
                string baseFull = Path.GetFullPath(baseDirectory);
                // Path.Combine 对根路径项会直接采用它，绝对路径因此不用单独分支。
                string candidate = value.Length == 0 ? baseFull : Path.Combine(baseFull, value);
                fullPath = Path.GetFullPath(candidate);
            }
            catch (Exception ex)
            {
                fullPath = "";
                error = $"工作目录无效：{display}（{ex.Message}）";
                return false;
            }

            if (!Directory.Exists(fullPath))
            {
                // 错误里点名规范化后的绝对路径，失败时 WorkingDirectory 留空表示不可用。
                error = $"工作目录不存在：{fullPath}";
                fullPath = "";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 组装最终启动参数。批处理按 cmd /s /c 的标准形式包装：
        /// <c>/d /s /c ""&lt;完整路径&gt;" &lt;用户参数&gt;"</c>；用户参数一律原样拼接，不改引号。
        /// </summary>
        private static void BuildCommandLine(LaunchRequest request, ResolvedProgram resolved, string args,
            IDictionary<string, string> effective)
        {
            if (resolved.Kind == ChildKind.Batch)
            {
                request.FileName = GetComSpec(effective);
                request.Arguments = args.Length == 0
                    ? "/d /s /c \"\"" + resolved.FullPath + "\"\""
                    : "/d /s /c \"\"" + resolved.FullPath + "\" " + args + "\"";
                return;
            }

            request.FileName = resolved.FullPath;
            request.Arguments = args;
        }

        /// <summary>
        /// cmd.exe 取自传入的有效环境（父环境快照 + 覆盖项），不再读活进程环境：
        /// 并发启动时，另一个线程的临时环境覆盖窗口不会影响这里的包装命令。
        /// 有效环境里没有 COMSPEC 时回落到裸名 cmd.exe（由系统按 PATH 查找）。
        /// </summary>
        private static string GetComSpec(IDictionary<string, string> effective)
        {
            string comspec;
            if (effective != null && effective.TryGetValue("COMSPEC", out comspec) && !string.IsNullOrEmpty(comspec))
            {
                return comspec;
            }

            return "cmd.exe";
        }
    }
}
