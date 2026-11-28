namespace LinkStudio.Core.Services.Interfaces
{
    /// <summary>
    /// 系统硬件设备变更监听接口（用于串口热插拔自动刷新）。
    /// 具体实现由各平台壳子提供，Core 与 UI 层不依赖 Windows API。
    /// </summary>
    public interface IDeviceChangeMonitor : IDisposable
    {
        event EventHandler? DevicesChanged;
    }
}
