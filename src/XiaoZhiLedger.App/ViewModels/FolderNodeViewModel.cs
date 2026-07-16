using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using XiaoZhiLedger.Core.Models;

namespace XiaoZhiLedger.App.ViewModels;

public sealed class FolderNodeViewModel : INotifyPropertyChanged
{
    private int _totalCount;
    private int _unusedCount;

    public FolderNodeViewModel(FolderNodeModel model)
    {
        Name = model.Name;
        FullPath = model.FullPath;
        _totalCount = model.TotalCount;
        _unusedCount = model.UnusedCount;
        Children = new ObservableCollection<FolderNodeViewModel>(
            model.Children.Select(child => new FolderNodeViewModel(child)));
    }

    public string Name { get; }
    public string FullPath { get; }
    public int TotalCount => _totalCount;
    public int UnusedCount => _unusedCount;
    public string DisplayText => $"{Name}   未用 {UnusedCount} / 总 {TotalCount}";
    public ObservableCollection<FolderNodeViewModel> Children { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateCounts(FolderNodeModel model)
    {
        var countsChanged = _totalCount != model.TotalCount || _unusedCount != model.UnusedCount;
        _totalCount = model.TotalCount;
        _unusedCount = model.UnusedCount;

        if (countsChanged)
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(UnusedCount));
            OnPropertyChanged(nameof(DisplayText));
        }

        var modelsByPath = model.Children.ToDictionary(child => child.FullPath, StringComparer.OrdinalIgnoreCase);
        foreach (var child in Children)
        {
            if (modelsByPath.TryGetValue(child.FullPath, out var childModel))
            {
                child.UpdateCounts(childModel);
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
