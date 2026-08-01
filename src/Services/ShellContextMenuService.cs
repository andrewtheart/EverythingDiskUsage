using EverythingDiskUsage.Native;
using System.Diagnostics;

namespace EverythingDiskUsage.Services;

public sealed class ShellContextMenuService : IShellContextMenuService
{
    private readonly IAppLogger _logger;

    public ShellContextMenuService()
        : this(new AppLoggerAdapter())
    {
    }

    public ShellContextMenuService(IAppLogger logger)
    {
        _logger = logger;
    }

    public ShellContextMenuResult Show(
        IntPtr owner,
        string path,
        int screenX,
        int screenY,
        Func<ShellContextMenuCommand, bool>? shouldInvokeShell = null)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.Info($"Shell context menu service request; owner=0x{owner.ToInt64():X}; path='{path}'; x={screenX}; y={screenY}; hasInvokeFilter={shouldInvokeShell is not null}");
        try
        {
            var result = ShellContextMenu.Show(owner, path, screenX, screenY, shouldInvokeShell);
            stopwatch.Stop();
            _logger.Info($"Shell context menu service completed; path='{path}'; commandSelected={result.CommandSelected}; verb='{result.Verb}'; menuText='{result.MenuText}'; shellInvoked={result.ShellInvoked}; elapsedMs={stopwatch.ElapsedMilliseconds}");
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error($"Shell context menu service failed; path='{path}'; elapsedMs={stopwatch.ElapsedMilliseconds}", ex);
            throw;
        }
    }
}
