// =====================================================================================
// SelfTest.Pipeline.cs —— 启动管线用例：有效环境 → 解析程序 → 工作目录 → 启动参数。
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
        // 启动管线用例的夹具：配置项 + 空父环境；全部使用绝对路径，临时目录由用例自己清理。

        private static Dictionary<string, string> EmptyEnv()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static ConfigItem NewLaunchItem(string program, string args = "", string workDir = "",
            Dictionary<string, string> env = null)
        {
            var item = new ConfigItem
            {
                Program = program,
                Args = args,
                WorkDir = workDir
            };
            if (env != null)
            {
                item.Env = env;
            }

            return item;
        }

        private static void PipelineEmptyProgramReturnsError()
        {
            string appDir = CreateTempDirectoryWithFiles();
            try
            {
                foreach (string program in new[] { null, "", "   " })
                {
                    LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(program), appDir, EmptyEnv());

                    Assert(request != null, "Build 不应返回 null");
                    Assert(!string.IsNullOrEmpty(request.Error), "空程序应填入中文错误");
                    Assert(request.Error.Contains("未设置程序"), "空程序错误应提示未设置程序：" + request.Error);
                    Assert(string.IsNullOrEmpty(request.FileName), "空程序不应给出启动文件名");
                    AssertEqual(ChildKind.Executable, request.Kind);
                    Assert(!request.IsBatch, "空程序不应按批处理包装");
                    Assert(request.EnvOverrides != null, "失败时 EnvOverrides 也不应为 null");
                }
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineResolvesAndExpandsWorkDir()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            string runDir = CreateTempDirectory();
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "MYDIR", runDir }
                };
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "-x 1", "%MYDIR%", env);

                LaunchRequest request = LaunchPipeline.Build(item, appDir, EmptyEnv());

                AssertEqual("", request.Error);
                AssertSamePath(AbsolutePathOf(appDir, "app.exe"), request.FileName);
                AssertEqual("-x 1", request.Arguments);
                // 覆盖表里给值的 %MYDIR% 必须展开成覆盖表里的目录。
                AssertSamePath(runDir, request.WorkingDirectory);
                AssertEqual(ChildKind.Executable, request.Kind);
                Assert(!request.IsBatch, "exe 不应按批处理包装");
                Assert(request.EnvOverrides != null, "EnvOverrides 不应为 null");
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(runDir);
            }
        }

        private static void PipelineDefaultWorkDirIsBaseDir()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                LaunchRequest request = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe")), appDir, EmptyEnv());

                AssertEqual("", request.Error);
                AssertSamePath(Path.GetFullPath(appDir), request.WorkingDirectory);

                // WorkDir 为 null 与空字符串等价，同样回落到 baseDir。
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"));
                item.WorkDir = null;
                LaunchRequest nullDir = LaunchPipeline.Build(item, appDir, EmptyEnv());

                AssertEqual("", nullDir.Error);
                AssertSamePath(Path.GetFullPath(appDir), nullDir.WorkingDirectory);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineMissingWorkDirReturnsError()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                string missing = Path.Combine(appDir, "no-such-dir");
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", missing);

                LaunchRequest request = LaunchPipeline.Build(item, appDir, EmptyEnv());

                Assert(!string.IsNullOrEmpty(request.Error), "工作目录不存在时必须填入错误");
                AssertEqual("工作目录不存在：" + Path.GetFullPath(missing), request.Error);
                Assert(string.IsNullOrEmpty(request.WorkingDirectory), "失败时 WorkingDirectory 应为空字符串");
                Assert(string.IsNullOrEmpty(request.FileName), "工作目录失败时不应给出启动文件名");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineBatchWrapsWithCmd()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("a b", "x.bat"));
            try
            {
                string bat = AbsolutePathOf(appDir, "a b", "x.bat");
                string expectedComSpec = Environment.GetEnvironmentVariable("COMSPEC");
                if (string.IsNullOrEmpty(expectedComSpec))
                {
                    expectedComSpec = "cmd.exe";
                }

                // COMSPEC 现在从父环境快照（有效环境）取，不再读活环境：快照里显式给出才能断言具体路径。
                var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "COMSPEC", expectedComSpec }
                };
                LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(bat, "-flag"), appDir, parent);

                AssertEqual("", request.Error);
                AssertSamePath(expectedComSpec, request.FileName);
                // 标准 cmd /s /c 形式：整条命令再包一层引号，bat 路径单独加引号。
                AssertEqual("/d /s /c \"\"" + bat + "\" -flag\"", request.Arguments);
                AssertEqual(ChildKind.Batch, request.Kind);
                Assert(request.IsBatch, "Batch 类型的 IsBatch 应为 true");
                AssertSamePath(Path.GetFullPath(appDir), request.WorkingDirectory);

                // 快照里没有 COMSPEC 时回落到裸名 cmd.exe（不再偷看活环境）。
                LaunchRequest withoutComSpec = LaunchPipeline.Build(NewLaunchItem(bat, "-flag"), appDir, EmptyEnv());
                AssertEqual("", withoutComSpec.Error);
                AssertEqual("cmd.exe", withoutComSpec.FileName);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineKeepsArgsVerbatim()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("dir with space", "app.exe"));
            try
            {
                string exe = AbsolutePathOf(appDir, "dir with space", "app.exe");
                const string args = "-L \"127.0.0.1:8080\" --header='a b' \"\"";
                LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(exe, args), appDir, EmptyEnv());

                AssertEqual("", request.Error);
                AssertSamePath(exe, request.FileName);
                // 参数原样透传：不加引号、不剥引号、不 Trim。
                AssertEqual(args, request.Arguments);
                AssertEqual(ChildKind.Executable, request.Kind);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelinePropagatesEnvOverrides()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ROOT", @"C:\root" },
                    { "PATH", @"C:\windows" }
                };
                var overrides = new Dictionary<string, string>
                {
                    { "BASE", @"C:\tools" },
                    { "PATH", @"%BASE%;%ROOT%" },
                    { "COPY", "%PATH%" }
                };
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", "", overrides);

                LaunchRequest request = LaunchPipeline.Build(item, appDir, parent);

                AssertEqual("", request.Error);
                Assert(request.EnvOverrides != null, "EnvOverrides 不应为 null");
                // 只带本项覆盖项，父环境不塞进来（启动方自己合并）。
                AssertEqual(3, request.EnvOverrides.Count);
                AssertEqual(@"C:\tools;C:\root", request.EnvOverrides["PATH"]);
                AssertEqual(@"C:\tools;C:\root", request.EnvOverrides["COPY"]);
                AssertEqual(@"C:\tools", request.EnvOverrides["BASE"]);
                Assert(!request.EnvOverrides.ContainsKey("ROOT"), "父环境变量不应出现在 EnvOverrides 里");
                Assert(request.EnvOverrides.ContainsKey("path"), "EnvOverrides 的键应大小写不敏感");

                // 没有任何覆盖项时是空表而不是 null。
                LaunchRequest plain = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe")), appDir, EmptyEnv());
                Assert(plain.EnvOverrides != null, "无覆盖项时 EnvOverrides 不应为 null");
                AssertEqual(0, plain.EnvOverrides.Count);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineWorkDirUsesParentEffectiveEnv()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            string runDir = CreateTempDirectory();
            try
            {
                // 变量来自父环境：有效环境包含父环境 + 覆盖项，所以 workDir 里的 %PARENTDIR% 同样要展开。
                var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PARENTDIR", runDir }
                };
                LaunchRequest request = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", "%PARENTDIR%"), appDir, parent);

                AssertEqual("", request.Error);
                AssertSamePath(runDir, request.WorkingDirectory);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(runDir);
            }
        }

        private static void PipelineRelativeWorkDirResolvesAgainstBaseDir()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                Directory.CreateDirectory(Path.Combine(appDir, "sub run"));
                LaunchRequest request = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", Path.Combine("sub run", "nested")),
                    appDir, EmptyEnv());

                // nested 不存在：错误里应出现按 baseDir 解析后的绝对路径，而不是当前目录。
                Assert(!string.IsNullOrEmpty(request.Error), "相对路径不存在时应填入错误");
                AssertEqual("工作目录不存在：" + AbsolutePathOf(appDir, "sub run", "nested"), request.Error);

                LaunchRequest existing = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", "sub run"), appDir, EmptyEnv());
                AssertEqual("", existing.Error);
                AssertSamePath(AbsolutePathOf(appDir, "sub run"), existing.WorkingDirectory);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineMalformedWorkDirDoesNotThrow()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                string malformed = appDir + Path.DirectorySeparatorChar + "bad\0dir";
                LaunchRequest request = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "", malformed), appDir, EmptyEnv());

                Assert(request != null, "Build 不应返回 null");
                Assert(!string.IsNullOrEmpty(request.Error), "畸形工作目录应填入中文错误");
                Assert(request.Error.Contains("工作目录"), "错误应说明工作目录无效：" + request.Error);
                Assert(string.IsNullOrEmpty(request.WorkingDirectory), "失败时 WorkingDirectory 应为空字符串");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineEnvTooLargeReturnsEnvError()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                // 覆盖项本身就超过 32K：错误必须原样从 EnvBlock 传到 LaunchRequest。
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"), "-x");
                item.Env = new Dictionary<string, string>
                {
                    { "BIG", new string('x', 32765) }
                };

                string envError;
                EnvBlock.BuildEffective(EmptyEnv(), item.Env, out envError);
                Assert(!string.IsNullOrEmpty(envError), "夹具应超过 32K 上限");

                LaunchRequest request = LaunchPipeline.Build(item, appDir, EmptyEnv());

                AssertEqual(envError, request.Error);
                // 顺序决定：环境先失败时不再解析程序，也不该给出启动文件名。
                Assert(string.IsNullOrEmpty(request.FileName), "环境块超限时不应解析程序");

                // 父环境已经超限时同样必须失败（不能只在覆盖项上校验）。
                var bigParent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "BIG", new string('x', 32765) }
                };
                string parentError;
                EnvBlock.BuildEffective(bigParent, EmptyEnv(), out parentError);
                Assert(!string.IsNullOrEmpty(parentError), "父环境夹具应超过 32K 上限");

                LaunchRequest fromParent = LaunchPipeline.Build(
                    NewLaunchItem(AbsolutePathOf(appDir, "app.exe")), appDir, bigParent);
                AssertEqual(parentError, fromParent.Error);
                Assert(string.IsNullOrEmpty(fromParent.FileName), "父环境超限时不应解析程序");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineNotFoundUsesR13Message()
        {
            string appDir = CreateTempDirectoryWithFiles();
            string emptyDir = CreateTempDirectoryWithFiles();
            try
            {
                var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "PATH", emptyDir }
                };

                LaunchRequest bare = LaunchPipeline.Build(
                    NewLaunchItem("definitely-not-a-real-tool-xyz"), appDir, parent);

                Assert(!string.IsNullOrEmpty(bare.Error), "裸名找不到时应填入错误");
                AssertEqual("在应用目录与有效 PATH 中均未找到 definitely-not-a-real-tool-xyz（共搜索 2 个目录）",
                    bare.Error);
                Assert(string.IsNullOrEmpty(bare.FileName), "找不到时不应给出启动文件名");

                // 显式路径未命中：沿用解析器原文（已经点名了路径），不能改写成 PATH 措辞。
                string explicitPath = Path.Combine(appDir, "missing", "nope.exe");
                LaunchRequest explicitRequest = LaunchPipeline.Build(
                    NewLaunchItem(explicitPath), appDir, parent);

                Assert(!string.IsNullOrEmpty(explicitRequest.Error), "显式路径不存在时应填入错误");
                Assert(explicitRequest.Error.Contains("未找到程序："),
                    "显式路径错误应沿用解析器原文：" + explicitRequest.Error);
                Assert(explicitRequest.Error.Contains(explicitPath),
                    "解析器错误应点名完整路径：" + explicitRequest.Error);
                Assert(!explicitRequest.Error.StartsWith("在应用目录与有效 PATH 中均未找到"),
                    "显式路径不应套用 PATH 措辞：" + explicitRequest.Error);
            }
            finally
            {
                DeleteTempDirectory(appDir);
                DeleteTempDirectory(emptyDir);
            }
        }

        private static void PipelineBatchWithEmptyArgsWrapsWithTrailingQuotes()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("a b", "x.bat"));
            try
            {
                string bat = AbsolutePathOf(appDir, "a b", "x.bat");

                LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(bat, ""), appDir, EmptyEnv());
                AssertEqual("", request.Error);
                // 空参数时结尾只剩两层引号：/d /s /c ""<path>""。
                AssertEqual("/d /s /c \"\"" + bat + "\"\"", request.Arguments);
                Assert(request.IsBatch, "空参数的 bat 仍应按批处理包装");

                // Args 为 null 与空字符串等价。
                LaunchRequest nullArgs = LaunchPipeline.Build(NewLaunchItem(bat, null), appDir, EmptyEnv());
                AssertEqual("", nullArgs.Error);
                AssertEqual("/d /s /c \"\"" + bat + "\"\"", nullArgs.Arguments);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        // R14：批处理路径含空格、参数含引号时的包装串必须逐字符正确；WorkDir = null 必须被容忍。

        private static void PipelineBatchWithSpacesAndQuotedArgsExactWrapper()
        {
            string appDir = CreateTempDirectoryWithFiles(Path.Combine("a b", "x.bat"));
            try
            {
                string bat = AbsolutePathOf(appDir, "a b", "x.bat");
                LaunchRequest request = LaunchPipeline.Build(NewLaunchItem(bat, "-x \"a b\""), appDir, EmptyEnv());

                AssertEqual("", request.Error);
                // 含空格路径 + 含引号参数：整条命令再包一层引号，用户参数原样拼接。
                AssertEqual("/d /s /c \"\"" + bat + "\" -x \"a b\"\"", request.Arguments);
                Assert(request.IsBatch, "含空格的 .bat 仍应按批处理包装");
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }

        private static void PipelineNullWorkDirIsTolerated()
        {
            string appDir = CreateTempDirectoryWithFiles("app.exe");
            try
            {
                ConfigItem item = NewLaunchItem(AbsolutePathOf(appDir, "app.exe"));
                item.WorkDir = null;

                LaunchRequest request = LaunchPipeline.Build(item, appDir, EmptyEnv());

                AssertEqual("", request.Error);
                AssertSamePath(Path.GetFullPath(appDir), request.WorkingDirectory);
            }
            finally
            {
                DeleteTempDirectory(appDir);
            }
        }
    }
}
