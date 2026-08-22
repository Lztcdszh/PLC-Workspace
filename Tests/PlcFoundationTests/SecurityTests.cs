using MySecurityLibrary;
using MySecurityLibrary.Models;
using MySecurityLibrary.Services;
using Xunit;

namespace PlcFoundationTests;

public class SecurityTests
{
    [Fact]
    public void Pbkdf2_Hash_Verify_Roundtrip()
    {
        var hasher = new Pbkdf2PasswordHasher();
        hasher.Hash("admin123", out var salt, out var hash);

        Assert.False(string.IsNullOrEmpty(salt));
        Assert.False(string.IsNullOrEmpty(hash));
        Assert.True(hasher.Verify("admin123", salt, hash));
        Assert.False(hasher.Verify("wrong", salt, hash));
        Assert.False(hasher.Verify("", salt, hash));
    }

    [Fact]
    public void SqliteUserStore_Seed_And_Crud()
    {
        var db = Path.Combine(Path.GetTempPath(), $"sectest_{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteUserStore(db, new Pbkdf2PasswordHasher());
            store.EnsureSeeded();
            Assert.Equal(1, store.Count());
            var admin = store.GetByUserName("admin");
            Assert.NotNull(admin);
            Assert.Equal(UserRole.Admin, admin!.Role);

            // 创建普通用户
            var u = store.Create("alice", "爱丽丝", UserRole.Normal, "pass@123", "admin");
            Assert.True(u.Id > 0);
            Assert.True(store.GetByUserName("alice") != null);

            // 重复用户名抛异常
            Assert.Throws<InvalidOperationException>(() =>
                store.Create("alice", "x", UserRole.Normal, "p", "admin"));

            // 更新资料 + 改密码 + 登录成功
            store.UpdateProfile(u.Id, "爱丽丝2", UserRole.Normal, true);
            store.SetPassword(u.Id, "newpass");
            var auth = new AuthService(store, new Pbkdf2PasswordHasher());
            Assert.Equal(LoginResult.Success, auth.Login("alice", "newpass"));
            Assert.True(auth.IsAuthenticated);
            auth.Logout();
            Assert.False(auth.IsAuthenticated);

            // 错误密码
            Assert.Equal(LoginResult.WrongPassword, auth.Login("alice", "bad"));

            // 禁用后不可登录
            store.UpdateProfile(u.Id, "爱丽丝2", UserRole.Normal, false);
            Assert.Equal(LoginResult.Disabled, auth.Login("alice", "newpass"));

            // 删除
            store.Delete(u.Id);
            Assert.Null(store.GetByUserName("alice"));
        }
        finally
        {
            if (File.Exists(db)) { try { File.Delete(db); } catch { } }
        }
    }

    [Fact]
    public void Authorization_Admin_AllPermissions_Normal_Limited()
    {
        Assert.True(Authorization.Has(UserRole.Admin, Permission.ManageUsers));
        Assert.True(Authorization.Has(UserRole.Normal, Permission.View));
        Assert.True(Authorization.Has(UserRole.Normal, Permission.Operate));
        Assert.False(Authorization.Has(UserRole.Normal, Permission.ManageUsers));
    }
}
