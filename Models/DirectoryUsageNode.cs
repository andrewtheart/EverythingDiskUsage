using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EverythingDiskUsage.Models;

public sealed class DirectoryUsageNode : INotifyPropertyChanged
{
    private readonly Dictionary<string, DirectoryUsageNode> _childrenByName = new(StringComparer.OrdinalIgnoreCase);

    public DirectoryUsageNode(string name, string fullPath, DirectoryUsageNode? parent = null)
    {
        Name = name;
        FullPath = fullPath;
        Parent = parent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string FullPath { get; }

    public DirectoryUsageNode? Parent { get; }

    public ObservableCollection<DirectoryUsageNode> Children { get; } = [];

    public long SizeBytes { get; private set; }


    public long FileCount { get; private set; }

    public long DirectFileCount { get; private set; }

    public long DirectFileSizeBytes { get; private set; }
    public int FolderCount { get; private set; }
    public double PercentOfParent { get; private set; }
    public DateTime? LastModifiedUtc { get; private set; }
    public DateTime? LastAccessedUtc { get; private set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FullPath : Name;
    public string SizeText => FormatBytes(SizeBytes);
    public string AllocatedText => SizeText;
    public string FileCountText => FileCount.ToString("N0");
    public string FolderCountText => FolderCount.ToString("N0");
    public string PercentText => PercentOfParent >= 99.95 ? "100%" : $"{PercentOfParent:0.0}%";
    public string LastModifiedText => FormatDate(LastModifiedUtc);
    public string LastAccessedText => FormatDate(LastAccessedUtc);

    internal void AddAggregateFile(long sizeBytes, DateTime? lastModifiedUtc, DateTime? lastAccessedUtc)
    {
        SizeBytes += Math.Max(0, sizeBytes);
        FileCount++;
        LastModifiedUtc = MaxDate(LastModifiedUtc, lastModifiedUtc);
        LastAccessedUtc = MaxDate(LastAccessedUtc, lastAccessedUtc);
    }

    internal void AddDirectFile(long sizeBytes)
    {
        DirectFileSizeBytes += Math.Max(0, sizeBytes);
        DirectFileCount++;
    }

    internal DirectoryUsageNode GetOrAddChild(string name, string fullPath)
    {
        if (_childrenByName.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var child = new DirectoryUsageNode(name, fullPath, this);
        _childrenByName[name] = child;
        Children.Add(child);
        return child;
    }

    internal void FinalizeStats(long parentSizeBytes)
    {
        var sortedChildren = Children
            .OrderByDescending(child => child.SizeBytes)
            .ThenBy(child => child.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Children.Clear();
        foreach (var child in sortedChildren)
        {
            child.FinalizeStats(SizeBytes);
            Children.Add(child);
        }

        FolderCount = Children.Count + Children.Sum(child => child.FolderCount);
        PercentOfParent = parentSizeBytes <= 0 ? 100d : SizeBytes * 100d / parentSizeBytes;
        NotifyAll();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(DirectFileCount));
        OnPropertyChanged(nameof(DirectFileSizeBytes));
        OnPropertyChanged(nameof(FolderCount));
        OnPropertyChanged(nameof(PercentOfParent));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(AllocatedText));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(FolderCountText));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(LastModifiedText));
        OnPropertyChanged(nameof(LastAccessedText));
    }

    private static DateTime? MaxDate(DateTime? current, DateTime? candidate)
    {
        if (candidate is null)
        {
            return current;
        }

        if (current is null || candidate.Value > current.Value)
        {
            return candidate;
        }

        return current;
    }

    private static string FormatDate(DateTime? utcDate)
    {
        return utcDate is null ? string.Empty : utcDate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}