using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hotpass.App.Views;

public partial class SingleView : UserControl
{
    public SingleView()
    {
        InitializeComponent();
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: ViewModels.PassRowViewModel row })
            row.IsExpanded = !row.IsExpanded;
    }

    private void Thumbnail_Click(object sender, MouseButtonEventArgs e)
    {
        // サムネイルクリックで既定ビューアで原寸表示
        if (sender is Border { Tag: string path } && System.IO.File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // ビューア起動失敗は致命的でないため無視
            }
        }
    }
}
