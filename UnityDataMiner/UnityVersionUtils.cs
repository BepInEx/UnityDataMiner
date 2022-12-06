using AssetRipper.VersionUtilities;

namespace UnityDataMiner;

internal static class UnityVersionUtils
{
    public static bool TryParse(string version, out UnityVersion result)
    {
        try
        {
            result = UnityVersion.Parse(version);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }
}