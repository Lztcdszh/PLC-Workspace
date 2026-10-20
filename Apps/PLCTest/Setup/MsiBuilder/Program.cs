using System;
using System.IO;
using System.Linq;
using System.Text;
using WixSharp;
// WixSharp.File 与 System.IO.File 同名，这里给前者取别名，避免全文件都要写限定名
using WixFile = WixSharp.File;

namespace MsiBuilder
{
    /// <summary>
    /// 为 PLCTesting 自包含发布目录生成 Windows 安装包（MSI）。
    ///
    /// 环境约束（本沙箱）：
    ///   - WiX v5 (`wix build`) 不可用 → 用 WixSharp 1.26 + WiX 3 二进制（candle/light）
    ///   - net8 不认 WixSharp 的 net462 资产 → 用 Reference HintPath（见 csproj）
    ///   - 缺 Windows Installer 脚本引擎 → LightOptions 必须带 -sval 跳过 ICE 校验
    ///   - zh-CN → 代码页 936 → 必须先注册 CodePagesEncodingProvider
    ///
    /// 用法：WIXSHARP_WIXDIR=<wixsharp.wix.bin/tools/bin> dotnet run -c Release
    /// </summary>
    internal static class Program
    {
        // ---- 路径（绝对路径，避免受工作目录影响）----
        private const string RepoRoot = @"C:\Users\Administrator\source\repos";
        private static readonly string Src = Path.Combine(RepoRoot, @"Apps\PLCTest\PLCTesting-SelfContained-win-x64");
        private static readonly string Icon = Path.Combine(RepoRoot, @"Apps\PLCTesting\Assets\app.ico");
        private static readonly string OutDir = Path.Combine(RepoRoot, @"Apps\PLCTest");

        // ---- 产品信息（与历史版本保持一致，保证能升级旧安装）----
        private const string ProductName = "PLC测试台";
        private const string InstallDirName = "PLCTesting";
        private const string ExeName = "PLCTesting.exe";
        private const string ShortcutName = "PLC测试台";
        private const string OutFileName = "PLCTestingSetup";   // 不要带 .msi，WixSharp 会自己加

        private const string ProductVersion = "1.2.0";

        /// <summary>
        /// 用户数据 / 现场配置文件：**不纳入 MSI 组件清单**。
        /// 实测踩过的坑（1.1.0 首版 MSI）：
        ///   ① 卸载时被一并删除 —— 现场辛苦调好的数据点配置直接没了；
        ///   ② 重装/升级时用源码里的默认值覆盖用户改过的设置
        ///      （用户的 dataPoints.json 里 LogToDb=false，被覆盖回 true）。
        /// 排除后由程序自己负责首次创建：DataPointRegistry 在文件缺失时用内置默认点，
        /// 数据点编辑器的「保存」会写盘生成该文件；AppSettingsStore 同理。
        /// </summary>
        private static readonly string[] UserDataFiles =
            { "dataPoints.json", "mqttconfig.json", "appsettings.json" };

        /// <summary>升级识别码。**必须与历史版本一致**，否则新旧版会并存而不是升级。</summary>
        private static readonly Guid UpgradeCode = new("7E4B3C2D-0E5F-4B8A-9D6C-2F3A4B5C6D7E");

        private static void Main()
        {
            // zh-CN → codepage 936（GBK），net8 默认不带，必须注册
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // 沙箱没有 Windows Installer 脚本引擎，ICE 校验必然失败 → -sval 跳过
            Compiler.LightOptions = "-sw1076 -sw1079 -sval";

            if (!Directory.Exists(Src))
            {
                Console.WriteLine($"[FAIL] 自包含发布目录不存在，请先 dotnet publish：\n       {Src}");
                Environment.Exit(1);
            }
            if (!System.IO.File.Exists(Path.Combine(Src, ExeName)))
            {
                Console.WriteLine($"[FAIL] 发布目录里找不到 {ExeName}：\n       {Src}");
                Environment.Exit(1);
            }
            if (!System.IO.File.Exists(Icon))
            {
                Console.WriteLine($"[FAIL] 图标不存在：{Icon}");
                Environment.Exit(1);
            }

            var root = BuildDir(Src, InstallDirName);

            var project = new Project(ProductName, root)
            {
                Version = new Version(ProductVersion),
                Language = "zh-CN",
                UpgradeCode = UpgradeCode,

                // 不设 ProductId：交给 WiX 生成新的 ProductCode，
                // 配合固定 UpgradeCode + MajorUpgrade 才能正确"升级替换"而不是并存。
                OutDir = OutDir,
                OutFileName = OutFileName,
            };

            // 旧版安装包没有 Upgrade 表，导致新旧版并存 —— 这里补上主升级策略
            project.MajorUpgrade = new MajorUpgrade
            {
                AllowDowngrades = false,
                DowngradeErrorMessage = "已安装更高版本的「" + ProductName + "」，请先卸载后再安装。",
            };

            Console.WriteLine($"产品      : {ProductName}");
            Console.WriteLine($"版本      : {ProductVersion}");
            Console.WriteLine($"源目录    : {Src}");
            Console.WriteLine($"安装到    : Program Files\\{InstallDirName}");
            Console.WriteLine($"UpgradeCode: {UpgradeCode}");
            Console.WriteLine("正在构建 MSI …");

            project.BuildMsi();

            var msi = Path.Combine(OutDir, OutFileName + ".msi");
            var size = new FileInfo(msi).Length / 1024.0 / 1024.0;
            Console.WriteLine($"\nDONE -> {msi}  ({size:0.0} MB)");
        }

        /// <summary>递归展开目录为 WixSharp 的 Dir/File 树（跳过用户数据文件）。</summary>
        private static WixEntity[] Items(string dir)
        {
            var files = new DirectoryInfo(dir).GetFiles()
                // 用户配置文件交给程序自己管理，不纳入 MSI（见 UserDataFiles 注释）
                .Where(fi => !UserDataFiles.Contains(fi.Name, StringComparer.OrdinalIgnoreCase))
                .Select<FileInfo, WixEntity>(fi =>
                {
                    var f = new WixFile(fi.FullName);
                    // 只有主程序挂快捷方式：桌面 + 开始菜单，用自定义图标
                    if (fi.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        f.Shortcuts = new[]
                        {
                            new FileShortcut(ShortcutName, "DesktopFolder")    { IconFile = Icon, IconIndex = 0 },
                            new FileShortcut(ShortcutName, "ProgramMenuFolder") { IconFile = Icon, IconIndex = 0 },
                        };
                    }
                    return f;
                });

            var dirs = Directory.GetDirectories(dir)
                .Select(d => (WixEntity)BuildDir(d, Path.GetFileName(d)));

            return files.Concat(dirs).ToArray();
        }

        private static Dir BuildDir(string dir, string name) => new(name, Items(dir));
    }
}
