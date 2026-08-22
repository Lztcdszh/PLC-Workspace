using System.Windows;

namespace MySecurityLibrary.Views;

/// <summary>重置密码对话框。Password 属性在确认一致后对外暴露。</summary>
public partial class PasswordResetDialog : Window
{
    /// <summary>两次输入一致后的新密码（否则为空）。</summary>
    public string Password { get; private set; } = "";

    public PasswordResetDialog()
    {
        InitializeComponent();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PwdBox.Password))
        {
            MessageBox.Show("新密码不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (PwdBox.Password != PwdConfirmBox.Password)
        {
            MessageBox.Show("两次输入的密码不一致。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Password = PwdBox.Password;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
