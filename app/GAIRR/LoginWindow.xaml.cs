using System.Windows;
using System.Windows.Input;

namespace GAIRR;

/// <summary>
/// 演示登录窗口（LoginWindow.xaml 后置代码）：模态门禁，叠在主窗口之上弹出。
/// User 为登录成功时回传给 App 的演示用户信息；
/// DialogResult=true 表示放行（App 向已显示的主窗口注入用户），false 表示关闭/取消（App 关闭主窗口退出）。
/// </summary>
public partial class LoginWindow : Window
{
    /// <summary>登录成功时的演示用户信息（取消/关闭时为 null）。</summary>
    public UserInfo? User { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        // 演示账号预填：仅为模拟登录流程，无任何真实校验
        txtUser.Text = "demo";
        txtPwd.Password = "demo123";
    }

    /// <summary>模拟登录成功：置演示用户信息并放行（DialogResult=true 会顺带关闭本窗）。</summary>
    void OnSimulateSuccess(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(txtUser.Text) ? "demo" : txtUser.Text.Trim();
        User = new UserInfo(name, "演示用户");
        DialogResult = true;
    }

    /// <summary>点右上角关闭按钮：与取消相同，不放行，App 收到 false 后 Shutdown 退出。</summary>
    void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>取消登录：不放行，App 收到 false 后 Shutdown 退出。</summary>
    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>账号/密码输入变化时隐藏错误提示条，便于用户修正后重新登录。</summary>
    void OnInputChanged(object sender, RoutedEventArgs e)
    {
        errBar.Visibility = Visibility.Collapsed;
        errMsg.Text = string.Empty;
    }

    /// <summary>无系统标题栏时按住标题行拖动窗口。</summary>
    void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
