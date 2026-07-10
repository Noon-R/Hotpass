using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Hotpass.Adapters.Pix;
using Hotpass.App.ViewModels;
using Hotpass.Core.Storage;

namespace Hotpass.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 一部環境(RDP・特定ドライバ)で WPF のレンダースレッドが機能せず白ウィンドウになるため、
        // HW アクセラレーションが効かない環境ではソフトウェア描画に切り替える。
        // HOTPASS_SW_RENDER=1 で強制、=0 で無効化。
        var sw = Environment.GetEnvironmentVariable("HOTPASS_SW_RENDER");
        var autoDetect = RenderCapability.Tier >> 16 == 0 || SystemParameters.IsRemoteSession;
        if (sw == "1" || (sw != "0" && autoDetect))
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        WirePixAdapter();
        WireNsightAdapter();
        base.OnStartup(e);
    }

    /// <summary>Nsight GPU Trace エクスポート CSV の読込。外部ツール不要なので常時有効。</summary>
    private static void WireNsightAdapter()
    {
        MainViewModel.NsightImporter = async csvPath =>
        {
            var importer = new Hotpass.Adapters.Nsight.NsightTraceImporter();
            var result = await Task.Run(() => importer.Import(csvPath));
            return new CaptureViewModel(result.Capture, result.Passes, csvPath);
        };
    }

    /// <summary>pixtool が見つかる場合のみ、実 .wpix の読込と画像抽出を有効化する。</summary>
    private static void WirePixAdapter()
    {
        if (PixToolLocator.Find() is null) return;

        MainViewModel.WpixImporter = async wpixPath =>
        {
            var importer = new PixCaptureImporter(PixToolRunner.CreateDefault());
            var result = await Task.Run(() => importer.ImportAsync(wpixPath));
            return new CaptureViewModel(result.Capture, result.Passes, wpixPath);
        };

        PassRowViewModel.ExtractImageHandler = async row =>
        {
            var wpixPath = row.Owner.SourceFilePath!;
            var extractor = new PixImageExtractor(PixToolRunner.CreateDefault());
            var image = await Task.Run(() => extractor.ExtractAsync(wpixPath, row.Name));

            // マニフェスト(images テーブル)に追記し、UI に反映
            var dbPath = Path.Combine(PixCaptureImporter.GetWorkDir(wpixPath), "hotpass.db");
            if (File.Exists(dbPath))
            {
                using var store = new CaptureStore(dbPath);
                var capture = store.GetCaptures().FirstOrDefault();
                if (capture is not null)
                {
                    store.AddImage(new Core.Model.PassImage
                    {
                        CaptureId = capture.Id,
                        PassName = image.PassName,
                        ResourceName = image.ResourceName,
                        EventId = image.EventId,
                        PngPath = image.PngPath,
                    });
                }
            }
            row.Images.Add(image);
        };
    }
}
