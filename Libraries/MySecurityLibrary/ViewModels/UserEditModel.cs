using MySecurityLibrary.Models;

namespace MySecurityLibrary.ViewModels;

/// <summary>用户编辑对话框的临时数据模型（双向绑定用）。</summary>
public class UserEditModel
{
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Normal;
    public bool IsEnabled { get; set; } = true;
    /// <summary>新增时必填；编辑时留空表示不改密码。</summary>
    public string Password { get; set; } = "";
}
