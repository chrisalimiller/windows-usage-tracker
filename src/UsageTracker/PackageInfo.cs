using System.Runtime.InteropServices;
using System.Text;

namespace UsageTracker;

/// <summary>
/// Whether the app runs from an MSIX package (the Microsoft Store version) or as a plain exe.
/// A packaged app sees a private, redirected copy of %LOCALAPPDATA% and its own registry
/// hive, so a few things (start with Windows, the log folder) work differently there.
/// </summary>
internal static class PackageInfo
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly Lazy<string?> Family = new(ReadFamilyName);

    public static bool IsPackaged => Family.Value is not null;

    /// <summary>The package family name, or null for the plain exe.</summary>
    public static string? FamilyName => Family.Value;

    /// <summary>
    /// The package's own data folder as Explorer sees it (%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalState),
    /// or null for the plain exe.
    /// </summary>
    public static string? LocalStateFolder => Family.Value is { } family
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family, "LocalState")
        : null;

    private static string? ReadFamilyName()
    {
        try
        {
            var length = 0;
            var result = GetCurrentPackageFamilyName(ref length, null);
            if (result == AppModelErrorNoPackage || result != ErrorInsufficientBuffer || length <= 0) return null;
            var buffer = new StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, buffer) == 0 ? buffer.ToString() : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, StringBuilder? packageFamilyName);
}
