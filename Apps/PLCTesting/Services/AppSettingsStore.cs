using PLCTesting.Models;
using System;
using System.IO;
using System.Text.Json;

namespace PLCTesting.Services
{
    /// <summary>
    /// 程序级设置的读写（appsettings.json）。
    /// 与 MqttConfig 一样"文件缺失时用默认值"，首次保存时自建文件 ——
    /// 因为该文件被排除在 MSI 组件清单外，安装时并不存在。
    /// </summary>
    public class AppSettingsStore
    {
        public const string FileName = "appsettings.json";

        private readonly string _configPath;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            // 枚举写成字符串（"Prompt"/"Auto"），文件可读、也不会因枚举顺序变化而错位
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        private AppSettings _current = new();
        private readonly object _lock = new();

        /// <summary>进程内单例（生产代码用这个）。</summary>
        public static AppSettingsStore Default { get; } = new();

        /// <summary>默认构造：用程序目录下的 appsettings.json。</summary>
        private AppSettingsStore() : this(Path.Combine(AppContext.BaseDirectory, FileName))
        {
        }

        /// <param name="configPath">配置文件完整路径；传 null/空则用程序目录下的默认路径。</param>
        public AppSettingsStore(string configPath)
        {
            _configPath = string.IsNullOrWhiteSpace(configPath)
                ? Path.Combine(AppContext.BaseDirectory, FileName)
                : configPath;
            Load();
        }

        /// <summary>配置文件的完整路径。</summary>
        public string FilePath => _configPath;

        /// <summary>当前设置。</summary>
        public AppSettings Current
        {
            get { lock (_lock) return _current; }
            private set { lock (_lock) _current = value ?? new AppSettings(); }
        }

        /// <summary>从磁盘读取；文件不存在或损坏时保留默认值，绝不抛异常。</summary>
        public void Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var cfg = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (cfg != null)
                    {
                        Current = cfg;
                        return;
                    }
                }
                Current = new AppSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] 加载失败: {ex.Message}");
                Current = new AppSettings();
            }
        }

        /// <summary>把当前设置写回磁盘。失败时把异常抛给调用方提示。</summary>
        public void Save()
        {
            var snapshot = Current;
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_configPath, json);
        }

        /// <summary>用界面上的值更新内存（不落盘）。</summary>
        /// <remarks>
        /// ⚠️ 这里必须把 <see cref="AppSettings"/> 的**每一个字段**都搬过来。
        /// 早期只搬了 AutoStartOnBoot，新增字段会在"保存系统设置"时被静默清空
        /// （表现：刚设好的更新策略，点一次保存就变回默认值）。
        /// </remarks>
        public void Update(AppSettings settings)
        {
            if (settings == null) return;
            Current = new AppSettings
            {
                AutoStartOnBoot = settings.AutoStartOnBoot,
                UpdateMode = settings.UpdateMode,
                SkippedVersion = settings.SkippedVersion ?? ""
            };
        }
    }
}
