namespace LinkStudio.UI.Services
{
    /// <summary>
    /// 独立预设发送窗口管理服务。
    /// Windows 桌面端打开独立的 WPF 窗口，与主窗口并排使用、不遮挡不虚化接收区。
    /// </summary>
    public interface IPresetWindowService
    {
        bool IsSupported { get; }

        void OpenOrActivate();

        void Close();
    }
}
