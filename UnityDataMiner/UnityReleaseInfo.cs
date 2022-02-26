using System.Collections.Generic;
using System.Linq;
using AssetRipper.VersionUtilities;

namespace UnityDataMiner;

public record UnityBuildInfo(Dictionary<string, UnityBuildInfo.Module> Components)
{
    public record Module(string Title, string Url, UnityVersion? Version);

    public Module Unity => Components["Unity"];

    public Module? WindowsMono => Components.TryGetValue("Windows-Mono", out var result) || Components.TryGetValue("Windows", out result) ? result : null;
    public Module? Android => Components.TryGetValue("Android", out var result) ? result : null;

    public static UnityBuildInfo Parse(string ini)
    {
        var sections = IniParser.Parse(ini);

        return new UnityBuildInfo(sections.ToDictionary(
            kv => kv.Key,
            kv => new Module(
                kv.Value["title"],
                kv.Value["url"],
                kv.Value.ContainsKey("version") ? UnityVersion.Parse(kv.Value["version"]) : null
            )
        ));
    }
}
