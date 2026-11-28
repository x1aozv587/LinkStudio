using System.Windows;
using LinkStudio.Core.Services;
using LinkStudio.Core.Services.Interfaces;
using LinkStudio.Desktop.Win.Services;
using LinkStudio.UI.Services;
using LinkStudio.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace LinkStudio.Desktop.Win;

/// <summary>
/// WPF 主窗口，负责承载主 BlazorWebView 并将共享 DI 容器注入静态资源。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IFilterWindowService _filterWindowService;

    public MainWindow()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddWpfBlazorWebView();
        serviceCollection.AddSingleton<IFolderPickerService, WindowsFolderPickerService>();
        serviceCollection.AddSingleton<IFilterWindowService, WindowsFilterWindowService>();
        serviceCollection.AddSingleton<IAppsSettingsService>(_ => new AppsSettingsService());
        serviceCollection.AddSingleton<WorkbenchSettings>();
        serviceCollection.AddSingleton<TerminalSessionHub>();

        var serviceProvider = serviceCollection.BuildServiceProvider();
        _filterWindowService = serviceProvider.GetRequiredService<IFilterWindowService>();

        // 必须在 InitializeComponent() 之前把 DI 容器放入窗口 Resources：
        // XAML 解析器实例化 BlazorWebView 时会立刻查找 {DynamicResource services}。
        Resources.Add("services", serviceProvider);

        InitializeComponent();

        // 主窗口关闭时同步关闭独立搜索/过滤窗口，避免后台残留进程。
        Closed += (_, _) => _filterWindowService.Close();
    }
}
