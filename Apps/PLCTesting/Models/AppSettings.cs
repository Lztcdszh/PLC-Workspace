using System.Text.Json.Serialization;

namespace PLCTesting.Models
{
    /// <summary>
    /// 自动更新策略。
    /// </summary>
    public enum UpdateMode
    {
        /// <summary>不检查更新（完全离线环境用）。</summary>
        Off = 0,

        /// <summary>
        /// 发现新版本后提示，由人确认后才下载安装（默认）。
        /// 现场是工业上位机，自动重启有风险，所以默认走这一档。
        /// </summary>
        Prompt = 1,

        /// <summary>
        /// 自动下载并静默安装，装完自动重启。
        /// 注意：程序自身是 asInvoker（不提权），未提权时 Windows 会弹一次 UAC，
        /// 这是系统限制，无法绕过。
        /// </summary>
        Auto = 2
    }

    /// <summary>
    /// 程序级设置（与 MQTT 数据点配置无关，只描述"这个程序自己怎么运行"）。
    /// 持久化到 appsettings.json（位于程序目录），该文件不出现在 MSI 组件清单里 ——
    /// 否则卸载会删掉、升级会用默认值覆盖用户现场设置。
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// 开机自启动：勾选后写入当前用户注册表 Run 项，Windows 登录时自动运行本程序。
        /// 仅对当前用户生效，不需要管理员权限（不写 HKLM）。
        /// <para>
        /// 注意：Run 项固定带 <c>--background</c> 参数，因此"开机自启动"必然后台运行
        /// （不弹窗口、只在托盘）；平时手动双击图标不带参数，正常显示登录界面和主窗口。
        /// 是否后台启动只认命令行参数，不再有独立的"后台运行"勾选——
        /// 早期版本用它导致"正常打开也进托盘"的误报，已废除（旧文件里的字段被忽略）。
        /// </para>
        /// </summary>
        public bool AutoStartOnBoot { get; set; } = false;

        /// <summary>
        /// 自动更新策略。默认 <see cref="UpdateMode.Prompt"/>（提示后由人确认）。
        /// </summary>
        public UpdateMode UpdateMode { get; set; } = UpdateMode.Prompt;

        /// <summary>
        /// 用户选择"跳过此版本"的版本号（如 "1.1.7"）。
        /// 命中该版本时不再自动弹提示，但仍可在系统设置里手动检查并安装。
        /// </summary>
        public string SkippedVersion { get; set; } = "";
    }
}
