using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace gostGUI.Shared
{
    /// <summary>
    /// UI 进程交给 runner 的一份启动参数。JSON 字段名故意用短名（命令行里每个字符都算长度），
    /// 空值不写出；<see cref="EnvOverrides"/> 在反序列化后一律是大小写不敏感字典（键顺序保持 JSON 顺序）。
    /// </summary>
    public sealed class LaunchSpec
    {
        /// <summary>协议版本，恒为 1。</summary>
        [JsonPropertyName("v")]
        public int V { get; set; } = 1;

        /// <summary>本次启动的随机握手标识（guid "N" 格式）。</summary>
        [JsonPropertyName("nonce")]
        public string Nonce { get; set; }

        /// <summary>命名管道名："gostgui." + <see cref="Nonce"/>。</summary>
        [JsonPropertyName("pipe")]
        public string Pipe { get; set; }

        /// <summary>要启动的程序绝对路径（批处理场景下是 cmd.exe，<see cref="Arguments"/> 已包装）。</summary>
        [JsonPropertyName("program")]
        public string Program { get; set; }

        /// <summary>命令行参数；批处理已按 cmd /d /s /c 规则包装，runner 原样透传。</summary>
        [JsonPropertyName("args")]
        public string Arguments { get; set; }

        /// <summary>工作目录绝对路径。</summary>
        [JsonPropertyName("workDir")]
        public string WorkingDirectory { get; set; }

        /// <summary>程序类型；线格式写成 "exe" / "batch"。</summary>
        [JsonPropertyName("kind")]
        public ChildKind Kind { get; set; }

        /// <summary>本项的环境变量覆盖项（值已展开）；runner 在它自己的继承环境上再做增量覆盖。</summary>
        [JsonPropertyName("env")]
        public Dictionary<string, string> EnvOverrides { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>用户是否显式要求管理员权限；runner 只负责如实转达，不做提权判断。</summary>
        [JsonPropertyName("requestedAdmin")]
        public bool RequestedAdmin { get; set; }
    }

    /// <summary>
    /// LaunchSpec 的线格式编解码：JSON 短名、标准 Base64（UTF-8 JSON、无 BOM）与命令行长度判断。
    /// 纯逻辑层：不碰文件、不启动进程，UI 与 runner 都能直接复用。
    /// </summary>
    public static class LaunchSpecCodec
    {
        /// <summary>命令行参数长度上限；Base64 后超过它就必须改走 --spec-file 临时文件。</summary>
        public const int CommandLineLimit = 8000;

        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                // 只省略 null：空字符串、false、0 都是有效值，必须照写。
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                // 中文原样保留，不转成 \uXXXX：既省长度，也便于排查 spec 内容。
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            options.Converters.Add(new ChildKindJsonConverter());
            return options;
        }

        /// <summary>序列化成短名字段、null 省略的 JSON。</summary>
        public static string ToJson(LaunchSpec spec)
        {
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec), "启动参数为空，无法序列化。");
            }

            return JsonSerializer.Serialize(spec, JsonOptions);
        }

        /// <summary>解析 JSON；非法内容一律转成中文 FormatException，绝不让 JsonException/NRE 漏给启动方。</summary>
        public static LaunchSpec FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException("启动参数 JSON 为空，无法解析。");
            }

            LaunchSpec spec;
            try
            {
                spec = JsonSerializer.Deserialize<LaunchSpec>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new FormatException("启动参数 JSON 无效：" + ex.Message, ex);
            }

            if (spec == null)
            {
                throw new FormatException("启动参数 JSON 是 null，不是有效的启动参数对象。");
            }

            NormalizeEnvironment(spec);
            return spec;
        }

        /// <summary>把 spec 编成标准 Base64 的 UTF-8 JSON（无 BOM），可直接塞进命令行参数。</summary>
        public static string ToBase64(LaunchSpec spec)
        {
            string json = ToJson(spec);
            return Convert.ToBase64String(new UTF8Encoding(false).GetBytes(json));
        }

        /// <summary>
        /// 解 Base64 并解析 JSON。空输入、非法 Base64、非 UTF-8 内容、坏 JSON 都给出中文异常，
        /// 不允许退化成 NullReferenceException。
        /// </summary>
        public static LaunchSpec FromBase64(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                throw new ArgumentException("启动参数 Base64 为空，无法解析。", nameof(base64));
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException ex)
            {
                throw new FormatException("启动参数不是合法的 Base64 编码：" + ex.Message, ex);
            }

            string json;
            try
            {
                // 严格 UTF-8：非法字节必须报错，不能被替换字符悄悄吞掉。
                json = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (ArgumentException ex)
            {
                throw new FormatException("启动参数的 Base64 内容不是合法的 UTF-8 文本：" + ex.Message, ex);
            }

            return FromJson(json);
        }

        /// <summary>Base64 后超过命令行长度的唯一判断依据（不引入别的启发式）。</summary>
        public static bool NeedsTempFile(LaunchSpec spec)
        {
            return ToBase64(spec).Length > CommandLineLimit;
        }

        /// <summary>
        /// 反序列化后的字典比较器是大小写敏感的；这里重建一遍，换成 OrdinalIgnoreCase 并保持 JSON 里的键顺序。
        /// </summary>
        private static void NormalizeEnvironment(LaunchSpec spec)
        {
            if (spec.EnvOverrides == null)
            {
                spec.EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in spec.EnvOverrides)
            {
                normalized[pair.Key] = pair.Value;
            }

            spec.EnvOverrides = normalized;
        }
    }

    /// <summary>把 ChildKind 写成 "exe" / "batch"，读回来也接受这两个词。</summary>
    internal sealed class ChildKindJsonConverter : JsonConverter<ChildKind>
    {
        public override ChildKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("启动类型 kind 必须是字符串 \"exe\" 或 \"batch\"。");
            }

            string value = reader.GetString();
            switch (value)
            {
                case "exe":
                    return ChildKind.Executable;
                case "batch":
                    return ChildKind.Batch;
                default:
                    throw new JsonException($"未知的启动类型 kind：{value}，只支持 \"exe\" 或 \"batch\"。");
            }
        }

        public override void Write(Utf8JsonWriter writer, ChildKind value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value == ChildKind.Batch ? "batch" : "exe");
        }
    }
}
