// =====================================================================================
// SelfTest.Resolver.cs —— 程序解析用例：PATH / PATHEXT / 有效环境与路径规范化。
// 本文件是自检套件的 partial：原单体 SelfTest.cs 已按领域拆分（该文件已不存在），用例名/断言/注册顺序未改。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using gostGUI.Shared;

namespace gostGUI
{
    internal static partial class SelfTest
    {
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
    }
}
