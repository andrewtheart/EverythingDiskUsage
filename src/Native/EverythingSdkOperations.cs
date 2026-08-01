using System.Text;

namespace EverythingDiskUsage.Native;

internal interface IEverythingSdkOperations
{
    object SyncRoot { get; }

    bool IsDBLoaded();
    void Reset();
    void SetSearch(string search);
    void SetMatchPath(bool enabled);
    void SetMatchCase(bool enabled);
    void SetOffset(uint offset);
    void SetMax(uint maximumResults);
    void SetRequestFlags(uint requestFlags);
    bool Query(bool wait);
    uint GetNumResults();
    uint GetTotResults();
    uint GetLastError();
    bool IsFileResult(uint index);
    uint GetResultFullPathName(uint index, StringBuilder buffer, uint maximumCount);
    bool GetResultSize(uint index, out long sizeBytes);
    string ErrorMessage(uint code);
}

internal sealed class EverythingSdkOperations : IEverythingSdkOperations
{
    public object SyncRoot => EverythingSdk.Lock;

    public bool IsDBLoaded() => EverythingSdk.IsDBLoaded();
    public void Reset() => EverythingSdk.Reset();
    public void SetSearch(string search) => EverythingSdk.SetSearch(search);
    public void SetMatchPath(bool enabled) => EverythingSdk.SetMatchPath(enabled);
    public void SetMatchCase(bool enabled) => EverythingSdk.SetMatchCase(enabled);
    public void SetOffset(uint offset) => EverythingSdk.SetOffset(offset);
    public void SetMax(uint maximumResults) => EverythingSdk.SetMax(maximumResults);
    public void SetRequestFlags(uint requestFlags) => EverythingSdk.SetRequestFlags(requestFlags);
    public bool Query(bool wait) => EverythingSdk.Query(wait);
    public uint GetNumResults() => EverythingSdk.GetNumResults();
    public uint GetTotResults() => EverythingSdk.GetTotResults();
    public uint GetLastError() => EverythingSdk.GetLastError();
    public bool IsFileResult(uint index) => EverythingSdk.IsFileResult(index);
    public uint GetResultFullPathName(uint index, StringBuilder buffer, uint maximumCount) =>
        EverythingSdk.GetResultFullPathName(index, buffer, maximumCount);
    public bool GetResultSize(uint index, out long sizeBytes) => EverythingSdk.GetResultSize(index, out sizeBytes);
    public string ErrorMessage(uint code) => EverythingSdk.ErrorMessage(code);
}