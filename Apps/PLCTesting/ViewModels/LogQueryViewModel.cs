using PLCTesting.Models;
using PLCTesting.Services;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace PLCTesting.ViewModels
{
    /// <summary>
    /// 历史变化查询页面视图模型。
    ///
    /// 相比初版补齐的能力：
    ///   - 地址下拉**动态**从库里统计（原来硬编码 3 个地址，新增数据点看不到，会被误判成"没数据"）
    ///   - 关键字搜索（标签名 / 地址 / 旧值 / 新值 / 描述）
    ///   - 快捷时间范围（最近 1 小时 / 今天 / 最近 7 天 / 全部）
    ///   - 起止时间精确到分钟（DatePicker 只能到日，查"今天下午"就不够用了）
    ///   - 「只看真正变化」开关（库里有 0→0 这类噪音记录）
    ///   - 排序可切换升降序
    ///   - 每页条数可选、首页/末页跳转、总页数显示
    ///   - 导出当前筛选结果为 CSV（UTF-8 BOM，Excel 打开不乱码）
    ///   - 查询异步执行，数据量大时不卡界面
    /// </summary>
    public class LogQueryViewModel : BindableBase
    {
        private readonly ChangeLogRepository _repository;
        private bool _suppressAutoQuery;

        public ObservableCollection<ChangeLogRecord> Records { get; } = new ObservableCollection<ChangeLogRecord>();
        public ObservableCollection<AddressStat> AddressOptions { get; } = new ObservableCollection<AddressStat>();

        public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToArray();
        public IReadOnlyList<int> Minutes { get; } = Enumerable.Range(0, 60).ToArray();
        public IReadOnlyList<int> PageSizeOptions { get; } = new[] { 50, 100, 200, 500, 1000 };

        public LogQueryViewModel(ChangeLogRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));

            QueryCommand = new DelegateCommand(async () => await QueryAsync(true));
            FirstPageCommand = new DelegateCommand(async () => await GoPageAsync(0), () => PageIndex > 0 && !IsBusy);
            PrevPageCommand = new DelegateCommand(async () => await GoPageAsync(PageIndex - 1), () => PageIndex > 0 && !IsBusy);
            NextPageCommand = new DelegateCommand(async () => await GoPageAsync(PageIndex + 1), () => PageIndex + 1 < TotalPages && !IsBusy);
            LastPageCommand = new DelegateCommand(async () => await GoPageAsync(TotalPages - 1), () => PageIndex + 1 < TotalPages && !IsBusy);
            QuickRangeCommand = new DelegateCommand<string>(async key => await ApplyQuickRangeAsync(key));
            ExportCommand = new DelegateCommand(async () => await ExportAsync(), () => !IsBusy && TotalCount > 0);
            ResetFiltersCommand = new DelegateCommand(async () => await ResetFiltersAsync(), () => !IsBusy);
            SeedDemoCommand = new DelegateCommand(ExecuteSeedDemo, () => !IsBusy);
            ClearAllCommand = new DelegateCommand(ExecuteClearAll, () => !IsBusy);

            // 默认：最近 24 小时、时间倒序、每页 100
            var now = DateTime.Now;
            _startDate = now.Date.AddDays(-1);
            _endDate = now.Date;
            _startHour = 0;
            _startMinute = 0;
            _endHour = 23;
            _endMinute = 59;

            _suppressAutoQuery = true;
            RefreshAddressOptions(null);
            _suppressAutoQuery = false;

            _ = QueryAsync(false);
        }

        #region 筛选条件

        private AddressStat _selectedAddressOption;
        /// <summary>地址下拉选中项；Address 为 null 表示"全部地址"。</summary>
        public AddressStat SelectedAddressOption
        {
            get => _selectedAddressOption;
            set => SetProperty(ref _selectedAddressOption, value);
        }

        private string _keyword = "";
        public string Keyword
        {
            get => _keyword;
            set => SetProperty(ref _keyword, value);
        }

        private DateTime? _startDate;
        public DateTime? StartDate
        {
            get => _startDate;
            set => SetProperty(ref _startDate, value);
        }

        private DateTime? _endDate;
        public DateTime? EndDate
        {
            get => _endDate;
            set => SetProperty(ref _endDate, value);
        }

        private int _startHour;
        public int StartHour
        {
            get => _startHour;
            set => SetProperty(ref _startHour, value);
        }

        private int _startMinute;
        public int StartMinute
        {
            get => _startMinute;
            set => SetProperty(ref _startMinute, value);
        }

        private int _endHour;
        public int EndHour
        {
            get => _endHour;
            set => SetProperty(ref _endHour, value);
        }

        private int _endMinute;
        public int EndMinute
        {
            get => _endMinute;
            set => SetProperty(ref _endMinute, value);
        }

        private bool _changedOnly = true;
        /// <summary>只看真正发生变化的记录（默认开，过滤掉 0→0 这类噪音）。</summary>
        public bool ChangedOnly
        {
            get => _changedOnly;
            set => SetProperty(ref _changedOnly, value);
        }

        private bool _sortAscending;
        /// <summary>false=最新在前（默认）。</summary>
        public bool SortAscending
        {
            get => _sortAscending;
            set => SetProperty(ref _sortAscending, value);
        }

        private int _selectedPageSize = 100;
        public int SelectedPageSize
        {
            get => _selectedPageSize;
            set => SetProperty(ref _selectedPageSize, value);
        }

        #endregion

        #region 状态

        private int _pageIndex;
        public int PageIndex
        {
            get => _pageIndex;
            private set
            {
                if (SetProperty(ref _pageIndex, value))
                {
                    RaisePropertyChanged(nameof(PageText));
                    RaisePageCommands();
                }
            }
        }

        private int _totalCount;
        public int TotalCount
        {
            get => _totalCount;
            private set
            {
                if (SetProperty(ref _totalCount, value))
                {
                    RaisePropertyChanged(nameof(TotalPages));
                    RaisePropertyChanged(nameof(PageText));
                    RaisePropertyChanged(nameof(IsEmpty));
                    RaisePropertyChanged(nameof(HasRecords));
                    ExportCommand?.RaiseCanExecuteChanged();
                    RaisePageCommands();
                }
            }
        }

        public int TotalPages => TotalCount <= 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)Math.Max(1, SelectedPageSize));

        public string PageText => $"第 {PageIndex + 1} / {TotalPages} 页";

        public bool IsEmpty => !IsBusy && TotalCount == 0;
        public bool HasRecords => TotalCount > 0;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RaisePropertyChanged(nameof(IsEmpty));
                    RaiseAllCommands();
                }
            }
        }

        private string _statusText = "就绪";
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        #endregion

        #region 命令

        public DelegateCommand QueryCommand { get; }
        public DelegateCommand FirstPageCommand { get; }
        public DelegateCommand PrevPageCommand { get; }
        public DelegateCommand NextPageCommand { get; }
        public DelegateCommand LastPageCommand { get; }
        public DelegateCommand<string> QuickRangeCommand { get; }
        public DelegateCommand ExportCommand { get; }
        public DelegateCommand ResetFiltersCommand { get; }
        public DelegateCommand SeedDemoCommand { get; }
        public DelegateCommand ClearAllCommand { get; }

        #endregion

        #region 查询实现

        /// <summary>用当前界面条件组装查询对象。</summary>
        private ChangeLogQuery BuildQuery(int pageIndex = 0)
        {
            DateTime? from = null, to = null;
            if (StartDate.HasValue)
                from = StartDate.Value.Date.AddHours(Clamp(StartHour, 0, 23)).AddMinutes(Clamp(StartMinute, 0, 59));
            if (EndDate.HasValue)
                to = EndDate.Value.Date.AddHours(Clamp(EndHour, 0, 23)).AddMinutes(Clamp(EndMinute, 0, 59)).AddSeconds(59);

            return new ChangeLogQuery
            {
                Address = SelectedAddressOption?.Address,
                Keyword = Keyword,
                From = from,
                To = to,
                ChangedOnly = ChangedOnly,
                Ascending = SortAscending,
                PageSize = SelectedPageSize,
                PageIndex = pageIndex,
            };
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private async Task QueryAsync(bool resetPage)
        {
            if (IsBusy) return;

            IsBusy = true;
            try
            {
                var pageIndex = resetPage ? 0 : PageIndex;
                var query = BuildQuery(pageIndex);

                StatusText = "查询中…";
                var page = await Task.Run(() => _repository.QueryPage(query)).ConfigureAwait(true);

                // 切页后总数可能变小（期间有新数据/清空），把页码收敛回合法范围
                if (page.Total > 0 && pageIndex > 0 && page.Items.Count == 0)
                {
                    PageIndex = page.Total <= 1 ? 0 : (int)Math.Ceiling(page.Total / (double)Math.Max(1, SelectedPageSize)) - 1;
                    var retry = BuildQuery(PageIndex);
                    page = await Task.Run(() => _repository.QueryPage(retry)).ConfigureAwait(true);
                }

                Records.Clear();
                foreach (var r in page.Items) Records.Add(r);

                PageIndex = pageIndex;
                TotalCount = page.Total;
                PageIndex = Math.Min(PageIndex, Math.Max(0, TotalPages - 1));

                var filterDesc = DescribeFilters();
                StatusText = page.Total == 0
                    ? $"没有符合条件的记录（{filterDesc}）"
                    : $"共 {page.Total} 条 · 第 {PageIndex + 1}/{TotalPages} 页 · 每页 {SelectedPageSize} 条（{filterDesc}）";
            }
            catch (Exception ex)
            {
                StatusText = "查询失败：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task GoPageAsync(int pageIndex)
        {
            var target = Math.Max(0, Math.Min(pageIndex, TotalPages - 1));
            if (target == PageIndex) return;
            PageIndex = target;
            await QueryAsync(false);
        }

        /// <summary>把当前条件拼成一句人类可读的描述，显示在状态栏。</summary>
        private string DescribeFilters()
        {
            var parts = new List<string>();

            var addr = SelectedAddressOption?.Address;
            if (!string.IsNullOrWhiteSpace(addr))
                parts.Add($"地址 {addr}");
            else
                parts.Add("全部地址");

            if (!string.IsNullOrWhiteSpace(Keyword))
                parts.Add($"含「{Keyword.Trim()}」");

            if (StartDate.HasValue || EndDate.HasValue)
            {
                var from = StartDate.HasValue
                    ? StartDate.Value.Date.AddHours(Clamp(StartHour, 0, 23)).AddMinutes(Clamp(StartMinute, 0, 59))
                    : (DateTime?)null;
                var to = EndDate.HasValue
                    ? EndDate.Value.Date.AddHours(Clamp(EndHour, 0, 23)).AddMinutes(Clamp(EndMinute, 0, 59))
                    : (DateTime?)null;
                if (from.HasValue && to.HasValue) parts.Add($"{from:MM-dd HH:mm} ~ {to:MM-dd HH:mm}");
                else if (from.HasValue) parts.Add($"{from:MM-dd HH:mm} 起");
                else if (to.HasValue) parts.Add($"截至 {to:MM-dd HH:mm}");
            }

            if (ChangedOnly) parts.Add("只看变化");
            parts.Add(SortAscending ? "时间正序" : "时间倒序");

            return string.Join(" · ", parts);
        }

        #endregion

        #region 地址下拉

        /// <summary>按库里实际出现的地址重建下拉项，尽量保持原来选中的地址。</summary>
        private void RefreshAddressOptions(string keepAddress)
        {
            try
            {
                var stats = _repository.GetAddressStats();
                var total = stats.Sum(s => s.Count);

                var rebuilt = new List<AddressStat> { AddressStat.All(total) };
                rebuilt.AddRange(stats);

                AddressOptions.Clear();
                foreach (var s in rebuilt) AddressOptions.Add(s);

                var match = rebuilt.FirstOrDefault(o =>
                                string.Equals(o.Address, keepAddress, StringComparison.OrdinalIgnoreCase))
                            ?? rebuilt[0];

                // 直接改 backing field：走 setter 会让 ComboBox 在 Clear 过程中反复回写 null
                _selectedAddressOption = match;
                RaisePropertyChanged(nameof(SelectedAddressOption));
            }
            catch (Exception ex)
            {
                StatusText = "读取地址列表失败：" + ex.Message;
            }
        }

        private async Task RefreshAddressOptionsPreservingSelectionAsync()
        {
            var keep = SelectedAddressOption?.Address;
            var stats = await Task.Run(() => _repository.GetAddressStats()).ConfigureAwait(true);
            var total = stats.Sum(s => s.Count);

            var rebuilt = new List<AddressStat> { AddressStat.All(total) };
            rebuilt.AddRange(stats);

            AddressOptions.Clear();
            foreach (var s in rebuilt) AddressOptions.Add(s);

            var match = rebuilt.FirstOrDefault(o =>
                            string.Equals(o.Address, keep, StringComparison.OrdinalIgnoreCase))
                        ?? rebuilt[0];

            _selectedAddressOption = match;
            RaisePropertyChanged(nameof(SelectedAddressOption));
        }

        #endregion

        #region 快捷操作

        private async Task ApplyQuickRangeAsync(string key)
        {
            var now = DateTime.Now;
            switch (key)
            {
                case "1h":
                    StartDate = now.AddHours(-1).Date; StartHour = now.AddHours(-1).Hour; StartMinute = now.AddHours(-1).Minute;
                    EndDate = now.Date; EndHour = now.Hour; EndMinute = now.Minute;
                    break;
                case "today":
                    StartDate = now.Date; StartHour = 0; StartMinute = 0;
                    EndDate = now.Date; EndHour = 23; EndMinute = 59;
                    break;
                case "7d":
                    StartDate = now.Date.AddDays(-6); StartHour = 0; StartMinute = 0;
                    EndDate = now.Date; EndHour = 23; EndMinute = 59;
                    break;
                case "30d":
                    StartDate = now.Date.AddDays(-29); StartHour = 0; StartMinute = 0;
                    EndDate = now.Date; EndHour = 23; EndMinute = 59;
                    break;
                case "all":
                    StartDate = null; EndDate = null;
                    break;
                default:
                    return;
            }
            await QueryAsync(true);
        }

        private async Task ResetFiltersAsync()
        {
            _suppressAutoQuery = true;
            try
            {
                Keyword = "";
                ChangedOnly = true;
                SortAscending = false;
                SelectedPageSize = 100;

                var now = DateTime.Now;
                StartDate = now.Date.AddDays(-1);
                StartHour = 0; StartMinute = 0;
                EndDate = now.Date;
                EndHour = 23; EndMinute = 59;
            }
            finally { _suppressAutoQuery = false; }

            await RefreshAddressOptionsPreservingSelectionAsync();
            await QueryAsync(true);
        }

        private async Task ExportAsync()
        {
            if (IsBusy) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出查询结果",
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = $"PLC变化日志_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            };
            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            try
            {
                StatusText = "正在导出…";
                var query = BuildQuery(0);
                query.PageSize = int.MaxValue;
                query.PageIndex = 0;

                var rows = await Task.Run(() => _repository.QueryAll(query)).ConfigureAwait(true);
                var path = dialog.FileName;

                await Task.Run(() => WriteCsv(path, rows)).ConfigureAwait(true);

                StatusText = $"已导出 {rows.Count} 条到 {System.IO.Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                StatusText = "导出失败：" + ex.Message;
                MessageBox.Show("导出失败：" + ex.Message, "导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>写 CSV；UTF-8 带 BOM，Excel 直接打开中文不乱码。</summary>
        private static void WriteCsv(string path, IEnumerable<ChangeLogRecord> rows)
        {
            static string Q(string s)
            {
                s ??= "";
                return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                    ? "\"" + s.Replace("\"", "\"\"") + "\""
                    : s;
            }

            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.WriteLine("时间,PLC地址,标签名,数据类型,旧值,新值,描述");
            foreach (var r in rows)
            {
                writer.WriteLine(string.Join(",",
                    Q(r.TimeStamp.ToString("yyyy-MM-dd HH:mm:ss.fff")),
                    Q(r.TagAddress),
                    Q(r.TagName),
                    Q(r.DataType),
                    Q(r.OldValue),
                    Q(r.NewValue),
                    Q(r.Description)));
            }
        }

        private void ExecuteSeedDemo()
        {
            try
            {
                _repository.SeedDemoData();
                RefreshAddressOptions(SelectedAddressOption?.Address);
                _ = QueryAsync(true);
                StatusText = "已生成演示数据。";
            }
            catch (Exception ex)
            {
                StatusText = "生成演示数据失败：" + ex.Message;
            }
        }

        private void ExecuteClearAll()
        {
            if (MessageBox.Show("确定要清空全部日志吗？此操作不可恢复。", "清空确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                _repository.DeleteAll();
                RefreshAddressOptions(null);
                _ = QueryAsync(true);
            }
            catch (Exception ex)
            {
                StatusText = "清空失败：" + ex.Message;
            }
        }

        #endregion

        #region 命令可用性

        private void RaisePageCommands()
        {
            FirstPageCommand?.RaiseCanExecuteChanged();
            PrevPageCommand?.RaiseCanExecuteChanged();
            NextPageCommand?.RaiseCanExecuteChanged();
            LastPageCommand?.RaiseCanExecuteChanged();
        }

        private void RaiseAllCommands()
        {
            QueryCommand?.RaiseCanExecuteChanged();
            ResetFiltersCommand?.RaiseCanExecuteChanged();
            ExportCommand?.RaiseCanExecuteChanged();
            SeedDemoCommand?.RaiseCanExecuteChanged();
            ClearAllCommand?.RaiseCanExecuteChanged();
            RaisePageCommands();
        }

        #endregion
    }
}
