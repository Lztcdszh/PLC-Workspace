using System.ComponentModel;
using PLCTesting.Security.Models;

namespace PLCTesting.Security.Services;

/// <summary>登录结果。</summary>
public enum LoginResult
{
    Success,
    UserNotFound,
    WrongPassword,
    Disabled
}

/// <summary>认证服务抽象。提供当前登录用户、登录/登出、以及基于角色的权限判断。</summary>
public interface IAuthService : INotifyPropertyChanged
{
    /// <summary>当前已登录用户；未登录为 null。</summary>
    AppUser? CurrentUser { get; }

    /// <summary>是否已登录。</summary>
    bool IsAuthenticated { get; }

    /// <summary>是否需要在启动时强制登录（本应用恒为 true）。</summary>
    bool RequiresLogin { get; }

    /// <summary>当前用户是否为管理员（可管理用户）。</summary>
    bool CanManageUsers { get; }

    /// <summary>尝试登录；成功返回 Success 并写入 CurrentUser。</summary>
    LoginResult Login(string userName, string password);

    /// <summary>登出当前用户。</summary>
    void Logout();
}
