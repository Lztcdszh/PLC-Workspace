using System;
using System.Globalization;
using System.Windows;

namespace MyPLCUserControlLibrary
{
    /// <summary>
    /// 趋势曲线控件的右键「属性设置」对话框（精简版）：仅设置标题、Y 轴最小/最大值、报警值。
    /// 打开时从控件读取当前属性，「确定」回写（实时生效），「取消」不修改。
    /// </summary>
    public partial class PlcTrendSettingsDialog : Window
    {
        private readonly PlcTrendControl _owner;

        public PlcTrendSettingsDialog(PlcTrendControl owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            InitializeComponent();
            Populate();
        }

        private void Populate()
        {
            TxtTitle.Text = _owner.Title ?? string.Empty;

            ChkAutoScale.IsChecked = _owner.AutoScaleY;
            TxtMinY.Text = _owner.MinY.ToString(CultureInfo.CurrentCulture);
            TxtMaxY.Text = _owner.MaxY.ToString(CultureInfo.CurrentCulture);

            ChkAlarm.IsChecked = _owner.AlarmEnabled;
            TxtAlarm.Text = _owner.AlarmValue.ToString(CultureInfo.CurrentCulture);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _owner.Title = (TxtTitle.Text ?? string.Empty).Trim();

                _owner.AutoScaleY = ChkAutoScale.IsChecked == true;
                if (double.TryParse(TxtMinY.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var min))
                    _owner.MinY = min;
                if (double.TryParse(TxtMaxY.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var max))
                    _owner.MaxY = max;

                _owner.AlarmEnabled = ChkAlarm.IsChecked == true;
                if (double.TryParse(TxtAlarm.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var alarm))
                    _owner.AlarmValue = alarm;

                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"应用属性时出错：{ex.Message}", "趋势曲线属性",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
