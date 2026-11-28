using System.Windows;

namespace LinkStudio.Desktop.Win;

/// <summary>
/// 独立搜索/过滤窗口，与主窗口共享同一个 DI 容器与实时日志中心。
/// </summary>
public partial class FilterWindow : Window
{
    public FilterWindow(IServiceProvider serviceProvider)
    {
        // 在 InitializeComponent 之前放入共享 DI 容器，使 BlazorWebView 拿到同一份单例状态。
        Resources.Add("services", serviceProvider);
        InitializeComponent();
    }
}
