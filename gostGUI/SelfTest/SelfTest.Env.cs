// =====================================================================================
// SelfTest.Env.cs —— 环境变量用例：EnvBlock 解析/展开/合并/32K 上限，以及子进程环境块应用的守卫。
// 本文件是自检套件的 partial：原单体 SelfTest.cs 已按领域拆分（该文件已不存在），用例名/断言/注册顺序未改。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using gostGUI.Shared;

namespace gostGUI
{
    internal static partial class SelfTest
    {
        private static void EnvIsCaseInsensitive()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = WriteConfig(dir,
                    "{\n" +
                    "  \"items\": [\n" +
                    "    { \"name\": \"a\", \"env\": { \"Path\": \"C:\\\\tools\", \"GOST_LOG\": \"debug\" } }\n" +
                    "  ]\n" +
                    "}");

                ConfigLoadResult result = Common.LoadFrom(path);
                Assert(result.Ok, "env 应能加载：" + result.Error);
                Dictionary<string, string> env = result.Data.Items[0].Env;
                Assert(env != null, "env 不应为 null");
                AssertEqual(2, env.Count);
                AssertEqual("C:\\tools", env["PATH"]);
                AssertEqual("debug", env["gost_log"]);
                Assert(env.ContainsKey("path"), "键比较应忽略大小写");
                // 保持 JSON 里的键顺序，供后续 %VAR% 展开按出现顺序处理。
                AssertEqual("Path", new List<string>(env.Keys)[0]);

                // 代码里直接赋值时也应自动变成大小写不敏感。
                ConfigItem item = new ConfigItem();
                item.Env = new Dictionary<string, string> { { "Key", "v" } };
                AssertEqual("v", item.Env["KEY"]);
                AssertEqual("Key", new List<string>(item.Env.Keys)[0]);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        // 环境变量块：解析 / 展开 / 合并 / 32K 上限 / 回写。用例只用内存数据，不碰文件。

        private static void EnvParseLinesSkipsBlanksAndComments()
        {
            EnvParseResult parsed = EnvBlock.ParseLines(
                "A=1\r\n" +
                "\r\n" +
                "   \r\n" +
                "# 整行注释\r\n" +
                "  # 缩进注释\r\n" +
                "B=2");

            Assert(parsed != null, "ParseLines 不应返回 null");
            Assert(parsed.Overrides != null, "Overrides 不应为 null");
            Assert(parsed.Warnings != null, "Warnings 不应为 null");
            AssertEqual(2, parsed.Overrides.Count);
            AssertEqual("1", parsed.Overrides["A"]);
            AssertEqual("2", parsed.Overrides["B"]);
            AssertEqual(0, parsed.Warnings.Count);

            // 缺 '=' 的行与空键名的行只产生中文警告，绝不能抛异常。
            EnvParseResult broken = EnvBlock.ParseLines("NOEQUALS\r\n= 空键\r\nC=3");
            AssertEqual(1, broken.Overrides.Count);
            AssertEqual("3", broken.Overrides["C"]);
            AssertEqual(2, broken.Warnings.Count);
            Assert(broken.Warnings[0].Contains("第 1 行"), "缺少 '=' 的警告应带行号：" + broken.Warnings[0]);
            Assert(broken.Warnings[1].Contains("第 2 行"), "空键名的警告应带行号：" + broken.Warnings[1]);

            // null / 空白文本 → 空表、无警告，且不抛异常。
            foreach (string text in new[] { null, "", "   \r\n\t" })
            {
                EnvParseResult empty = EnvBlock.ParseLines(text);
                Assert(empty.Overrides != null && empty.Overrides.Count == 0, "空文本应返回空覆盖表");
                Assert(empty.Warnings != null && empty.Warnings.Count == 0, "空文本不应产生警告");
            }
        }

        private static void EnvParseLinesSplitsOnFirstEquals()
        {
            EnvParseResult parsed = EnvBlock.ParseLines(
                "A=b=c\r\n" +
                "  KEY  =  value with spaces  \r\n" +
                "EMPTY=\r\n" +
                "EQ==x\r\n" +
                "DUP=first\r\n" +
                "DUP=second\r\n" +
                "dup=third");

            AssertEqual(5, parsed.Overrides.Count);
            AssertEqual("b=c", parsed.Overrides["A"]);
            AssertEqual("value with spaces", parsed.Overrides["KEY"]);
            AssertEqual("", parsed.Overrides["EMPTY"]);
            AssertEqual("=x", parsed.Overrides["EQ"]);
            // 重复键（大小写不敏感）只保留最后一个值。
            AssertEqual("third", parsed.Overrides["DUP"]);
            AssertEqual(0, parsed.Warnings.Count);
        }

        private static void EnvExpandItemOrderThenParent()
        {
            var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ROOT", @"C:\root" }
            };
            var overrides = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("BASE", @"C:\tools"),
                new KeyValuePair<string, string>("PATH", @"%BASE%;%ROOT%;%UNKNOWN%"),
                new KeyValuePair<string, string>("COPY", "%PATH%")
            };

            string error;
            Dictionary<string, string> effective = EnvBlock.BuildEffective(parent, overrides, out error);
            AssertEqual("", error);
            AssertEqual(@"C:\tools;C:\root;%UNKNOWN%", effective["PATH"]);
            // 单次展开：后出现的键看到的是前一项已展开的值，%UNKNOWN% 不会二次展开。
            AssertEqual(@"C:\tools;C:\root;%UNKNOWN%", effective["COPY"]);
            AssertEqual(@"C:\root", effective["ROOT"]);

            IDictionary<string, string> lookup = effective;
            AssertEqual(@"C:\root", EnvBlock.ExpandPercent("%ROOT%", lookup));
            AssertEqual("%NOPE%", EnvBlock.ExpandPercent("%NOPE%", lookup));
            AssertEqual("%%", EnvBlock.ExpandPercent("%%", lookup));
            AssertEqual("100%", EnvBlock.ExpandPercent("100%", lookup));
            AssertEqual("tail%ROOT", EnvBlock.ExpandPercent("tail%ROOT", lookup));
            AssertEqual("", EnvBlock.ExpandPercent(null, lookup));
        }

        private static void EnvBuildEffectiveOverridesCaseInsensitive()
        {
            var parent = new Dictionary<string, string>
            {
                { "Path", @"C:\windows" },
                { "KEEP", "keep" }
            };
            var overrides = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("PATH", @"%Path%;C:\tools")
            };

            string error;
            Dictionary<string, string> effective = EnvBlock.BuildEffective(parent, overrides, out error);
            AssertEqual("", error);
            AssertEqual(2, effective.Count);
            // 展开时按大小写不敏感命中父环境；最终键名以覆盖表的写法为准，且只出现一次。
            AssertEqual(@"C:\windows;C:\tools", effective["PATH"]);
            List<string> keys = new List<string>(effective.Keys);
            Assert(keys.Contains("PATH"), "父环境键应被覆盖表键名替换");
            Assert(!keys.Contains("Path"), "同一键不应同时保留两种大小写");
            AssertEqual("keep", effective["keep"]);

            // 覆盖表内部只有大小写差异的重复键也只留最后一个。
            var duplicates = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Foo", "1"),
                new KeyValuePair<string, string>("foo", "2")
            };
            Dictionary<string, string> deduped = EnvBlock.BuildEffective(
                new Dictionary<string, string>(), duplicates, out error);
            AssertEqual("", error);
            AssertEqual(1, deduped.Count);
            AssertEqual("foo", new List<string>(deduped.Keys)[0]);
            AssertEqual("2", deduped["FOO"]);
        }

        private static void EnvBuildEffectiveTooLargeReturnsError()
        {
            string error;

            // key(1) + '='(1) + value + '\0'(1) = value.Length + 3；32764 正好等于上限 32767。
            var atLimit = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("K", new string('x', 32764))
            };
            Dictionary<string, string> ok = EnvBlock.BuildEffective(
                new Dictionary<string, string>(), atLimit, out error);
            AssertEqual("", error);
            AssertEqual(32764, ok["K"].Length);

            var tooLarge = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("K", new string('x', 32765))
            };
            Dictionary<string, string> failed = EnvBlock.BuildEffective(
                new Dictionary<string, string>(), tooLarge, out error);
            Assert(!string.IsNullOrEmpty(error), "超过 32K 上限时必须返回中文错误");
            Assert(error.Contains("32767"), "错误信息应包含上限值：" + error);
            Assert(failed != null, "失败时仍返回字典，但调用方只认 error");

            var big = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("BIG", new string('y', 40 * 1024))
            };
            EnvBlock.BuildEffective(new Dictionary<string, string>(), big, out error);
            Assert(!string.IsNullOrEmpty(error), "40KB 的环境块必须失败");
        }

        private static void EnvFormatLinesRoundTrips()
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "B", "2" },
                { "A", "1" },
                { "EMPTY", "" },
                { "中文键", "中文值" }
            };

            string text = EnvBlock.FormatLines(overrides);
            AssertEqual("B=2\r\nA=1\r\nEMPTY=\r\n中文键=中文值", text);
            Assert(!text.EndsWith("\r\n"), "最后一行不应带多余换行");
            AssertEqual("", EnvBlock.FormatLines(null));
            AssertEqual("", EnvBlock.FormatLines(new List<KeyValuePair<string, string>>()));

            EnvParseResult parsed = EnvBlock.ParseLines(text);
            AssertEqual(0, parsed.Warnings.Count);
            AssertEqual(4, parsed.Overrides.Count);
            AssertEqual("", parsed.Overrides["EMPTY"]);
            AssertEqual("中文值", parsed.Overrides["中文键"]);
            AssertEqual(text, EnvBlock.FormatLines(parsed.Overrides));
        }

        // 子进程环境块应用：R12 的 32767+2 守卫与“无覆盖即完全不碰 psi”的纯内存用例，不启动进程。

        /// <summary>
        /// 当前父环境按 Windows 口径的块大小：每项 key + '=' + value + '\0'，再加结尾两个 '\0'。
        /// 与 ChildEnvironment 的守卫口径一致（大小写不敏感、同名只留最后一个值）。
        /// 用例真正钉住的是错误文本与“接受/拒绝”边界；这里的公式只用于把夹具推到边界。
        /// </summary>
        private static long ParentEnvironmentBlockSize()
        {
            var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = Convert.ToString(entry.Key);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                effective[key] = Convert.ToString(entry.Value) ?? "";
            }

            long total = 2;
            foreach (KeyValuePair<string, string> pair in effective)
            {
                total += pair.Key.Length + 1 + pair.Value.Length + 1;
            }

            return total;
        }

        /// <summary>
        /// 直接读取 psi.EnvironmentVariables 在 net48 里的私有懒播种字段，不触发属性 getter：
        /// 字段为 null 表示 Apply 从未访问/写过它，这是“无覆盖项即不碰 psi”的最强证据。
        /// 字段名在 net48 固定；取不到时返回 false，由调用用例写 SKIP 记录，而不是静默放过断言。
        /// </summary>
        private static bool TryGetStartInfoEnvironment(ProcessStartInfo psi,
            out System.Collections.Specialized.StringDictionary environment)
        {
            environment = null;
            var field = typeof(ProcessStartInfo).GetField("environmentVariables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null)
            {
                return false;
            }

            environment = field.GetValue(psi) as System.Collections.Specialized.StringDictionary;
            return true;
        }

        private static void ChildEnvTooLargeBlockReturnsChineseError()
        {
            // 父环境 + 覆盖项都按“key + '=' + value + '\0'”累计，再加结尾两个 '\0'；把夹具推到 32777。
            const int overflowBytes = 10;
            string key = "GOSTGUI_SELFTEST_FAT_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            long parentSize = ParentEnvironmentBlockSize();
            int valueLength = (int)(32767 + overflowBytes - parentSize - (key.Length + 2));
            Assert(valueLength > 0, "父环境过大，构造不出超限夹具（父环境块 " + parentSize + " 字节）");

            var psi = new ProcessStartInfo("cmd.exe");
            var logs = new List<string>();
            string error;
            ChildEnvironment.Apply(
                psi,
                new[] { new KeyValuePair<string, string>(key, new string('x', valueLength)) },
                logs.Add,
                out error);

            Assert(!string.IsNullOrEmpty(error), "环境块超过 32767 时必须返回错误");
            Assert(ContainsChinese(error), "超限错误应为中文：" + error);
            Assert(error.Contains("32767"), "超限错误应点名 Windows 上限：" + error);
            // 守卫在规范化/写入之前：既不应产生 System 日志，也不应碰 psi 与本进程环境。
            AssertEqual(0, logs.Count);
            System.Collections.Specialized.StringDictionary psiEnvironment;
            if (TryGetStartInfoEnvironment(psi, out psiEnvironment))
            {
                Assert(psiEnvironment == null, "超限时不应触碰 psi.EnvironmentVariables");
            }
            else
            {
                Notice("SKIP ChildEnv_TooLargeBlock_ReturnsChineseError 的 psi 未播种断言：找不到 environmentVariables 私有字段");
            }

            Assert(Environment.GetEnvironmentVariable(key) == null, "超限时不应把覆盖项写进本进程环境：" + key);
        }

        private static void ChildEnvNoOverridesIsNoOp()
        {
            var psi = new ProcessStartInfo("cmd.exe");
            string key = "GOSTGUI_SELFTEST_NOOP_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var logs = new List<string>();

            string error;
            ChildEnvironment.Apply(psi, null, logs.Add, out error);
            AssertEqual("", error);
            ChildEnvironment.Apply(psi, new List<KeyValuePair<string, string>>(), logs.Add, out error);
            AssertEqual("", error);

            AssertEqual(0, logs.Count);
            System.Collections.Specialized.StringDictionary psiEnvironment;
            if (TryGetStartInfoEnvironment(psi, out psiEnvironment))
            {
                Assert(psiEnvironment == null, "无覆盖项时不应访问 psi.EnvironmentVariables");
            }
            else
            {
                Notice("SKIP ChildEnv_NoOverrides_IsNoOp 的 psi 未播种断言：找不到 environmentVariables 私有字段");
            }

            Assert(Environment.GetEnvironmentVariable(key) == null, "无覆盖项时不应写本进程环境：" + key);
        }

        /// <summary>
        /// R12 的 `&gt;` 比较边界：环境块恰好 32767 字节必须接受，32768 字节必须拒绝。
        /// 先跑拒绝分支（守卫不触发规范化），再用同一父环境口径跑接受分支（它才会触发一次性规范化）。
        /// </summary>
        private static void ChildEnvBlockSizeBoundary()
        {
            const int limit = 32767;
            string key = "GOSTGUI_SELFTEST_BOUNDARY_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            long parentSize = ParentEnvironmentBlockSize();
            int atLimitLength = (int)(limit - parentSize - (key.Length + 2));
            Assert(atLimitLength > 0, "父环境过大，构造不出 32767 边界夹具（父环境块 " + parentSize + " 字节）");

            // 32768：拒绝，错误文本应报告真实大小与上限；守卫在规范化之前，不应产生日志。
            var tooBigPsi = new ProcessStartInfo("cmd.exe");
            var tooBigLogs = new List<string>();
            string error;
            ChildEnvironment.Apply(tooBigPsi,
                new[] { new KeyValuePair<string, string>(key, new string('x', atLimitLength + 1)) },
                tooBigLogs.Add, out error);
            Assert(!string.IsNullOrEmpty(error), "32768 字节必须被拒绝");
            Assert(error.Contains("32768"), "错误信息应报告实际大小 32768：" + error);
            Assert(error.Contains("32767"), "错误信息应点名上限 32767：" + error);
            AssertEqual(0, tooBigLogs.Count);

            // 恰好 32767：接受（error 为空）。本分支会触发一次性的父环境规范化，所以用例登记在 Direct 之后。
            var atLimitPsi = new ProcessStartInfo("cmd.exe");
            var atLimitLogs = new List<string>();
            ChildEnvironment.Apply(atLimitPsi,
                new[] { new KeyValuePair<string, string>(key, new string('x', atLimitLength)) },
                atLimitLogs.Add, out error);
            AssertEqual("", error);
        }
    }
}
