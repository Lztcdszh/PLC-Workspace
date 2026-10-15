using PLCTesting.Models;
using Prism.Commands;
using System;
using Prism.Mvvm;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace PLCTesting.ViewModels
{
    /// <summary>
    /// 数据点编辑器 ViewModel：读取 / 编辑 / 保存 dataPoints.json，
    /// 让"加任何数据"无需手改 JSON 文件。
    /// </summary>
    public class DataPointEditorViewModel : BindableBase
    {
        private static readonly string ConfigPath =
            Path.Combine(AppContext.BaseDirectory, "dataPoints.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        /// <summary>
        /// 可选的数据类型（与 DataPointRegistry.CreateTag 的 switch 对齐）。
        /// 用 ObservableCollection 而不是只读数组：Load() 会把配置文件里出现的非标准写法
        /// （如 "short" / "byte" / "uint16"，CreateTag 也认）补进来。
        /// 否则 ComboBox 的 SelectedItem 因值不在 Items 里而被 WPF 置成 null，
        /// 再经 TwoWay 写回就把用户原本正确的类型抹掉了。
        /// </summary>
        public ObservableCollection<string> DataTypeOptions { get; } = new()
        {
            "Bool", "Float", "Int", "Int16", "Word", "UInt8",
            "DWord", "Double", "String"
        };

        public ObservableCollection<DataPointConfig> Points { get; } = new();

        private DataPointConfig? _selected;
        public DataPointConfig? Selected
        {
            get => _selected;
            set => SetProperty(ref _selected, value);
        }

        public DelegateCommand AddCommand { get; }
        public DelegateCommand RemoveCommand { get; }
        public DelegateCommand SaveCommand { get; }
        public DelegateCommand CancelCommand { get; }

        public DataPointEditorViewModel()
        {
            AddCommand = new DelegateCommand(ExecuteAdd);
            RemoveCommand = new DelegateCommand(ExecuteRemove, () => Selected != null)
                .ObservesProperty(() => Selected);
            SaveCommand = new DelegateCommand(ExecuteSave);
            CancelCommand = new DelegateCommand(() => RequestClose?.Invoke(false));

            Load();
        }

        /// <summary>请求关闭窗口；参数为 DialogResult。</summary>
        public event Action<bool>? RequestClose;

        /// <summary>
        /// 保存成功（文件已落盘）后触发。宿主据此热重载数据点注册表，
        /// 免去"改完必须重启才生效"的问题。
        /// </summary>
        public event Action? Saved;

        private void Load()
        {
            Points.Clear();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var list = JsonSerializer.Deserialize<List<DataPointConfig>>(
                        File.ReadAllText(ConfigPath));
                    if (list != null)
                    {
                        foreach (var p in list.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                            AddPoint(p);
                        return;
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"读取 dataPoints.json 失败：{ex.Message}", "数据点编辑器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 无配置时给一个空行，方便直接上手
            AddPoint(new DataPointConfig());
        }

        private void AddPoint(DataPointConfig p)
        {
            Points.Add(p);
            EnsureTypeOption(p.DataType);
        }

        /// <summary>把非标准但 CreateTag 仍认的类型写法补进下拉选项，避免选中值被置空。</summary>
        private void EnsureTypeOption(string dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return;
            var t = dataType.Trim();
            if (!DataTypeOptions.Any(o => string.Equals(o, t, StringComparison.OrdinalIgnoreCase)))
                DataTypeOptions.Add(t);
        }

        private void ExecuteAdd()
        {
            var p = new DataPointConfig { Name = "新数据点", DataType = "Float" };
            AddPoint(p);
            Selected = p;
        }

        private void ExecuteRemove()
        {
            if (Selected != null)
                Points.Remove(Selected);
        }

        private void ExecuteSave()
        {
            // 简单校验：Name 不能为空
            var blanks = Points.Where(p => string.IsNullOrWhiteSpace(p.Name)).ToList();
            if (blanks.Count > 0)
            {
                MessageBox.Show("存在名称为空的数据点，请先填写名称。", "数据点编辑器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(Points.ToList(), JsonOptions);
                File.WriteAllText(ConfigPath, json);

                // 先通知宿主热重载，再关窗（关窗后 VM 可能被回收）
                Saved?.Invoke();
                RequestClose?.Invoke(true);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"保存失败：{ex.Message}", "数据点编辑器",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
