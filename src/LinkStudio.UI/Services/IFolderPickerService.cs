namespace LinkStudio.UI.Services
{
    /// <summary>
    /// 跨平台文件夹选择服务抽象。
    /// Windows 桌面端通过 WPF 原生 OpenFolderDialog 弹窗选择；
    /// Web Server 端无法直接访问本地文件系统对话框，返回 null。
    /// </summary>
    public interface IFolderPickerService
    {
        bool IsSupported { get; }

        Task<string?> PickFolderAsync(string title, string? initialDirectory = null);
    }
}
