using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using XiaoZhiLedger.Core.Models;

namespace XiaoZhiLedger.App.ViewModels;

public sealed class MediaCardViewModel : INotifyPropertyChanged
{
    private string _status;
    private int _dragCount;
    private string _note;
    private string _updatedAt;
    private string _lastDraggedAt;
    private ImageSource? _previewImage;
    private ImageSource? _basePreviewImage;
    private bool _isPreviewBusy;
    private string _previewHint = "等待生成代表帧";
    private double _scrubRatio;

    public MediaCardViewModel(
        MediaItem item,
        LedgerProjectMaterialState projectState,
        string note,
        bool hadProjectState)
    {
        Item = item;
        _status = projectState.Status;
        _dragCount = projectState.DragCount;
        _note = note;
        _updatedAt = projectState.UpdatedAt;
        _lastDraggedAt = projectState.LastDraggedAt;
        HadProjectState = hadProjectState;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaItem Item { get; }
    public string Id => Item.Id;
    public string Path => Item.Path;
    public string Name => Item.Name;
    public string RelativeFolder => string.IsNullOrWhiteSpace(Item.RelativeFolder) ? "根目录" : Item.RelativeFolder;
    public bool HadProjectState { get; private set; }
    public string Status => _status;
    public int DragCount => _dragCount;
    public string Note
    {
        get => _note;
        set
        {
            if (_note == value)
            {
                return;
            }

            _note = value;
            OnPropertyChanged();
        }
    }
    public ImageSource? PreviewImage { get => _previewImage; private set { _previewImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPreviewImage)); } }
    public bool HasPreviewImage => PreviewImage is not null;
    public bool IsPreviewBusy { get => _isPreviewBusy; private set { _isPreviewBusy = value; OnPropertyChanged(); } }
    public string PreviewHint { get => _previewHint; private set { _previewHint = value; OnPropertyChanged(); } }
    public double ScrubRatio { get => _scrubRatio; private set { _scrubRatio = value; OnPropertyChanged(); } }
    public string ScrubPercentText => $"{Math.Round(ScrubRatio * 100):N0}%";
    public string SizeText => FormatFileSize(Item.Size);
    public string DateText => Item.LastWriteTime.ToString("yyyy/MM/dd  HH:mm");
    public string MetaText => $"{SizeText}  ·  {DateText}";
    public string StatusText => Status switch
    {
        "used" => $"已使用 · {DragCount} 次",
        "candidate" => DragCount > 0 ? $"备选 · 已用 {DragCount} 次" : "备选 · 0 次",
        "excluded" => DragCount > 0 ? $"不考虑 · 已用 {DragCount} 次" : "不考虑 · 0 次",
        _ => DragCount > 0 ? $"未使用 · 已用 {DragCount} 次" : "未使用 · 0 次"
    };
    public string StatusBackground => Status switch
    {
        "used" => "#E7F1FF",
        "candidate" => "#FFF1D9",
        "excluded" => "#EDEEF1",
        _ => "#FCE8E8"
    };
    public string StatusForeground => Status switch
    {
        "used" => "#276AB3",
        "candidate" => "#9B640D",
        "excluded" => "#5E626B",
        _ => "#B81F27"
    };

    public LedgerProjectMaterialState? CaptureState() => HadProjectState
        ? new LedgerProjectMaterialState(Status, DragCount, _updatedAt, _lastDraggedAt)
        : null;

    public void ApplyState(LedgerProjectMaterialState? state)
    {
        HadProjectState = state is not null;
        _status = state?.Status ?? "unused";
        _dragCount = state?.DragCount ?? 0;
        _updatedAt = state?.UpdatedAt ?? "";
        _lastDraggedAt = state?.LastDraggedAt ?? "";
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(DragCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
    }

    public void ApplySuccessfulDrag()
    {
        HadProjectState = true;
        _status = "used";
        _dragCount++;
        _updatedAt = DateTimeOffset.Now.ToString("o");
        _lastDraggedAt = _updatedAt;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(DragCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
    }

    public void SetBasePreview(ImageSource image)
    {
        _basePreviewImage = image;
        PreviewImage = image;
        PreviewHint = "";
    }

    public void SetScrubPreview(ImageSource image, double ratio)
    {
        PreviewImage = image;
        ScrubRatio = Math.Clamp(ratio, 0, 1);
        OnPropertyChanged(nameof(ScrubPercentText));
    }

    public void RestoreBasePreview()
    {
        PreviewImage = _basePreviewImage;
        ScrubRatio = 0;
        OnPropertyChanged(nameof(ScrubPercentText));
    }

    public void SetPreviewBusy(bool busy, string hint)
    {
        IsPreviewBusy = busy;
        if (!string.IsNullOrWhiteSpace(hint))
        {
            PreviewHint = hint;
        }
    }

    public void SetPreviewUnavailable(string hint)
    {
        PreviewHint = hint;
        PreviewImage = null;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1L << 30)
        {
            return $"{bytes / (double)(1L << 30):N2} GB";
        }

        if (bytes >= 1L << 20)
        {
            return $"{bytes / (double)(1L << 20):N1} MB";
        }

        return $"{bytes / 1024d:N0} KB";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
