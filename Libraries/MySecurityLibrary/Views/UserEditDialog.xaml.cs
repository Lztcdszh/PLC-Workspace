using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using MySecurityLibrary.Models;
using MySecurityLibrary.ViewModels;

namespace MySecurityLibrary.Views;

/// <summary>用户新增 / 编辑对话框。返回 Model 供上层写入仓储。</summary>
public partial class UserEditDialog : Window
{
    public UserEditModel Model { get; } = new();

    public UserEditDialog(AppUser? existing, bool isEdit)
    {
        InitializeComponent();

        RoleCombo.ItemsSource = Enum.GetValues(typeof(UserRole)).Cast<UserRole>().ToArray();

        if (isEdit && existing != null)
        {
            TitleText.Text = "编辑用户";
            UserNameBox.Text = existing.UserName;
            UserNameBox.IsReadOnly = true;
            UserNameBox.Foreground = System.Windows.Media.Brushes.Gray;
            DisplayNameBox.Text = existing.DisplayName;
            RoleCombo.SelectedItem = existing.Role;
            EnabledBox.IsChecked = existing.IsEnabled;
            PwdLabel.Text = "重置密码（留空则不修改）";
            PwdHint.Text = "仅当需要修改密码时填写。";
        }
        else
        {
            TitleText.Text = "新增用户";
            RoleCombo.SelectedItem = UserRole.Normal;
            PwdHint.Text = "初始密码，必填（至少 1 位）。";
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var userName = UserNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(userName))
        {
            MessageBox.Show("用户名不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            MessageBox.Show("请填写密码。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Model.UserName = userName;
        Model.DisplayName = DisplayNameBox.Text.Trim();
        Model.Role = (UserRole)(RoleCombo.SelectedItem ?? UserRole.Normal);
        Model.IsEnabled = EnabledBox.IsChecked == true;
        Model.Password = PasswordBox.Password;

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>角色 -> 中文显示名转换器（供编辑对话框 ComboBox 使用）。</summary>
public class RoleDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is UserRole r ? r.DisplayName() : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
