var builder = WebApplication.CreateBuilder(args);

// 注册 Blazor Server 交互渲染引擎，供 macOS / 浏览器模式托管共享的 LinkStudio.UI 组件库。
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<LinkStudio.Server.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
