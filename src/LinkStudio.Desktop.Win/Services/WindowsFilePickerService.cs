using System.IO;
using System.Windows;
using LinkStudio.UI.Services;
using Microsoft.Win32;

namespace LinkStudio.Desktop.Win.Services
{
    /// <summary>
    /// Windows 桌面端本地文件选择弹窗实现（基于 WPF 原生 OpenFileDialog）。
    /// </summary>
    internal sealed class WindowsFilePickerService : IFilePickerService
    {
        public bool IsSupported => true;

        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return Task.FromResult<string?>(null);
            }

            return dispatcher.InvokeAsync(() =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = title,
                    Filter = filter,
                    Multiselect = false,
                    CheckFileExists = true
                };

                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                var owner = Application.Current?.MainWindow;
                var accepted = owner is not null
                    ? dialog.ShowDialog(owner) == true
                    : dialog.ShowDialog() == true;

                return accepted ? dialog.FileName : null;
            }).Task;
        }
    }
}
