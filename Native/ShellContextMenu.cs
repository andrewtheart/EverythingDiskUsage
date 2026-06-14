using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace EverythingDiskUsage.Native;

public sealed record ShellContextMenuCommand(string Path, string Verb, string MenuText, uint CommandId);

public sealed record ShellContextMenuResult(bool CommandSelected, string Verb, string MenuText, uint CommandId, bool ShellInvoked)
{
    public static ShellContextMenuResult None { get; } = new(false, string.Empty, string.Empty, 0, false);
}

internal static class ShellContextMenu
{
    private const uint CmdFirst = 1;
    private const uint CmdLast = 0x7FFF;
    private const uint CmfNormal = 0x00000000;
    private const uint CmfExtendedVerbs = 0x00000100;
    private const uint GcsVerbW = 0x00000004;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint MfByCommand = 0x00000000;
    private const uint CmicMaskUnicode = 0x00004000;
    private const uint CmicMaskPtInvoke = 0x20000000;
    private const int SwShowNormal = 1;

    private static readonly Guid ShellFolderId = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenuId = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenu2Id = new("000214F4-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenu3Id = new("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719");

    public static ShellContextMenuResult Show(
        IntPtr owner,
        string path,
        int screenX,
        int screenY,
        Func<ShellContextMenuCommand, bool>? shouldInvokeShell = null)
    {
        var itemPidl = IntPtr.Zero;
        var childPidlArray = IntPtr.Zero;
        var contextMenuPtr = IntPtr.Zero;
        var contextMenu2Ptr = IntPtr.Zero;
        var contextMenu3Ptr = IntPtr.Zero;
        var menuHandle = IntPtr.Zero;
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;
        IContextMenu2? contextMenu2 = null;
        IContextMenu3? contextMenu3 = null;
        HwndSource? source = null;
        HwndSourceHook? hook = null;

        try
        {
            var parseResult = SHParseDisplayName(path, IntPtr.Zero, out itemPidl, 0, out _);
            if (parseResult != 0 || itemPidl == IntPtr.Zero)
            {
                throw Marshal.GetExceptionForHR(parseResult) ?? new InvalidOperationException($"Could not resolve shell item: {path}");
            }

            var bindResult = SHBindToParent(itemPidl, ShellFolderId, out parentFolder, out var childPidl);
            if (bindResult != 0 || parentFolder is null || childPidl == IntPtr.Zero)
            {
                throw Marshal.GetExceptionForHR(bindResult) ?? new InvalidOperationException($"Could not bind shell item parent: {path}");
            }

            childPidlArray = Marshal.AllocCoTaskMem(IntPtr.Size);
            Marshal.WriteIntPtr(childPidlArray, childPidl);

            var uiObjectResult = parentFolder.GetUIObjectOf(owner, 1, childPidlArray, ContextMenuId, IntPtr.Zero, out contextMenuPtr);
            if (uiObjectResult != 0 || contextMenuPtr == IntPtr.Zero)
            {
                throw Marshal.GetExceptionForHR(uiObjectResult) ?? new InvalidOperationException($"Could not create shell context menu: {path}");
            }

            contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
            contextMenu2 = TryGetContextMenu2(contextMenuPtr, out contextMenu2Ptr);
            contextMenu3 = TryGetContextMenu3(contextMenuPtr, out contextMenu3Ptr);

            menuHandle = CreatePopupMenu();
            if (menuHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create native popup menu.");
            }

            contextMenu.QueryContextMenu(menuHandle, 0, CmdFirst, CmdLast, CmfNormal | CmfExtendedVerbs);

            source = HwndSource.FromHwnd(owner);
            if (source is not null && (contextMenu2 is not null || contextMenu3 is not null))
            {
                hook = (IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    handled = HandleShellMenuMessage(contextMenu2, contextMenu3, msg, wParam, lParam);
                    return IntPtr.Zero;
                };
                source.AddHook(hook);
            }

            var selectedCommandId = TrackPopupMenuEx(menuHandle, TpmReturnCmd | TpmRightButton, screenX, screenY, owner, IntPtr.Zero);
            if (selectedCommandId == 0)
            {
                return ShellContextMenuResult.None;
            }

            if (source is not null && hook is not null)
            {
                source.RemoveHook(hook);
                hook = null;
            }

            var commandOffset = selectedCommandId - CmdFirst;
            var verb = GetCommandVerb(contextMenu, commandOffset);
            var menuText = GetCommandMenuText(menuHandle, selectedCommandId);
            var command = new ShellContextMenuCommand(path, verb, menuText, selectedCommandId);
            var invokeShell = shouldInvokeShell?.Invoke(command) ?? true;

            if (invokeShell)
            {
                var invokeInfo = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CmicMaskUnicode | CmicMaskPtInvoke,
                    hwnd = owner,
                    lpVerb = (IntPtr)commandOffset,
                    lpVerbW = (IntPtr)commandOffset,
                    nShow = SwShowNormal,
                    ptInvoke = new POINT { X = screenX, Y = screenY }
                };

                var invokeResult = contextMenu.InvokeCommand(ref invokeInfo);
                if (invokeResult != 0)
                {
                    throw Marshal.GetExceptionForHR(invokeResult) ?? new InvalidOperationException($"Shell command failed: {menuText}");
                }
            }

            return new ShellContextMenuResult(true, verb, menuText, selectedCommandId, invokeShell);
        }
        finally
        {
            if (source is not null && hook is not null)
            {
                source.RemoveHook(hook);
            }

            if (menuHandle != IntPtr.Zero)
            {
                DestroyMenu(menuHandle);
            }

            if (contextMenu3Ptr != IntPtr.Zero)
            {
                Marshal.Release(contextMenu3Ptr);
            }

            if (contextMenu2Ptr != IntPtr.Zero)
            {
                Marshal.Release(contextMenu2Ptr);
            }

            if (contextMenuPtr != IntPtr.Zero)
            {
                Marshal.Release(contextMenuPtr);
            }

            if (parentFolder is not null)
            {
                Marshal.ReleaseComObject(parentFolder);
            }

            if (childPidlArray != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(childPidlArray);
            }

            if (itemPidl != IntPtr.Zero)
            {
                CoTaskMemFree(itemPidl);
            }
        }
    }

    private static IContextMenu2? TryGetContextMenu2(IntPtr contextMenuPtr, out IntPtr contextMenu2Ptr)
    {
        var id = ContextMenu2Id;
        if (Marshal.QueryInterface(contextMenuPtr, in id, out contextMenu2Ptr) != 0 || contextMenu2Ptr == IntPtr.Zero)
        {
            return null;
        }

        return (IContextMenu2)Marshal.GetObjectForIUnknown(contextMenu2Ptr);
    }

    private static IContextMenu3? TryGetContextMenu3(IntPtr contextMenuPtr, out IntPtr contextMenu3Ptr)
    {
        var id = ContextMenu3Id;
        if (Marshal.QueryInterface(contextMenuPtr, in id, out contextMenu3Ptr) != 0 || contextMenu3Ptr == IntPtr.Zero)
        {
            return null;
        }

        return (IContextMenu3)Marshal.GetObjectForIUnknown(contextMenu3Ptr);
    }

    private static bool HandleShellMenuMessage(IContextMenu2? contextMenu2, IContextMenu3? contextMenu3, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (contextMenu3 is not null)
        {
            var result = contextMenu3.HandleMenuMsg2((uint)msg, wParam, lParam, out _);
            return result == 0;
        }

        if (contextMenu2 is not null)
        {
            var result = contextMenu2.HandleMenuMsg((uint)msg, wParam, lParam);
            return result == 0;
        }

        return false;
    }

    private static string GetCommandVerb(IContextMenu contextMenu, uint commandOffset)
    {
        var builder = new StringBuilder(260);
        var result = contextMenu.GetCommandString(commandOffset, GcsVerbW, IntPtr.Zero, builder, (uint)builder.Capacity);
        return result == 0 ? builder.ToString() : string.Empty;
    }

    private static string GetCommandMenuText(IntPtr menuHandle, uint commandId)
    {
        var builder = new StringBuilder(260);
        var length = GetMenuString(menuHandle, commandId, builder, builder.Capacity, MfByCommand);
        return length > 0 ? builder.ToString() : string.Empty;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv, out IntPtr ppidlLast);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int nMaxCount, uint uFlag);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr rgfReserved, out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);

        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(uint idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, uint cchMax);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    private interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(uint idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(uint idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }
}