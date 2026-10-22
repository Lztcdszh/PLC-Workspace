using MyPLCManagerLibrary.Interfaces;
using PLCTesting.Models;
using PLCTesting.Services;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;

namespace PLCTesting.ViewModels
{
    /// <summary>
    /// MQTT 配置窗口视图模型：编辑一份配置副本，保存时写回服务并持久化；
    /// 提供测试连接、启动/停止转发、立即发布一次。
    /// 发布方式（变化时 / 定时）在界面上用两个勾选项表达，保存时组合为 MqttPublishMode。
    /// </summary>
    public class MqttConfigViewModel : BindableBase
    {
        private readonly MqttPublisherService _service;

        private MqttConfig _config;
        public MqttConfig Config
        {
            get => _config;
            set => SetProperty(ref _config, value);
        }

        public MqttPublisherService Service => _service;

        #region 发布方式（界面态）

        private bool _publishOnChange;
        /// <summary>变化时发布：标签值一变就发一帧。</summary>
        public bool PublishOnChange
        {
            get => _publishOnChange;
            set { if (SetProperty(ref _publishOnChange, value)) RaisePropertyChanged(nameof(ModeHint)); }
        }

        private bool _publishPeriodic;
        /// <summary>定时发布：按固定周期把全部启用点重发一遍（值不变也发）。</summary>
        public bool PublishPeriodic
        {
            get => _publishPeriodic;
            set { if (SetProperty(ref _publishPeriodic, value)) RaisePropertyChanged(nameof(ModeHint)); }
        }

        private double _intervalSeconds = 1;
        /// <summary>定时发布周期（秒），由 IntervalText 解析而来，已收敛到 0.2 ~ 3600。</summary>
        public double IntervalSeconds => _intervalSeconds;

        private string _intervalText = "1";
        /// <summary>
        /// 周期输入框文本。用字符串承接输入，避免用户删空/输一半时触发 WPF 数值转换错误红框；
        /// 解析成功即更新 IntervalSeconds，解析失败保留上一次有效值。
        /// </summary>
        public string IntervalText
        {
            get => _intervalText;
            set
            {
                if (!SetProperty(ref _intervalText, value)) return;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ||
                    double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out sec))
                {
                    _intervalSeconds = Math.Max(0.2, Math.Min(3600, sec));
                }
                RaisePropertyChanged(nameof(IntervalSeconds));
                RaisePropertyChanged(nameof(ModeHint));
            }
        }

        /// <summary>当前发布方式的自然语言描述，显示在配置窗口里。</summary>
        public string ModeHint
        {
            get
            {
                var interval = Math.Max(0.2, Math.Min(3600, IntervalSeconds));
                return (_publishOnChange, _publishPeriodic) switch
                {
                    (true, true) => $"值变化立即发，同时每 {interval:0.##} 秒把所有启用点重发一遍。",
                    (true, false) => "仅在值发生变化时发布（值长期不变时监视端会看到旧的时间戳）。",
                    (false, true) => $"仅按周期发布：每 {interval:0.##} 秒把所有启用点重发一遍。",
                    _ => "⚠ 两种方式都未勾选，启动后不会发布任何数据。"
                };
            }
        }

        #endregion

        #region TLS 加密传输（界面态，写回 Config）

        /// <summary>是否启用 TLS 加密上传。切换时联动证书配置区的显隐。</summary>
        public bool UseTls
        {
            get => Config.UseTls;
            set
            {
                if (Config.UseTls == value) return;
                Config.UseTls = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(TlsOptionsVisible));
                RaisePropertyChanged(nameof(TlsSummary));
            }
        }

        /// <summary>证书相关选项只在勾选 TLS 时显示。</summary>
        public bool TlsOptionsVisible => Config.UseTls;

        /// <summary>当前加密配置的一句话摘要，显示在标题旁。</summary>
        public string TlsSummary
        {
            get
            {
                if (!Config.UseTls) return "未加密（明文传输）";
                if (Config.AllowUntrustedCert) return "已加密 · 跳过证书校验（联调模式）";
                var hasCa = !string.IsNullOrWhiteSpace(Config.CaCertPath);
                var hasClient = !string.IsNullOrWhiteSpace(Config.ClientCertPath);
                if (hasCa && hasClient) return "已加密 · 自签 CA + 客户端证书（双向认证）";
                if (hasCa) return "已加密 · 自建 CA 校验";
                if (hasClient) return "已加密 · 客户端证书（双向认证）";
                return "已加密 · 使用系统信任库校验";
            }
        }

        public string CaCertPath
        {
            get => Config.CaCertPath;
            set
            {
                if (Config.CaCertPath == value) return;
                Config.CaCertPath = value ?? string.Empty;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(TlsSummary));
            }
        }

        public string ClientCertPath
        {
            get => Config.ClientCertPath;
            set
            {
                if (Config.ClientCertPath == value) return;
                Config.ClientCertPath = value ?? string.Empty;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(TlsSummary));
            }
        }

        public string ClientCertPassword
        {
            get => Config.ClientCertPassword;
            set
            {
                if (Config.ClientCertPassword == value) return;
                Config.ClientCertPassword = value ?? string.Empty;
                RaisePropertyChanged();
            }
        }

        public string TlsTargetHost
        {
            get => Config.TlsTargetHost;
            set
            {
                if (Config.TlsTargetHost == value) return;
                Config.TlsTargetHost = value ?? string.Empty;
                RaisePropertyChanged();
            }
        }

        public bool AllowUntrustedCert
        {
            get => Config.AllowUntrustedCert;
            set
            {
                if (Config.AllowUntrustedCert == value) return;
                Config.AllowUntrustedCert = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(TlsSummary));
            }
        }

        public DelegateCommand BrowseCaCertCommand { get; }
        public DelegateCommand BrowseClientCertCommand { get; }

        #endregion

        private string _testResult = "";
        public string TestResult
        {
            get => _testResult;
            set => SetProperty(ref _testResult, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public DelegateCommand SaveCommand { get; }
        public DelegateCommand TestCommand { get; }
        public DelegateCommand TogglePublishCommand { get; }
        public DelegateCommand PublishNowCommand { get; }

        public MqttConfigViewModel(MqttPublisherService service, IConnectionManager connection)
        {
            _service = service;
            Config = Clone(service.Config);

            // 把枚举拆成两个勾选项，便于勾「两者都要」
            PublishOnChange = Config.IsOnChange;
            PublishPeriodic = Config.IsPeriodic;
            _intervalSeconds = Config.EffectiveIntervalMs / 1000.0;
            _intervalText = _intervalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

            SaveCommand = new DelegateCommand(async () => await ExecuteSaveAsync());
            TestCommand = new DelegateCommand(async () => await ExecuteTestAsync());
            TogglePublishCommand = new DelegateCommand(async () => await ExecuteTogglePublishAsync());
            PublishNowCommand = new DelegateCommand(async () => await ExecutePublishNowAsync(),
                () => _service.IsConnected)
                .ObservesProperty(() => Service.IsConnected);

            BrowseCaCertCommand = new DelegateCommand(() => BrowseCert(
                "选择 CA 根证书（服务端证书的签发者）",
                "证书文件 (*.crt;*.cer;*.pem)|*.crt;*.cer;*.pem|所有文件 (*.*)|*.*",
                p => CaCertPath = p));

            BrowseClientCertCommand = new DelegateCommand(() => BrowseCert(
                "选择客户端证书（含私钥，通常为 .pfx/.p12）",
                "证书文件 (*.pfx;*.p12;*.pem)|*.pfx;*.p12;*.pem|所有文件 (*.*)|*.*",
                p => ClientCertPath = p));
        }

        private static void BrowseCert(string title, string filter, Action<string> apply)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() == true) apply(dialog.FileName);
        }

        private async Task ExecuteSaveAsync()
        {
            IsBusy = true;
            try
            {
                ApplyUiStateToConfig();

                // 覆盖服务配置并持久化
                _service.Config = Clone(Config);
                _service.SaveConfig();
                await _service.ApplyConfigAsync();

                MessageBox.Show("MQTT 配置已保存并应用。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteTestAsync()
        {
            IsBusy = true;
            TestResult = "正在测试连接…";
            try
            {
                ApplyUiStateToConfig();
                var ok = await _service.TestConnectionAsync(Config);
                TestResult = ok
                    ? "✅ 连接成功！Broker 可达。"
                    : $"❌ 连接失败：{_service.LastError}";
            }
            catch (Exception ex)
            {
                TestResult = $"❌ 测试异常：{ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteTogglePublishAsync()
        {
            IsBusy = true;
            try
            {
                if (_service.IsRunning)
                    await _service.StopAsync();
                else
                    await _service.StartAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecutePublishNowAsync()
        {
            IsBusy = true;
            try
            {
                var n = await _service.PublishNowAsync();
                TestResult = n < 0
                    ? "❌ 未连接 Broker，无法发布。"
                    : $"📤 已立即发布 {n} 个数据点。";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>把界面上的发布方式/周期组合回 Config。</summary>
        private void ApplyUiStateToConfig()
        {
            Config.PublishMode = (PublishOnChange, PublishPeriodic) switch
            {
                (true, true) => MqttPublishMode.OnChangeAndPeriodic,
                (true, false) => MqttPublishMode.OnChange,
                (false, true) => MqttPublishMode.Periodic,
                _ => MqttPublishMode.OnChange  // 都没勾：退化为变化触发，服务端仍可识别
            };

            var seconds = Math.Max(0.2, Math.Min(3600, IntervalSeconds));
            Config.PublishIntervalMs = (int)Math.Round(seconds * 1000);
            // 回写纠正界面显示（例如用户输入了非法字符）
            var normalized = seconds.ToString("0.##", CultureInfo.InvariantCulture);
            if (IntervalText != normalized)
                IntervalText = normalized;
        }

        private static MqttConfig Clone(MqttConfig src)
        {
            return new MqttConfig
            {
                Enabled = src.Enabled,
                BrokerHost = src.BrokerHost,
                BrokerPort = src.BrokerPort,
                BaseTopic = src.BaseTopic,
                PublishMode = src.PublishMode,
                PublishIntervalMs = src.PublishIntervalMs,
                // ⚠️ TLS 字段必须一起拷贝：漏掉的话保存后加密配置会被悄悄重置
                UseTls = src.UseTls,
                CaCertPath = src.CaCertPath,
                ClientCertPath = src.ClientCertPath,
                ClientCertPassword = src.ClientCertPassword,
                TlsTargetHost = src.TlsTargetHost,
                AllowUntrustedCert = src.AllowUntrustedCert
            };
        }
    }
}
