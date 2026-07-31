using MyPLCManagerLibrary.Model;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MyPLCManagerLibrary
{
    public class TagService : IDisposable
    {
        private readonly PLCService _plcService;
        private CancellationTokenSource? _monitorCts;
        private Task? _monitorTask;
        private bool _isMonitoring;
        private bool _disposed = false;
        private bool _isFirstRun = true;   // 首轮全量更新标志

        public ObservableCollection<PLCTag> Tags { get; } = new();

        public event EventHandler<TagChangedEventArgs>? TagValueChanged;

        public class TagChangedEventArgs : EventArgs
        {
            public string TagName { get; }
            public object? NewValue { get; }
            public object? OldValue { get; }
            public TagChangedEventArgs(string tagName, object? newValue, object? oldValue)
            {
                TagName = tagName; NewValue = newValue; OldValue = oldValue;
            }
        }

        public TagService(PLCService plcService)
        {
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            Debug.WriteLine("TagService 初始化");
        }

        public void AddTag(PLCTag tag)
        {
            if (tag == null) return;
            if (Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                Application.Current.Dispatcher.Invoke(() => AddTag(tag));
                return;
            }
            if (!Tags.Contains(tag))
            {
                Tags.Add(tag);
                Debug.WriteLine($"添加标签: {tag.Name} ({tag.Address})");
            }
        }

        public PLCTag? GetTag(string name)
        {
            if (Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                return Application.Current.Dispatcher.Invoke(() => Tags.FirstOrDefault(t => t.Name == name));
            }
            return Tags.FirstOrDefault(t => t.Name == name);
        }

        public async Task<bool> ReadTagAsync(PLCTag tag)
        {
            if (tag == null) return false;
            if (!_plcService.IsConnected) return false;

            try
            {
                var (value, success) = await ReadTagValueAsync(tag).ConfigureAwait(false);
                if (success && value != null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var old = tag.Value;
                        tag.Value = value;
                        if (!Equals(old, value))
                            TagValueChanged?.Invoke(this, new TagChangedEventArgs(tag.Name, value, old));
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadTagAsync 异常 - {tag?.Name}: {ex.Message}");
            }
            return false;
        }

        private async Task<(object? Value, bool Success)> ReadTagValueAsync(PLCTag tag)
        {
            if (tag == null) return (null, false);
            try
            {
                var dt = tag.DataType;
                object? value = dt switch
                {
                    Type t when t == typeof(bool) => await _plcService.ReadAsync<bool>(tag.Address).ConfigureAwait(false),
                    Type t when t == typeof(float) => await _plcService.ReadAsync<float>(tag.Address).ConfigureAwait(false),
                    Type t when t == typeof(int) => (int)(await _plcService.ReadAsync<uint>(tag.Address).ConfigureAwait(false)),
                    Type t when t == typeof(short) => (short)(await _plcService.ReadAsync<ushort>(tag.Address).ConfigureAwait(false)),
                    Type t when t == typeof(ushort) => await _plcService.ReadAsync<ushort>(tag.Address).ConfigureAwait(false),
                    Type t when t == typeof(byte) => await _plcService.ReadAsync<byte>(tag.Address).ConfigureAwait(false),
                    Type t when t == typeof(uint) => await _plcService.ReadAsync<uint>(tag.Address).ConfigureAwait(false),
                    Type t when t == typeof(double) => await _plcService.ReadAsync<double>(tag.Address).ConfigureAwait(false),
                    Type t when t == typeof(string) => await _plcService.ReadAsync<string>(tag.Address).ConfigureAwait(false),
                    _ => null
                };
                if (value != null) return (value, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadTagValueAsync 异常 - {tag.Name}: {ex.Message}");
            }
            return (null, false);
        }

        public async Task<bool> WriteTagAsync(PLCTag tag, object value)
        {
            if (tag == null) return false;
            if (!_plcService.IsConnected) return false;

            if (Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                return await Application.Current.Dispatcher.InvokeAsync(async () =>
                    await WriteTagAsync(tag, value)).Task.Unwrap();
            }

            try
            {
                // 关键修复：按「待写值自身的运行时类型」派发写入，而非依赖 tag.DataType。
                // 同一 TagName 被多个控件（如可写的 PlcNumericInput 与只读的 PlcValueDisplay）注册时，
                // 后注册者会因 ContainsTag 去重而沿用先注册者的类型（例如 float）。
                // 若仍按 tag.DataType 做 (float)value 这类拆箱强转，而 value 实为 short，
                // 会抛 InvalidCastException 导致写入失败。改为由调用方已解析好的值类型决定写入形态：
                // 既消除异常，也保证写入宽度（short→ushort 写 DBW 字地址）与控件意图一致。
                bool ok = value switch
                {
                    bool b => await _plcService.WriteAsync(tag.Address, b),
                    sbyte sb => await _plcService.WriteAsync(tag.Address, sb),
                    byte by => await _plcService.WriteAsync(tag.Address, by),
                    short s => await _plcService.WriteAsync(tag.Address, (ushort)s),
                    ushort us => await _plcService.WriteAsync(tag.Address, us),
                    int i => await _plcService.WriteAsync(tag.Address, i),
                    uint ui => await _plcService.WriteAsync(tag.Address, ui),
                    long l => await _plcService.WriteAsync(tag.Address, l),
                    ulong ul => await _plcService.WriteAsync(tag.Address, ul),
                    float f => await _plcService.WriteAsync(tag.Address, f),
                    double d => await _plcService.WriteAsync(tag.Address, d),
                    string str => await _plcService.WriteAsync(tag.Address, str),
                    _ => await WriteViaTagTypeFallbackAsync(tag, value),
                };

                if (ok)
                {
                    var old = tag.Value;
                    tag.Value = value;
                    TagValueChanged?.Invoke(this, new TagChangedEventArgs(tag.Name, value, old));
                }
                return ok;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WriteTagAsync 异常 - {tag?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 兜底：当待写值类型不在上述显式分支时（极少见），先按标签声明类型安全转换再写入，
        /// 避免直接拆箱强转在类型不一致时抛异常。
        /// </summary>
        private async Task<bool> WriteViaTagTypeFallbackAsync(PLCTag tag, object? value)
        {
            if (value == null) return false;
            try
            {
                var converted = Convert.ChangeType(value, tag.DataType, CultureInfo.InvariantCulture);
                return await (tag.DataType switch
                {
                    Type t when t == typeof(bool) => _plcService.WriteAsync(tag.Address, (bool)converted),
                    Type t when t == typeof(sbyte) => _plcService.WriteAsync(tag.Address, (sbyte)converted),
                    Type t when t == typeof(byte) => _plcService.WriteAsync(tag.Address, (byte)converted),
                    Type t when t == typeof(short) => _plcService.WriteAsync(tag.Address, (ushort)(short)converted),
                    Type t when t == typeof(ushort) => _plcService.WriteAsync(tag.Address, (ushort)converted),
                    Type t when t == typeof(int) => _plcService.WriteAsync(tag.Address, (int)converted),
                    Type t when t == typeof(uint) => _plcService.WriteAsync(tag.Address, (uint)converted),
                    Type t when t == typeof(long) => _plcService.WriteAsync(tag.Address, (long)converted),
                    Type t when t == typeof(ulong) => _plcService.WriteAsync(tag.Address, (ulong)converted),
                    Type t when t == typeof(float) => _plcService.WriteAsync(tag.Address, (float)converted),
                    Type t when t == typeof(double) => _plcService.WriteAsync(tag.Address, (double)converted),
                    Type t when t == typeof(string) => _plcService.WriteAsync(tag.Address, (string)converted),
                    _ => Task.FromResult(false),
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WriteViaTagTypeFallbackAsync 异常 - {tag?.Name}: {ex.Message}");
                return false;
            }
        }

        public void StartMonitoring(int intervalMs = 1000)
        {
            if (_isMonitoring) return;
            _isFirstRun = true; // 每次重启监控都重置首轮标志，确保重新读取所有标签
            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoopAsync(TimeSpan.FromMilliseconds(Math.Max(50, intervalMs)), _monitorCts.Token));
            _isMonitoring = true;
            Debug.WriteLine($"TagService: 开始监控，间隔 {intervalMs}ms");
        }

        public async Task StopMonitoringAsync(int waitMs = 1000)
        {
            if (!_isMonitoring) return;
            try
            {
                _monitorCts?.Cancel();
                if (_monitorTask != null)
                    await Task.WhenAny(_monitorTask, Task.Delay(waitMs)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"StopMonitoringAsync 异常: {ex.Message}");
            }
            finally
            {
                _monitorCts?.Dispose();
                _monitorCts = null;
                _monitorTask = null;
                _isMonitoring = false;
                Debug.WriteLine("TagService: 停止监控");
            }
        }

        public void StopMonitoring()
        {
            try { StopMonitoringAsync().Wait(1000); } catch { }
        }

        private async Task MonitorLoopAsync(TimeSpan interval, CancellationToken ct)
        {
            int loopCount = 0;
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    loopCount++;
                    if (ct.IsCancellationRequested) break;

                    if (!_plcService.IsConnected)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TagService] 等待 PLC 连接... 总数=0 更新=0");
                        continue;
                    }

                    List<PLCTag> monitoredTags;
                    try
                    {
                        monitoredTags = await Application.Current.Dispatcher.InvokeAsync(() =>
                            Tags.Where(t => t.IsMonitored).ToList());
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TagService] 获取监控标签快照失败: {ex.Message}");
                        continue;
                    }

                    if (monitoredTags.Count == 0)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TagService] 监控列表为空 总数=0 更新=0");
                        continue;
                    }

                    // 并发读取所有监控标签
                    var readTasks = monitoredTags.Select(tag => ReadTagValueAsync(tag));
                    var results = await Task.WhenAll(readTasks).ConfigureAwait(false);

                    var updates = new List<(PLCTag Tag, object? NewValue)>();
                    for (int i = 0; i < monitoredTags.Count; i++)
                    {
                        var tag = monitoredTags[i];
                        var (value, success) = results[i];
                        if (!success)
                        {
                            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TagService] 标签 {tag.Name} ({tag.Address}) 读取失败，跳过");
                            continue;
                        }

                        bool valueChanged = !Equals(tag.Value, value);
                        if (_isFirstRun)
                        {
                            updates.Add((tag, value));
                        }
                        else if (valueChanged)
                        {
                            updates.Add((tag, value));
                        }
                    }

                    if (_isFirstRun)
                    {
                        _isFirstRun = false;
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TagService] 首轮全量更新完成，共 {updates.Count} 个标签被更新");
                    }

                    if (updates.Count > 0)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            foreach (var (tag, newValue) in updates)
                            {
                                var old = tag.Value;
                                tag.Value = newValue;
                                TagValueChanged?.Invoke(this, new TagChangedEventArgs(tag.Name, newValue, old));
                            }
                        });
                    }

                    // 每个循环输出总数和更新个数（带时间戳）
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TagService] 循环结束: 总数={monitoredTags.Count}, 更新={updates.Count}");
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TagService] 监控循环被取消");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TagService] MonitorLoopAsync 异常: {ex.Message}\n{ex.StackTrace}");
            }
        }



        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try { StopMonitoringAsync().Wait(1000); } catch { }
                    Debug.WriteLine("释放TagService资源...");
                }
                _disposed = true;
            }
        }

        ~TagService() => Dispose(false);
    }
}