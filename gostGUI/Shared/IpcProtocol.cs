using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace gostGUI.Shared
{
    /// <summary>
    /// 管道里的一条 IPC 消息。字段名是短名 t/n/p/e/d/c，null 字段不写出；
    /// 空字符串是有意义的值（例如子进程输出空行），必须照写。
    /// </summary>
    public sealed class IpcMessage
    {
        /// <summary>消息类型：ready|started|out|exit|error|stop。</summary>
        [JsonPropertyName("t")]
        public string Type { get; set; }

        /// <summary>握手标识；ready 消息回带它供 UI 校验。</summary>
        [JsonPropertyName("n")]
        public string Nonce { get; set; }

        /// <summary>子进程 PID；started 消息带。</summary>
        [JsonPropertyName("p")]
        public int Pid { get; set; }

        /// <summary>输出流别：out 消息专用，"o" = stdout，"e" = stderr。</summary>
        [JsonPropertyName("e")]
        public string Stream { get; set; }

        /// <summary>消息负载：输出文本或错误说明。空字符串与 null 语义不同。</summary>
        [JsonPropertyName("d")]
        public string Data { get; set; }

        /// <summary>退出码；exit 消息带。</summary>
        [JsonPropertyName("c")]
        public int ExitCode { get; set; }
    }

    /// <summary>
    /// UTF-8 NDJSON 分帧：一行一条消息，JSON 转义保证帧内不出现原始换行。
    /// 只用 <see cref="Stream"/> / <see cref="StreamReader"/>，不依赖管道类型，UI 与 runner 都能复用。
    /// </summary>
    public static class IpcFraming
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            // 只省略 null：Data="" 必须保留，否则空行输出会被误当成 null。
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>把一个消息序列化成一行 NDJSON（UTF-8 + '\n'）并 Flush，写完即可被对端读到。</summary>
        public static void Write(Stream stream, IpcMessage message)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream), "IPC 输出流为空，无法写入消息。");
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message), "IPC 消息为空，无法写入。");
            }

            // STJ 会把 \t、\n、"、\ 全部转义，所以序列化结果里不可能出现原始换行。
            string json = JsonSerializer.Serialize(message, JsonOptions);
            byte[] bytes = new UTF8Encoding(false).GetBytes(json + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>
        /// 读一行并解析；空行跳过，读到流尾返回 null。
        /// 畸形 JSON 抛出中文 FormatException，绝不静默返回 null。
        /// </summary>
        public static IpcMessage Read(StreamReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader), "IPC 输入读取器为空，无法读取消息。");
            }

            while (true)
            {
                string line = reader.ReadLine();
                if (line == null)
                {
                    return null;
                }

                // 结尾的空行、心跳空行都不是消息，继续读到下一行或流尾。
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                IpcMessage message;
                try
                {
                    message = JsonSerializer.Deserialize<IpcMessage>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw new FormatException("收到的 IPC 消息不是合法 JSON：" + ex.Message, ex);
                }

                if (message == null)
                {
                    throw new FormatException("收到的 IPC 消息是 JSON null，不是有效消息。");
                }

                return message;
            }
        }
    }
}
