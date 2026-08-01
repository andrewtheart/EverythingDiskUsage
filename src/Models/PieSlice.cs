namespace EverythingDiskUsage.Models;

public sealed record PieSlice(
    string Label,
    long SizeBytes,
    double Percent,
    System.Windows.Media.Brush Fill,
    DirectoryUsageNode? Node,
    DirectoryUsageNode? ExplorerNode)
{
    public string SizeText => DirectoryUsageNode.FormatBytes(SizeBytes);

    public string PercentText => Percent >= 99.95 ? "100%" : $"{Percent:0.0}%";

    public bool IsSelectable => Node is not null;

    public bool CanOpenInExplorer => ExplorerNode is not null;
}