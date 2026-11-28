using System.Windows;
using LinkStudio.Core;
using LinkStudio.Core.Services;
using LinkStudio.Core.Services.Interfaces;
using LinkStudio.Core.Workspaces;
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
    private readonly IPresetWindowService _presetWindowService;
    private readonly WindowsDeviceChangeMonitor _deviceChangeMonitor;

    public MainWindow()
    {
        _deviceChangeMonitor = new WindowsDeviceChangeMonitor();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddWpfBlazorWebView();
        serviceCollection.AddSingleton<IFolderPickerService, WindowsFolderPickerService>();
        serviceCollection.AddSingleton<IFilePickerService, WindowsFilePickerService>();
        serviceCollection.AddSingleton<IFilterWindowService, WindowsFilterWindowService>();
        serviceCollection.AddSingleton<IPresetWindowService, WindowsPresetWindowService>();
        serviceCollection.AddSingleton<IDeviceChangeMonitor>(_deviceChangeMonitor);
        serviceCollection.AddSingleton<IAppsSettingsService>(_ => new AppsSettingsService());
        serviceCollection.AddSingleton<IWorkspaceStore>(_ => new JsonWorkspaceStore());
        serviceCollection.AddSingleton<WorkbenchSettings>();
        serviceCollection.AddSingleton<TerminalSessionHub>();

        var serviceProvider = serviceCollection.BuildServiceProvider();
        _filterWindowService = serviceProvider.GetRequiredService<IFilterWindowService>();
        _presetWindowService = serviceProvider.GetRequiredService<IPresetWindowService>();

        // 必须在 InitializeComponent() 之前把 DI 容器放入窗口 Resources：
        // XAML 解析器实例化 BlazorWebView 时会立刻查找 {DynamicResource services}。
        Resources.Add("services", serviceProvider);

        InitializeComponent();
        Title = $"{LinkStudioMetadata.AppName} {LinkStudioMetadata.DisplayVersion}";

        // 挂载 Win32 WM_DEVICECHANGE 监听，插拔 USB 串口自动刷新列表。
        _deviceChangeMonitor.Attach(this);

        // 主窗口关闭时同步关闭独立子窗口并释放热插拔监听。
        Closed += (_, _) =>
        {
            _filterWindowService.Close();
            _presetWindowService.Close();
            _deviceChangeMonitor.Dispose();
        };
    }
}
