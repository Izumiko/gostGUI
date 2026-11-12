// =====================================================================================
// SelfTest.Ipc.cs —— IPC 契约用例：LaunchSpec 编解码与 NDJSON 帧。
// 本文件是 SelfTest 的 partial：成员由 SelfTest.cs 原样搬来（用例名/断言/注册顺序未改）。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using gostGUI.Native;
using gostGUI.Shared;
using Microsoft.Win32.SafeHandles;

namespace gostGUI
{
    internal static partial class SelfTest
    {
        // IPC 契约用例的夹具：只构造内存里的绝对路径与流，绝不碰文件系统、绝不启动进程。

        private static LaunchSpec NewSpec()
        {
            string nonce = Guid.NewGuid().ToString("N");
            string appDir = Path.Combine(Path.GetTempPath(), "gostgui-spec");
            return new LaunchSpec
            {
                V = 1,
                Nonce = nonce,
                Pipe = "gostgui." + nonce,
                Program = Path.Combine(appDir, "gost.exe"),
                Arguments = "-L 127.0.0.1:8080",
                WorkingDirectory = appDir,
                Kind = ChildKind.Executable,
                EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "GOST_LOG", "debug" },
                    { "Path", @"C:\tools" },
                    { "中文键", "中文值" }
                },
                RequestedAdmin = false
            };
        }

        private static void AssertSpecEqual(LaunchSpec expected, LaunchSpec actual)
        {
            Assert(actual != null, "反序列化结果不应为 null");
            AssertEqual(expected.V, actual.V);
            AssertEqual(expected.Nonce, actual.Nonce);
            AssertEqual(expected.Pipe, actual.Pipe);
            AssertEqual(expected.Program, actual.Program);
            AssertEqual(expected.Arguments, actual.Arguments);
            AssertEqual(expected.WorkingDirectory, actual.WorkingDirectory);
            AssertEqual(expected.Kind, actual.Kind);
            AssertEqual(expected.RequestedAdmin, actual.RequestedAdmin);
            Assert(actual.EnvOverrides != null, "EnvOverrides 不应为 null");
            AssertEqual(expected.EnvOverrides.Count, actual.EnvOverrides.Count);
            foreach (KeyValuePair<string, string> pair in expected.EnvOverrides)
            {
                Assert(actual.EnvOverrides.ContainsKey(pair.Key), "反序列化后缺少环境变量：" + pair.Key);
                AssertEqual(pair.Value, actual.EnvOverrides[pair.Key]);
            }
        }

        /// <summary>把一条消息写进内存流，返回原始帧文本（含结尾换行，便于断言帧形态与多帧拼接）。</summary>
        private static string FrameText(IpcMessage message)
        {
            using (var stream = new MemoryStream())
            {
                IpcFraming.Write(stream, message);
                return new UTF8Encoding(false, true).GetString(stream.ToArray());
            }
        }

        private static StreamReader ReaderOver(string text)
        {
            return new StreamReader(
                new MemoryStream(new UTF8Encoding(false).GetBytes(text)),
                new UTF8Encoding(false, true));
        }

        private static int CountChar(string text, char value)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c == value)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>异常消息必须是中文：非法输入只允许得到中文的 FormatException/ArgumentException，绝不允许 NRE。</summary>
        private static void AssertMalformedInputRejected(Action body, string what)
        {
            Exception captured = null;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                captured = ex;
            }

            Assert(captured != null, what + " 应抛出异常，而不是被静默接受");
            Assert(captured is FormatException || captured is ArgumentException,
                what + " 应抛出 FormatException 或 ArgumentException，实际 " + captured.GetType().Name + ": " + captured.Message);
            Assert(ContainsChinese(captured.Message), what + " 的异常消息应为中文：" + captured.Message);
        }

        private static bool ContainsChinese(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char c in text)
            {
                if (c >= '\u4e00' && c <= '\u9fff')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>记录 Flush 次数的内存流：Write 必须对调用方流 Flush，保证对端立刻可读。</summary>
        private sealed class FlushCountingStream : MemoryStream
        {
            public int FlushCount;

            public override void Flush()
            {
                FlushCount++;
                base.Flush();
            }
        }

        private static void SpecBase64RoundTrip()
        {
            LaunchSpec spec = NewSpec();
            string base64 = LaunchSpecCodec.ToBase64(spec);

            Assert(!string.IsNullOrEmpty(base64), "Base64 不应为空");
            Assert(base64.IndexOf(' ') < 0, "标准 Base64 不应包含空格，可直接塞进 lpParameters");

            byte[] bytes = Convert.FromBase64String(base64);
            string json = new UTF8Encoding(false, true).GetString(bytes);
            Assert(json.Length > 0 && json[0] == '{', "Base64 解出的应是 JSON 对象：" + json);
            Assert(json.Contains("\"nonce\""), "JSON 必须使用短字段名 nonce：" + json);
            Assert(json.Contains("\"kind\":\"exe\""), "Kind 必须序列化成 \"exe\"：" + json);

            LaunchSpec back = LaunchSpecCodec.FromBase64(base64);
            AssertSpecEqual(spec, back);
            Assert(back.EnvOverrides.ContainsKey("path"), "反序列化后的 EnvOverrides 必须大小写不敏感");
            AssertEqual("debug", back.EnvOverrides["gost_log"]);
            // 保留 JSON 里的键顺序，供 %VAR% 展开等按出现顺序处理。
            AssertEqual("GOST_LOG", new List<string>(back.EnvOverrides.Keys)[0]);
        }

        private static void SpecNeedsTempFileWhenTooLong()
        {
            AssertEqual(8000, LaunchSpecCodec.CommandLineLimit);

            // Base64 长度只能是 4 的倍数：先找到恰好等于上限的那一档，再加一个字符。
            LaunchSpec spec = NewSpec();
            int argsLength = 4000;
            while (true)
            {
                spec.Arguments = new string('x', argsLength);
                if (LaunchSpecCodec.ToBase64(spec).Length == LaunchSpecCodec.CommandLineLimit)
                {
                    spec.Arguments = new string('x', argsLength + 1);
                    if (LaunchSpecCodec.ToBase64(spec).Length > LaunchSpecCodec.CommandLineLimit)
                    {
                        break;
                    }
                }

                argsLength++;
                Assert(argsLength < 10000, "未能构造出恰好 8000 字符 Base64 的用例夹具");
            }

            spec.Arguments = new string('x', argsLength);
            AssertEqual(8000, LaunchSpecCodec.ToBase64(spec).Length);
            Assert(!LaunchSpecCodec.NeedsTempFile(spec), "Base64 恰好 8000 字符时不应改用临时文件");

            spec.Arguments = new string('x', argsLength + 1);
            AssertEqual(8004, LaunchSpecCodec.ToBase64(spec).Length);
            Assert(LaunchSpecCodec.NeedsTempFile(spec), "Base64 超过 8000 字符时必须改用临时文件");

            spec.Arguments = new string('x', 60000);
            Assert(LaunchSpecCodec.NeedsTempFile(spec), "超长参数必须改用临时文件");
        }

        private static void SpecJsonRoundTripPreservesChineseAndQuotes()
        {
            LaunchSpec spec = NewSpec();
            spec.Program = Path.Combine(Path.GetTempPath(), "工具 目录", "gost.exe");
            spec.Arguments = "-L \"127.0.0.1:8080\" --tag \"含中文与\\反斜杠\"";
            spec.WorkingDirectory = Path.Combine(Path.GetTempPath(), "工作 目录");
            spec.Kind = ChildKind.Batch;
            spec.RequestedAdmin = true;
            spec.EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "PATH", @"C:\工具;%PATH%" },
                { "引号键", "值里有\"引号\"和\\反斜杠" }
            };

            string json = LaunchSpecCodec.ToJson(spec);
            Assert(json.Contains("中文"), "中文必须原样保留，不能转义成 \\uXXXX：" + json);
            Assert(!json.Contains("\\u4e2d"), "中文不应被转义成 \\uXXXX：" + json);
            Assert(json.Contains("\"kind\":\"batch\""), "Kind 必须序列化成 \"batch\"：" + json);

            LaunchSpec back = LaunchSpecCodec.FromJson(json);
            AssertSpecEqual(spec, back);
            AssertEqual(ChildKind.Batch, back.Kind);
            Assert(back.EnvOverrides.ContainsKey("path"), "反序列化后的 EnvOverrides 必须大小写不敏感");
        }

        private static void SpecFromBase64MalformedThrowsChinese()
        {
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromBase64(null), "null Base64");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromBase64(""), "空 Base64");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromBase64("   "), "纯空白 Base64");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromBase64("这不是 base64!!!"), "非法 Base64 字符");
            AssertMalformedInputRejected(
                () => LaunchSpecCodec.FromBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"v\":1"))),
                "截断的 JSON");
            AssertMalformedInputRejected(
                () => LaunchSpecCodec.FromBase64(Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0xFD })),
                "非法 UTF-8 字节");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromJson(null), "null JSON");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromJson("{"), "畸形 JSON");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromJson("{\"kind\":\"weird\"}"), "未知 kind");
            AssertMalformedInputRejected(() => LaunchSpecCodec.FromJson("null"), "JSON null");
        }

        private static void IpcFrameRoundTripEscapesNewlineTabQuoteBackslash()
        {
            var message = new IpcMessage
            {
                Type = "out",
                Nonce = Guid.NewGuid().ToString("N"),
                Pid = 4321,
                Stream = "o",
                Data = "a\tb\n\"c\"\\d"
            };

            using (var stream = new FlushCountingStream())
            {
                IpcFraming.Write(stream, message);
                Assert(stream.FlushCount >= 1, "Write 必须 Flush，保证对端能立刻读到");

                string text = new UTF8Encoding(false, true).GetString(stream.ToArray());
                Assert(text.EndsWith("\n", StringComparison.Ordinal), "一帧必须以 \\n 结尾");
                // 帧内只能有结尾这一个原始换行；\t / \n / " / \ 必须走 JSON 转义。
                AssertEqual(1, CountChar(text, '\n'));
                AssertEqual(0, CountChar(text, '\r'));
                AssertEqual(0, CountChar(text, '\t'));
                Assert(text.Contains("\\t"), "制表符必须转义成 \\t：" + text);
                Assert(text.Contains("\\n"), "换行必须转义成 \\n：" + text);
                Assert(text.Contains("\\\""), "双引号必须转义：" + text);
                Assert(text.Contains("\\\\"), "反斜杠必须转义：" + text);

                stream.Position = 0;
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                {
                    IpcMessage back = IpcFraming.Read(reader);
                    Assert(back != null, "应读回一帧");
                    AssertEqual("out", back.Type);
                    AssertEqual(message.Nonce, back.Nonce);
                    AssertEqual(4321, back.Pid);
                    AssertEqual("o", back.Stream);
                    AssertEqual(message.Data, back.Data);
                }
            }
        }

        private static void IpcFrameRoundTripChineseAndEmptyString()
        {
            var chinese = new IpcMessage { Type = "out", Stream = "e", Data = "中文输出：你好，世界" };
            var empty = new IpcMessage { Type = "ready", Nonce = "n-1", Data = "" };
            var nullData = new IpcMessage { Type = "stop" };

            string chineseLine = FrameText(chinese);
            string emptyLine = FrameText(empty);
            string nullLine = FrameText(nullData);

            Assert(chineseLine.Contains("中文输出：你好，世界"), "中文应原样出现在帧里：" + chineseLine);
            Assert(emptyLine.Contains("\"d\":\"\""), "空字符串必须写成 \"d\":\"\"，不能当成 null 省略：" + emptyLine);
            Assert(!nullLine.Contains("\"d\""), "null 的 Data 必须省略：" + nullLine);

            using (StreamReader reader = ReaderOver(chineseLine + emptyLine + nullLine))
            {
                IpcMessage first = IpcFraming.Read(reader);
                AssertEqual("中文输出：你好，世界", first.Data);
                AssertEqual("e", first.Stream);

                IpcMessage second = IpcFraming.Read(reader);
                AssertEqual("", second.Data);
                Assert(second.Data != null, "空字符串不能变成 null");

                IpcMessage third = IpcFraming.Read(reader);
                AssertEqual("stop", third.Type);
                Assert(third.Data == null, "省略的 Data 读回应为 null");
                Assert(third.Stream == null, "省略的 Stream 读回应为 null");

                Assert(IpcFraming.Read(reader) == null, "三帧读完后必须返回 null");
            }
        }

        private static void IpcReadReturnsNullAtEof()
        {
            using (StreamReader reader = ReaderOver(""))
            {
                Assert(IpcFraming.Read(reader) == null, "空流必须返回 null");
            }

            // 只有空行的流不是消息：跳过空白后仍是文件尾。
            using (StreamReader reader = ReaderOver("\r\n\n   \n"))
            {
                Assert(IpcFraming.Read(reader) == null, "空行不是消息，读到文件尾必须返回 null");
            }

            // 读完最后一帧之后再读一次同样是 null，而不是抛异常。
            string line = FrameText(new IpcMessage { Type = "exit", ExitCode = 0 });
            using (StreamReader reader = ReaderOver(line))
            {
                IpcMessage message = IpcFraming.Read(reader);
                AssertEqual("exit", message.Type);
                AssertEqual(0, message.ExitCode);
                Assert(IpcFraming.Read(reader) == null, "读完最后一帧后必须返回 null");
            }
        }

        private static void IpcReadMalformedJsonThrowsChinese()
        {
            using (StreamReader reader = ReaderOver("{not json}\n"))
            {
                AssertMalformedInputRejected(() => IpcFraming.Read(reader), "畸形 JSON 帧");
            }

            using (StreamReader reader = ReaderOver("{\"t\":\"out\"\n"))
            {
                AssertMalformedInputRejected(() => IpcFraming.Read(reader), "截断的 JSON 帧");
            }
        }
    }
}
