using System.Reflection;

namespace LinkStudio.Core;

/// <summary>
/// 应用程序的基础标识信息，供双端 UI 与单元测试共享读取。
/// 版本号统一由根目录 Directory.Build.props 注入程序集，保证单点维护。
/// </summary>
public static class LinkStudioMetadata
{
    public const string AppName = "LinkStudio";

    public static string Version { get; } = ResolveVersion();

    public static string DisplayVersion => $"v{Version}";

    private static string ResolveVersion()
    {
        var assembly = typeof(LinkStudioMetadata).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.1.0.2";
    }
}
