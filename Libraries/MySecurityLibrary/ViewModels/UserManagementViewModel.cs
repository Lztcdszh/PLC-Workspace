using System.Collections.ObjectModel;
using System.Windows;
using Prism.Commands;
using Prism.Mvvm;
using MySecurityLibrary.Models;
using MySecurityLibrary.Services;
using MySecurityLibrary.Views;

namespace MySecurityLibrary.ViewModels;

/// <summary>用户管理窗口视图模型：列表 + 新增 / 编辑 / 删除 / 重置密码。</summary>
public class UserManagementViewModel : BindableBase
{
    private readonly IUserStore _store;
    private readonly IAuthService _auth;
    private ObservableCollection<AppUser> _users = new();
    private AppUser? _selected;

    public UserManagementViewModel(IUserStore store, IAuthService auth)
    {
        _store = store;
        _auth = auth;
        AddCommand = new DelegateCommand(Add);
        EditCommand = new DelegateCommand(Edit, () => Selected != null);
        DeleteCommand = new DelegateCommand(Delete, () => Selected != null && CanDeleteSelected);
        ResetPasswordCommand = new DelegateCommand(ResetPassword, () => Selected != null);
        RefreshCommand = new DelegateCommand(Load);
        Load();
    }

    public ObservableCollection<AppUser> Users
    {
        get => _users;
        set => SetProperty(ref _users, value);
    }

    public AppUser? Selected
    {
        get => _selected;
        set
        {
            SetProperty(ref _selected, value);
            EditCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            ResetPasswordCommand.RaiseCanExecuteChanged();
        }
    }

    public DelegateCommand AddCommand { get; }
    public DelegateCommand EditCommand { get; }
    public DelegateCommand DeleteCommand { get; }
    public DelegateCommand ResetPasswordCommand { get; }
    public DelegateCommand RefreshCommand { get; }

    private bool CanDeleteSelected => Selected != null && Selected.Id != _auth.CurrentUser?.Id;

    private void Load() => Users = new ObservableCollection<AppUser>(_store.GetAll());

    private void Add()
    {
        var dlg = new UserEditDialog(null, false) { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _store.Create(dlg.Model.UserName, dlg.Model.DisplayName, dlg.Model.Role,
                dlg.Model.Password, _auth.CurrentUser?.UserName ?? "system");
            Load();
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"创建失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Edit()
    {
        if (Selected == null) return;
        var dlg = new UserEditDialog(Selected, true) { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() != true) return;
        _store.UpdateProfile(Selected.Id, dlg.Model.DisplayName, dlg.Model.Role, dlg.Model.IsEnabled);
        if (!string.IsNullOrEmpty(dlg.Model.Password))
            _store.SetPassword(Selected.Id, dlg.Model.Password);
        Load();
    }

    private void Delete()
    {
        if (Selected == null) return;
        if (Selected.Id == _auth.CurrentUser?.Id)
        {
            MessageBox.Show("不能删除当前登录账号。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"确认删除用户「{Selected.DisplayName}」({Selected.UserName})？此操作不可撤销。",
                "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _store.Delete(Selected.Id);
        Load();
    }

    private void ResetPassword()
    {
        if (Selected == null) return;
        var dlg = new PasswordResetDialog { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _store.SetPassword(Selected.Id, dlg.Password);
            MessageBox.Show("密码已重置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"重置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
