using MyPLCManagerLibrary;
using PLCTesting.Security.Services;
using PLCTesting.Security.Views;
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
        private NotifyIcon _trayIcon;
        private MainWindow _mainWindow;
        private bool _forceClose;

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
            base.OnInitialized();

            _mainWindow = (MainWindow)Current.MainWindow;
            if (_mainWindow != null)
            {
                _mainWindow.Closing += Main_Closing;
            }

            // 首次运行写入默认管理员（admin / admin123）
            Container.Resolve<IUserStore>().EnsureSeeded();

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
                ExitApp();
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
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
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
            _trayIcon?.Dispose();
            _trayIcon = null;
            base.OnExit(e);
        }
    }
}
