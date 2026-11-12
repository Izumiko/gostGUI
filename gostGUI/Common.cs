using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using gostGUI;


/// <summary>单个启动项配置。</summary>
public class ConfigItem
{
    private string _workDir = "";
    private Dictionary<string, string> _env;

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("program")]
    public string Program { get; set; }

    [JsonPropertyName("args")]
    public string Args { get; set; }

    /// <summary>工作目录；空字符串表示使用本工具所在目录（与 v1.8 行为一致）。</summary>
    [JsonPropertyName("workDir")]
    public string WorkDir
    {
        get { return _workDir; }
        set { _workDir = value ?? ""; }
    }

    /// <summary>环境变量增量覆盖；null 表示不覆盖。键名按 Windows 语义大小写不敏感。</summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string> Env
    {
        get { return _env; }
        set
        {
            if (value == null)
            {
                _env = null;
                return;
            }

            // 用大小写不敏感的比较器重建，同时保留 JSON 里的键顺序（供后续 %VAR% 按出现顺序展开）。
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in value)
            {
                copy[pair.Key] = pair.Value;
            }
            _env = copy;
        }
    }

    [JsonPropertyName("runAsAdmin")]
    public bool RunAsAdmin { get; set; }

    public override string ToString()
    {
        return Name ?? string.Empty;
    }
}


/// <summary>窗口位置与尺寸（单位为 DIP）；X / Y 为 -1 表示交给系统决定。</summary>
public class ConfigWindow
{
    public const int DefaultWidth = 1024;
    public const int DefaultHeight = 640;
    public const int DefaultX = -1;
    public const int DefaultY = -1;

    [JsonPropertyName("w")]
    public int W { get; set; } = DefaultWidth;

    [JsonPropertyName("h")]
    public int H { get; set; } = DefaultHeight;

    [JsonPropertyName("x")]
    public int X { get; set; } = DefaultX;

    [JsonPropertyName("y")]
    public int Y { get; set; } = DefaultY;

    [JsonPropertyName("max")]
    public bool Max { get; set; }

    /// <summary>捕获该几何时的窗口 DPI；旧配置没有该字段时按 96 处理。</summary>
    [JsonPropertyName("dpi")]
    public int Dpi { get; set; } = 96;

    /// <summary>
    /// 加载时的绝对下限：只拒绝无法使用的尺寸。真正的设计下限（880×560，按工作区放宽）
    /// 由 FormMain 在恢复/捕获时用 WindowGeometry.MinimumDipFor 计算。
    /// </summary>
    internal void Normalize()
    {
        if (W < 1 || H < 1 || W > WindowGeometry.MaxSize || H > WindowGeometry.MaxSize)
        {
            W = DefaultWidth;
            H = DefaultHeight;
        }

        Dpi = WindowGeometry.NormalizeDpi(Dpi);
    }
}


/// <summary>整份配置；theme / window 是全局 UI 状态，损坏或越界时回落默认值。</summary>
public class ConfigData
{
    [JsonPropertyName("autoRun")]
    public bool AutoRun { get; set; }

    [JsonPropertyName("theme")]
    [JsonConverter(typeof(ThemeModeJsonConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    [JsonPropertyName("window")]
    public ConfigWindow Window { get; set; } = new ConfigWindow();

    [JsonPropertyName("items")]
    public List<ConfigItem> Items { get; set; } = new List<ConfigItem>();
}


/// <summary>配置加载结果：数据层不弹任何 UI，由界面层决定如何提示。</summary>
public class ConfigLoadResult
{
    private ConfigLoadResult()
    {
    }

    public bool Ok { get; private set; }

    /// <summary>失败原因（中文）；成功时为空字符串。</summary>
    public string Error { get; private set; }

    /// <summary>加载成功时的数据；失败时为默认配置，保证调用方始终拿到可用对象。</summary>
    public ConfigData Data { get; private set; }

    internal static ConfigLoadResult Success(ConfigData data)
    {
        return new ConfigLoadResult
        {
            Ok = true,
            Error = "",
            Data = data ?? new ConfigData()
        };
    }

    internal static ConfigLoadResult Failure(string error)
    {
        return new ConfigLoadResult
        {
            Ok = false,
            Error = string.IsNullOrEmpty(error) ? "加载配置文件失败。" : error,
            Data = new ConfigData()
        };
    }
}


/// <summary>config.json 中 theme 字段与 ThemeMode 的转换：字符串映射，非法值回落 System，类型不符报错。</summary>
public sealed class ThemeModeJsonConverter : JsonConverter<ThemeMode>
{
    public override ThemeMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return ThemeMode.System;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("theme 必须是 system、light 或 dark 字符串。");
        }

        return ThemeModes.Parse(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, ThemeMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(ThemeModes.ToConfigString(value));
    }
}


public class Common
{
    private static readonly object SaveLock = new object();

    private static readonly JsonSerializerOptions SaveOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    /// <summary>最近一次保存失败的原因；保存成功时为空字符串。</summary>
    public static string LastSaveError { get; private set; } = "";

    public static string GetApplicationPath()
    {
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>
    /// 从指定路径加载配置。文件不存在按默认配置成功返回；解析/读取失败返回 Ok=false 与中文原因，绝不弹框。
    /// </summary>
    public static ConfigLoadResult LoadFrom(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return ConfigLoadResult.Failure("配置文件路径为空。");
        }

        try
        {
            if (!File.Exists(path))
            {
                // 首次运行没有 config.json 是正常情况，按默认值继续。
                return ConfigLoadResult.Success(new ConfigData());
            }

            string jsonString = File.ReadAllText(path);
            ConfigData data = JsonSerializer.Deserialize<ConfigData>(jsonString);
            if (data == null)
            {
                data = new ConfigData();
            }

            Normalize(data);
            return ConfigLoadResult.Success(data);
        }
        catch (JsonException ex)
        {
            return ConfigLoadResult.Failure("配置文件格式错误：" + ex.Message);
        }
        catch (IOException ex)
        {
            return ConfigLoadResult.Failure("无法读取配置文件：" + ex.Message);
        }
        catch (Exception ex)
        {
            // 兜底：任何异常都转成失败结果，保证加载路径不弹框、不崩溃。
            return ConfigLoadResult.Failure("加载配置文件失败：" + ex.Message);
        }
    }

    /// <summary>原子保存：先写 &lt;path&gt;.tmp，再替换目标文件；失败返回 false 并记录原因。</summary>
    public static bool SaveTo(ConfigData configData, string filePath)
    {
        if (configData == null)
        {
            LastSaveError = "待保存的配置为空。";
            return false;
        }

        if (string.IsNullOrEmpty(filePath))
        {
            LastSaveError = "配置文件路径为空。";
            return false;
        }

        lock (SaveLock)
        {
            string tempPath = filePath + ".tmp";
            try
            {
                string jsonString = JsonSerializer.Serialize(configData, SaveOptions);
                File.WriteAllText(tempPath, jsonString);

                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }

                LastSaveError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastSaveError = "保存配置文件失败：" + ex.Message;
                TryDeleteFile(tempPath);
                return false;
            }
        }
    }

    private static void Normalize(ConfigData data)
    {
        if (data.Items == null)
        {
            data.Items = new List<ConfigItem>();
        }

        // 单个 null 项无法被界面/启动逻辑消费，直接丢弃。
        data.Items.RemoveAll(item => item == null);

        if (data.Window == null)
        {
            data.Window = new ConfigWindow();
        }
        else
        {
            data.Window.Normalize();
        }

        if (!Enum.IsDefined(typeof(ThemeMode), data.Theme))
        {
            data.Theme = ThemeMode.System;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理临时文件失败不掩盖真正的保存错误。
        }
    }
}
