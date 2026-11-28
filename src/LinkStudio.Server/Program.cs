using LinkStudio.Core.Services;
using LinkStudio.Core.Services.Interfaces;
using LinkStudio.UI.Services;
using LinkStudio.UI.State;

var builder = WebApplication.CreateBuilder(args);

// 注册 Blazor Server 交互渲染引擎，供 macOS / 浏览器模式托管共享的 LinkStudio.UI 组件库。
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<IFolderPickerService, UnsupportedFolderPickerService>();
builder.Services.AddSingleton<IFilterWindowService, UnsupportedFilterWindowService>();
builder.Services.AddSingleton<IPresetWindowService, UnsupportedPresetWindowService>();
builder.Services.AddSingleton<IDeviceChangeMonitor, UnsupportedDeviceChangeMonitor>();
builder.Services.AddSingleton<IAppsSettingsService>(_ => new AppsSettingsService());
builder.Services.AddSingleton<WorkbenchSettings>();
builder.Services.AddSingleton<TerminalSessionHub>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<LinkStudio.Server.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// 浏览器端无原生本地文件夹弹窗，提供空实现保证共享 UI 正常注入。
/// </summary>
internal sealed class UnsupportedFolderPickerService : IFolderPickerService
{
    public bool IsSupported => false;

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// 浏览器端暂不提供独立桌面窗口，提供空实现保证共享 UI 正常注入。
/// </summary>
internal sealed class UnsupportedFilterWindowService : IFilterWindowService
{
    public bool IsSupported => false;

    public void OpenOrActivate() { }

    public void Close() { }
}

internal sealed class UnsupportedPresetWindowService : IPresetWindowService
{
    public bool IsSupported => false;

    public void OpenOrActivate() { }

    public void Close() { }
}

/// <summary>
/// 浏览器端无 Win32 设备变更广播，提供空实现保证共享 UI 正常注入。
/// </summary>
internal sealed class UnsupportedDeviceChangeMonitor : IDeviceChangeMonitor
{
    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public void Dispose() { }
}
