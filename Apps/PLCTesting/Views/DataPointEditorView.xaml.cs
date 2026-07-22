using PLCTesting.ViewModels;
using System.Windows;

namespace PLCTesting.Views
{
    /// <summary>
    /// 数据点编辑器窗口：可视化编辑 dataPoints.json。
    /// </summary>
    public partial class DataPointEditorView : Window
    {
        public DataPointEditorView()
        {
            InitializeComponent();
            var vm = new DataPointEditorViewModel();
            vm.RequestClose += ok => DialogResult = ok;
            DataContext = vm;
        }
    }
}
