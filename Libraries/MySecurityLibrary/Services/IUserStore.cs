using System;
using System.Collections.Generic;
using MySecurityLibrary.Models;

namespace MySecurityLibrary.Services;

/// <summary>用户数据仓储抽象。上层（AuthService / 用户管理界面）只依赖此接口，便于替换存储实现。</summary>
public interface IUserStore
{
    /// <summary>首次运行时写入默认管理员（仅当无任何用户时）。</summary>
    void EnsureSeeded();

    /// <summary>返回全部用户（含禁用）。</summary>
    List<AppUser> GetAll();

    /// <summary>按用户名精确查找（忽略大小写由实现保证）。</summary>
    AppUser? GetByUserName(string userName);

    /// <summary>用户总数。</summary>
    int Count();

    /// <summary>创建用户（内部完成密码哈希）。返回新建实体（含 Id）。</summary>
    AppUser Create(string userName, string displayName, UserRole role, string password, string createdBy);

    /// <summary>更新用户资料（显示名 / 角色 / 启用状态）。</summary>
    void UpdateProfile(int id, string displayName, UserRole role, bool isEnabled);

    /// <summary>重置指定用户密码（内部完成哈希）。</summary>
    void SetPassword(int id, string newPassword);

    /// <summary>记录最近登录时间。</summary>
    void UpdateLastLogin(int id, DateTime at);

    /// <summary>按 Id 删除用户。</summary>
    void Delete(int id);
}
