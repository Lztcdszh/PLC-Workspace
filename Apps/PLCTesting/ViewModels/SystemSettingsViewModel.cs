using PLCTesting.Models;
using PLCTesting.Services;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Windows;

namespace PLCTesting.ViewModels
{
    /// <summary>
    /// 系统设置窗口：开机自启动。
    /// 保存时把勾选状态写进 appsettings.json，并同步注册表 Run 项 ——
    /// 两者必须一致，否则会出现"界面勾了但开机没反应"或"注册表还有但界面没勾"。
    /// <para>
    /// 运行方式语义（2026-10-15 起）：是否后台启动只认命令行 <c>--background</c>。
    /// Run 项固定带该参数 → 开机自启动一定是后台托盘运行；手动双击图标不带参数 →
    /// 正常登录 + 主窗口。不再提供"后台运行"勾选（旧实报：正常打开也进托盘）。
    /// </para>
    /// </summary>
    public class SystemSettingsViewModel : BindableBase
    {
        private readonly AppSettingsStore _store;
        private readonly StartupService _startup;

        /// <summary>自动更新服务（界面直接绑定 Update.* 用它的状态与命令）。</summary>
        public UpdateService Update { get; }

        private bool _autoStartOnBoot;
        /// <summary>开机自启动（写入当前用户 HKCU Run 项，带 --background 参数）。</summary>
        public bool AutoStartOnBoot
        {
            get => _autoStartOnBoot;
            set => SetProperty(ref _autoStartOnBoot, value);
        }

        /// <summary>是否启用自动检测更新（关闭 = 永不联网检查）。</summary>
        public bool UpdateCheckEnabled
        {
            get => Update.Mode != UpdateMode.Off;
            set
            {
                Update.Mode = value
                    ? (Update.IsAuto ? UpdateMode.Auto : UpdateMode.Prompt)
                    : UpdateMode.Off;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(UpdateAutoInstall));
            }
        }

        /// <summary>检测到新版本后是否自动安装（否则只在界面/托盘提示，由人确认）。</summary>
        public bool UpdateAutoInstall
        {
            get => Update.Mode == UpdateMode.Auto;
            set
            {
                Update.Mode = value ? UpdateMode.Auto : UpdateMode.Prompt;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(UpdateCheckEnabled));
            }
        }

        private string _statusText = "";
        /// <summary>注册表实际状态（开机启动是否真的写进去了）。</summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public DelegateCommand SaveCommand { get; }

        /// <param name="store">设置存储；为空则用进程单例。</param>
        /// <param name="startup">开机启动服务；为空则用真实注册表。</param>
        /// <param name="update">自动更新服务；为空则用进程单例。</param>
        public SystemSettingsViewModel(AppSettingsStore store = null, StartupService startup = null, UpdateService update = null)
        {
            _store = store ?? AppSettingsStore.Default;
            _startup = startup ?? new StartupService();
            Update = update ?? UpdateService.Default;

            // 以注册表为准回显勾选状态：开机启动可能被外部工具/杀毒清理掉
            _startup.SyncTo(_store.Current);

            _autoStartOnBoot = _store.Current.AutoStartOnBoot;

            // Update 自己改 Mode 时，本 VM 的两个派生属性要跟着刷新
            Update.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(UpdateService.Mode) || string.IsNullOrEmpty(e.PropertyName))
                {
                    RaisePropertyChanged(nameof(UpdateCheckEnabled));
                    RaisePropertyChanged(nameof(UpdateAutoInstall));
                }
            };

            SaveCommand = new DelegateCommand(ExecuteSave);
            RefreshStatus();

            // 打开设置窗口就顺手查一次（已是最新/失败都会把结果显示在状态栏）
            if (Update.Mode != UpdateMode.Off)
                _ = Update.CheckAsync(false);
        }

        private void RefreshStatus()
        {
            var enabled = _startup.IsEnabled;
            var cmd = _startup.CommandLine;
            StatusText = enabled
                ? $"已写入开机启动项：{cmd}"
                : "未设置开机启动（当前用户）。勾选后点保存即生效。";
        }

        private void ExecuteSave()
        {
            try
            {
                // ⚠️ 必须把 Update 里已经改好的策略原样带上：
                // 自动更新的勾选是"即时保存"的，这里再 new 一个空设置会把它们清回默认值。
                var settings = new AppSettings
                {
                    AutoStartOnBoot = AutoStartOnBoot,
                    UpdateMode = Update.Mode,
                    SkippedVersion = _store.Current.SkippedVersion
                };

                _startup.Set(AutoStartOnBoot);
                _store.Update(settings);
                _store.Save();

                RefreshStatus();

                MessageBox.Show(
                    AutoStartOnBoot
                        ? "已保存。\n\n开机自启动：已写入注册表，下次登录 Windows 时自动在后台运行（不弹窗口，只在托盘）。\n平时双击图标打开：正常显示登录界面和主窗口。"
                        : "已保存。\n\n开机自启动：已取消。\n双击图标打开时正常显示登录界面和主窗口。",
                    "系统设置",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败：{ex.Message}", "系统设置",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
