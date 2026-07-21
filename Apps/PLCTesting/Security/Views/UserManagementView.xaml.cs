using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PLCTesting.Security.Services;
using PLCTesting.Security.ViewModels;

namespace PLCTesting.Security.Views;

/// <summary>用户管理窗口：展示用户列表并提供增删改与重置密码。</summary>
public partial class UserManagementView : Window
{
    public UserManagementView(IUserStore store, IAuthService auth)
    {
        InitializeComponent();
        DataContext = new UserManagementViewModel(store, auth);
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is UserManagementViewModel vm && vm.Selected != null)
            vm.EditCommand.Execute();
    }
}

/// <summary>bool → 文本（用于启用/禁用状态）。</summary>
public class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "开";
    public string FalseText { get; set; } = "关";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? TrueText : FalseText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → 画刷（用于状态指示点）。</summary>
public class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = Brushes.Green;
    public Brush FalseBrush { get; set; } = Brushes.Gray;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
