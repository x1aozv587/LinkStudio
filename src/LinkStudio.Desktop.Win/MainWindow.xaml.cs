using System.Windows;
using LinkStudio.Core.Services;
using LinkStudio.Core.Services.Interfaces;
using LinkStudio.Desktop.Win.Services;
using LinkStudio.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LinkStudio.Desktop.Win;

/// <summary>
/// WPF 主窗口，仅负责承载全屏 BlazorWebView 并将容器注入静态资源。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddWpfBlazorWebView();
        serviceCollection.AddSingleton<IFolderPickerService, WindowsFolderPickerService>();
        serviceCollection.AddSingleton<IAppsSettingsService>(_ => new AppsSettingsService());

        // 必须在 InitializeComponent() 之前把 DI 容器放入窗口 Resources：
        // XAML 解析器实例化 BlazorWebView 时会立刻查找 {DynamicResource services}，
        // 若顺序写反会导致 BlazorWebView 初始化阶段拿不到依赖注入容器。
        Resources.Add("services", serviceCollection.BuildServiceProvider());

        InitializeComponent();
    }
}
