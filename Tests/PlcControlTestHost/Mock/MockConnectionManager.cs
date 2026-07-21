using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;

namespace PlcControlTestHost.Mock
{
    /// <summary>
    /// 用于离线测试控件库的仿真连接管理器。
    /// 无需真实 PLC：连接成功后以定时器驱动已注册标签值随机游走/翻转，
    /// 并通过 TagValueChanged 事件实时推送，使所有只读/趋势/报警类控件持续刷新。
    /// 支持 WriteTagAsync 回写并“锁定”该标签，使其不再被仿真覆盖，便于验证写入类控件。
    /// </summary>
    public class MockConnectionManager : IConnectionManager
    {
        private readonly Dictionary<string, PLCTag> _tags = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _lockedTags = new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _simTimer;
        private readonly Random _rng = new();
        private bool _isConnected;
        private ConnectionState _state = ConnectionState.Disconnected;
        private int _refCount;

        public MockConnectionManager()
        {
            _simTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _simTimer.Tick += SimTimer_Tick;
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;
        private void RaisePropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion

        #region 基础信息

        public string Name => "仿真PLC(Mock)";
        public PLCConfig ConnectionConfig { get; } = new PLCConfig
        {
            Name = "Mock",
            IP = "127.0.0.1",
            Type = PLCType.S7200Smart,
        };

        #endregion

        #region 连接状态

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected == value) return;
                _isConnected = value;
                _state = value ? ConnectionState.Connected : ConnectionState.Disconnected;
                RaisePropertyChanged(nameof(IsConnected));
                RaisePropertyChanged(nameof(ConnectionStatus));
                RaisePropertyChanged(nameof(ConnectionStatusText));
                RaisePropertyChanged(nameof(ConnectionStatusColor));
            }
        }

        public ConnectionState ConnectionStatus => _state;
        public bool IsConnecting => false;
        public string ConnectionStatusText => _isConnected ? "已连接（仿真）" : "未连接";
        public string ConnectionStatusColor => _isConnected ? "#67C23A" : "#F56C6C";
        public string StatusIcon => _isConnected ? "🟢" : "⚪";

        #endregion

        #region 引用计数

        public void AddReference() => _refCount++;
        public void RemoveReference()
        {
            if (_refCount > 0) _refCount--;
        }
        public int ReferenceCount => _refCount;

        #endregion

        #region 连接操作

        public Task<bool> ConnectAsync()
        {
            IsConnected = true;
            if (!_simTimer.IsEnabled) _simTimer.Start();
            LogMessage?.Invoke(this, "仿真连接已建立");
            return Task.FromResult(true);
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            _simTimer.Stop();
            LogMessage?.Invoke(this, "仿真连接已断开");
            return Task.CompletedTask;
        }

        #endregion

        #region 标签管理

        public void AddTag(PLCTag tag)
        {
            if (tag == null) return;
            lock (_tags)
            {
                if (!_tags.ContainsKey(tag.Name))
                    _tags[tag.Name] = tag;
            }
        }

        public PLCTag GetTag(string name)
        {
            lock (_tags)
                return _tags.TryGetValue(name, out var t) ? t : null!;
        }

        public PLCTag[] GetAllTags()
        {
            lock (_tags)
                return _tags.Values.ToArray();
        }

        public List<PLCTag> GetAllTagsSnapshot()
        {
            lock (_tags)
                return _tags.Values.ToList();
        }

        public bool ContainsTag(string tagName)
        {
            lock (_tags)
                return _tags.ContainsKey(tagName);
        }

        public int TagCount
        {
            get { lock (_tags) return _tags.Count; }
        }

        public bool HasTags => TagCount > 0;

        #endregion

        #region 标签读写

        public Task<object> ReadTagAsync(string tagName)
        {
            var tag = GetTag(tagName);
            return Task.FromResult(tag?.Value ?? (object)null!);
        }

        public Task<bool> WriteTagAsync(string tagName, object value)
        {
            if (!_isConnected) return Task.FromResult(false);
            var tag = GetTag(tagName);
            if (tag == null) return Task.FromResult(false);

            var old = tag.Value;
            tag.Value = value;
            lock (_lockedTags) _lockedTags.Add(tagName); // 写入后锁定，避免被仿真覆盖
            TagValueChanged?.Invoke(this, new TagService.TagChangedEventArgs(tagName, value, old));
            LogMessage?.Invoke(this, $"写入 {tagName} = {value}");
            return Task.FromResult(true);
        }

        public Task<bool> ReadMultipleTagsAsync(params string[] tagNames)
        {
            foreach (var n in tagNames) _ = ReadTagAsync(n);
            return Task.FromResult(true);
        }

        #endregion

        #region 监控

        public void StartMonitoring(int interval = 500)
        {
            if (interval > 0) _simTimer.Interval = TimeSpan.FromMilliseconds(interval);
            if (!_simTimer.IsEnabled) _simTimer.Start();
        }

        public void StopMonitoring() => _simTimer.Stop();

        public event EventHandler<TagService.TagChangedEventArgs>? TagValueChanged;

        #endregion

        #region 仿真

        /// <summary>暂停/恢复仿真对全部标签的自动驱动。</summary>
        public bool IsSimulating => _simTimer.IsEnabled;

        public void PauseSimulation() => _simTimer.Stop();
        public void ResumeSimulation() { if (_isConnected) _simTimer.Start(); }

        /// <summary>解除所有被写入锁定的标签，使其重新参与仿真。</summary>
        public void UnlockAll()
        {
            lock (_lockedTags) _lockedTags.Clear();
        }

        /// <summary>手动注入一个标签值（绕过锁定）。</summary>
        public void InjectValue(string tagName, object value)
        {
            var tag = GetTag(tagName);
            if (tag == null) return;
            var old = tag.Value;
            tag.Value = value;
            TagValueChanged?.Invoke(this, new TagService.TagChangedEventArgs(tagName, value, old));
        }

        private void SimTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isConnected) return;
            List<PLCTag> snapshot;
            lock (_tags) snapshot = _tags.Values.ToList();

            foreach (var tag in snapshot)
            {
                lock (_lockedTags)
                    if (_lockedTags.Contains(tag.Name)) continue;

                var old = tag.Value;
                var next = SimulateNext(tag, old);
                if (Equals(next, old)) continue;
                tag.Value = next;
                TagValueChanged?.Invoke(this, new TagService.TagChangedEventArgs(tag.Name, next, old));
            }
        }

        private object SimulateNext(PLCTag tag, object current)
        {
            var t = tag.DataType;
            try
            {
                if (t == typeof(bool))
                    return _rng.NextDouble() < 0.15 || (bool)Convert.ChangeType(current, typeof(bool));

                if (t == typeof(string))
                {
                    string[] samples = { "运行正常", "待机", "维护中", "批次A-1024", "批次B-2048", "OK" };
                    return samples[_rng.Next(samples.Length)];
                }

                double cur = current == null ? 0 : Convert.ToDouble(current);
                double step = (t == typeof(float) || t == typeof(double)) ? 3.0 : 2.0;
                double next = cur + (_rng.NextDouble() * 2 - 1) * step;
                // 随机游走并做软边界反弹
                if (next > 100) next = 100 - (_rng.NextDouble() * 10);
                if (next < 0) next = _rng.NextDouble() * 10;
                // 保留小数位以匹配类型
                if (t == typeof(float)) return (float)Math.Round(next, 2);
                if (t == typeof(double)) return Math.Round(next, 3);
                if (t == typeof(int)) return (int)Math.Round(next);
                if (t == typeof(short)) return (short)Math.Round(next);
                if (t == typeof(ushort)) return (ushort)Math.Max(0, Math.Round(next));
                if (t == typeof(byte)) return (byte)Math.Max(0, Math.Min(255, Math.Round(next)));
                if (t == typeof(uint)) return (uint)Math.Max(0, Math.Round(next));
                if (t == typeof(long)) return (long)Math.Round(next);
                if (t == typeof(ulong)) return (ulong)Math.Max(0, Math.Round(next));
                return next;
            }
            catch
            {
                return current;
            }
        }

        #endregion

        #region 日志 / 释放

        public event EventHandler<string>? LogMessage;

        public void Dispose()
        {
            _simTimer.Stop();
            _tags.Clear();
            _lockedTags.Clear();
        }

        #endregion
    }
}
