using Microsoft.Win32;
using PLCTesting.Models;
using System;
using System.Diagnostics;
using System.IO;

namespace PLCTesting.Services
{
    /// <summary>
    /// 开机自启动：往当前用户的 Run 项里写/删一条启动命令。
    /// <para>
    /// 为什么用 HKCU 而不是 HKLM：只对"当前用户"生效、不需要管理员权限；
    /// 工业现场的上位机通常以普通域/本地账户登录，UAC 一提权就弹窗反而不好用。
    /// </para>
    /// <para>
    /// 为什么不用 <c>reg.exe</c>：.NET 自带 <c>Microsoft.Win32.Registry</c> 直接操作，
    /// 不依赖外部进程，也不受本机安全策略对注册表命令行工具的拦截影响。
    /// </para>
    /// </summary>
    public class StartupService
    {
        /// <summary>Run 项路径（当前用户）。</summary>
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>本程序在 Run 项里的值名。</summary>
        public const string ValueName = "PLCTesting";

        private readonly RegistryKey _runKey;

        /// <summary>
        /// 可注入注册表基键：默认写真实 HKCU（生产行为）；
        /// 测试时传一个临时子键，避免污染用户的开机启动项。
        /// </summary>
        public StartupService(RegistryKey runKey = null)
        {
            if (runKey != null)
            {
                _runKey = runKey;
            }
            else
            {
                _runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                           ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            }
        }

        /// <summary>开机自启动当前是否已启用（以注册表实际内容为准，不是以配置文件为准）。</summary>
        public bool IsEnabled
        {
            get
            {
                if (_runKey == null) return false;
                var v = _runKey.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(v);
            }
        }

        /// <summary>当前 Run 项里记录的命令行（未启用时为空串）。</summary>
        public string CommandLine
        {
            get
            {
                if (_runKey == null) return string.Empty;
                return _runKey.GetValue(ValueName) as string ?? string.Empty;
            }
        }

        /// <summary>
        /// 设置开机自启动。
        /// </summary>
        /// <param name="enabled">true 写入；false 删除。</param>
        /// <param name="extraArgument">
        /// 追加到 exe 后面的命令行参数。自启动一律带 <c>--background</c>，
        /// 保证开机进系统时不弹登录框、不抢焦点，直接安静地跑在托盘里。
        /// </param>
        public void Set(bool enabled, string extraArgument = "--background")
        {
            if (_runKey == null)
                throw new InvalidOperationException("无法访问开机启动注册表项（HKCU Run）。");

            if (enabled)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                    throw new InvalidOperationException("无法确定程序自身的可执行文件路径。");

                var cmd = string.IsNullOrWhiteSpace(extraArgument)
                    ? $"\"{exe}\""
                    : $"\"{exe}\" {extraArgument.Trim()}";

                _runKey.SetValue(ValueName, cmd, RegistryValueKind.String);
            }
            else
            {
                // 只有"确实是我们写的那条"才删，避免误删同名第三方项
                if (IsEnabled)
                    _runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        /// <summary>把注册表实际状态同步回设置对象（开机启动被外部工具/杀毒清理掉时，界面勾选状态跟着变）。</summary>
        public void SyncTo(AppSettings settings)
        {
            if (settings == null) return;
            settings.AutoStartOnBoot = IsEnabled;
        }

        /// <summary>本程序是否以 --background 方式启动（无界面）。</summary>
        public static bool HasBackgroundArgument(string[] args)
        {
            if (args == null) return false;
            foreach (var a in args)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                var s = a.Trim().ToLowerInvariant();
                if (s is "--background" or "-background" or "/background" or "--silent" or "-silent")
                    return true;
            }
            return false;
        }
    }
}
