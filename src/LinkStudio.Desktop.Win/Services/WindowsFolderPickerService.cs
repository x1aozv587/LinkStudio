using System.IO;
using System.Windows;
using LinkStudio.UI.Services;
using Microsoft.Win32;

namespace LinkStudio.Desktop.Win.Services
{
    /// <summary>
    /// Windows 桌面端文件夹选择弹窗实现（基于 .NET 8 WPF 原生 OpenFolderDialog）。
    /// </summary>
    internal sealed class WindowsFolderPickerService : IFolderPickerService
    {
        public bool IsSupported => true;

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return Task.FromResult<string?>(null);
            }

            // WPF 模态对话框必须在主 UI 线程上打开。
            return dispatcher.InvokeAsync(() =>
            {
                var dialog = new OpenFolderDialog
                {
                    Title = title,
                    Multiselect = false
                };

                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                var owner = Application.Current?.MainWindow;
                var accepted = owner is not null
                    ? dialog.ShowDialog(owner) == true
                    : dialog.ShowDialog() == true;

                return accepted ? dialog.FolderName : null;
            }).Task;
        }
    }
}
