using MyPLCManagerLibrary;
using MyPLCFoundationLibrary.Logging;
using MySecurityLibrary.Services;
using MySecurityLibrary.Views;
using PLCTesting.ViewModels;
using PLCTesting.Views;
using Prism.Ioc;
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace PLCTesting
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        // 强制 PerMonitorV2 DPI 感知：本工程 UseWindowsForms=true，.NET 运行时会把 DPI 模式
        // 降级为 SystemAware，导致高分屏（含不同缩放的多屏）下整窗被位图拉伸、界面发糊。
        // 必须在 WPF/WinForms 初始化之前用 Win32 API 锁定，否则后续设置会被忽略。
        private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
        private const int PROCESS_PER_MONITOR_DPI_AWARE_V2 = 3;
        private const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(int value);

        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        static App()
        {
            try
            {
                // 优先 V2：先试 user32 的 Context API（返回 true=成功），
                // 再试 shcore 的 V2 枚举值（返回 S_OK=0 为成功）。
                // 若两者都被运行时抢先占用（极少），则退而求其次锁定 PerMonitor（仍比 SystemAware 清晰）。
                bool v2 = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                if (!v2 && SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE_V2) != 0)
                {
                    // V2 均失败 -> 至少 PerMonitor
                    SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
                }
            }
            catch { /* 极少数系统不支持时回退框架默认，不影响启动 */ }
        }

        private NotifyIcon _trayIcon;
        private MainWindow _mainWindow;
        private bool _forceClose;

        /// <summary>
        /// 结构化日志必须在"任何业务对象被创建之前"初始化，所以放在实例构造函数里。
        /// ⚠️ 不能放在 OnInitialized：Prism 的 CreateShell() 早于它执行，而 CreateShell 会
        /// 解析 MainWindow → MainWindowViewModel → MqttPublisherService 等一堆服务对象；
        /// 一旦这些对象在初始化前写了日志，StructuredLogger 内部的 Lazy&lt;IAppLogger&gt;
        /// 会先用**默认配置**惰性初始化并永久定格，之后 Init(真实配置) 再也改不动它，
        /// 结果是所有日志（含 TagService、变更记录）都写不到 plctesting-*.log 里。
        /// </summary>
        public App()
        {
            try
            {
                StructuredLogger.Init(new LogConfig
                {
                    AppName = "PLCTesting",
                    FileNamePrefix = "plctesting",
                    ConsoleEnabled = false,
                    MinLevel = AppLogLevel.Information,
                    RetainedFileCountLimit = 31
                });
            }
            catch
            {
                // 日志初始化失败不能阻碍软件启动（会退化为无日志，但界面可用）
            }
        }

        /// <summary>
        /// 本次是否以"后台方式"启动：不带主窗口、不弹登录框，只在托盘里安静运行。
        /// 唯一判定来源：命令行带了 --background / --silent（开机自启动项固定带这个参数）。
        /// 正常双击图标 / 开始菜单不带参数 → 一定走登录 + 显示主窗口。
        /// （早期版本还看 appsettings.json 的 RunInBackground 勾选，实报反馈
        ///  "正常打开也后台运行"后废除：手动打开就必须有界面，只有开机自启动才后台。）
        /// </summary>
        private bool _startHidden;

        protected override Window CreateShell()
        {
            return Container.Resolve<MainWindow>();
        }

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // 用户管理 / 认证模块（单例、可移植：仅依赖 Prism + SQLite）
            containerRegistry.RegisterSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
            containerRegistry.RegisterSingleton<IUserStore, SqliteUserStore>();
            containerRegistry.RegisterSingleton<IAuthService, AuthService>();
        }

        /// <summary>
        /// 延迟显示主窗口：先走强制登录流程（见 OnInitialized），登录成功后才 Show。
        /// 避免未授权时主界面一闪而过。
        /// </summary>
        protected override void InitializeShell(Window shell)
        {
        }

        protected override void OnInitialized()
        {
            // ⚠️ 故意不调用 base.OnInitialized()：
            // Prism 基类（PrismApplicationBase）的默认实现只有一句 `MainWindow?.Show()`——
            // 它会在我们自己的窗口管控逻辑之前把 shell 直接显示出来。
            // 正常模式下登录框是模态的、盖在主窗口上方，用户看不出主窗口已被提前显示；
            // 但后台启动（--background 或勾选"后台运行"）会跳过登录流程直接 return，
            // 主窗口便未经登录赤裸弹在桌面上——实报缺陷"只有登陆界面后台运行了，主界面还是打开"。
            // 因此窗口显示时机完全由本方法自带逻辑控制：
            //   登录成功后才 Show()；后台启动则始终不 Show()，只留托盘图标。
            // （此前"未显示主窗口"的日志写在 base 调用之后，具有误导性，实际窗口早已被 base 弹出。）

            // —— 结构化日志已在 App() 构造函数中初始化（早于 CreateShell 创建的任何服务对象）——
            // 上位机属"服务式"运行，不输出控制台；按天滚动、保留 31 天。
            var lifecycle = StructuredLogger.Current.ForContext("Lifecycle");
            lifecycle.Information(
                "软件启动 StartTime={StartTime} Version={Version} Machine={Machine}",
                DateTimeOffset.Now,
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?",
                Environment.MachineName);

            _mainWindow = (MainWindow)Current.MainWindow;
            if (_mainWindow != null)
            {
                _mainWindow.Closing += Main_Closing;
            }

            // —— 启动方式判定（要在登录之前：后台启动不该弹登录框）——
            // 只认命令行参数：开机自启动项固定带 --background（见 StartupService.Set），
            // 正常双击图标不带参数 → 一定走登录 + 主窗口。
            var settings = Services.AppSettingsStore.Default.Current;
            _startHidden = Services.StartupService.HasBackgroundArgument(Environment.GetCommandLineArgs());

            lifecycle.Information(
                "启动方式 StartHidden={StartHidden} Reason={Reason} AutoStartOnBoot={AutoStartOnBoot}",
                _startHidden,
                _startHidden ? "命令行参数(--background)" : "正常启动",
                settings.AutoStartOnBoot);

            // 首次运行写入默认管理员（admin / admin123）
            Container.Resolve<IUserStore>().EnsureSeeded();

            if (_startHidden)
            {
                // —— 后台启动：跳过强制登录，直接进托盘 ——
                // 登录改为"按需"：用户从托盘打开主窗口时才要求认证，
                // 这样无人值守的开机自启动不会被登录框卡住。
                //
                // ⚠️ 必须改成"仅显式关闭"：主窗口从未 Show 过，
                // Prism 默认的 OnLastWindowClose 会在"没有已打开窗口"时把进程直接退掉，
                // 表现为"自启动后进程一闪就没了"。退出只走 ExitApp() 里的显式 Shutdown()。
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                SetupTrayIcon();
                lifecycle.Information("后台启动：已创建托盘图标，未显示主窗口");
                ShowBalloon("PLCTesting 已在后台启动",
                    "PLC 连接与 MQTT 发布正在运行。双击托盘图标可打开主窗口。");
                return;
            }

            // 强制登录循环：登录成功才进入主界面，否则退出程序
            if (!ShowLoginUntilAuthenticated())
            {
                _forceClose = true;
                Current.Shutdown();
                return;
            }

            // 登录成功：订阅"退出登录"、创建托盘、显示主窗口
            if (_mainWindow.DataContext is MainWindowViewModel vm)
                vm.RequestLogout += OnRequestLogout;
            SetupTrayIcon();
            _mainWindow.Show();
        }

        /// <summary>循环显示登录框，直到认证成功；用户取消/关闭则返回 false（退出程序）。</summary>
        private bool ShowLoginUntilAuthenticated()
        {
            var auth = Container.Resolve<IAuthService>();
            while (true)
            {
                var login = Container.Resolve<LoginView>();
                var ok = login.ShowDialog() == true;
                if (!ok) return false;          // 关闭 / 取消 -> 退出程序
                if (auth.IsAuthenticated) return true;
            }
        }

        private void OnRequestLogout()
        {
            var auth = Container.Resolve<IAuthService>();
            auth.Logout();
            _mainWindow?.Hide();
            if (!ShowLoginUntilAuthenticated())
            {
                ExitApp();
                return;
            }

            // 重新登录成功：主窗口此前被 Hide 了，隐藏状态不会因登录成功而自动恢复，
            // 必须显式重新显示并置前，否则用户会遇到"登录成功了但界面不出来"。
            ShowMainWindow();
        }

        #region 窗口关闭 -> 缩到托盘

        private void Main_Closing(object sender, CancelEventArgs e)
        {
            // 强制退出（托盘菜单「退出」触发）时不再拦截
            if (_forceClose) return;

            // 先取消关闭，弹出选择：后台运行 / 退出 / 取消
            e.Cancel = true;

            var dlg = new ExitConfirmDialog { Owner = _mainWindow };
            var ok = dlg.ShowDialog() == true;
            if (!ok) return; // 未做选择（直接关掉对话框）= 取消，保持窗口

            if (dlg.Result == ExitChoice.Tray)
            {
                _mainWindow.Hide();
                ShowBalloon("PLCTesting 已最小化到系统托盘", "MQTT 仍在后台持续发布，双击图标可恢复窗口。");
            }
            else if (dlg.Result == ExitChoice.Exit)
            {
                ExitApp();
            }
        }

        #endregion

        #region 托盘图标

        private void SetupTrayIcon()
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            _trayIcon = new NotifyIcon
            {
                Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application,
                Text = "PLC 测试监控",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            var itemShow = new ToolStripMenuItem("显示主窗口");
            itemShow.Click += (s, e) => ShowMainWindow();
            var itemMqtt = new ToolStripMenuItem("MQTT 配置");
            itemMqtt.Click += (s, e) => OpenMqttConfig();
            var itemLogout = new ToolStripMenuItem("退出登录");
            itemLogout.Click += (s, e) => OnRequestLogout();
            var itemExit = new ToolStripMenuItem("退出");
            itemExit.Click += (s, e) => ExitApp();
            menu.Items.Add(itemShow);
            menu.Items.Add(itemMqtt);
            menu.Items.Add(itemLogout);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemExit);
            _trayIcon.ContextMenuStrip = menu;

            _trayIcon.DoubleClick += (s, e) => ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null) return;

            // 后台启动时没有走过登录流程；首次打开主窗口必须先认证。
            // 认证失败（用户在登录框上取消）→ 退回后台，保持托盘运行。
            var auth = Container.Resolve<IAuthService>();
            if (!auth.IsAuthenticated)
            {
                if (!ShowLoginUntilAuthenticated())
                {
                    ShowBalloon("未登录", "已取消登录，PLCTesting 继续在后台运行。");
                    return;
                }
                if (_mainWindow.DataContext is MainWindowViewModel vmAfterLogin)
                    vmAfterLogin.RequestLogout += OnRequestLogout;
            }

            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            // 临时置顶，确保从托盘恢复时可见
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
        }

        private void OpenMqttConfig()
        {
            ShowMainWindow();
            if (_mainWindow.DataContext is MainWindowViewModel vm)
                vm.OpenMqttConfigCommand.Execute();
        }

        private async void ExitApp()
        {
            // 真正退出：先停止 MQTT 转发，再释放托盘并关闭进程
            if (_mainWindow?.DataContext is MainWindowViewModel vm)
            {
                try { await vm.MqttService.StopAsync(); } catch { /* 忽略 */ }
            }

            _trayIcon?.Dispose();
            _trayIcon = null;

            _forceClose = true;
            Current.Shutdown();
        }

        private void ShowBalloon(string title, string text)
        {
            try
            {
                _trayIcon?.ShowBalloonTip(3000, "PLCTesting", $"{title}\n{text}", ToolTipIcon.Info);
            }
            catch
            {
                // 部分系统/远程桌面下气泡提示可能不可用，忽略即可
            }
        }

        #endregion

        protected override void OnExit(ExitEventArgs e)
        {
            // —— P0 结构化日志：记录软件关闭时间（覆盖正常退出/登录取消/托盘退出全部路径）——
            try
            {
                var lifecycle = StructuredLogger.Current.ForContext("Lifecycle");
                string user = "未登录/未知";
                try
                {
                    var auth = Container.Resolve<IAuthService>();
                    if (auth.CurrentUser != null) user = auth.CurrentUser.UserName;
                }
                catch { /* 取当前用户失败不影响关闭记录 */ }
                lifecycle.Information(
                    "软件关闭 ShutdownTime={ShutdownTime} ExitCode={ExitCode} User={User}",
                    DateTimeOffset.Now, e.ApplicationExitCode, user);
            }
            catch { /* 日志写入失败不应阻塞退出 */ }

            // 确保缓冲刷新到磁盘（文件 sink 同步写，这里再做一次兜底）
            StructuredLogger.CloseAndFlush();

            _trayIcon?.Dispose();
            _trayIcon = null;
            base.OnExit(e);
        }
    }
}
