using System.Windows;
using System.Windows.Interop;
using LinkStudio.Core.Services.Interfaces;

namespace LinkStudio.Desktop.Win.Services
{
    /// <summary>
    /// Windows 桌面端硬件热插拔监听器（基于 WPF HwndSource 拦截 WM_DEVICECHANGE）。
    /// 使用 Timer 做 150 ms 防抖，避免单次插拔触发多次枚举，且不产生 TaskCanceledException 调试输出噪音。
    /// </summary>
    internal sealed class WindowsDeviceChangeMonitor : IDeviceChangeMonitor
    {
        private const int WmDeviceChange = 0x0219;
        private const int DbtDevNodesChanged = 0x0007;
        private const int DbtDeviceArrival = 0x8000;
        private const int DbtDeviceRemoveComplete = 0x8004;

        private readonly object _gate = new();
        private readonly Timer _debounceTimer;
        private HwndSource? _hwndSource;
        private bool _disposed;

        public event EventHandler? DevicesChanged;

        public WindowsDeviceChangeMonitor()
        {
            _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Attach(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);

            if (PresentationSource.FromVisual(window) is HwndSource existingSource)
            {
                HookSource(existingSource);
                return;
            }

            window.SourceInitialized += OnWindowSourceInitialized;
        }

        private void OnWindowSourceInitialized(object? sender, EventArgs e)
        {
            if (sender is Window window)
            {
                window.SourceInitialized -= OnWindowSourceInitialized;
                if (!_disposed && PresentationSource.FromVisual(window) is HwndSource source)
                {
                    HookSource(source);
                }
            }
        }

        private void HookSource(HwndSource source)
        {
            lock (_gate)
            {
                if (_disposed || _hwndSource is not null)
                {
                    return;
                }

                _hwndSource = source;
                _hwndSource.AddHook(WndProc);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmDeviceChange)
            {
                var eventCode = wParam.ToInt32();
                if (eventCode is DbtDeviceArrival or DbtDeviceRemoveComplete or DbtDevNodesChanged)
                {
                    lock (_gate)
                    {
                        if (!_disposed)
                        {
                            _debounceTimer.Change(150, Timeout.Infinite);
                        }
                    }
                }
            }

            return IntPtr.Zero;
        }

        private void OnDebounceElapsed(object? state)
        {
            if (!_disposed)
            {
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            HwndSource? source;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _debounceTimer.Dispose();
                source = _hwndSource;
                _hwndSource = null;
            }

            source?.RemoveHook(WndProc);
        }
    }
}
