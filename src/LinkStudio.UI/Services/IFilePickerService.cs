namespace LinkStudio.UI.Services
{
    /// <summary>
    /// 跨平台文件选择服务抽象（供 V0.6 日志回放选择本地 .log/.txt/.nmea 文件）。
    /// </summary>
    public interface IFilePickerService
    {
        bool IsSupported { get; }

        Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null);
    }
}
