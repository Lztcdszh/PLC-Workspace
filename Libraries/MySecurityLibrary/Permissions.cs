using MySecurityLibrary.Models;

namespace MySecurityLibrary;

/// <summary>
/// 权限枚举（为未来细粒度权限预留，当前两档角色下仅用到 ManageUsers）。
/// 设计成与角色解耦，便于移植到需要多角色多权限的其它项目。
/// </summary>
public enum Permission
{
    /// <summary>只读 / 查看。</summary>
    View,

    /// <summary>操作设备（复位、参数设置、配置等）。</summary>
    Operate,

    /// <summary>管理用户（增删改、重置密码）。</summary>
    ManageUsers
}

/// <summary>角色-权限映射（两档角色：管理员全权限，普通用户仅基础操作）。</summary>
public static class Authorization
{
    /// <summary>判断某角色是否拥有指定权限。</summary>
    public static bool Has(UserRole role, Permission permission)
    {
        if (role == UserRole.Admin) return true;
        // 普通用户：仅可查看与基础操作，不可管理用户
        return permission is Permission.View or Permission.Operate;
    }
}
