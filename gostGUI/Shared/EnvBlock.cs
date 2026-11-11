using System;
using System.Collections.Generic;
using System.Text;

namespace gostGUI.Shared
{
    /// <summary>环境变量文本的解析结果：覆盖项 + 面向用户的中文警告。</summary>
    public sealed class EnvParseResult
    {
        /// <summary>解析出的覆盖项；键名按 Windows 语义大小写不敏感，保留首次出现的顺序。</summary>
        public Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>逐行解析时发现的问题（中文）；空列表表示全部合法。</summary>
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// 环境变量增量覆盖的纯逻辑：UI 文本 ↔ 覆盖表、%VAR% 单次展开、与父环境合并、32K 上限校验。
    /// 不引用 WinForms / System.Text.Json / 文件 IO，UI 与 runner 都能直接复用。
    /// </summary>
    public static class EnvBlock
    {
        /// <summary>Windows 环境块硬上限（含每个 key=value 结尾的 '\0'）。</summary>
        private const int MaxBlockSize = 32767;

        /// <summary>
        /// 把“每行一条 KEY=VALUE”的文本解析成覆盖表。
        /// 跳过空行与 '#' 开头的行；只按第一个 '=' 切分；键和值都 Trim()；
        /// 缺 '=' 或键名为空只记中文警告并跳过，绝不抛异常；重复键（大小写不敏感）保留最后一个值。
        /// </summary>
        public static EnvParseResult ParseLines(string text)
        {
            var result = new EnvParseResult();
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }

            // 统一换行后再切分，兼容 CRLF / LF / CR 三种输入。
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals < 0)
                {
                    result.Warnings.Add($"第 {i + 1} 行缺少 '='，已忽略：{line}");
                    continue;
                }

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (key.Length == 0)
                {
                    result.Warnings.Add($"第 {i + 1} 行键名为空，已忽略：{line}");
                    continue;
                }

                // 大小写不敏感的字典天然完成“重复键保留最后值”，同时保留首次出现的位置。
                result.Overrides[key] = value;
            }

            return result;
        }

        /// <summary>
        /// 把覆盖表写回“每行一条 KEY=VALUE”的 UI 文本：CRLF 换行、保持枚举顺序、空值渲染为 KEY=。
        /// 末尾不带多余换行；null 或空序列返回空字符串。
        /// </summary>
        public static string FormatLines(IEnumerable<KeyValuePair<string, string>> overrides)
        {
            if (overrides == null)
            {
                return "";
            }

            var builder = new StringBuilder();
            bool first = true;
            foreach (KeyValuePair<string, string> pair in overrides)
            {
                // 空键无法用 KEY=VALUE 表达，写出去也会被 ParseLines 当成警告忽略，这里直接跳过。
                if (pair.Key == null)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append("\r\n");
                }
                first = false;

                builder.Append(pair.Key).Append('=').Append(pair.Value ?? "");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 以父环境为底逐项覆盖，返回合并后的有效环境（键名大小写不敏感）。
        /// 每个覆盖值按枚举顺序用 ExpandPercent 展开，查找表能看到父环境与已经应用过的覆盖项。
        /// 合并后按 Windows 的 key=value\0 口径累计，超过 32767 时 error 为中文说明，
        /// 返回的字典仍可用但调用方必须把非空 error 当作失败。
        /// </summary>
        public static Dictionary<string, string> BuildEffective(
            IEnumerable<KeyValuePair<string, string>> parent,
            IEnumerable<KeyValuePair<string, string>> overrides,
            out string error)
        {
            var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (parent != null)
            {
                foreach (KeyValuePair<string, string> pair in parent)
                {
                    if (pair.Key == null)
                    {
                        continue;
                    }
                    effective[pair.Key] = pair.Value ?? "";
                }
            }

            if (overrides != null)
            {
                foreach (KeyValuePair<string, string> pair in overrides)
                {
                    if (pair.Key == null)
                    {
                        continue;
                    }

                    // 查找表就是当前的有效环境：父环境 + 已经应用过的覆盖项（已展开的值）。
                    string expanded = ExpandPercent(pair.Value ?? "", effective);

                    // 先删再写：父环境里只有大小写不同的同名键会被覆盖表的写法替换，最终只留一条。
                    effective.Remove(pair.Key);
                    effective[pair.Key] = expanded;
                }
            }

            long total = 0;
            foreach (KeyValuePair<string, string> pair in effective)
            {
                // key + '=' + value + '\0'；用 long 累计，避免极端输入溢出。
                total += pair.Key.Length + 1 + pair.Value.Length + 1;
            }

            if (total > MaxBlockSize)
            {
                error = $"环境变量块过大：合并后共 {total} 字节，超过 Windows 上限 {MaxBlockSize} 字节，请减少环境变量。";
                return effective;
            }

            error = "";
            return effective;
        }

        /// <summary>
        /// 单次展开值里的 %NAME%：已知名字替换成查找表里的值，未知名字 / 空名字 %% 原样保留，
        /// 未配对的孤立 '%' 也是字面量。展开结果不再递归展开，避免环。
        /// </summary>
        public static string ExpandPercent(string value, IDictionary<string, string> lookup)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var builder = new StringBuilder(value.Length);
            int index = 0;
            while (index < value.Length)
            {
                char current = value[index];
                if (current != '%')
                {
                    builder.Append(current);
                    index++;
                    continue;
                }

                int end = value.IndexOf('%', index + 1);
                if (end < 0)
                {
                    // 没有配对的 '%'：剩余部分原样保留。
                    builder.Append(value, index, value.Length - index);
                    break;
                }

                string name = value.Substring(index + 1, end - index - 1);
                string replacement;
                if (name.Length > 0 && lookup != null && lookup.TryGetValue(name, out replacement))
                {
                    builder.Append(replacement ?? "");
                }
                else
                {
                    // 未知 %NAME% 或空名字 %%：整段原样保留，不做二次展开。
                    builder.Append(value, index, end - index + 1);
                }

                index = end + 1;
            }

            return builder.ToString();
        }
    }
}
