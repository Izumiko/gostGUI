using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace gostGUI
{
    /// <summary>
    /// 隐藏的 --selftest 自检骨架：按登记顺序执行用例，结果写到控制台与 selftest.log。
    /// 退出码与 selftest.log 是权威输出，控制台输出只是尽力而为。
    /// </summary>
    internal static class SelfTest
    {
        private const string LogFileName = "selftest.log";
        private static readonly List<TestCase> Cases = new List<TestCase>();

        static SelfTest()
        {
            RegisterCases();
        }

        /// <summary>在这里按顺序登记所有自检用例。</summary>
        private static void RegisterCases()
        {
            Check("sanity", () => Assert(1 + 1 == 2));
        }

        public static void Check(string name, Action body)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (body == null) throw new ArgumentNullException(nameof(body));
            Cases.Add(new TestCase(name, body));
        }

        public static void Assert(bool condition, string message = null)
        {
            if (!condition)
            {
                throw new SelfTestFailureException(message ?? "断言失败");
            }
        }

        public static void AssertEqual<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new SelfTestFailureException($"断言失败：期望 <{FormatValue(expected)}>，实际 <{FormatValue(actual)}>");
            }
        }

        public static void AssertThrows<T>(Action body) where T : Exception
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            try
            {
                body();
            }
            catch (T)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new SelfTestFailureException($"断言失败：期望抛出 {typeof(T).Name}，实际抛出 {ex.GetType().Name}: {ex.Message}");
            }

            throw new SelfTestFailureException($"断言失败：期望抛出 {typeof(T).Name}，但没有抛出异常");
        }

        /// <summary>顺序执行全部用例，返回 0 表示全过、1 表示有失败。</summary>
        public static int RunAll()
        {
            var log = new StringBuilder();
            TextWriter console = ConsoleOutput.Open();
            int failed = 0;

            foreach (TestCase testCase in Cases)
            {
                try
                {
                    testCase.Body();
                    WriteLine(log, console, "PASS " + testCase.Name);
                }
                catch (Exception ex)
                {
                    failed++;
                    WriteLine(log, console, "FAIL " + testCase.Name + ": " + DescribeFailure(ex));
                }
            }

            WriteLine(log, console, $"共 {Cases.Count} 个用例，失败 {failed} 个");

            if (!TryWriteLog(log.ToString()))
            {
                // 日志是权威输出之一，写不出来就按失败处理。
                WriteLine(console, "FAIL 无法写入日志文件: " + LogPath);
                failed++;
            }

            FlushQuietly(console);
            return failed == 0 ? 0 : 1;
        }

        private static string LogPath
        {
            get { return Path.Combine(Common.GetApplicationPath(), LogFileName); }
        }

        private static bool TryWriteLog(string text)
        {
            try
            {
                File.WriteAllText(LogPath, text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string DescribeFailure(Exception ex)
        {
            if (ex is SelfTestFailureException)
            {
                return ex.Message;
            }

            return ex.GetType().Name + ": " + ex.Message;
        }

        private static string FormatValue(object value)
        {
            return value == null ? "null" : Convert.ToString(value);
        }

        private static void WriteLine(StringBuilder log, TextWriter console, string line)
        {
            log.AppendLine(line);
            WriteLine(console, line);
        }

        private static void WriteLine(TextWriter console, string line)
        {
            if (console == null) return;

            try
            {
                console.WriteLine(line);
            }
            catch (Exception)
            {
                // 控制台输出只用于人眼查看，不可用就忽略。
            }
        }

        private static void FlushQuietly(TextWriter console)
        {
            if (console == null) return;

            try
            {
                console.Flush();
                console.Dispose();
            }
            catch (Exception)
            {
                // 进程即将退出，刷新失败不影响退出码与日志。
            }
        }

        private sealed class TestCase
        {
            public TestCase(string name, Action body)
            {
                Name = name;
                Body = body;
            }

            public string Name { get; }

            public Action Body { get; }
        }

        private sealed class SelfTestFailureException : Exception
        {
            public SelfTestFailureException(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// WinExe 默认没有控制台，Console.Out 不可靠；这里改用 CONOUT$ 打开控制台输出，
        /// 打不开就回落到继承来的标准输出（可能也是空的），失败一律忽略。
        /// </summary>
        private static class ConsoleOutput
        {
            private const uint GenericWrite = 0x40000000;
            private const uint FileShareRead = 0x00000001;
            private const uint FileShareWrite = 0x00000002;
            private const uint OpenExisting = 0x00000003;

            public static TextWriter Open()
            {
                try
                {
                    SafeFileHandle handle = CreateFile(
                        "CONOUT$",
                        GenericWrite,
                        FileShareRead | FileShareWrite,
                        IntPtr.Zero,
                        OpenExisting,
                        0,
                        IntPtr.Zero);

                    if (!handle.IsInvalid)
                    {
                        return new StreamWriter(new FileStream(handle, FileAccess.Write, 1024), ConsoleEncoding())
                        {
                            AutoFlush = true
                        };
                    }
                }
                catch (Exception)
                {
                    // 没有控制台就算了。
                }

                try
                {
                    return Console.Out;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private static Encoding ConsoleEncoding()
            {
                try
                {
                    return Console.OutputEncoding;
                }
                catch (Exception)
                {
                    return Encoding.Default;
                }
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateFile(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);
        }
    }
}
