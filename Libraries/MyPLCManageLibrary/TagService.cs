using MyPLCFoundationLibrary.Logging;
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

        /// <summary>
        /// 监控循环的日志出口。用 <see cref="StructuredLogger"/> 而不是 Debug.WriteLine：
        /// 后者带 [Conditional("DEBUG")]，Release 构建里被整段编译掉 —— 现场"监控悄悄死掉、
        /// 界面数值冻结"时一条线索都留不下（这是本项目踩过的坑）。
        /// </summary>
        private static IAppLogger Log => StructuredLogger.Current.ForContext("TagService");

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

        #region 事件派发与 UI 编组（健壮性）

        /// <summary>
        /// 逐个订阅者分发标签变化事件，**单个订阅者抛异常不牵连其它订阅者，也不牵连监控循环**。
        /// </summary>
        /// <remarks>
        /// 历史缺陷：原来写的是 <c>TagValueChanged?.Invoke(...)</c>——多播委托在中途抛异常时，
        /// 异常会沿调用栈直接打到监控循环，把整个标签监控打死（且 Release 下连日志都没有）。
        /// 这里改为手工遍历调用列表、逐个 try/catch，抛异常者只记日志，其余照常收到值。
        /// </remarks>
        private void RaiseTagValueChangedSafely(string tagName, object? newValue, object? oldValue)
        {
            var handler = TagValueChanged;
            if (handler == null) return;

            var args = new TagChangedEventArgs(tagName, newValue, oldValue);
            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<TagChangedEventArgs>)d)(this, args);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "标签变化订阅者异常（已隔离，不影响其它订阅者与监控循环） Tag={Tag} 订阅者={Subscriber}",
                        tagName, d.Method.DeclaringType?.FullName + "." + d.Method.Name);
                }
            }
        }

        /// <summary>
        /// 在 UI 线程执行动作；无 WPF 宿主（如控制台探针/测试宿主）时直接就地执行，
        /// 避免 <see cref="Application.Current"/> 为 null 时抛 NullReferenceException 把读取吞掉。
        /// </summary>
        private static Task RunOnUiAsync(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            return dispatcher.InvokeAsync(action).Task;
        }

        #endregion

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
                    await RunOnUiAsync(() =>
                    {
                        var old = tag.Value;
                        tag.Value = value;
                        if (!Equals(old, value))
                            RaiseTagValueChangedSafely(tag.Name, value, old);
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
            if (!_plcService.IsConnected)
            {
                // 静默失败是现场最难查的一类问题：用户点按钮"没反应"时，
                // 日志里必须能直接看到"因为 PLC 未连接所以没写"。
                Log.Warning("写入被拒：PLC 未连接 Tag={Tag} Address={Address}", tag.Name, tag.Address);
                return false;
            }

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
                    RaiseTagValueChangedSafely(tag.Name, value, old);
                }
                else
                {
                    Log.Warning("写入未成功 Tag={Tag} Address={Address} Value={Value}", tag.Name, tag.Address, value);
                }
                return ok;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "写入异常 Tag={Tag} Address={Address}", tag?.Name, tag?.Address);
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
            // 除"已在运行"外，还要允许"上一轮循环已意外结束但 _isMonitoring 残留 true"时重新拉起，
            // 否则重连时 StartMonitoring 会直接 early-return，标签监控再也起不来。
            if (_isMonitoring && _monitorTask is { IsCompleted: false }) return;
            _isFirstRun = true; // 每次重启监控都重置首轮标志，确保重新读取所有标签
            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoopAsync(TimeSpan.FromMilliseconds(Math.Max(50, intervalMs)), _monitorCts.Token));
            _isMonitoring = true;
            Log.Information("开始标签监控，间隔 {Interval}ms", intervalMs);
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

        /// <summary>
        /// 标签监控循环。结构上把「每一拍」与「循环骨架」彻底分开：
        /// 循环骨架只在取消/致命异常时退出，而每一拍的任何异常都被就地吞掉并记日志。
        /// </summary>
        /// <remarks>
        /// ⚠️ 历史缺陷（现场实报"按钮全失效、导轨不闪烁"的同类高危面）：
        /// 原实现的 <c>try/catch</c> 包在 <c>while</c> **外面**，于是循环体内任意一次异常
        /// ——包括任意一个 <c>TagValueChanged</c> 订阅者抛出的异常——都会让监控线程直接退出且永不恢复；
        /// 而唯一的记录手段是 <c>Debug.WriteLine</c>，在 Release 构建里被整段编译掉，
        /// 连一行线索都不留。结果就是：界面数值永久冻结、所有依赖标签值的控件全部失效，
        /// 但进程照常运行、按钮照常可点，极难定位。
        /// </remarks>
        private async Task MonitorLoopAsync(TimeSpan interval, CancellationToken ct)
        {
            Log.Information("标签监控循环已启动，间隔 {Interval}ms", interval.TotalMilliseconds);
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        await MonitorTickAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 单拍失败只损失这一拍，下一拍继续；周期计时器不会因此停摆。
                        Log.Error(ex, "标签监控单拍异常（循环继续）");
                    }
                }
                Log.Information("标签监控循环正常结束");
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TagService] 监控循环被取消");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "标签监控循环异常退出");
            }
            finally
            {
                // 仅当「非取消」的自然退出（即异常逃逸）才重置运行标志，
                // 让后续重连触发的 StartMonitoring 能真正把监控重新拉起。
                if (!ct.IsCancellationRequested)
                {
                    _isMonitoring = false;
                    Log.Warning("标签监控循环意外退出，已重置运行标志（重连时将自动重新拉起）");
                }
            }
        }

        /// <summary>监控循环的「一拍」：取快照 → 并发读取 → 判定变化 → 回 UI 线程赋值并派发事件。</summary>
        private async Task MonitorTickAsync()
        {
            if (!_plcService.IsConnected) return;   // 未连接：静默跳过（不刷日志，避免每秒数条）

            List<PLCTag> monitoredTags;
            try
            {
                List<PLCTag>? snapshot = null;
                await RunOnUiAsync(() => { snapshot = Tags.Where(t => t.IsMonitored).ToList(); })
                    .ConfigureAwait(false);
                monitoredTags = snapshot ?? new List<PLCTag>();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "获取监控标签快照失败");
                return;
            }

            if (monitoredTags.Count == 0) return;

            // 并发读取所有监控标签
            var readTasks = monitoredTags.Select(tag => ReadTagValueAsync(tag));
            var results = await Task.WhenAll(readTasks).ConfigureAwait(false);

            var updates = new List<(PLCTag Tag, object? NewValue)>();
            int failed = 0;
            for (int i = 0; i < monitoredTags.Count; i++)
            {
                var tag = monitoredTags[i];
                var (value, success) = results[i];
                if (!success)
                {
                    failed++;
                    continue;
                }

                if (_isFirstRun || !Equals(tag.Value, value))
                    updates.Add((tag, value));
            }

            if (_isFirstRun)
            {
                _isFirstRun = false;
                Log.Information("首轮全量更新完成：监控 {Total} 个标签，播发 {Updated} 个，读取失败 {Failed} 个",
                    monitoredTags.Count, updates.Count, failed);
            }
            else if (failed > 0 && failed == monitoredTags.Count)
            {
                // 全部读失败：说明链路整体异常（连接假活/地址错位），必须留痕
                Log.Warning("本轮全部标签读取失败（共 {Total} 个），链路疑似异常", monitoredTags.Count);
            }

            if (updates.Count > 0)
            {
                await RunOnUiAsync(() =>
                {
                    foreach (var (tag, newValue) in updates)
                    {
                        var old = tag.Value;
                        tag.Value = newValue;              // 先落值：即便订阅者抛异常，缓存值也已是最新
                        RaiseTagValueChangedSafely(tag.Name, newValue, old);
                    }
                }).ConfigureAwait(false);
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