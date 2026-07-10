using System.IO;
using System.Windows;
using Hotpass.App.ViewModels;

namespace Hotpass.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        // コマンドライン引数のキャプチャファイル(.wpix / .csv)を起動時に読み込む
        Loaded += async (_, _) =>
        {
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                if ((arg.EndsWith(".wpix", StringComparison.OrdinalIgnoreCase) ||
                     arg.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) && File.Exists(arg))
                    await vm.LoadCaptureAsync(arg);
        };
#if DEBUG
        Loaded += (_, _) => Console.WriteLine(
            $"[diag] Loaded content={Content?.GetType().Name} bg={Background} " +
            $"size={ActualWidth}x{ActualHeight} open={(DataContext as MainViewModel)?.Open.Count}");
        ContentRendered += (_, _) => Console.WriteLine("[diag] ContentRendered");
#endif
    }

    private void AddPopupItem_Click(object sender, RoutedEventArgs e)
    {
        // 項目選択後にポップアップを閉じる(Command は別途実行される)
        AddToggle.IsChecked = false;
    }
}
