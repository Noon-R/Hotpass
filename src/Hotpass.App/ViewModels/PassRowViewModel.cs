using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hotpass.Core.Analysis;
using Hotpass.Core.Model;

namespace Hotpass.App.ViewModels;

/// <summary>Breakdown 表の 1 行(+クリックで開く詳細ドロワー)。</summary>
public partial class PassRowViewModel : ObservableObject
{
    private readonly CaptureViewModel _owner;

    public PassRecord Pass { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public PassRowViewModel(CaptureViewModel owner, PassRecord pass)
    {
        _owner = owner;
        Pass = pass;
        Images = new System.Collections.ObjectModel.ObservableCollection<PassImage>();
    }

    public CategoryMeta Cat => CategoryMeta.For(Pass.Category);

    public string Name => Pass.Name;
    public string EventText => $"event #{Pass.EventId}";
    public double DurMs => CaptureViewModel.ToMs(Pass.DurationNs);
    public string DurText => DurMs.ToString("0.0");
    public double PctOfFrame => FrameAnalyzer.PctOfFrame(Pass, _owner.Summary.TotalGpuNs);
    public string PctText => $"{PctOfFrame:0}%";
    public double PctFraction => PctOfFrame / 100.0;
    public string PctDetailText => $"{PctOfFrame:0.0}% of frame";

    public bool OccIsNa => !(_owner.Info.ProvidesOccupancy && Pass.OccupancyPct is not null);
    public string OccText => OccIsNa ? "—" : $"{Pass.OccupancyPct:0}%";
    public string LimiterText => _owner.Info.ProvidesLimiter && Pass.OccupancyLimiter is not null ? Pass.OccupancyLimiter : "—";
    public string SolText => _owner.Info.ProvidesSol && Pass.SolTopUnit is not null ? Pass.SolTopUnit : "—";

    public string ProvenanceText => _owner.IsPix
        ? $"From {_owner.FileName} (PIX GPU Capture) — SOL unit not recorded."
        : $"From {_owner.FileName} (Nsight GPU Trace) — occupancy limiter not recorded.";

    public string OpenInPixText => $"Open event #{Pass.EventId} in PIX ↗";

    /// <summary>PIX への導線は .wpix 由来のみ(Nsight CSV をシェルで開いても意味がない)。</summary>
    public bool CanOpenInPix => _owner.IsPix && _owner.SourceFilePath is not null && File.Exists(_owner.SourceFilePath);

    /// <summary>画像抽出の実装はアダプタ側で注入(実 .wpix 由来のキャプチャのみ有効)。</summary>
    public static Func<PassRowViewModel, Task>? ExtractImageHandler { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<PassImage> Images { get; }

    public CaptureViewModel Owner => _owner;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand(CanExecute = nameof(CanOpenInPix))]
    private void OpenInPix()
    {
        try
        {
            // .wpix の関連付けで PIX GUI を開く(イベントへの直接ジャンプは PIX 側 UI で event id 検索)
            Process.Start(new ProcessStartInfo(_owner.SourceFilePath!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"PIX の起動に失敗しました: {ex.Message}", "Hotpass", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void CopyEventId() => Clipboard.SetText(Pass.EventId.ToString());

    public bool CanExtractImage => ExtractImageHandler is not null && CanOpenInPix;

    [RelayCommand(CanExecute = nameof(CanExtractImage))]
    private async Task ExtractImageAsync()
    {
        if (ExtractImageHandler is { } handler)
            await handler(this);
    }
}
