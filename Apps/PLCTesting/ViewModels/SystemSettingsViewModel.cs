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

        private bool _autoStartOnBoot;
        /// <summary>开机自启动（写入当前用户 HKCU Run 项，带 --background 参数）。</summary>
        public bool AutoStartOnBoot
        {
            get => _autoStartOnBoot;
            set => SetProperty(ref _autoStartOnBoot, value);
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
        public SystemSettingsViewModel(AppSettingsStore store = null, StartupService startup = null)
        {
            _store = store ?? AppSettingsStore.Default;
            _startup = startup ?? new StartupService();

            // 以注册表为准回显勾选状态：开机启动可能被外部工具/杀毒清理掉
            _startup.SyncTo(_store.Current);

            _autoStartOnBoot = _store.Current.AutoStartOnBoot;

            SaveCommand = new DelegateCommand(ExecuteSave);
            RefreshStatus();
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
                var settings = new AppSettings
                {
                    AutoStartOnBoot = AutoStartOnBoot
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
