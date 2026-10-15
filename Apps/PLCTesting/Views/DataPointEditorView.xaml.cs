using PLCTesting.ViewModels;
using System;
using System.Windows;

namespace PLCTesting.Views
{
    /// <summary>
    /// 数据点编辑器窗口：可视化编辑 dataPoints.json。
    /// </summary>
    public partial class DataPointEditorView : Window
    {
        /// <param name="onSaved">
        /// 保存成功后的回调，宿主用它热重载数据点注册表。
        /// 允许为 null（此时仅落盘，不重载）。
        /// </param>
        public DataPointEditorView(Action? onSaved = null)
        {
            InitializeComponent();
            var vm = new DataPointEditorViewModel();
            vm.RequestClose += ok => DialogResult = ok;
            if (onSaved != null)
                vm.Saved += onSaved;
            DataContext = vm;
        }
    }
}
