using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hotpass.Core.SampleData;

namespace Hotpass.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    /// <summary>実 .wpix を開く実装(PIX アダプタ)。未接続なら null。</summary>
    public static Func<string, Task<CaptureViewModel>>? WpixImporter { get; set; }

    public ObservableCollection<CaptureViewModel> Open { get; } = [];
    public ObservableCollection<CaptureViewModel> Available { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingleMode))]
    private bool _isCompareMode;

    [ObservableProperty]
    private bool _isTimelineView;

    [ObservableProperty]
    private CaptureViewModel? _viewed;

    [ObservableProperty]
    private CaptureViewModel? _baseCapture;

    [ObservableProperty]
    private CaptureViewModel? _compareCapture;

    [ObservableProperty]
    private CompareState? _compare;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsSingleMode => !IsCompareMode;
    public bool HasCaptures => Open.Count > 0;

    public MainViewModel()
    {
        var samples = SampleCaptures.CreateAll()
            .Select(s => new CaptureViewModel(s.Capture, s.Passes))
            .ToList();
        // モックと同じ初期状態: 先頭2つを開いた状態で起動
        Open.Add(samples[0]);
        Open.Add(samples[1]);
        foreach (var s in samples.Skip(2)) Available.Add(s);
        Viewed = samples[0];

        // デバッグ/検証用: 起動時モード指定(HOTPASS_START_MODE=compare)
        if (Environment.GetEnvironmentVariable("HOTPASS_START_MODE") == "compare")
        {
            BaseCapture = samples[0];
            CompareCapture = samples[1];
            IsCompareMode = true;
        }
        Refresh();
    }

    partial void OnIsCompareModeChanged(bool value)
    {
        if (value)
        {
            BaseCapture ??= Open.FirstOrDefault();
            if (CompareCapture is null || CompareCapture == BaseCapture)
                CompareCapture = Open.FirstOrDefault(c => c != BaseCapture);
        }
        Refresh();
    }

    partial void OnViewedChanged(CaptureViewModel? value) => Refresh();
    partial void OnBaseCaptureChanged(CaptureViewModel? value) => Refresh();
    partial void OnCompareCaptureChanged(CaptureViewModel? value) => Refresh();

    private void Refresh()
    {
        foreach (var c in Open)
        {
            c.Role = IsCompareMode
                ? c == BaseCapture ? ChipRole.Base : c == CompareCapture ? ChipRole.Compare : ChipRole.None
                : c == Viewed ? ChipRole.Viewing : ChipRole.None;
        }
        Compare = IsCompareMode && BaseCapture is not null && CompareCapture is not null && BaseCapture != CompareCapture
            ? CompareState.Create(BaseCapture, CompareCapture)
            : null;
        OnPropertyChanged(nameof(HasCaptures));
    }

    [RelayCommand]
    private void SetMode(string mode) => IsCompareMode = mode == "compare";

    [RelayCommand]
    private void SetSubView(string view) => IsTimelineView = view == "timeline";

    [RelayCommand]
    private void View(CaptureViewModel c) => Viewed = c;

    [RelayCommand]
    private void SetBase(CaptureViewModel c)
    {
        if (c == CompareCapture) CompareCapture = BaseCapture;
        BaseCapture = c;
    }

    [RelayCommand]
    private void SetCompare(CaptureViewModel c)
    {
        if (c == BaseCapture) BaseCapture = CompareCapture;
        CompareCapture = c;
    }

    [RelayCommand]
    private void Swap()
    {
        (BaseCapture, CompareCapture) = (CompareCapture, BaseCapture);
    }

    [RelayCommand]
    private void AddSample(CaptureViewModel c)
    {
        Available.Remove(c);
        Open.Add(c);
        Viewed ??= c;
        Refresh();
    }

    [RelayCommand]
    private void Close(CaptureViewModel c)
    {
        Open.Remove(c);
        if (c.SourceFilePath is null) Available.Add(c); // サンプルは追加リストへ戻す
        if (Viewed == c) Viewed = Open.FirstOrDefault();
        if (BaseCapture == c) BaseCapture = Open.FirstOrDefault(x => x != CompareCapture);
        if (CompareCapture == c) CompareCapture = Open.FirstOrDefault(x => x != BaseCapture);
        Refresh();
    }

    /// <summary>起動引数などから .wpix を読み込む(ファイルダイアログを介さない経路)。</summary>
    public async Task LoadWpixAsync(string path)
    {
        if (WpixImporter is null) return;
        IsBusy = true;
        try
        {
            var cvm = await WpixImporter(path);
            Open.Add(cvm);
            Viewed = cvm;
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"キャプチャの読み込みに失敗しました:\n{ex.Message}", "Hotpass",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (WpixImporter is null)
        {
            MessageBox.Show("PIX アダプタが未接続です(サンプルキャプチャをご利用ください)。", "Hotpass",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PIX GPU Capture (*.wpix)|*.wpix|All files (*.*)|*.*",
            Title = "Add capture",
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            var cvm = await WpixImporter(dlg.FileName);
            Open.Add(cvm);
            Viewed = cvm;
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"キャプチャの読み込みに失敗しました:\n{ex.Message}", "Hotpass",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
