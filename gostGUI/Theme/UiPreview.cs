using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 隐藏的 --ui-preview &lt;dir&gt;：不显示窗口，用 DrawToBitmap 把两套主题各渲染成一张 PNG。
    /// 只在开发/验收时使用，正常启动路径不会碰到这里。
    /// </summary>
    internal static class UiPreview
    {
        private const string LogFileName = "ui-preview.log";
        private const int CanvasWidth = 900;
        private const int CanvasHeight = 640;

        public static int Run(string directory)
        {
            var log = new StringBuilder();
            string logPath = null;
            int exitCode = 0;

            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new ArgumentException("--ui-preview 需要一个输出目录参数。");
                }

                string trimmedDirectory = directory.Trim();
                if (trimmedDirectory.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "--ui-preview 需要一个输出目录参数，不能把另一个 -- 开关当作目录。");
                }

                string fullDirectory = Path.GetFullPath(trimmedDirectory);
                Directory.CreateDirectory(fullDirectory);
                logPath = Path.Combine(fullDirectory, LogFileName);

                string lightPath = Path.Combine(fullDirectory, "preview-light.png");
                string darkPath = Path.Combine(fullDirectory, "preview-dark.png");

                using (var scene = new PreviewScene())
                {
                    scene.Apply(Palette.Light);
                    scene.Prepare();
                    scene.FillLog(Palette.Light);
                    Render(scene.Form, lightPath);

                    scene.Apply(Palette.Dark);
                    scene.FillLog(Palette.Dark);
                    Render(scene.Form, darkPath);
                }

                WriteLine(log, "PASS ui-preview");
                WriteLine(log, "浅色预览：" + lightPath);
                WriteLine(log, "深色预览：" + darkPath);
            }
            catch (Exception ex)
            {
                exitCode = 1;
                WriteLine(log, "FAIL ui-preview: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (!string.IsNullOrEmpty(logPath))
            {
                try
                {
                    File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    // 日志写不出来不影响退出码；下面仍尝试打印到控制台。
                }
            }

            FlushConsole(log);
            return exitCode;
        }

        private static void Render(Form form, string path)
        {
            if (form == null) throw new ArgumentNullException(nameof(form));
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

            using (var bitmap = new Bitmap(CanvasWidth, CanvasHeight, PixelFormat.Format32bppArgb))
            {
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, CanvasWidth, CanvasHeight));
                bitmap.Save(path, ImageFormat.Png);
            }

            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new IOException("预览图未生成：" + path);
            }
        }

        private static void WriteLine(StringBuilder log, string line)
        {
            log.AppendLine(line);
            try
            {
                Console.WriteLine(line);
            }
            catch (Exception)
            {
                // WinExe 没有控制台时忽略，ui-preview.log 才是权威输出。
            }
        }

        private static void FlushConsole(StringBuilder log)
        {
            try
            {
                Console.Out.Flush();
            }
            catch (Exception)
            {
                // 同上。
            }
        }

        /// <summary>预览场景：创建一个不显示的 900×640 窗体，放入每种主题控件的代表状态。</summary>
        private sealed class PreviewScene : IDisposable
        {
            private readonly Font _baseFont;
            private readonly Font _headerFont;
            private readonly Label _header;
            private readonly Label _hint;
            private readonly ThemeManager _manager = new ThemeManager();
            private readonly ThemedRichTextBox _log;
            private readonly ThemedListBox _list;

            public PreviewScene()
            {
                _baseFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
                _headerFont = new Font(_baseFont.FontFamily, 13f, FontStyle.Bold, GraphicsUnit.Point);

                Form = new Form
                {
                    Text = "gostGUI v1.9 主题预览",
                    FormBorderStyle = FormBorderStyle.None,
                    AutoScaleMode = AutoScaleMode.None,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(0, 0),
                    ClientSize = new Size(CanvasWidth, CanvasHeight),
                    Font = _baseFont,
                    BackColor = Palette.Light.Surface
                };

                _header = new Label
                {
                    AutoSize = true,
                    Font = _headerFont,
                    Location = new Point(24, 16),
                    Text = "gostGUI v1.9 · 主题预览",
                    BackColor = Palette.Light.Surface,
                    ForeColor = Palette.Light.TextPrimary
                };
                _hint = new Label
                {
                    AutoSize = true,
                    Font = _baseFont,
                    Location = new Point(640, 20),
                    Text = "同一套语义色 · 浅色 / 深色",
                    BackColor = Palette.Light.Surface,
                    ForeColor = Palette.Light.TextMuted
                };
                Form.Controls.Add(_header);
                Form.Controls.Add(_hint);

                CardPanel launchCard = CreateCard(24, 64, 424, 300, "启动设置");
                Form.Controls.Add(launchCard);

                var programBox = new ThemedTextBox
                {
                    Text = "gost.exe",
                    Location = new Point(16, 48),
                    Size = new Size(392, 36)
                };
                launchCard.Controls.Add(programBox);

                var adminToggle = new ToggleSwitch
                {
                    Text = "以管理员身份运行",
                    Checked = false
                };
                adminToggle.Location = new Point(16, 100);
                launchCard.Controls.Add(adminToggle);

                var autoStartToggle = new ToggleSwitch
                {
                    Text = "开机自启",
                    Checked = true
                };
                autoStartToggle.Location = new Point(16, 136);
                launchCard.Controls.Add(autoStartToggle);

                var startButton = CreateButton("启动", ButtonVariant.Primary, 16, 190);
                var stopButton = CreateButton("停止", ButtonVariant.Secondary, 112, 190);
                var browseButton = CreateButton("浏览", ButtonVariant.Ghost, 208, 190);
                var deleteButton = CreateButton("删除", ButtonVariant.Danger, 304, 190);
                launchCard.Controls.Add(startButton);
                launchCard.Controls.Add(stopButton);
                launchCard.Controls.Add(browseButton);
                launchCard.Controls.Add(deleteButton);

                CardPanel listCard = CreateCard(464, 64, 412, 300, "配置列表");
                Form.Controls.Add(listCard);

                _list = new ThemedListBox
                {
                    Location = new Point(16, 48),
                    Size = new Size(380, 236),
                    IsRunningSelector = item => item is PreviewItem previewItem && previewItem.Running,
                    SubtitleSelector = item => (item as PreviewItem)?.Subtitle,
                    StatusTextSelector = item => (item as PreviewItem)?.Status
                };
                _list.Items.Add(new PreviewItem("item1", "-L 127.0.0.1:8080", true, "8m 12s"));
                _list.Items.Add(new PreviewItem("item2", "gost.exe", false, "已停止"));
                _list.Items.Add(new PreviewItem("item3", "C:\\Tools\\gost.exe", false, "未启动"));
                _list.SelectedIndex = 0;
                listCard.Controls.Add(_list);

                CardPanel outputCard = CreateCard(24, 380, 852, 236, "输出");
                Form.Controls.Add(outputCard);

                _log = new ThemedRichTextBox
                {
                    Location = new Point(16, 48),
                    Size = new Size(820, 172)
                };
                outputCard.Controls.Add(_log);
            }

            public Form Form { get; }

            public void Apply(Palette palette)
            {
                if (palette == null) return;

                _manager.Mode = ReferenceEquals(palette, Palette.Dark) ? ThemeMode.Dark : ThemeMode.Light;
                Form.BackColor = palette.Surface;
                Form.ForeColor = palette.TextPrimary;

                _header.BackColor = palette.Surface;
                _header.ForeColor = palette.TextPrimary;
                _hint.BackColor = palette.Surface;
                _hint.ForeColor = palette.TextMuted;

                _manager.ApplyTo(Form);
            }

            public void FillLog(Palette palette)
            {
                if (palette == null) return;

                _log.Inner.Clear();
                _log.AppendColored("stdout: gost 服务已启动，监听 127.0.0.1:8080" + Environment.NewLine, palette.LogStdout);
                _log.AppendColored("stderr: 连接上游失败：connection refused" + Environment.NewLine, palette.LogStderr);
                _log.AppendColored("系统: 已启动 (PID 4242)" + Environment.NewLine, palette.LogSystem);
            }

            /// <summary>DrawToBitmap 之前确保窗体与所有子控件的句柄都已创建，但绝不 Show。</summary>
            public void Prepare()
            {
                Form.CreateControl();
                ForceHandles(Form);
                Form.PerformLayout();
            }

            public void Dispose()
            {
                Form.Dispose();
                _headerFont.Dispose();
                _baseFont.Dispose();
            }

            private static CardPanel CreateCard(int x, int y, int width, int height, string title)
            {
                return new CardPanel
                {
                    Location = new Point(x, y),
                    Size = new Size(width, height),
                    Title = title
                };
            }

            private static FlatButton CreateButton(string text, ButtonVariant variant, int x, int y)
            {
                return new FlatButton
                {
                    Text = text,
                    Variant = variant,
                    Location = new Point(x, y),
                    Size = new Size(88, 34)
                };
            }

            private static void ForceHandles(Control control)
            {
                if (control == null) return;

                IntPtr handle = control.Handle;
                foreach (Control child in control.Controls)
                {
                    ForceHandles(child);
                }
            }
        }

        private sealed class PreviewItem
        {
            public PreviewItem(string name, string subtitle, bool running, string status)
            {
                Name = name ?? string.Empty;
                Subtitle = subtitle ?? string.Empty;
                Running = running;
                Status = status ?? string.Empty;
            }

            public string Name { get; }

            public string Subtitle { get; }

            public bool Running { get; }

            public string Status { get; }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}
