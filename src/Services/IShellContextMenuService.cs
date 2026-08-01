using EverythingDiskUsage.Native;

namespace EverythingDiskUsage.Services;

public interface IShellContextMenuService
{
    ShellContextMenuResult Show(
        IntPtr owner,
        string path,
        int screenX,
        int screenY,
        Func<ShellContextMenuCommand, bool>? shouldInvokeShell = null);
}
