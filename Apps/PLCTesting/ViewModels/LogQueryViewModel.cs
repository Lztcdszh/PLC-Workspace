using PLCTesting.Models;
using PLCTesting.Services;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;

namespace PLCTesting.ViewModels
{
    /// <summary>
    /// 历史变化查询页面视图模型。
    /// </summary>
    public class LogQueryViewModel : BindableBase
    {
        private readonly ChangeLogRepository _repository;

        public ObservableCollection<ChangeLogRecord> Records { get; } = new ObservableCollection<ChangeLogRecord>();
        public List<string> Addresses { get; } = new List<string> { "全部", "DB1.DBW230", "DB1.DBB172" };

        private string _selectedAddress = "全部";
        public string SelectedAddress
        {
            get => _selectedAddress;
            set => SetProperty(ref _selectedAddress, value);
        }

        private DateTime? _startTime;
        public DateTime? StartTime
        {
            get => _startTime;
            set => SetProperty(ref _startTime, value);
        }

        private DateTime? _endTime;
        public DateTime? EndTime
        {
            get => _endTime;
            set => SetProperty(ref _endTime, value);
        }

        private int _pageIndex;
        public int PageIndex
        {
            get => _pageIndex;
            set => SetProperty(ref _pageIndex, value);
        }

        private int _pageSize = 100;
        public int PageSize
        {
            get => _pageSize;
            set => SetProperty(ref _pageSize, value);
        }

        private int _totalCount;
        public int TotalCount
        {
            get => _totalCount;
            set => SetProperty(ref _totalCount, value);
        }

        private string _statusText = "就绪";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public DelegateCommand QueryCommand { get; }
        public DelegateCommand NextPageCommand { get; }
        public DelegateCommand PrevPageCommand { get; }
        public DelegateCommand SeedDemoCommand { get; }
        public DelegateCommand ClearAllCommand { get; }

        public LogQueryViewModel(ChangeLogRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));

            QueryCommand = new DelegateCommand(ExecuteQuery);
            NextPageCommand = new DelegateCommand(ExecuteNextPage, () => (PageIndex + 1) * PageSize < TotalCount);
            PrevPageCommand = new DelegateCommand(ExecutePrevPage, () => PageIndex > 0);
            SeedDemoCommand = new DelegateCommand(ExecuteSeedDemo);
            ClearAllCommand = new DelegateCommand(ExecuteClearAll);

            // 默认查询最近 24 小时
            EndTime = DateTime.Now;
            StartTime = EndTime.Value.AddDays(-1);
            ExecuteQuery();
        }

        private void ExecuteQuery()
        {
            try
            {
                string address = SelectedAddress == "全部" ? null : SelectedAddress;
                var from = StartTime;
                var to = EndTime;
                TotalCount = _repository.Count(address, from, to);
                var list = _repository.Query(address, from, to, PageSize, PageIndex);
                Records.Clear();
                foreach (var r in list) Records.Add(r);
                StatusText = $"共 {TotalCount} 条，第 {PageIndex + 1} 页，每页 {PageSize} 条";
            }
            catch (Exception ex)
            {
                StatusText = $"查询失败: {ex.Message}";
            }
            finally
            {
                NextPageCommand.RaiseCanExecuteChanged();
                PrevPageCommand.RaiseCanExecuteChanged();
            }
        }

        private void ExecuteNextPage()
        {
            PageIndex++;
            ExecuteQuery();
        }

        private void ExecutePrevPage()
        {
            if (PageIndex > 0) PageIndex--;
            ExecuteQuery();
        }

        private void ExecuteSeedDemo()
        {
            try
            {
                _repository.SeedDemoData();
                PageIndex = 0;
                ExecuteQuery();
                StatusText = "已生成演示数据。";
            }
            catch (Exception ex)
            {
                StatusText = $"生成演示数据失败: {ex.Message}";
            }
        }

        private void ExecuteClearAll()
        {
            if (MessageBox.Show("确定要清空全部日志吗？此操作不可恢复。", "清空确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _repository.DeleteAll();
                PageIndex = 0;
                ExecuteQuery();
            }
        }
    }
}
