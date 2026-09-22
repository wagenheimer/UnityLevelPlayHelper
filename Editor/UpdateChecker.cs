using Wagenheimer.PackageHub.Editor;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Opens the Package Hub on this package so the user can update it. Exposed as a button inside
    /// <see cref="LevelPlaySetupWindow"/> - it no longer has its own Tools menu item, so the
    /// LevelPlay entry point is a single menu.
    /// </summary>
    internal static class UpdateChecker
    {
        public static void CheckForUpdate(bool force = false)
        {
            PackageHubWindow.OpenToPackage("com.wagenheimer.levelplayhelper");
        }
    }
}
