using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using gostGUI.Native;
using gostGUI.Shared;
using Microsoft.Win32;

namespace gostGUI
{
    /// <summary>
    /// 主窗口：左边配置列表与操作按钮，右边“启动设置”详情编辑器与主题化日志面板。
    /// <para>
    /// 两个右栏区域各自可独立工作：详情编辑器整组回填/逐字段保存，日志面板按项切换视图、
    /// 批量着色排空（工作线程只入队）。
    /// </para>
    /// <para>
    /// R30 纪律：StartProcess / StopProcess 可能因中转 + UAC 阻塞数十秒，一律不落 UI 线程；
    /// 状态变化通过 PostToUi（BeginInvoke）回到界面线程。
    /// </para>
    /// </summary>
    public partial class FormMain : Form
    {
        private const string AppRegistryKey = "gostGUI";
        private const string RunRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";

        /// <summary>--ui-snapshot 固定输出尺寸（1024×640 DIP 设计基线）。</summary>
        private const int SnapshotWidth = 1024;
        private const int SnapshotHeight = 640;

        /// <summary>
        /// R36：启动时的自动恢复/开机自启跳过管理员项时，写进该项日志的唯一一行。
        /// 必须是精确文案，不得添加任何前缀（验收会逐字断言）。
        /// </summary>
        internal const string AutoRestoreSkipMessage = "已跳过：需要管理员权限，请手动启动";

        private ProcessManager _processManager;
        private ConfigurationManager _configManager;
        private LogViewManager _logViewManager;
        private readonly ThemeManager _themeManager = new ThemeManager();

        /// <summary>哪些项正在运行（含启动时刻，用于列表右侧的“运行中 8m12s”）。只在 UI 线程读写。</summary>
        private readonly Dictionary<ConfigItem, DateTime> _runStarted = new Dictionary<ConfigItem, DateTime>();

        /// <summary>正在启动/停止的项：按钮置灰并显示“启动中…”/“停止中…”。只在 UI 线程读写。</summary>
        private readonly HashSet<ConfigItem> _starting = new HashSet<ConfigItem>();
        private readonly HashSet<ConfigItem> _stopping = new HashSet<ConfigItem>();

        private readonly bool _suppressStartupStart;

        /// <summary>UI 线程 id：PostToUi 用它判断“真的在 UI 线程上”，不再依赖会撒谎的 InvokeRequired。</summary>
        private readonly int _uiThreadId;

        /// <summary>句柄还没建好时先排队的 UI 回调；句柄一建好（OnHandleCreated）就按顺序排空。</summary>
        private readonly object _pendingUiLock = new object();
        private readonly Queue<Action> _pendingUiActions = new Queue<Action>();

        /// <summary>自检/快照用的外部配置管理器（null 表示用应用目录的 config.json）。</summary>
        private readonly ConfigurationManager _injectedConfigManager;

        private bool _startupRestoreDone;
        private bool _isExiting;
        private bool _exitRequested;
        private bool _processManagerDisposed;
        private bool _disposeCompleted;
        private bool _syncingAutoStart;
        private bool _collapsed;
        private bool _endingRename;
        private int _leftExpandedColumnWidth;
        private int _collapsedColumnWidth;
        private ConfigItem _currentEditingItem;

        /// <summary>构造时的设计基线 DPI（--ui-snapshot / 自检可通过 StartupDpiOverride 覆盖）。</summary>
        private int _layoutDpi;

        /// <summary>恢复的最大化状态：窗口 Show 之后才能安全设置。</summary>
        private bool _restoreMaximized;

        /// <summary>真实退出路径只保存一次窗口状态，避免 FormClosing 重复弹提示。</summary>
        private bool _exitWindowStateAttempted;

        /// <summary>退出路径保存窗口状态时抑制 SaveFailed 横幅，只保留 R45 的模态框/气泡。</summary>
        private bool _suppressSaveFailedBanner;

        /// <summary>横幅是否处于“已显示”状态（用于自检；隐藏窗体的 Visible 会因子控件不可见而为 false）。</summary>
        private bool _bannerShown;

        /// <summary>
        /// 最近一次退出/隐藏保存失败的中文原因（含退出前提交横幅并入的原因；空字符串表示成功或还没保存过）。
        /// </summary>
        private string _lastWindowSaveFailure = "";

        /// <summary>
        /// R2-13：退出前提交期间升起的全部横幅消息（按出现顺序去重）。从托盘退出或系统关机时窗口可能
        /// 已经隐藏，横幅会随退出一起被销毁；这里累计所有不同原因，交给 <see cref="PersistWindowState"/>
        /// 的 R45 退出模态框显形，关机路径则改由托盘气泡与系统日志显形。
        /// </summary>
        private readonly List<string> _exitCommitBannerReasons = new List<string>();

        /// <summary>为 true 时 <see cref="ShowBanner"/> 只记录原因、不上屏（退出前提交的横幅没人看得见）。</summary>
        private bool _captureExitCommitBanner;

        /// <summary>自检用：非 0 时替代系统 DPI 作为布局基线（生产路径永远是 0）。</summary>
        internal static int StartupDpiOverride { get; set; }

        /// <summary>自检用：抑制退出路径的托盘气泡/模态框，避免阻塞无消息循环的用例。</summary>
        internal bool SuppressDialogsForTest { get; set; }

        /// <summary>详情编辑器当前回填/保存的目标项（null 表示没有选中项）。只在 UI 线程读写。</summary>
        private ConfigItem _detailItem;

        /// <summary>回填字段期间为 true：抑制 TextChanged/Leave/CheckedChanged 触发的保存。</summary>
        private bool _detailLoading;

        /// <summary>程序解析结果缓存：键 = 程序 + 有效环境签名 + 应用目录；主题切换/重复提交不再扫文件系统。</summary>
        private string _programHintCacheKey;
        private ResolvedProgram _programHintCacheResult;

        /// <summary>最近一次已经提示过的环境变量解析警告，避免每次失焦重复刷同一条系统日志。</summary>
        private string _lastEnvWarningSignature;

        /// <summary>正常的启动路径：从配置恢复上次运行状态，并显示窗口与托盘图标。</summary>
        public FormMain()
            : this(false)
        {
        }

        /// <summary>
        /// 内部构造：suppressStartupStart=true 时不执行启动时的自动恢复（--ui-snapshot 与自检用），
        /// 公共默认行为不受影响。
        /// </summary>
        internal FormMain(bool suppressStartupStart)
            : this(suppressStartupStart, null)
        {
        }

        /// <summary>自检用：注入临时目录里的 ConfigurationManager，避免碰到用户那份 config.json。</summary>
        internal FormMain(bool suppressStartupStart, ConfigurationManager configurationManager)
        {
            // 必须先记住 UI 线程：PostToUi 的内联判定完全依赖它（Control.InvokeRequired
            // 在句柄没建好时对任何线程都返回 false，不能用来判断线程归属）。
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;
            _suppressStartupStart = suppressStartupStart;
            _injectedConfigManager = configurationManager;
            InitializeComponent();
            InitializeManagers();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄出现后补一次深色标题栏与积压的 UI 回调：
            // 构造函数阶段没有句柄，不能让任何回调/主题调用抢先建句柄。
            ThemeManager.ApplyDarkTitleBar(this, ReferenceEquals(_themeManager.Current, Palette.Dark));

            DrainPendingUiActions();
        }

        /// <summary>
        /// 把动作排到 UI 线程。
        /// <para>
        /// 线程归属只认构造函数里记下的 <see cref="_uiThreadId"/>：<c>Control.InvokeRequired</c>
        /// 在窗体/祖先都没有句柄时对<b>任何</b>线程都返回 false，拿它当“我在 UI 线程”会把
        /// “窗口还没显示就先自动恢复配置项”这条启动路径上的进程事件直接跑在工作线程里。
        /// </para>
        /// <para>
        /// 句柄没建好时先入队，句柄一建好（OnHandleCreated/OnShown）按入队顺序在 UI 线程排空。
        /// </para>
        /// </summary>
        private void PostToUi(Action action)
        {
            TryPostUi(action);
        }

        /// <summary>自检缝：走与生产代码完全相同的 PostToUi 路径（用于验证句柄建立前的跨线程投递）。</summary>
        internal void PostToUiForTest(Action action)
        {
            PostToUi(action);
        }

        /// <summary>PostToUi 的“能投递吗”版本：UI 线程已不可用（已释放）时返回 false，供退出流程兜底。</summary>
        private bool TryPostUi(Action action)
        {
            if (action == null)
            {
                return true;
            }

            if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
            {
                RunUiAction(action);
                return true;
            }

            if (IsDisposed || Disposing)
            {
                return false;
            }

            lock (_pendingUiLock)
            {
                _pendingUiActions.Enqueue(action);
            }

            if (!IsHandleCreated)
            {
                // 句柄还没建好：留在队列里，等 OnHandleCreated 统一排空。
                return true;
            }

            return TryScheduleUiDrain();
        }

        private bool TryScheduleUiDrain()
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated)
                {
                    return false;
                }

                BeginInvoke(new Action(DrainPendingUiActions));
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("PostToUi 投递失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>在 UI 线程上按入队顺序执行积压的 UI 回调；在别的线程调用会被忽略。</summary>
        private void DrainPendingUiActions()
        {
            if (Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                return;
            }

            while (true)
            {
                Action next;
                lock (_pendingUiLock)
                {
                    if (_pendingUiActions.Count == 0)
                    {
                        return;
                    }

                    next = _pendingUiActions.Dequeue();
                }

                RunUiAction(next);
            }
        }

        private static void RunUiAction(Action action)
        {
            if (action == null)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("PostToUi 回调失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 退出/隐藏前的最后一道提交：窗口可能在没有触发任何 Leave 的情况下直接关闭，
        /// 用户刚输入但还没失焦的字段必须在这里落盘，否则会被静默丢弃。
        /// 提交失败只记调试输出，绝不能拦住退出流程。
        /// </summary>
        private void CommitPendingDetailEdits()
        {
            try
            {
                CommitDetailFields();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("退出前提交详情编辑失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 退出前的最后一次提交，并捕获这次提交里升起的全部不同横幅（R2-13）。
        /// <para>
        /// 从托盘退出或系统关机时窗口可能已经隐藏：<see cref="ShowBanner"/> 此时把消息记进
        /// <see cref="_exitCommitBannerReasons"/> 而不上屏，调用方再把所有原因按顺序并进退出保存失败提示
        /// （都算错误；同一原因只记一次）。这样“提交失败 → 窗口状态保存成功”不会再让横幅被静默丢弃。
        /// </para>
        /// </summary>
        /// <returns>退出前提交期间升起的横幅原因（多个用“；”连接，去重）；没有则为 null。</returns>
        private string CommitPendingDetailEditsForExit()
        {
            _exitCommitBannerReasons.Clear();
            bool previousCapture = _captureExitCommitBanner;
            _captureExitCommitBanner = true;
            try
            {
                CommitPendingDetailEdits();
            }
            finally
            {
                _captureExitCommitBanner = previousCapture;
            }

            string reason = _exitCommitBannerReasons.Count == 0
                ? null
                : string.Join("；", _exitCommitBannerReasons.ToArray());
            _exitCommitBannerReasons.Clear();
            return reason;
        }

        private void DisposeProcessManagerQuietly()
        {
            ProcessManager manager = _processManager;
            if (manager == null || _processManagerDisposed)
            {
                return;
            }

            _processManagerDisposed = true;
            try
            {
                manager.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("释放进程管理器失败：" + ex.Message);
            }
        }

        private void DisposeManagedExtras()
        {
            if (_disposeCompleted)
            {
                return;
            }

            _disposeCompleted = true;

            try
            {
                _elapsedTimer?.Stop();
            }
            catch (Exception)
            {
                // 计时器已释放。
            }

            try
            {
                _logViewManager?.Dispose();
            }
            catch (Exception)
            {
                // 日志视图可能已随宿主控件释放。
            }

            try
            {
                _notifyIcon?.Dispose();
            }
            catch (Exception)
            {
                // 同上。
            }

            // 静态事件会把窗体实例钉在内存里（自检每跑一次就多一个），释放时必须退订。
            try
            {
                Application.ApplicationExit -= OnApplicationExit;
            }
            catch (Exception)
            {
                // 退订失败不影响界面释放。
            }

            // 主题监听同样不能把窗体留在一个比它活得更久的对象上。
            try
            {
                _themeManager.Changed -= OnThemeChanged;
            }
            catch (Exception)
            {
                // 同上。
            }

            DisposeProcessManagerQuietly();

            try
            {
                _renameBox?.Dispose();
            }
            catch (Exception)
            {
                // 行内编辑框可能已随列表释放。
            }

            _baseFont?.Dispose();
            _titleFont?.Dispose();
            _sectionFont?.Dispose();
            _monoFont?.Dispose();

            if (_appIcon != null)
            {
                try
                {
                    Icon = null;
                    _appIcon.Dispose();
                }
                catch (Exception)
                {
                    // 图标可能被系统缓存，释放失败可忽略。
                }
            }
        }

    }

}
