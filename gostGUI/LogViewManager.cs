using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace gostGUI
{
    /// <summary>
    /// 每项一个 <see cref="ThemedRichTextBox"/> 的日志面板：
    /// 工作线程只把日志塞进 <see cref="ConcurrentQueue{T}"/>，UI 定时器每 ~120ms 批量排空，
    /// 按 <see cref="LogKind"/> 着色、每项 1000 行上限、贴底才自动滚动。
    /// </summary>
    public sealed class LogViewManager : IDisposable
    {
        /// <summary>每项日志行数上限：超过就整段清空（与 v1.8 的策略一致）。</summary>
        private const int MaxLinesPerItem = 1000;

        /// <summary>一次 Tick 最多处理多少条，避免单次排空把 UI 长时间占住。</summary>
        private const int DrainBatchLimit = 200;

        private sealed class PendingEntry
        {
            public ConfigItem Item;
            public LogKind Kind;
            public string Text;
            public int Generation;
        }

        private sealed class LogRun
        {
            public string Text;
            public LogKind Kind;
        }

        private sealed class ViewState
        {
            public ThemedRichTextBox View;
            public int LineCount;
            public readonly List<LogRun> Runs = new List<LogRun>();
        }

        private readonly Control _parentControl;
        private readonly Font _monoFont;
        private readonly Func<Action, bool> _postToUi;
        private readonly Dictionary<ConfigItem, ViewState> _views = new Dictionary<ConfigItem, ViewState>();

        /// <summary>清空代数：清空时 +1，之前的排队日志在排空时被丢弃（否则清空后旧行又冒出来）。</summary>
        private readonly ConcurrentDictionary<ConfigItem, int> _generations =
            new ConcurrentDictionary<ConfigItem, int>();

        private readonly ConcurrentQueue<PendingEntry> _queue = new ConcurrentQueue<PendingEntry>();
        private readonly Timer _timer;
        private Palette _palette = Palette.Light;
        private bool _disposed;

        public LogViewManager(Control parentControl, Font monoFont, Func<Action, bool> postToUi)
        {
            _parentControl = parentControl ?? throw new ArgumentNullException(nameof(parentControl));
            _monoFont = monoFont;
            _postToUi = postToUi;

            // WinForms Timer 的 Tick 永远在 UI 线程上；工作线程只入队。
            _timer = new Timer { Interval = 120 };
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        public void AddLogView(ConfigItem item)
        {
            if (item == null || _views.ContainsKey(item))
            {
                return;
            }

            var view = new ThemedRichTextBox
            {
                Dock = DockStyle.Fill,
                Visible = false
            };

            if (_monoFont != null)
            {
                view.Inner.Font = _monoFont;
            }

            view.Inner.WordWrap = false;
            view.ApplyTheme(_palette);

            var state = new ViewState { View = view };
            _views[item] = state;
            _generations.TryAdd(item, 0);
            _parentControl.Controls.Add(view);
        }

        public void RemoveLogView(ConfigItem item)
        {
            if (item == null)
            {
                return;
            }

            ViewState state;
            if (_views.TryGetValue(item, out state))
            {
                _views.Remove(item);
                try
                {
                    _parentControl.Controls.Remove(state.View);
                    state.View.Dispose();
                }
                catch (Exception)
                {
                    // 释放失败不影响后续视图切换。
                }
            }

            int ignored;
            _generations.TryRemove(item, out ignored);
        }

        public void ShowLogView(ConfigItem item)
        {
            foreach (KeyValuePair<ConfigItem, ViewState> entry in _views)
            {
                entry.Value.View.Visible = ReferenceEquals(entry.Key, item);
            }

            ViewState selected;
            if (item != null && _views.TryGetValue(item, out selected))
            {
                selected.View.BringToFront();
            }
        }

        /// <summary>
        /// 追加一条日志。绝不阻塞调用线程：只入队，由 UI 定时器批量写入。
        /// </summary>
        public void AppendText(ConfigItem item, LogKind kind, string text)
        {
            if (item == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            int generation;
            _generations.TryGetValue(item, out generation);
            _queue.Enqueue(new PendingEntry
            {
                Item = item,
                Kind = kind,
                Text = text,
                Generation = generation
            });
        }

        public void ClearLog(ConfigItem item)
        {
            if (item == null)
            {
                return;
            }

            _generations.AddOrUpdate(item, 1, (key, value) => value + 1);

            ViewState state;
            if (_views.TryGetValue(item, out state))
            {
                state.View.Text = string.Empty;
                state.Runs.Clear();
                state.LineCount = 0;
            }
        }

        /// <summary>当前项日志的纯文本（“复制”按钮用）；没有视图时返回空字符串。</summary>
        public string GetText(ConfigItem item)
        {
            ViewState state;
            if (item != null && _views.TryGetValue(item, out state))
            {
                return state.View.Text;
            }

            return string.Empty;
        }

        /// <summary>主题切换：底色跟着换；已有日志按新的语义色重写，避免深色底上留着浅色正文。</summary>
        public void ApplyTheme(Palette palette)
        {
            if (palette == null)
            {
                return;
            }

            bool paletteChanged = !ReferenceEquals(_palette, palette);
            _palette = palette;

            foreach (ViewState state in _views.Values)
            {
                state.View.ApplyTheme(palette);
                if (paletteChanged)
                {
                    RebuildColors(state);
                }
            }
        }

        internal void DrainForTest()
        {
            DrainPending(int.MaxValue);
        }

        /// <summary>触发一次生产环境里 120ms UI Timer 的 Tick（自检无消息循环，需要手动泵）。</summary>
        internal void PumpTimerForTest()
        {
            Timer_Tick(this, EventArgs.Empty);
        }

        internal ThemedRichTextBox GetViewForTest(ConfigItem item)
        {
            ViewState state;
            if (item != null && _views.TryGetValue(item, out state))
            {
                return state.View;
            }

            return null;
        }

        internal int LineCountForTest(ConfigItem item)
        {
            ViewState state;
            if (item != null && _views.TryGetValue(item, out state))
            {
                return state.LineCount;
            }

            return 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
                _timer.Dispose();
            }
            catch (Exception)
            {
                // 计时器可能已随消息循环结束而释放。
            }

            PendingEntry ignored;
            while (_queue.TryDequeue(out ignored))
            {
            }

            foreach (ViewState state in _views.Values)
            {
                try
                {
                    if (!state.View.IsDisposed)
                    {
                        state.View.Dispose();
                    }
                }
                catch (Exception)
                {
                    // 宿主会再释放一次，这里失败可忽略。
                }
            }

            _views.Clear();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_disposed || _queue.IsEmpty)
            {
                return;
            }

            Action drain = () => DrainPending(DrainBatchLimit);
            Func<Action, bool> post = _postToUi;
            if (post == null)
            {
                drain();
                return;
            }

            post(drain);
        }

        /// <summary>只在 UI 线程上调用：按入队顺序把日志写进对应视图。</summary>
        private void DrainPending(int maxEntries)
        {
            if (_disposed)
            {
                return;
            }

            int processed = 0;
            PendingEntry entry;
            while (processed < maxEntries && _queue.TryDequeue(out entry))
            {
                processed++;

                ViewState state;
                if (!_views.TryGetValue(entry.Item, out state))
                {
                    continue;
                }

                int currentGeneration;
                if (_generations.TryGetValue(entry.Item, out currentGeneration) &&
                    currentGeneration != entry.Generation)
                {
                    // 清空之后残留的旧排队日志：丢弃。
                    continue;
                }

                AppendCore(state, entry.Kind, entry.Text);
            }
        }

        private void AppendCore(ViewState state, LogKind kind, string text)
        {
            int lines = CountLines(text);
            if (state.LineCount + lines > MaxLinesPerItem)
            {
                // 超过上限：整段清空（v1.8 行为），从这一条重新累计。
                state.View.Text = string.Empty;
                state.Runs.Clear();
                state.LineCount = 0;
            }

            state.View.AppendColored(text, ColorFor(kind, text));
            state.LineCount += lines;
            state.Runs.Add(new LogRun { Text = text, Kind = kind });
        }

        private Color ColorFor(LogKind kind, string text)
        {
            switch (kind)
            {
                case LogKind.Stderr:
                    return _palette.LogStderr;
                case LogKind.System:
                    return IsErrorText(text) ? _palette.Danger : _palette.LogSystem;
                default:
                    return _palette.LogStdout;
            }
        }

        /// <summary>系统行里带这些词的按危险色显示（启动失败 / 已取消管理员授权 / 进程异常等）。</summary>
        private static bool IsErrorText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return text.IndexOf("失败", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("错误", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("异常", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("无法", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("已取消", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("找不到", StringComparison.Ordinal) >= 0;
        }

        /// <summary>廉价行数统计：数 '\n'；没有结尾换行的最后一段也算一行。</summary>
        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    count++;
                }
            }

            if (text[text.Length - 1] != '\n')
            {
                count++;
            }

            return count;
        }

        /// <summary>主题切换后用新的语义色重写已有 run，并尽量保持原来的阅读位置。</summary>
        private void RebuildColors(ViewState state)
        {
            if (state.Runs.Count == 0)
            {
                return;
            }

            RichTextBox inner = state.View.Inner;
            int firstVisible = -1;
            bool atBottom = true;
            try
            {
                atBottom = IsAtBottom(inner);
                firstVisible = inner.GetCharIndexFromPosition(new Point(1, 1));
            }
            catch (Exception)
            {
                // 句柄没建好：按贴底处理即可。
            }

            try
            {
                inner.SuspendLayout();
                state.View.Text = string.Empty;
                foreach (LogRun run in state.Runs)
                {
                    state.View.AppendColored(run.Text, ColorFor(run.Kind, run.Text));
                }
            }
            catch (Exception)
            {
                // 重着色失败不能影响主题切换本身。
            }
            finally
            {
                inner.ResumeLayout();
            }

            try
            {
                if (atBottom)
                {
                    inner.SelectionStart = inner.TextLength;
                    inner.SelectionLength = 0;
                    inner.ScrollToCaret();
                }
                else if (firstVisible >= 0)
                {
                    inner.SelectionStart = Math.Min(firstVisible, inner.TextLength);
                    inner.SelectionLength = 0;
                    inner.ScrollToCaret();
                }
            }
            catch (Exception)
            {
                // 滚动位置恢复失败不影响日志内容。
            }
        }

        private static bool IsAtBottom(RichTextBox box)
        {
            if (box == null || box.TextLength == 0)
            {
                return true;
            }

            try
            {
                Point last = box.GetPositionFromCharIndex(box.TextLength - 1);
                return last.Y + box.Font.Height <= box.ClientSize.Height;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
