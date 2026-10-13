using System.Security.Principal;

namespace Shadow.Core;

internal static class ElevationHelper
{
    private static bool? _isElevated;

    /// <summary>当前进程是否运行在管理员完整性级别。</summary>
    internal static bool IsElevated
    {
        get
        {
            if (_isElevated is null)
            {
                try
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    _isElevated = new WindowsPrincipal(identity)
                        .IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    _isElevated = false;
                }
            }
            return _isElevated.Value;
        }
    }
}
