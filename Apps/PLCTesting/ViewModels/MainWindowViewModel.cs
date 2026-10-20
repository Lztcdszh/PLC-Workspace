using MyPLCFoundationLibrary.Logging;
using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using MySecurityLibrary.Models;
using MySecurityLibrary.Services;
using MySecurityLibrary.Views;
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
        public DelegateCommand OpenDataPointEditorCommand { get; private set; }
        public DelegateCommand OpenUserManagementCommand { get; private set; }
        public DelegateCommand OpenSystemSettingsCommand { get; private set; }
        public DelegateCommand LogoutCommand { get; private set; }

        /// <summary>请求退出登录（由 App 处理：隐藏主窗口并重新弹登录框）。</summary>
        public event Action? RequestLogout;

        /// <summary>当前登录用户（未登录为 null）。</summary>
        public AppUser? CurrentUser => _auth.CurrentUser;

        /// <summary>当前用户是否为管理员（决定是否显示"用户管理"）。</summary>
        public bool CanManageUsers => _auth.CanManageUsers;

        /// <summary>当前用户角色的中文显示名。</summary>
        public string CurrentUserRoleText => _auth.CurrentUser?.Role.DisplayName() ?? "";

        /// <summary>
        /// 数据点注册表。提升为字段是为了让「数据点编辑器」保存后能热重载它 ——
        /// 否则注册表停留在启动那一刻的快照，用户改的 PublishMqtt / LogToDb / 新增删除点都要重启软件才生效。
        /// </summary>
        public DataPointRegistry DataPoints { get; }

        /// <summary>MQTT 发布服务（配置窗口与之共享同一实例，保存即热更新）。</summary>
        public MqttPublisherService MqttService { get; }

        /// <summary>自动更新服务（主窗口只用来显示"有新版本"角标，安装动作在系统设置里）。</summary>
        public UpdateService Update { get; } = UpdateService.Default;

        /// <summary>变化日志仓储（供日志记录器和查询窗口共享）。</summary>
        public ChangeLogRepository ChangeLogRepository { get; }

        /// <summary>数据接入服务：把任意数据（含非 PLC 的计算值/外部数据）写库 + 发 MQTT 的统一入口。</summary>
        public DataIngestionService DataIngestion { get; }

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

            // 数据点注册表：从 dataPoints.json 读取要记录/发布的数据点，并注册为 PLC 标签
            DataPoints = new DataPointRegistry();
            DataPoints.ApplyTo(Connection);

            // 注册标签（假设复位位地址为 DB1.DBX10.0）
            var resetTag = PLCTag.CreateBool("参数复位", "DB1.DBX50.0", "参数复位脉冲位");
            Connection.AddTag(resetTag);

            // 初始化 SQLite 日志仓储与变化记录器
            ChangeLogRepository = new ChangeLogRepository(ChangeLogRepository.DefaultDbPath);
            _changeLogger = new PlcChangeLogger(Connection, ChangeLogRepository, DataPoints);

            // 初始化 MQTT 发布服务：若配置为启用，则开机自动连接并转发
            MqttService = new MqttPublisherService(Connection, DataPoints);
            if (MqttService.Config.Enabled)
            {
                _ = MqttService.StartAsync();
            }

            // 数据接入服务：封装"任意数据"入口，通过抽象接口分别写库 / 发 MQTT（两者解耦、可独立缺失）
            DataIngestion = new DataIngestionService(DataPoints, ChangeLogRepository, MqttService);
            DataIngestionService.Current = DataIngestion;

            ResetParametersCommand = new DelegateCommand(ExecuteResetParameters);
            OpenLogQueryCommand = new DelegateCommand(ExecuteOpenLogQuery);
            OpenParameterSettingsCommand = new DelegateCommand(ExecuteOpenParameterSettings);
            OpenMqttConfigCommand = new DelegateCommand(ExecuteOpenMqttConfig);
            OpenDataPointEditorCommand = new DelegateCommand(ExecuteOpenDataPointEditor);
            OpenUserManagementCommand = new DelegateCommand(ExecuteOpenUserManagement);
            OpenSystemSettingsCommand = new DelegateCommand(ExecuteOpenSystemSettings);
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

        private void ExecuteOpenSystemSettings()
        {
            // ⚠️ ShowDialog 会把 Owner（主窗口）临时置为 IsEnabled=false。
            // 一旦窗口在显示过程中抛异常（1.1.7 实报：ProgressBar 双向绑只读属性 → 崩溃），
            // Owner 就再也不会被恢复，表现为"点了系统设置，主界面全灰没反应"。
            // 所以这里必须兜住异常并显式把 Owner 恢复可用。
            Window window = null;
            try
            {
                window = new SystemSettingsView
                {
                    Owner = Application.Current?.MainWindow
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                var owner = Application.Current?.MainWindow;
                if (owner != null) owner.IsEnabled = true;
                StructuredLogger.Current.ForContext("SystemSettings")
                    .Error(ex, "打开系统设置窗口失败");
                MessageBox.Show(
                    "打开系统设置失败：" + ex.Message,
                    "系统设置",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                if (window != null) { try { window.Close(); } catch { } }
            }
        }

        private void ExecuteOpenDataPointEditor()
        {
            var window = new DataPointEditorView(OnDataPointsSaved)
            {
                Owner = Application.Current?.MainWindow
            };
            window.ShowDialog();
        }

        /// <summary>
        /// 数据点编辑器保存后的热重载：重新读盘并重新注册 PLC 标签，
        /// 让新的 PublishMqtt / LogToDb 开关、新增或删除的点立即生效（无需重启软件）。
        /// </summary>
        private void OnDataPointsSaved()
        {
            DataPoints.Reload();
            DataPoints.ApplyTo(Connection);
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