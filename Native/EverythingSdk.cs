using System.Runtime.InteropServices;
using System.Text;

namespace EverythingDiskUsage.Native;

internal static class EverythingSdk
{
    private const string DllName = "Everything64.dll";

    internal static readonly object Lock = new();

    internal const uint EVERYTHING_OK = 0;
    internal const uint EVERYTHING_ERROR_MEMORY = 1;
    internal const uint EVERYTHING_ERROR_IPC = 2;
    internal const uint EVERYTHING_ERROR_REGISTERCLASSEX = 3;
    internal const uint EVERYTHING_ERROR_CREATEWINDOW = 4;
    internal const uint EVERYTHING_ERROR_CREATETHREAD = 5;
    internal const uint EVERYTHING_ERROR_INVALIDINDEX = 6;
    internal const uint EVERYTHING_ERROR_INVALIDCALL = 7;
    internal const uint EVERYTHING_ERROR_INVALIDREQUEST = 8;
    internal const uint EVERYTHING_ERROR_INVALIDPARAMETER = 9;

    internal const uint EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME = 0x00000004;
    internal const uint EVERYTHING_REQUEST_SIZE = 0x00000010;
    internal const uint EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;
    internal const uint EVERYTHING_REQUEST_DATE_ACCESSED = 0x00000080;

    [DllImport(DllName, EntryPoint = "Everything_SetSearchW", CharSet = CharSet.Unicode)]
    internal static extern void SetSearch(string lpSearchString);

    [DllImport(DllName, EntryPoint = "Everything_SetMatchPath")]
    internal static extern void SetMatchPath(bool bEnable);

    [DllImport(DllName, EntryPoint = "Everything_SetMatchCase")]
    internal static extern void SetMatchCase(bool bEnable);

    [DllImport(DllName, EntryPoint = "Everything_SetMax")]
    internal static extern void SetMax(uint dwMax);

    [DllImport(DllName, EntryPoint = "Everything_SetOffset")]
    internal static extern void SetOffset(uint dwOffset);

    [DllImport(DllName, EntryPoint = "Everything_SetRequestFlags")]
    internal static extern void SetRequestFlags(uint dwRequestFlags);

    [DllImport(DllName, EntryPoint = "Everything_QueryW", CharSet = CharSet.Unicode)]
    internal static extern bool Query(bool bWait);

    [DllImport(DllName, EntryPoint = "Everything_GetNumResults")]
    internal static extern uint GetNumResults();

    [DllImport(DllName, EntryPoint = "Everything_GetTotResults")]
    internal static extern uint GetTotResults();

    [DllImport(DllName, EntryPoint = "Everything_GetLastError")]
    internal static extern uint GetLastError();

    [DllImport(DllName, EntryPoint = "Everything_IsFileResult")]
    internal static extern bool IsFileResult(uint nIndex);

    [DllImport(DllName, EntryPoint = "Everything_GetResultFullPathNameW", CharSet = CharSet.Unicode)]
    internal static extern uint GetResultFullPathName(uint nIndex, StringBuilder lpString, uint nMaxCount);

    [DllImport(DllName, EntryPoint = "Everything_GetResultSize")]
    internal static extern bool GetResultSize(uint nIndex, out long lpSize);

    [DllImport(DllName, EntryPoint = "Everything_GetResultDateModified")]
    internal static extern bool GetResultDateModified(uint nIndex, out long lpFileTime);

    [DllImport(DllName, EntryPoint = "Everything_GetResultDateAccessed")]
    internal static extern bool GetResultDateAccessed(uint nIndex, out long lpFileTime);

    [DllImport(DllName, EntryPoint = "Everything_Reset")]
    internal static extern void Reset();

    [DllImport(DllName, EntryPoint = "Everything_CleanUp")]
    internal static extern void CleanUp();

    [DllImport(DllName, EntryPoint = "Everything_IsDBLoaded")]
    internal static extern bool IsDBLoaded();

    internal static string ErrorMessage(uint code) => code switch
    {
        EVERYTHING_OK => "OK",
        EVERYTHING_ERROR_MEMORY => "Out of memory",
        EVERYTHING_ERROR_IPC => "Everything is not running",
        EVERYTHING_ERROR_REGISTERCLASSEX => "Unable to register window class",
        EVERYTHING_ERROR_CREATEWINDOW => "Unable to create listening window",
        EVERYTHING_ERROR_CREATETHREAD => "Unable to create listening thread",
        EVERYTHING_ERROR_INVALIDINDEX => "Invalid index",
        EVERYTHING_ERROR_INVALIDCALL => "Invalid call",
        EVERYTHING_ERROR_INVALIDREQUEST => "Invalid request data",
        EVERYTHING_ERROR_INVALIDPARAMETER => "Invalid parameter",
        _ => $"Unknown error ({code})"
    };
}