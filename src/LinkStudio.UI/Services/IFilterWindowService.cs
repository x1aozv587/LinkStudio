namespace LinkStudio.UI.Services
{
    /// <summary>
    /// 独立搜索/过滤窗口管理服务。
    /// Windows 桌面端打开独立的 WPF 窗口，与主窗口共享同一份日志与过滤规则状态。
    /// </summary>
    public interface IFilterWindowService
    {
        bool IsSupported { get; }

        void OpenOrActivate();

        void Close();
    }
}
