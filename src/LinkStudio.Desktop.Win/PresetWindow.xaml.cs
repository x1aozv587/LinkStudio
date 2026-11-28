using System.Windows;

namespace LinkStudio.Desktop.Win;

/// <summary>
/// 独立预设发送窗口，与主窗口共享同一个 DI 容器，方便并排点击发送并直视接收区。
/// </summary>
public partial class PresetWindow : Window
{
    public PresetWindow(IServiceProvider serviceProvider)
    {
        Resources.Add("services", serviceProvider);
        InitializeComponent();
    }
}
