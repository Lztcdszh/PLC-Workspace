using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using PLCTesting.Security.Models;
using PLCTesting.Security.Services;
using PLCTesting.Security.Views;
using PLCTesting.Services;
using PLCTesting.Views;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace PLCTesting.ViewModels
{
    public class MainWindowViewModel : BindableBase
    {
        private string _title = "骨料巡检机器人调试上位机";
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        private IConnectionManager _connection;
        public IConnectionManager Connection
        {
            get => _connection;
            set => SetProperty(ref _connection, value);
        }

        private readonly IAuthService _auth;
        private readonly IUserStore _userStore;

        public DelegateCommand ResetParametersCommand { get; private set; }
        public DelegateCommand OpenLogQueryCommand { get; private set; }
        public DelegateCommand OpenParameterSettingsCommand { get; private set; }
        public DelegateCommand OpenMqttConfigCommand { get; private set; }
        public DelegateCommand OpenUserManagementCommand { get; private set; }
        public DelegateCommand LogoutCommand { get; private set; }

        /// <summary>请求退出登录（由 App 处理：隐藏主窗口并重新弹登录框）。</summary>
        public event Action? RequestLogout;

        /// <summary>当前登录用户（未登录为 null）。</summary>
        public AppUser? CurrentUser => _auth.CurrentUser;

        /// <summary>当前用户是否为管理员（决定是否显示"用户管理"）。</summary>
        public bool CanManageUsers => _auth.CanManageUsers;

        /// <summary>当前用户角色的中文显示名。</summary>
        public string CurrentUserRoleText => _auth.CurrentUser?.Role.DisplayName() ?? "";

        /// <summary>MQTT 发布服务（配置窗口与之共享同一实例，保存即热更新）。</summary>
        public MqttPublisherService MqttService { get; }

        /// <summary>变化日志仓储（供日志记录器和查询窗口共享）。</summary>
        public ChangeLogRepository ChangeLogRepository { get; }

        private PlcChangeLogger _changeLogger;

        public MainWindowViewModel(IAuthService auth, IUserStore userStore)
        {
            _auth = auth;
            _userStore = userStore;
            // 认证状态变化（登录 / 登出 / 重新登录）时刷新顶栏绑定
            _auth.PropertyChanged += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.PropertyName) ||
                    e.PropertyName is nameof(IAuthService.CurrentUser) or nameof(IAuthService.CanManageUsers))
                {
                    RaisePropertyChanged(nameof(CurrentUser));
                    RaisePropertyChanged(nameof(CanManageUsers));
                    RaisePropertyChanged(nameof(CurrentUserRoleText));
                }
            };

            var plcConfig = new PLCConfig
            {
                Name = "PLC1",
                IP = "192.168.7.80",
                Type = PLCType.S7200Smart,
                Protocol = ProtocolType.S7
            };
            Connection = PLCConnection.GetOrCreate(plcConfig);

            // 注册标签（假设复位位地址为 DB1.DBX10.0）
            var resetTag = PLCTag.CreateBool("参数复位", "DB1.DBX50.0", "参数复位脉冲位");
            Connection.AddTag(resetTag);

            // 初始化 SQLite 日志仓储与变化记录器
            ChangeLogRepository = new ChangeLogRepository(ChangeLogRepository.DefaultDbPath);
            _changeLogger = new PlcChangeLogger(Connection, ChangeLogRepository);

            // 初始化 MQTT 发布服务：若配置为启用，则开机自动连接并转发
            MqttService = new MqttPublisherService(Connection);
            if (MqttService.Config.Enabled)
            {
                _ = MqttService.StartAsync();
            }

            ResetParametersCommand = new DelegateCommand(ExecuteResetParameters);
            OpenLogQueryCommand = new DelegateCommand(ExecuteOpenLogQuery);
            OpenParameterSettingsCommand = new DelegateCommand(ExecuteOpenParameterSettings);
            OpenMqttConfigCommand = new DelegateCommand(ExecuteOpenMqttConfig);
            OpenUserManagementCommand = new DelegateCommand(ExecuteOpenUserManagement);
            LogoutCommand = new DelegateCommand(() => RequestLogout?.Invoke());

            // 程序退出时断开 MQTT 连接，避免句柄泄漏
            Application.Current.Exit += (s, e) => { _ = MqttService.StopAsync(); };
        }

        private void ExecuteOpenUserManagement()
        {
            // 仅管理员可打开；按钮可见性已由 CanManageUsers 控制
            if (!_auth.CanManageUsers) return;
            var window = new UserManagementView(_userStore, _auth)
            {
                Owner = Application.Current?.MainWindow
            };
            window.Show();
        }

        private void ExecuteOpenParameterSettings()
        {
            var window = new ParameterSettingsView(Connection)
            {
                Owner = Application.Current?.MainWindow
            };
            window.Show();
        }

        private void ExecuteOpenMqttConfig()
        {
            var window = new MqttConfigView(MqttService, Connection)
            {
                Owner = Application.Current?.MainWindow
            };
            window.Show();
        }

        private void ExecuteOpenLogQuery()
        {
            var window = new LogQueryView(ChangeLogRepository)
            {
                Owner = Application.Current?.MainWindow
            };
            window.Show();
        }

        private async void ExecuteResetParameters()
        {
            var result = MessageBox.Show(
                "确认要将所有参数恢复为默认值吗？\n此操作将写入PLC并覆盖当前设定。",
                "参数复位确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK) return;

            if (Connection == null || !Connection.IsConnected)
            {
                MessageBox.Show("PLC 未连接，无法执行复位。", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // 发送复位脉冲：先写 true，延时 1 秒后写 false
                await Connection.WriteTagAsync("参数复位", true);
                await Task.Delay(1000);
                await Connection.WriteTagAsync("参数复位", false);

                MessageBox.Show("复位指令已发送。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"复位失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}