using System.Windows;
using LinkStudio.UI.Services;

namespace LinkStudio.Desktop.Win.Services
{
    /// <summary>
    /// Windows 桌面端独立预设发送窗口管理器。
    /// </summary>
    internal sealed class WindowsPresetWindowService : IPresetWindowService
    {
        private readonly IServiceProvider _serviceProvider;
        private PresetWindow? _window;

        public WindowsPresetWindowService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public bool IsSupported => true;

        public void OpenOrActivate()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            dispatcher.InvokeAsync(() =>
            {
                if (_window is not null)
                {
                    if (_window.WindowState == WindowState.Minimized)
                    {
                        _window.WindowState = WindowState.Normal;
                    }

                    _window.Activate();
                    return;
                }

                var window = new PresetWindow(_serviceProvider);
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_window, window))
                    {
                        _window = null;
                    }
                };

                _window = window;
                window.Show();
                window.Activate();
            });
        }

        public void Close()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            dispatcher.InvokeAsync(() =>
            {
                var window = _window;
                _window = null;
                window?.Close();
            });
        }
    }
}
