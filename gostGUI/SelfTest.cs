using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using gostGUI.Shared;
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
            Check("Load_OldFormat_GetsDefaults", LoadOldFormatGetsDefaults);
            Check("Save_ThenLoad_RoundTripsNewFields", SaveThenLoadRoundTripsNewFields);
            Check("Load_UnknownFieldsAndWrongTypes_DoesNotThrow", LoadUnknownFieldsAndWrongTypesDoesNotThrow);
            Check("Load_MissingFile_ReturnsOkWithDefaults", LoadMissingFileReturnsOkWithDefaults);
            Check("Env_IsCaseInsensitive", EnvIsCaseInsensitive);
            Check("Env_ParseLines_SkipsBlanksAndComments", EnvParseLinesSkipsBlanksAndComments);
            Check("Env_ParseLines_SplitsOnFirstEquals", EnvParseLinesSplitsOnFirstEquals);
            Check("Env_Expand_ItemOrderThenParent", EnvExpandItemOrderThenParent);
            Check("Env_BuildEffective_OverridesCaseInsensitive", EnvBuildEffectiveOverridesCaseInsensitive);
            Check("Env_BuildEffective_TooLarge_ReturnsError", EnvBuildEffectiveTooLargeReturnsError);
            Check("Env_FormatLines_RoundTrips", EnvFormatLinesRoundTrips);

            // 程序解析：PATH / PATHEXT / 有效环境。夹具只建空文件，绝不启动进程。
            Check("Resolve_BareName_FoundInPathDir", ResolveBareNameFoundInPathDir);
            Check("Resolve_BareName_AppDirWins", ResolveBareNameAppDirWins);
            Check("Resolve_AppliesPathext", ResolveAppliesPathext);
            Check("Resolve_UsesEffectiveEnvironment", ResolveUsesEffectiveEnvironment);
            Check("Resolve_PathWithSpacesAndQuotes", ResolvePathWithSpacesAndQuotes);
            Check("Resolve_RelativeToBaseDir", ResolveRelativeToBaseDir);
            Check("Resolve_BatchFile_KindIsBatch", ResolveBatchFileKindIsBatch);
            Check("Resolve_Missing_ReportsSearchedCount", ResolveMissingReportsSearchedCount);
            Check("Resolve_PathLookup_IsCaseInsensitive", ResolvePathLookupIsCaseInsensitive);
            Check("Resolve_Pathext_FallsBackToDefault", ResolvePathextFallsBackToDefault);
            Check("Resolve_PathEntry_QuotesAndVariables", ResolvePathEntryQuotesAndVariables);
            Check("Resolve_Ps1_IsRejected", ResolvePs1IsRejected);
            Check("Resolve_MalformedPath_DoesNotThrow", ResolveMalformedPathDoesNotThrow);
            Check("Resolve_EmptyProgram_ReturnsError", ResolveEmptyProgramReturnsError);
        }

        // 配置模型用例全部使用绝对临时路径：--selftest 不改变当前目录。
        private static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gostgui-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTempDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception)
            {
                // 临时目录清理失败不影响用例结论。
            }
        }

        private static string WriteConfig(string dir, string json)
        {
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }

        private static void LoadOldFormatGetsDefaults()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = WriteConfig(dir,
                    "{\n" +
                    "  \"autoRun\": true,\n" +
                    "  \"items\": [\n" +
                    "    { \"name\": \"old\", \"enable\": true, \"program\": \"gost.exe\", \"args\": \"-L 127.0.0.1:8080\" }\n" +
                    "  ]\n" +
                    "}");

                ConfigLoadResult result = Common.LoadFrom(path);
                Assert(result != null, "LoadFrom 不应返回 null");
                Assert(result.Ok, "旧格式配置应加载成功：" + result.Error);
                Assert(result.Data != null, "加载结果应包含数据");
                AssertEqual(1, result.Data.Items.Count);

                ConfigItem item = result.Data.Items[0];
                AssertEqual("old", item.Name);
                AssertEqual(true, item.Enable);
                AssertEqual("gost.exe", item.Program);
                AssertEqual("-L 127.0.0.1:8080", item.Args);
                AssertEqual("", item.WorkDir);
                Assert(item.Env == null, "旧格式没有 env 时 Env 应为 null（表示不覆盖）");
                AssertEqual(false, item.RunAsAdmin);

                AssertEqual(ThemeMode.System, result.Data.Theme);
                Assert(result.Data.Window != null, "旧格式没有 window 时应使用默认窗口状态");
                AssertEqual(1024, result.Data.Window.W);
                AssertEqual(640, result.Data.Window.H);
                AssertEqual(-1, result.Data.Window.X);
                AssertEqual(-1, result.Data.Window.Y);
                AssertEqual(false, result.Data.Window.Max);
                AssertEqual("", result.Error);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        private static void SaveThenLoadRoundTripsNewFields()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                ConfigData data = new ConfigData
                {
                    AutoRun = true,
                    Theme = ThemeMode.Dark,
                    Window = new ConfigWindow { W = 1280, H = 800, X = 100, Y = 50, Max = true },
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem
                        {
                            Name = "节点一",
                            Enable = true,
                            Program = "gost.exe",
                            Args = "-L 127.0.0.1:8080",
                            WorkDir = "D:\\工作 目录",
                            Env = new Dictionary<string, string>
                            {
                                { "GOST_LOG", "debug" },
                                { "EMPTY", "" },
                                { "中文键", "中文值" }
                            },
                            RunAsAdmin = true
                        }
                    }
                };

                Assert(Common.SaveTo(data, path), "保存应成功：" + Common.LastSaveError);
                Assert(File.Exists(path), "保存后配置文件应存在");
                Assert(!File.Exists(path + ".tmp"), "保存后不应残留临时文件");

                ConfigLoadResult result = Common.LoadFrom(path);
                Assert(result.Ok, "重新加载应成功：" + result.Error);
                ConfigData loaded = result.Data;
                AssertEqual(true, loaded.AutoRun);
                AssertEqual(ThemeMode.Dark, loaded.Theme);
                AssertEqual(1280, loaded.Window.W);
                AssertEqual(800, loaded.Window.H);
                AssertEqual(100, loaded.Window.X);
                AssertEqual(50, loaded.Window.Y);
                AssertEqual(true, loaded.Window.Max);

                AssertEqual(1, loaded.Items.Count);
                ConfigItem item = loaded.Items[0];
                AssertEqual("节点一", item.Name);
                AssertEqual("D:\\工作 目录", item.WorkDir);
                AssertEqual(true, item.RunAsAdmin);
                Assert(item.Env != null, "env 不应丢失");
                AssertEqual("debug", item.Env["GOST_LOG"]);
                AssertEqual("", item.Env["EMPTY"]);
                AssertEqual("中文值", item.Env["中文键"]);
                AssertEqual(3, item.Env.Count);
                AssertEqual("GOST_LOG", new List<string>(item.Env.Keys)[0]);
                Assert(File.ReadAllText(path, Encoding.UTF8).Contains("中文值"), "中文值不应被转义成 \\uXXXX");
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        private static void LoadUnknownFieldsAndWrongTypesDoesNotThrow()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = WriteConfig(dir,
                    "{\n" +
                    "  \"unknownTop\": { \"x\": 1 },\n" +
                    "  \"theme\": \"unknown-theme\",\n" +
                    "  \"items\": [\n" +
                    "    { \"name\": \"a\", \"enable\": true, \"program\": \"gost.exe\", \"args\": \"\", \"unknownItem\": [1, 2, 3] }\n" +
                    "  ]\n" +
                    "}");

                ConfigLoadResult ignored = Common.LoadFrom(path);
                Assert(ignored.Ok, "未知字段应被忽略且加载成功：" + ignored.Error);
                AssertEqual(1, ignored.Data.Items.Count);
                AssertEqual(ThemeMode.System, ignored.Data.Theme);

                // 类型不符：enable 是字符串而不是 bool，必须返回 Ok=false 而不是抛异常。
                path = WriteConfig(dir, "{\"items\":[{\"name\":\"a\",\"enable\":\"yes\"}]}");
                ConfigLoadResult wrongType = Common.LoadFrom(path);
                Assert(!wrongType.Ok, "类型不符时 Ok 应为 false");
                Assert(!string.IsNullOrEmpty(wrongType.Error), "类型不符时应给出中文错误说明");
                Assert(wrongType.Data != null, "加载失败时也应返回可用的默认数据");
                Assert(wrongType.Data.Items != null, "加载失败时 Items 不应为 null");
                AssertEqual(0, wrongType.Data.Items.Count);

                // window 类型不符同样返回失败而不是崩溃。
                path = WriteConfig(dir, "{\"window\": \"big\"}");
                ConfigLoadResult wrongWindow = Common.LoadFrom(path);
                Assert(!wrongWindow.Ok, "window 类型不符时 Ok 应为 false");

                // env 类型不符。
                path = WriteConfig(dir, "{\"items\":[{\"name\":\"a\",\"env\":\"K=V\"}]}");
                ConfigLoadResult wrongEnv = Common.LoadFrom(path);
                Assert(!wrongEnv.Ok, "env 类型不符时 Ok 应为 false");
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        private static void LoadMissingFileReturnsOkWithDefaults()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "no-such-config.json");
                ConfigLoadResult result = Common.LoadFrom(path);
                Assert(result != null, "LoadFrom 不应返回 null");
                Assert(result.Ok, "文件不存在时应按默认值成功：" + result.Error);
                AssertEqual("", result.Error);
                Assert(result.Data != null, "加载结果应包含默认数据");
                Assert(result.Data.Items != null, "Items 不应为 null");
                AssertEqual(0, result.Data.Items.Count);
                AssertEqual(false, result.Data.AutoRun);
                AssertEqual(ThemeMode.System, result.Data.Theme);
                Assert(result.Data.Window != null, "Window 不应为 null");
                AssertEqual(1024, result.Data.Window.W);
                AssertEqual(640, result.Data.Window.H);
                Assert(!File.Exists(path), "加载不存在的文件不应创建它");
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

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

        // 程序解析用例的夹具：临时目录 + 指定的空文件（相对路径可以带子目录），只建不跑。

        private static string CreateTempDirectoryWithFiles(params string[] relativePaths)
        {
            string dir = CreateTempDirectory();
            foreach (string relativePath in relativePaths)
            {
                string fullPath = Path.Combine(dir, relativePath);
                string parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.WriteAllBytes(fullPath, new byte[0]);
            }

            return dir;
        }

        /// <summary>把若干片段拼成绝对路径，供断言里的 FullPath 期望值使用。</summary>
        private static string AbsolutePathOf(string dir, params string[] parts)
        {
            string combined = dir;
            foreach (string part in parts)
            {
                combined = Path.Combine(combined, part);
            }

            return Path.GetFullPath(combined);
        }

        /// <summary>
        /// Windows 路径大小写不敏感：按 PATHEXT 补全时可能带出 .EXE / .BAT 这种大写写法，
        /// 比较 FullPath 时忽略大小写（指向的确实是同一个文件）。
        /// </summary>
        private static void AssertSamePath(string expected, string actual)
        {
            Assert(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
                $"断言失败：期望路径 <{expected}>，实际 <{actual}>");
        }

        private static void ResolveBareNameFoundInPathDir()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("fake.exe");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir }
                };

                ResolvedProgram result = ProgramResolver.Resolve("fake.exe", appDir, env);

                Assert(result != null, "Resolve 不应返回 null");
                Assert(result.Found, "PATH 目录里的 fake.exe 应命中：" + result.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "fake.exe"), result.FullPath);
                AssertEqual(ChildKind.Executable, result.Kind);
                AssertEqual("", result.Error);
                // 应用目录 + PATH 里的 1 个目录。
                AssertEqual(2, result.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolveBareNameAppDirWins()
        {
            string appDir = CreateTempDirectoryWithFiles("fake.exe");
            string toolsDir = CreateTempDirectoryWithFiles("fake.exe");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir }
                };

                ResolvedProgram result = ProgramResolver.Resolve("fake.exe", appDir, env);

                Assert(result.Found, "应用目录里的 fake.exe 应命中：" + result.Error);
                // 同名文件同时存在时应用目录优先，且只搜到第一个目录就停。
                AssertSamePath(AbsolutePathOf(appDir, "fake.exe"), result.FullPath);
                AssertEqual(1, result.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolveAppliesPathext()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("tool.exe");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir }
                };

                ResolvedProgram result = ProgramResolver.Resolve("tool", appDir, env);

                Assert(result.Found, "裸名 tool 应按 PATHEXT 补全成 tool.exe：" + result.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.exe"), result.FullPath);
                AssertEqual(ChildKind.Executable, result.Kind);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolveUsesEffectiveEnvironment()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string emptyDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("onlyineffective.exe");
            try
            {
                // 还没追加临时目录时的有效环境：PATH 里只有空目录，找不到。
                var parentLike = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", emptyDir }
                };
                ResolvedProgram withoutOverride = ProgramResolver.Resolve("onlyineffective", appDir, parentLike);
                Assert(!withoutOverride.Found, "这一轮不应找到 onlyineffective");

                // 本项 env 追加 PATH 后（模拟 PATH=%PATH%;临时目录），有效环境里才有它；不改进程环境。
                var withOverride = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", emptyDir + ";" + toolsDir }
                };
                ResolvedProgram withEnv = ProgramResolver.Resolve("onlyineffective", appDir, withOverride);

                Assert(withEnv.Found, "PATH 来自有效环境，追加后应命中：" + withEnv.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "onlyineffective.exe"), withEnv.FullPath);
                // 应用目录 + 空目录 + 临时工具目录。
                AssertEqual(3, withEnv.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(emptyDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolvePathWithSpacesAndQuotes()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("a b", "x.exe"));
            try
            {
                string program = "\"" + AbsolutePathOf(appDir, "a b", "x.exe") + "\"";
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // 首尾空白 + 成对引号都必须被剥掉。
                ResolvedProgram result = ProgramResolver.Resolve("  " + program + "  ", appDir, env);

                Assert(result.Found, "带空格并加引号的完整路径应命中：" + result.Error);
                AssertSamePath(AbsolutePathOf(appDir, "a b", "x.exe"), result.FullPath);
                AssertEqual(ChildKind.Executable, result.Kind);
                // 显式路径不搜 PATH，目录数应为 0。
                AssertEqual(0, result.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveRelativeToBaseDir()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("sub", "rel.exe"));
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ResolvedProgram result = ProgramResolver.Resolve(Path.Combine("sub", "rel.exe"), appDir, env);
                Assert(result.Found, "相对路径应按 baseDir 解析：" + result.Error);
                AssertSamePath(AbsolutePathOf(appDir, "sub", "rel.exe"), result.FullPath);

                // 相对路径没有扩展名时同样按 PATHEXT 补全。
                ResolvedProgram completed = ProgramResolver.Resolve(Path.Combine("sub", "rel"), appDir, env);
                Assert(completed.Found, "相对路径应按 PATHEXT 补全：" + completed.Error);
                AssertSamePath(AbsolutePathOf(appDir, "sub", "rel.exe"), completed.FullPath);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveBatchFileKindIsBatch()
        {
            string appDir = CreateTempDirectoryWithFiles("run.cmd", "run.bat");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ResolvedProgram cmd = ProgramResolver.Resolve("run.cmd", appDir, env);
                Assert(cmd.Found, "run.cmd 应命中：" + cmd.Error);
                AssertEqual(ChildKind.Batch, cmd.Kind);

                ResolvedProgram bat = ProgramResolver.Resolve("run.bat", appDir, env);
                Assert(bat.Found, "run.bat 应命中：" + bat.Error);
                AssertEqual(ChildKind.Batch, bat.Kind);

                // 裸名 run 按默认 PATHEXT 顺序（.COM;.EXE;.BAT;.CMD）先命中 .bat。
                ResolvedProgram bare = ProgramResolver.Resolve("run", appDir, env);
                Assert(bare.Found, "裸名 run 应按 PATHEXT 命中：" + bare.Error);
                AssertSamePath(AbsolutePathOf(appDir, "run.bat"), bare.FullPath);
                AssertEqual(ChildKind.Batch, bare.Kind);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveMissingReportsSearchedCount()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string dir1 = CreateTempDirectoryWithFiles();
            string dir2 = CreateTempDirectoryWithFiles();
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", dir1 + ";" + dir2 }
                };

                ResolvedProgram result = ProgramResolver.Resolve("nope", appDir, env);

                Assert(!result.Found, "不存在的程序不应命中");
                // 应用目录 + PATH 的 2 个目录。
                AssertEqual(3, result.SearchedDirectories);
                Assert(!string.IsNullOrEmpty(result.Error), "找不到时应给出中文错误");
                Assert(result.Error.Contains("未找到"), "错误应说明找不到：" + result.Error);
                Assert(result.Error.Contains("3"), "错误应带上搜索目录数：" + result.Error);
                Assert(string.IsNullOrEmpty(result.FullPath), "找不到时 FullPath 应为空");
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(dir1);
                DeleteTempDirectory(dir2);
            }
        }

        private static void ResolvePathLookupIsCaseInsensitive()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("tool.exe");
            try
            {
                // 默认比较器是区分大小写的，键名故意写成 pAtH，解析器仍须大小写不敏感地取到 PATH。
                var env = new Dictionary<string, string>
                {
                    { "pAtH", toolsDir }
                };

                ResolvedProgram result = ProgramResolver.Resolve("tool.exe", appDir, env);

                Assert(result.Found, "PATH 查找应忽略大小写：" + result.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.exe"), result.FullPath);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolvePathextFallsBackToDefault()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string toolsDir = CreateTempDirectoryWithFiles("tool.cmd");
            try
            {
                // 完全没有 PATHEXT 时必须用默认的 .COM;.EXE;.BAT;.CMD。
                var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir }
                };
                ResolvedProgram absent = ProgramResolver.Resolve("tool", appDir, missing);
                Assert(absent.Found, "PATHEXT 缺失时应回落到默认值：" + absent.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.cmd"), absent.FullPath);
                AssertEqual(ChildKind.Batch, absent.Kind);

                // PATHEXT 为空字符串时同样回落。
                var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", toolsDir },
                    { "PATHEXT", "" }
                };
                ResolvedProgram blank = ProgramResolver.Resolve("tool", appDir, empty);
                Assert(blank.Found, "PATHEXT 为空时应回落到默认值：" + blank.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.cmd"), blank.FullPath);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(toolsDir);
            }
        }

        private static void ResolvePathEntryQuotesAndVariables()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string root = CreateTempDirectoryWithFiles(Path.Combine("tools dir", "tool.exe"));
            try
            {
                string toolsDir = Path.Combine(root, "tools dir");
                // PATH 项带引号且含 %VAR%，两件事都要在搜索前处理掉。
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TOOLS", toolsDir },
                    { "PATH", "\"%TOOLS%\"" }
                };

                ResolvedProgram result = ProgramResolver.Resolve("tool", appDir, env);

                Assert(result.Found, "带引号且含 %VAR% 的 PATH 项应命中：" + result.Error);
                AssertSamePath(AbsolutePathOf(toolsDir, "tool.exe"), result.FullPath);
                AssertEqual(2, result.SearchedDirectories);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(root);
            }
        }

        private static void ResolvePs1IsRejected()
        {
            string appDir = CreateTempDirectoryWithFiles("script.ps1");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ResolvedProgram result = ProgramResolver.Resolve(AbsolutePathOf(appDir, "script.ps1"), appDir, env);

                Assert(!result.Found, ".ps1 不应被判为可启动程序");
                Assert(!string.IsNullOrEmpty(result.Error), "不支持的类型应给出中文错误");
                Assert(result.Error.Contains(".ps1"), "错误应点名 .ps1：" + result.Error);
                Assert(result.Error.Contains("powershell -File"), "错误应提示改用 powershell -File：" + result.Error);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveMalformedPathDoesNotThrow()
        {
            string appDir = CreateTempDirectoryWithFiles();
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // 内嵌 NUL 的路径无法规范化：必须返回失败而不是抛异常。
                string malformed = appDir + Path.DirectorySeparatorChar + "bad\0name" + Path.DirectorySeparatorChar + "x.exe";
                ResolvedProgram result = ProgramResolver.Resolve(malformed, appDir, env);

                Assert(result != null, "Resolve 不应返回 null");
                Assert(!result.Found, "非法路径不应命中");
                Assert(!string.IsNullOrEmpty(result.Error), "非法路径应给出中文错误");
                Assert(result.Error.Contains("路径无效"), "错误应说明路径无效：" + result.Error);
                Assert(string.IsNullOrEmpty(result.FullPath), "非法路径不应返回 FullPath");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void ResolveEmptyProgramReturnsError()
        {
            string appDir = CreateTempDirectoryWithFiles();
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (string program in new[] { null, "", "   ", "\"\"", "  \"  \"  " })
                {
                    ResolvedProgram result = ProgramResolver.Resolve(program, appDir, env);
                    Assert(result != null, "Resolve 不应返回 null");
                    Assert(!result.Found, "空程序不应命中");
                    Assert(result.Error.Contains("未设置程序"), "空程序应提示未设置程序：" + result.Error);
                    AssertEqual(0, result.SearchedDirectories);
                }
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
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
