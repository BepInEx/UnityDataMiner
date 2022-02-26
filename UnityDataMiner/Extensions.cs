using AssetRipper.VersionUtilities;

namespace UnityDataMiner;

public static class Extensions
{
    public static string GetDownloadUrl(this UnityVersion unityVersion)
    {
        return unityVersion.Type == UnityVersionType.Final ? "https://download.unity3d.com/download_unity/" : "https://beta.unity3d.com/download/";
    }

    private static readonly UnityVersion _firstModular = new(5, 3);

    public static bool IsMonolithic(this UnityVersion unityVersion)
    {
        return unityVersion < _firstModular;
    }
}
