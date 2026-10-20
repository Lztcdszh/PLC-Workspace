#nullable enable
using MyPLCFoundationLibrary.Logging;
using PLCTesting.Models;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PLCTesting.Services
{
    /// <summary>
    /// 自动检测更新：从 GitHub Releases 取最新版本 → 比较 → 下载 MSI → 校验 → 提权安装 → 自动重启。
    /// <para>
    /// 为什么走 GitHub Releases：现场站点机只有公网出口，没有内网共享；
    /// Releases 既能放包（资产）又能放版本号与更新说明，一个接口就够。
    /// </para>
    /// <para>
    /// ⚠️ 安全边界：本程序清单是 <c>asInvoker</c>（不提权）。<c>msiexec /qn</c> 需要管理员权限，
    /// 所以未提权时必然弹一次 UAC，这是 Windows 的限制，代码绕不过去。
    /// 已提权（例如管理员手动启动）时才是真正无提示的静默升级。
    /// </para>
    /// </summary>
    public class UpdateService : BindableBase
    {
        private const string RepoOwner = "Lztcdszh";
        private const string RepoName = "PLC-Workspace";

        private static string LatestReleaseApi =>
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

        private static IAppLogger Log => StructuredLogger.Current.ForContext("Update");

        /// <summary>进程内单例（生产代码用这个，界面与托盘共用同一份状态）。</summary>
        public static UpdateService Default { get; } = new(AppSettingsStore.Default);

        private readonly AppSettingsStore _store;
        private readonly HttpClient _http;

        private string _downloadUrl = "";
        /// <summary>
        /// 资产的 API 端点（<c>assets[].url</c>，形如
        /// <c>https://api.github.com/repos/&lt;owner&gt;/&lt;repo&gt;/releases/assets/&lt;id&gt;</c>）。
        /// ⚠️ 这是**首选**下载地址：<c>browser_download_url</c> 指向 github.com，
        /// 该域名在部分现场网络（代理/防火墙）下完全不可达，会表现为"下载失败"。
        /// 带 <c>Accept: application/octet-stream</c> 请求 API 端点，GitHub 会 302 到
        /// <c>release-assets.githubusercontent.com</c>，公开仓库**无需认证**即可下载。
        /// </summary>
        private string _downloadApiUrl = "";
        private string _assetName = "";
        private string _assetDigest = "";
        private long _assetSize;

        private Version? _latestVersion;
        private bool _isBusy;
        private bool _isDownloading;
        private bool _isUpdateAvailable;
        private double _downloadProgress;
        private string _statusText = "尚未检查";
        private string _releaseNotes = "";
        private string _releasePageUrl = "";

        /// <summary>发现可用新版本（仅当用户没有跳过该版本时触发，供托盘弹提示）。</summary>
        public event Action<Version>? UpdateFound;

        /// <summary>请求宿主程序退出以便安装（由 App 负责真正关闭，需绕过退出确认框）。</summary>
        public event Action? ExitRequested;

        #region 只读状态

        /// <summary>当前运行版本（如 1.1.6）。</summary>
        public Version CurrentVersion { get; } =
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

        public string CurrentVersionText => "v" + Short(CurrentVersion);

        public string LatestVersionText => _latestVersion == null ? "—" : "v" + Short(_latestVersion);

        /// <summary>程序当前是否以管理员权限运行（决定能否真正静默安装）。</summary>
        public bool IsElevated
        {
            get
            {
                try
                {
                    using var id = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            private set { if (SetProperty(ref _isUpdateAvailable, value)) { RaisePropertyChanged(nameof(CanInstall)); } }
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set { if (SetProperty(ref _isBusy, value)) { RaisePropertyChanged(nameof(CanInstall)); } }
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            private set => SetProperty(ref _isDownloading, value);
        }

        /// <summary>下载进度（0~100）。</summary>
        public double DownloadProgress
        {
            get => _downloadProgress;
            private set => SetProperty(ref _downloadProgress, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string ReleaseNotes
        {
            get => _releaseNotes;
            private set => SetProperty(ref _releaseNotes, value);
        }

        public string ReleasePageUrl
        {
            get => _releasePageUrl;
            private set => SetProperty(ref _releasePageUrl, value);
        }

        /// <summary>有可用新版本、且远端确实提供了可安装的包。</summary>
        public bool CanInstall =>
            IsUpdateAvailable
            && (!string.IsNullOrEmpty(_downloadApiUrl) || !string.IsNullOrEmpty(_downloadUrl))
            && !IsBusy;

        /// <summary>更新策略（读写 appsettings.json）。</summary>
        public UpdateMode Mode
        {
            get => _store.Current.UpdateMode;
            set
            {
                if (_store.Current.UpdateMode == value) return;
                var s = _store.Current;
                var next = new AppSettings
                {
                    AutoStartOnBoot = s.AutoStartOnBoot,
                    UpdateMode = value,
                    SkippedVersion = s.SkippedVersion
                };
                _store.Update(next);
                _store.Save();
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsAuto));
            }
        }

        public bool IsAuto
        {
            get => Mode == UpdateMode.Auto;
            set => Mode = value ? UpdateMode.Auto : UpdateMode.Prompt;
        }

        #endregion

        public DelegateCommand CheckCommand { get; }
        public DelegateCommand InstallCommand { get; }
        public DelegateCommand SkipCommand { get; }

        public UpdateService(AppSettingsStore store)
        {
            _store = store ?? AppSettingsStore.Default;
            // ⚠️ Timeout 绝不能设小值：HttpClient.Timeout 覆盖的是**整个请求，包含读取响应体**。
            // 安装包约 70MB，20 秒内根本下不完（实测网络好时约 8~11 秒，慢时几分钟），
            // 会直接抛 TaskCanceledException，表现为「下载失败」。
            // 因此这里设为无限超时，改由每个请求各自的 CancellationTokenSource 控制：
            //   - 检查更新（只读一小段 JSON）：30 秒
            //   - 下载安装包：20 分钟
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            CheckCommand = new DelegateCommand(async () => await CheckAsync(true));
            InstallCommand = new DelegateCommand(async () =>
            {
                try { await DownloadAndInstallAsync(); }
                catch (Exception ex) { StatusText = "安装失败：" + ex.Message; Log.Error(ex, "更新安装失败"); }
            });
            SkipCommand = new DelegateCommand(() =>
            {
                if (_latestVersion == null) return;
                var s = _store.Current;
                _store.Update(new AppSettings
                {
                    AutoStartOnBoot = s.AutoStartOnBoot,
                    UpdateMode = s.UpdateMode,
                    SkippedVersion = Short(_latestVersion)
                });
                _store.Save();
                StatusText = $"已跳过 {LatestVersionText}，该版本不再提示（仍可点「检查更新」手动安装）";
            });
        }

        #region 检查

        /// <param name="manual">true = 用户在界面上主动点"检查更新"（忽略 Off 策略与"已跳过"版本）。</param>
        public async Task CheckAsync(bool manual = false)
        {
            if (IsBusy) return;

            var mode = _store.Current.UpdateMode;
            if (!manual && mode == UpdateMode.Off)
            {
                StatusText = "自动更新已关闭";
                return;
            }

            IsBusy = true;
            try
            {
                StatusText = "正在检查更新…";
                Log.Information("检查更新 当前版本={Current} 源={Api}", Short(CurrentVersion), LatestReleaseApi);

                using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
                req.Headers.UserAgent.ParseAdd($"PLCTesting-Updater/{Short(CurrentVersion)}");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");

                // 只取一小段 JSON，30 秒足够；与下载用的长超时分开，避免互相拖累
                using var checkCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var resp = await _http.SendAsync(req, checkCts.Token).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    StatusText = "远端还没有发布版本";
                    Log.Information("检查更新：远端无 Release（404）");
                    return;
                }
                if (resp.StatusCode == (HttpStatusCode)403 && resp.Headers.Contains("X-RateLimit-Remaining"))
                {
                    StatusText = "GitHub 接口访问过于频繁，稍后再试";
                    Log.Warning("检查更新被 GitHub 限流");
                    return;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    StatusText = $"检查失败：HTTP {(int)resp.StatusCode}";
                    Log.Warning("检查更新失败 HTTP {Status}", (int)resp.StatusCode);
                    return;
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                ParseRelease(json);

                if (!IsUpdateAvailable)
                {
                    StatusText = $"已是最新版本（{CurrentVersionText}）";
                    Log.Information("检查更新：已是最新 {Current}", Short(CurrentVersion));
                    return;
                }

                var skipped = string.Equals(_store.Current.SkippedVersion, Short(_latestVersion!), StringComparison.OrdinalIgnoreCase);
                StatusText = $"发现新版本 {LatestVersionText}（当前 {CurrentVersionText}）"
                             + (string.IsNullOrEmpty(_downloadUrl) ? "，但远端没有提供 .msi 包" : "");

                Log.Information("检查更新：发现新版本 {Latest}（当前 {Current}）已跳过={Skipped} 包={Asset}",
                    Short(_latestVersion!), Short(CurrentVersion), skipped, _assetName);

                if (skipped && !manual) return;

                if (mode == UpdateMode.Auto && !skipped)
                    await DownloadAndInstallAsync().ConfigureAwait(false);
                else
                    UpdateFound?.Invoke(_latestVersion!);
            }
            catch (Exception ex)
            {
                StatusText = "检查失败：" + ex.Message;
                Log.Warning(ex, "检查更新失败");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ParseRelease(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            Version? ver = null;
            var cleaned = new string(tag.Where(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');
            if (Version.TryParse(cleaned, out var parsed)) ver = parsed;

            _latestVersion = ver;
            _releasePageUrl = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";
            var body = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
            // 发布说明是 Markdown，界面上只有一行两行的位置：去掉 #/*/` 之类的标记，
            // 把换行压成空格，只留一句能看懂的话（否则窗口里会直接看到 "## 修复：..."）。
            ReleaseNotes = PlainSummary(body, 120);

            _downloadUrl = "";
            _downloadApiUrl = "";
            _assetName = "";
            _assetDigest = "";
            _assetSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                // 优先取 .msi（可直接静默安装），退而取 .zip
                JsonElement? pick = null;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
                }
                if (pick == null)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
                    }
                }

                if (pick != null)
                {
                    var a = pick.Value;
                    _assetName = a.TryGetProperty("name", out var n2) ? (n2.GetString() ?? "") : "";
                    _downloadUrl = a.TryGetProperty("browser_download_url", out var u) ? (u.GetString() ?? "") : "";
                    // API 端点：首选，走 api.github.com（现场网络通常只有这个域名可达）
                    _downloadApiUrl = a.TryGetProperty("url", out var apiUrl) ? (apiUrl.GetString() ?? "") : "";
                    _assetSize = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    // GitHub 自 2024 起为资产提供 digest（形如 "sha256:xxxx"），有就用来校验
                    _assetDigest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                        ? (d.GetString() ?? "") : "";
                }
            }

            IsUpdateAvailable = ver != null && Normalize(ver) > Normalize(CurrentVersion);
            RaisePropertyChanged(nameof(LatestVersionText));
        }

        #endregion

        #region 下载 / 安装

        /// <summary>下载并安装；成功后请求宿主退出，由引导脚本装完自动重启。</summary>
        public async Task DownloadAndInstallAsync()
        {
            if (!CanInstall) return;
            if (!_assetName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "远端只提供了 zip 包，请到发布页手动下载安装";
                return;
            }

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PLCTesting", "Updates");
            Directory.CreateDirectory(dir);
            var msiPath = Path.Combine(dir, $"PLCTestingSetup-{Short(_latestVersion!)}.msi");

            IsBusy = true;
            IsDownloading = true;
            DownloadProgress = 0;
            try
            {
                // 候选下载地址，**API 端点优先**：
                //   browser_download_url 指向 github.com，很多现场网络（代理/防火墙）完全不可达，
                //   直连会 10 秒超时后 HTTP=000，表现为「更新下载失败」。
                //   assets[].url + Accept: application/octet-stream 走 api.github.com，
                //   由 GitHub 302 到 release-assets.githubusercontent.com，公开仓库无需认证。
                var candidates = new (string Url, bool IsApi)[]
                {
                    (_downloadApiUrl, true),
                    (_downloadUrl, false),
                }.Where(c => !string.IsNullOrEmpty(c.Url)).ToArray();

                if (candidates.Length == 0)
                {
                    StatusText = "远端没有提供可用的下载地址";
                    Log.Error(null, "下载中止：两个下载地址都为空");
                    return;
                }

                Exception lastError = null;
                var verified = false;

                foreach (var (url, isApi) in candidates)
                {
                    for (var attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            StatusText = attempt == 1
                                ? $"正在下载 {_assetName}…"
                                : $"正在重试下载 {_assetName}（第 {attempt} 次）…";

                            Log.Information("开始下载更新包 第{Attempt}次 IsApi={IsApi} Url={Url} → {Path}",
                                attempt, isApi, url, msiPath);

                            await DownloadToFileAsync(url, isApi, msiPath).ConfigureAwait(false);

                            // 校验：优先用远端 digest，退而核对大小
                            var sizeOk = _assetSize <= 0 || new FileInfo(msiPath).Length == _assetSize;
                            var digestOk = true;
                            if (_assetDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            {
                                var expect = _assetDigest.Substring(7).Trim().ToLowerInvariant();
                                var actual = await Sha256Async(msiPath).ConfigureAwait(false);
                                digestOk = string.Equals(expect, actual, StringComparison.OrdinalIgnoreCase);
                                Log.Information("更新包校验 SHA256 期望={Expect} 实际={Actual} 一致={Ok}",
                                    expect, actual, digestOk);
                            }

                            if (!sizeOk || !digestOk)
                            {
                                try { File.Delete(msiPath); } catch { }
                                StatusText = "更新包校验失败，已删除，正在换一个地址重试…";
                                Log.Error(null, "更新包校验失败 大小一致={SizeOk} 摘要一致={DigestOk}", sizeOk, digestOk);
                                continue;   // 换下一个地址/下一次重试
                            }

                            verified = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            Log.Warning(ex, "下载失败 第{Attempt}次 IsApi={IsApi} Url={Url}", attempt, isApi, url);
                            try { if (File.Exists(msiPath)) File.Delete(msiPath); } catch { }
                            if (attempt < 2)
                            {
                                StatusText = $"下载中断，正在重试（{ex.Message}）…";
                                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                            }
                        }
                    }

                    if (verified) break;

                    if (candidates.Length > 1)
                    {
                        Log.Information("该地址下载失败，改用下一个地址重试");
                        StatusText = "当前地址不可用，正在换备用地址…";
                    }
                }

                if (!verified)
                {
                    StatusText = "下载失败：" + (lastError?.Message ?? "所有下载地址均不可用");
                    Log.Error(lastError, "更新包下载最终失败（已尝试全部地址与重试）");
                    return;
                }

                StatusText = "下载完成，正在启动安装…";
                Log.Information("更新包下载完成 {Path}（{Size} 字节），准备安装", msiPath, new FileInfo(msiPath).Length);

                LaunchInstaller(msiPath);
            }
            finally
            {
                IsDownloading = false;
                IsBusy = false;
            }
        }

        /// <summary>
        /// 把 url 下载到 destPath。
        /// 超时策略：HttpClient.Timeout 已设为无限，这里用 <see cref="CancellationTokenSource"/>
        /// 给整个下载 20 分钟上限（70MB 在 3G 网络下也够）。
        /// isApi=true 时加 <c>Accept: application/octet-stream</c>，否则 API 端点只返回 JSON 元数据。
        /// </summary>
        private async Task DownloadToFileAsync(string url, bool isApi, string destPath)
        {
            using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (isApi) req.Headers.Accept.ParseAdd("application/octet-stream");
            req.Headers.UserAgent.ParseAdd($"PLCTesting-Updater/{Short(CurrentVersion)}");

            using var resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, overall.Token)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? _assetSize;
            if (total <= 0) total = _assetSize;

            await using var src = await resp.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false);
            await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), overall.Token)
                                   .ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), overall.Token).ConfigureAwait(false);
                done += read;
                if (total > 0) DownloadProgress = Math.Round(done * 100.0 / total, 1);
            }

            Log.Information("下载流结束 已写入={Done} 期望={Total} IsApi={IsApi}", done, total, isApi);
        }

        /// <summary>
        /// 写一个引导 .cmd：等本进程退出 → 静默装 MSI → 用原来的命令行参数重启程序。
        /// 必须等自己退出，否则安装器替换 exe/dll 时文件被占用会失败。
        /// </summary>
        private void LaunchInstaller(string msiPath)
        {
            var exePath = Process.GetCurrentProcess().MainModule!.FileName;
            var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1));
            var pid = Environment.ProcessId;
            var dir = Path.GetDirectoryName(msiPath)!;
            var resultLog = Path.Combine(dir, "last-install.txt");

            // 整个脚本保持纯 ASCII：cmd 默认代码页下中文会乱码，且路径里有中文时更容易出问题
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 437 >nul");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("  goto wait");
            sb.AppendLine(")");
            sb.AppendLine($"msiexec /i \"{msiPath}\" /qn /norestart");
            sb.AppendLine($"echo msiexec exit code = %ERRORLEVEL% > \"{resultLog}\"");
            sb.AppendLine("ping -n 4 127.0.0.1 >nul");
            sb.AppendLine(string.IsNullOrEmpty(args)
                ? $"start \"\" \"{exePath}\""
                : $"start \"\" \"{exePath}\" {args}");
            sb.AppendLine("del \"%~f0\"");

            var helper = Path.Combine(Path.GetTempPath(), $"plctesting-update-{pid}.cmd");
            File.WriteAllText(helper, sb.ToString(), new ASCIIEncoding());

            var psi = new ProcessStartInfo(helper) { UseShellExecute = true };
            if (!IsElevated)
            {
                // 未提权：借 UAC 提权一次，之后脚本里的 msiexec 才是管理员身份
                psi.Verb = "runas";
                StatusText = "需要管理员权限安装，请在系统提示中点击「是」…";
                Log.Warning("更新安装需要提权（当前进程非管理员），将弹出 UAC");
            }

            try
            {
                Process.Start(psi);
                Log.Information("已启动更新引导脚本 {Helper}，等待本进程退出后安装", helper);
                ExitRequested?.Invoke();
            }
            catch (Exception ex)
            {
                StatusText = "启动安装失败：" + ex.Message;
                Log.Error(ex, "启动更新引导脚本失败 {Helper}", helper);
            }
        }

        private static async Task<string> Sha256Async(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            await using var fs = File.OpenRead(path);
            var hash = await sha.ComputeHashAsync(fs).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        #endregion

        #region 辅助

        /// <summary>
        /// 把 GitHub Release 的 Markdown 正文压成一句纯文本摘要：
        /// 去掉标题井号、强调星号、行内代码反引号与引用符号，换行折成空格，超长截断。
        /// </summary>
        internal static string PlainSummary(string markdown, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";

            var sb = new StringBuilder(markdown.Length);
            foreach (var ch in markdown)
            {
                // # * ` > | 这些标记在摘要里没有意义；- 只保留作连字符
                if (ch is '#' or '*' or '`' or '>' or '|') continue;
                sb.Append(ch == '\r' ? ' ' : ch);
            }

            var flat = sb.ToString()
                .Replace("\n", " ")
                .Replace("  ", " ")
                .Trim();

            if (flat.Length <= maxLen) return flat;

            // 尽量在句子边界截断，避免半句话
            var cut = flat.LastIndexOf('。', Math.Min(maxLen - 1, flat.Length - 1));
            if (cut < maxLen / 3) cut = maxLen - 1;
            return flat.Substring(0, cut + 1).TrimEnd() + "…";
        }

        /// <summary>只保留 主.次.修订 三段，避免 1.1.6 与 1.1.6.0 比较时被判为不同版本。</summary>
        internal static string Short(Version v) =>            v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

        internal static Version Normalize(Version v) =>
            new(v.Major, v.Minor, v.Build > 0 ? v.Build : 0);

        #endregion
    }
}
