using System;
using System.Collections.Generic;
using System.IO;
using gostGUI.Shared;

namespace gostGUI
{
    /// <summary>程序解析结果；Found=false 时 Error 一定有面向用户的中文说明。</summary>
    public sealed class ResolvedProgram
    {
        public bool Found;
        public string FullPath;
        public ChildKind Kind;
        public string Error;
        public int SearchedDirectories;
    }

    /// <summary>
    /// 把配置里的 program 解析成可启动的完整路径。纯逻辑层：只用 File.Exists 判断存在，
    /// 不启动进程、不弹 UI，也不读进程环境——PATH / PATHEXT / %VAR% 全部来自传入的有效环境。
    /// </summary>
    public static class ProgramResolver
    {
        /// <summary>PATHEXT 缺失或为空时的 Windows 默认扩展名集合。</summary>
        private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

        /// <summary>路径分隔符与盘符/卷分隔符，用来区分"裸名"和"带路径"。</summary>
        private static readonly char[] PathSeparators = { '\\', '/', ':' };

        public static ResolvedProgram Resolve(string program, string baseDir,
            IReadOnlyDictionary<string, string> effectiveEnv)
        {
            var result = new ResolvedProgram { FullPath = "", Error = "" };

            // 1. 先 Trim、剥掉首尾引号（允许只出现在一侧），再 Trim 掉引号内的空白。
            string value = StripQuotesAndTrim(program ?? "");
            if (value.Length == 0)
            {
                return Fail(result, "未设置程序，请填写程序名或完整路径。");
            }

            // 查找表统一成大小写不敏感：PATH / PATHEXT 的键名大小写不可信，%VAR% 展开也要一致。
            IDictionary<string, string> lookup = BuildLookup(effectiveEnv);
            value = EnvBlock.ExpandPercent(value, lookup).Trim();
            if (value.Length == 0)
            {
                return Fail(result, "未设置程序，请填写程序名或完整路径。");
            }

            string[] extensions = ParsePathExtensions(LookupValue(lookup, "PATHEXT"));

            if (IsPathLike(value))
            {
                return ResolveExplicitPath(result, value, baseDir, extensions, lookup);
            }

            return ResolveBareName(result, value, baseDir, extensions, lookup);
        }

        /// <summary>带路径（含分隔符 / 盘符 / UNC）的程序：相对 baseDir 解析，必要时按 PATHEXT 补全。</summary>
        private static ResolvedProgram ResolveExplicitPath(ResolvedProgram result, string value, string baseDir,
            string[] extensions, IDictionary<string, string> lookup)
        {
            string fullPath;
            try
            {
                string candidate = value;
                if (!Path.IsPathRooted(candidate))
                {
                    string appDir = (baseDir ?? "").Trim();
                    if (appDir.Length > 0)
                    {
                        candidate = Path.Combine(Path.GetFullPath(EnvBlock.ExpandPercent(appDir, lookup)), candidate);
                    }
                }

                fullPath = Path.GetFullPath(candidate);
            }
            catch (Exception ex)
            {
                // 畸形路径（非法字符、过长等）只报错，绝不抛出。
                return Fail(result, $"程序路径无效：{value}（{ex.Message}）");
            }

            if (File.Exists(fullPath))
            {
                return Complete(result, fullPath);
            }

            if (!HasExtension(fullPath))
            {
                foreach (string extension in extensions)
                {
                    if (File.Exists(fullPath + extension))
                    {
                        return Complete(result, fullPath + extension);
                    }
                }
            }

            return Fail(result, $"未找到程序：{fullPath}，请检查路径是否正确。");
        }

        /// <summary>裸名：依次搜索应用目录与有效 PATH，每个目录先试原名，再按 PATHEXT 补全。</summary>
        private static ResolvedProgram ResolveBareName(ResolvedProgram result, string name, string baseDir,
            string[] extensions, IDictionary<string, string> lookup)
        {
            List<string> directories = BuildSearchDirectories(baseDir, lookup);

            foreach (string directory in directories)
            {
                List<string> candidates;
                // 畸形 PATH 项（非法字符等）只跳过它自己，不影响后面的目录。
                if (!TryBuildCandidates(directory, name, extensions, out candidates))
                {
                    continue;
                }

                // 只统计真正探测过的目录（baseDir 也算一个），按要求不去重。
                result.SearchedDirectories++;

                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        return Complete(result, candidate);
                    }
                }
            }

            return Fail(result,
                $"在应用目录与有效 PATH 中均未找到程序 \"{name}\"（已搜索 PATH 的 {result.SearchedDirectories} 个目录）。");
        }

        /// <summary>候选目录 = [baseDir] + PATH 各项（跳过空项、剥引号、展开 %VAR%）。</summary>
        private static List<string> BuildSearchDirectories(string baseDir, IDictionary<string, string> lookup)
        {
            var directories = new List<string>();

            string appDir = (baseDir ?? "").Trim();
            if (appDir.Length > 0)
            {
                directories.Add(EnvBlock.ExpandPercent(appDir, lookup));
            }

            string pathValue = LookupValue(lookup, "PATH");
            if (!string.IsNullOrEmpty(pathValue))
            {
                foreach (string entry in pathValue.Split(';'))
                {
                    string directory = StripQuotesAndTrim(entry);
                    if (directory.Length == 0)
                    {
                        continue;
                    }

                    directory = EnvBlock.ExpandPercent(directory, lookup).Trim();
                    if (directory.Length == 0)
                    {
                        continue;
                    }

                    directories.Add(directory);
                }
            }

            return directories;
        }

        /// <summary>把目录与名字（必要时加 PATHEXT 扩展名）拼成待检查的绝对路径；畸形目录返回 false。</summary>
        private static bool TryBuildCandidates(string directory, string name, string[] extensions,
            out List<string> candidates)
        {
            candidates = new List<string>();
            try
            {
                string fullDirectory = Path.GetFullPath(directory);
                string basePath = Path.Combine(fullDirectory, name);
                candidates.Add(basePath);

                // 已经有扩展名的名字只按原样试；没有扩展名才逐个补 PATHEXT。
                if (!HasExtension(name))
                {
                    foreach (string extension in extensions)
                    {
                        candidates.Add(basePath + extension);
                    }
                }

                return true;
            }
            catch (Exception)
            {
                candidates.Clear();
                return false;
            }
        }

        /// <summary>命中的文件按扩展名分类；不支持的扩展名返回中文错误。</summary>
        private static ResolvedProgram Complete(ResolvedProgram result, string fullPath)
        {
            string extension = ExtensionOf(fullPath).ToLowerInvariant();

            // 无扩展名的命中交给 CreateProcess 直接尝试，按可执行文件处理。
            if (extension.Length == 0 || extension == ".exe" || extension == ".com")
            {
                return Succeed(result, fullPath, ChildKind.Executable);
            }

            if (extension == ".bat" || extension == ".cmd")
            {
                return Succeed(result, fullPath, ChildKind.Batch);
            }

            return Fail(result,
                $"不支持的程序类型：{fullPath}。仅支持 .exe / .com / .bat / .cmd；.ps1 等脚本请用 powershell -File 启动。");
        }

        /// <summary>把有效环境复制成大小写不敏感的查找表，供 PATH / PATHEXT / %VAR% 共用。</summary>
        private static IDictionary<string, string> BuildLookup(IReadOnlyDictionary<string, string> env)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (env == null)
            {
                return lookup;
            }

            foreach (KeyValuePair<string, string> pair in env)
            {
                if (pair.Key == null)
                {
                    continue;
                }

                lookup[pair.Key] = pair.Value ?? "";
            }

            return lookup;
        }

        private static string LookupValue(IDictionary<string, string> lookup, string key)
        {
            string value;
            return lookup.TryGetValue(key, out value) ? (value ?? "") : "";
        }

        /// <summary>解析 PATHEXT；缺失、为空或全是空项时回落到 Windows 默认值。</summary>
        private static string[] ParsePathExtensions(string pathExt)
        {
            List<string> extensions = SplitExtensions(pathExt);
            if (extensions.Count == 0)
            {
                extensions = SplitExtensions(DefaultPathExt);
            }

            return extensions.ToArray();
        }

        private static List<string> SplitExtensions(string value)
        {
            var extensions = new List<string>();
            if (string.IsNullOrEmpty(value))
            {
                return extensions;
            }

            foreach (string part in value.Split(';'))
            {
                string extension = part.Trim();
                if (extension.Length == 0)
                {
                    continue;
                }

                // 容错：PATHEXT 项通常带点，用户漏写点时补上，否则补全永远不会命中。
                if (extension[0] != '.')
                {
                    extension = "." + extension;
                }

                extensions.Add(extension);
            }

            return extensions;
        }

        private static bool IsPathLike(string value)
        {
            return value.IndexOfAny(PathSeparators) >= 0;
        }

        /// <summary>判断是否带扩展名：分隔符之后的点才算，末尾的点不算。</summary>
        private static bool HasExtension(string value)
        {
            int dot = value.LastIndexOf('.');
            if (dot <= 0 || dot == value.Length - 1)
            {
                return false;
            }

            return dot > value.LastIndexOfAny(PathSeparators);
        }

        private static string ExtensionOf(string value)
        {
            return HasExtension(value) ? value.Substring(value.LastIndexOf('.')) : "";
        }

        private static string StripQuotesAndTrim(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length > 0 && trimmed[0] == '"')
            {
                trimmed = trimmed.Substring(1);
            }

            if (trimmed.Length > 0 && trimmed[trimmed.Length - 1] == '"')
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed.Trim();
        }

        private static ResolvedProgram Succeed(ResolvedProgram result, string fullPath, ChildKind kind)
        {
            result.Found = true;
            result.FullPath = fullPath;
            result.Kind = kind;
            result.Error = "";
            return result;
        }

        private static ResolvedProgram Fail(ResolvedProgram result, string error)
        {
            result.Found = false;
            result.FullPath = "";
            result.Error = error;
            return result;
        }
    }
}
