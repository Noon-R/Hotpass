using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hotpass.App.Views;

public partial class CompareView : UserControl
{
    public CompareView()
    {
        InitializeComponent();
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        // 画像を持つ行だけドロワーを開閉(それ以外は開いても空なので反応させない)
        if (sender is System.Windows.FrameworkElement { DataContext: ViewModels.CompareRow { HasImages: true } row })
            row.IsExpanded = !row.IsExpanded;
    }

    private void Thumbnail_Click(object sender, MouseButtonEventArgs e)
    {
        // サムネイルクリックで既定ビューアで原寸表示
        if (sender is System.Windows.Controls.Border { Tag: string path } && System.IO.File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // ビューア起動失敗は致命的でないため無視
            }
            e.Handled = true; // 行の開閉トグルに伝播させない
        }
    }
}
