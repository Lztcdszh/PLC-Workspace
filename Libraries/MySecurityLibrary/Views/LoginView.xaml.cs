using System.Windows;
using System.Windows.Input;
using MySecurityLibrary.Services;
using MySecurityLibrary.ViewModels;

namespace MySecurityLibrary.Views;

/// <summary>登录窗口：强制登录流程的入口。校验成功才设置 DialogResult=true。</summary>
public partial class LoginView : Window
{
    public LoginView(IAuthService auth)
    {
        InitializeComponent();
        DataContext = new LoginViewModel(auth);
        Loaded += (s, e) =>
        {
            // 预填默认管理员账号，用户可直接点击登录
            UserNameBox.Text = "admin";
            PasswordBox.Password = "admin123";
            UserNameBox.Focus();
        };
        // 防止点击外部时窗口消失卡死：失去焦点立即重新激活并置顶
        Deactivated += (s, e) =>
        {
            Activate();
            Topmost = true;
        };
        // 鼠标左键按下时允许拖动无边框窗口
        MouseLeftButtonDown += (s, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        };
    }

    private void DoLogin()
    {
        var vm = (LoginViewModel)DataContext;
        vm.UserName = UserNameBox.Text;
        vm.Password = PasswordBox.Password;
        if (vm.TryLogin())
            DialogResult = true;
    }

    private void BtnLogin_Click(object sender, RoutedEventArgs e) => DoLogin();

    /// <summary>关闭按钮：不登录直接退出。ShowDialog 返回 false → 程序退出。</summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoLogin();
    }
}
