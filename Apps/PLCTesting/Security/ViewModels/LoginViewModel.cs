using Prism.Mvvm;
using PLCTesting.Security.Services;

namespace PLCTesting.Security.ViewModels;

/// <summary>登录窗口视图模型：校验凭据并驱动 IAuthService 登录。</summary>
public class LoginViewModel : BindableBase
{
    private readonly IAuthService _auth;
    private string _userName = "";
    private string _password = "";
    private string _errorMessage = "";
    private bool _isAuthenticated;

    public LoginViewModel(IAuthService auth)
    {
        _auth = auth;
    }

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set => SetProperty(ref _isAuthenticated, value);
    }

    /// <summary>尝试登录；成功返回 true（调用方应关闭对话框）。</summary>
    public bool TryLogin()
    {
        ErrorMessage = "";
        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "请输入用户名和密码。";
            return false;
        }

        return _auth.Login(UserName.Trim(), Password) switch
        {
            LoginResult.Success => (IsAuthenticated = true),
            LoginResult.Disabled => Fail("该账号已被禁用，请联系管理员。"),
            _ => Fail("用户名或密码错误。")
        };
    }

    private bool Fail(string message)
    {
        ErrorMessage = message;
        IsAuthenticated = false;
        return false;
    }
}
