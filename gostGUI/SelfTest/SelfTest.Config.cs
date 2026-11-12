// =====================================================================================
// SelfTest.Config.cs —— 配置用例：模型加载/保存往返、旧格式、缺文件、失败先备份、复制、排序与保存失败可见性。
// 本文件是自检套件的 partial：原单体 SelfTest.cs 已按领域拆分（该文件已不存在），用例名/断言/注册顺序未改。
// =====================================================================================

using System.Collections.Generic;
using System.IO;
using System.Text;

namespace gostGUI
{
    internal static partial class SelfTest
    {
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

        /// <summary>
        /// 加载失败后第一次保存前必须把原文件备份成 config.json.bak（只备份一次），
        /// 备份不了就拒绝保存，绝不静默覆盖用户那份解析不了的配置。
        /// </summary>
        private static void ConfigSaveAfterFailedLoadBacksUpOriginal()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                const string corrupt = "{ 这不是合法 JSON ";
                File.WriteAllText(path, corrupt, new UTF8Encoding(false));

                var manager = new ConfigurationManager(path);
                Assert(!string.IsNullOrEmpty(manager.LoadError), "损坏的配置应记录中文加载错误。");
                AssertEqual(path + ".bak", manager.BackupFilePath);
                Assert(!File.Exists(manager.BackupFilePath), "加载阶段不应创建备份。");

                ConfigItem added = manager.AddNewItem();
                Assert(added != null, "AddNewItem 应返回新项。");
                Assert(File.Exists(manager.BackupFilePath), "加载失败后的第一次保存前必须备份原文件。");
                AssertEqual(corrupt, File.ReadAllText(manager.BackupFilePath, Encoding.UTF8));
                AssertEqual("", manager.SaveError);

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "覆盖后的配置应可解析：" + reloaded.Error);
                AssertEqual(1, reloaded.Data.Items.Count);

                // 只备份一次：第二次保存不再覆盖备份文件。
                File.WriteAllText(manager.BackupFilePath, "keep-me", new UTF8Encoding(false));
                manager.AddNewItem();
                AssertEqual("keep-me", File.ReadAllText(manager.BackupFilePath, Encoding.UTF8));
                AssertEqual("", manager.SaveError);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>复制配置项：名字为“原名 副本”（重名加序号），插在源项后面，且不继承运行状态。</summary>
        private static void ConfigDuplicateItemNamesAndInsertsAfterSource()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                var data = new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem
                        {
                            Name = "原始",
                            Enable = true,
                            Program = "gost.exe",
                            Args = "-L 127.0.0.1:8080",
                            WorkDir = "D:\\运行 目录",
                            RunAsAdmin = true,
                            Env = new Dictionary<string, string> { { "GOST_LOG", "debug" } }
                        },
                        new ConfigItem { Name = "第二", Program = "other.exe" }
                    }
                };
                Assert(Common.SaveTo(data, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                ConfigItem source = manager.Items[0];
                ConfigItem copy = manager.DuplicateItem(source);

                Assert(copy != null, "复制应返回新项。");
                AssertEqual("原始 副本", copy.Name);
                AssertEqual(1, new List<ConfigItem>(manager.Items).IndexOf(copy));
                AssertEqual(false, copy.Enable);
                AssertEqual(source.Program, copy.Program);
                AssertEqual(source.Args, copy.Args);
                AssertEqual(source.WorkDir, copy.WorkDir);
                AssertEqual(true, copy.RunAsAdmin);
                Assert(copy.Env != null && copy.Env["GOST_LOG"] == "debug", "复制应带上环境变量。");
                Assert(!ReferenceEquals(source.Env, copy.Env), "环境变量必须是独立副本。");

                ConfigItem second = manager.DuplicateItem(source);
                AssertEqual("原始 副本 2", second.Name);
                Assert(manager.DuplicateItem(null) == null, "复制 null 应返回 null 而不是抛异常。");

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "落盘后的配置应可解析：" + reloaded.Error);
                AssertEqual(4, reloaded.Data.Items.Count);
                // 每个副本都紧跟在源项后面：第二次复制后顺序是 原始 / 原始 副本 2 / 原始 副本 / 第二。
                AssertEqual("原始 副本 2", reloaded.Data.Items[1].Name);
                AssertEqual("原始 副本", reloaded.Data.Items[2].Name);
                AssertEqual("第二", reloaded.Data.Items[3].Name);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>上移/下移改变 ConfigurationManager.Items 的顺序并立即写盘；越界返回 false。</summary>
        private static void ConfigMoveItemPersistsOrder()
        {
            string dir = CreateTempDirectory();
            try
            {
                string path = Path.Combine(dir, "config.json");
                var data = new ConfigData
                {
                    Items = new List<ConfigItem>
                    {
                        new ConfigItem { Name = "a", Program = "a.exe" },
                        new ConfigItem { Name = "b", Program = "b.exe" },
                        new ConfigItem { Name = "c", Program = "c.exe" }
                    }
                };
                Assert(Common.SaveTo(data, path), "写夹具应成功：" + Common.LastSaveError);

                var manager = new ConfigurationManager(path);
                Assert(manager.MoveItem(manager.Items[2], -1), "第三项上移应成功。");
                AssertEqual("a", manager.Items[0].Name);
                AssertEqual("c", manager.Items[1].Name);
                AssertEqual("b", manager.Items[2].Name);

                ConfigLoadResult reloaded = Common.LoadFrom(path);
                Assert(reloaded.Ok, "落盘后的配置应可解析：" + reloaded.Error);
                AssertEqual("c", reloaded.Data.Items[1].Name);
                AssertEqual("b", reloaded.Data.Items[2].Name);

                Assert(!manager.MoveItem(manager.Items[0], -1), "已在最前时上移应返回 false。");
                Assert(!manager.MoveItem(manager.Items[2], 1), "已在最后时下移应返回 false。");
                Assert(!manager.MoveItem(null, 1), "null 项应返回 false。");
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }

        /// <summary>
        /// 数据层不得吞掉保存失败：以目录作为配置路径时 Save() 必然失败，失败必须同时可观测：
        /// 返回 false、SaveError 非空且为中文原因、SaveFailed 事件带同一原因触发一次。
        /// 全程使用绝对临时路径，不碰用户那份 config.json。
        /// </summary>
        private static void ConfigSaveFailureSurfaces()
        {
            string dir = CreateTempDirectory();
            try
            {
                // 目录不能被当作 config.json 写入：临时文件写完后替换/移动目标必然失败。
                string unsavablePath = Path.Combine(dir, "config-as-directory");
                Directory.CreateDirectory(unsavablePath);

                var manager = new ConfigurationManager(unsavablePath);
                AssertEqual("", manager.SaveError);

                int raised = 0;
                string reported = null;
                manager.SaveFailed += message =>
                {
                    raised++;
                    reported = message;
                };

                Assert(!manager.Save(), "以目录为配置路径时 Save() 必须返回 false。");
                Assert(!string.IsNullOrEmpty(manager.SaveError), "保存失败时 SaveError 必须保留原因。");
                Assert(manager.SaveError.Contains("保存"), "SaveError 应为中文保存错误：" + manager.SaveError);
                AssertEqual(1, raised);
                AssertEqual(manager.SaveError, reported);
            }
            finally
            {
                DeleteTempDirectory(dir);
            }
        }
    }
}
