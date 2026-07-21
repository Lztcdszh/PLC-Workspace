using System.ComponentModel;
using System.Runtime.CompilerServices;
using PLCTesting.Security.Models;

namespace PLCTesting.Security.Services;

/// <summary>
/// 认证服务实现。持有当前登录用户，提供登录/登出与角色判定。
/// 以单例形式由 DI 容器注入，全应用共享同一实例。
/// </summary>
public class AuthService : IAuthService
{
    private readonly IUserStore _store;
    private readonly IPasswordHasher _hasher;
    private AppUser? _current;

    public AuthService(IUserStore store, IPasswordHasher hasher)
    {
        _store = store;
        _hasher = hasher;
    }

    public AppUser? CurrentUser => _current;
    public bool IsAuthenticated => _current != null;
    public bool RequiresLogin => true;
    public bool CanManageUsers => _current?.Role == UserRole.Admin;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>主动通知（供上层在 CurrentUser 相关字段变化时调用）。</summary>
    public void RaiseAuthChanged()
    {
        Raise(nameof(CurrentUser));
        Raise(nameof(IsAuthenticated));
        Raise(nameof(CanManageUsers));
    }

    public LoginResult Login(string userName, string password)
    {
        var user = _store.GetByUserName(userName);
        if (user == null) return LoginResult.UserNotFound;
        if (!user.IsEnabled) return LoginResult.Disabled;
        if (!_hasher.Verify(password, user.PasswordSalt, user.PasswordHash))
            return LoginResult.WrongPassword;

        user.LastLoginAt = System.DateTime.Now;
        _store.UpdateLastLogin(user.Id, user.LastLoginAt.Value);
        _current = user;
        RaiseAuthChanged();
        return LoginResult.Success;
    }

    public void Logout()
    {
        _current = null;
        RaiseAuthChanged();
    }
}
