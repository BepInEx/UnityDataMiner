using System;
using Microsoft.Extensions.Logging;
using Microsoft.SyndicationFeed.Rss;

namespace UnityDataMiner;

/// <summary>
///     Minimal safe RSS parser that is able to read RSS feeds that don't fully conform to the RSS 2.0 specification.
///     If a value cannot be parsed, it the default value for the type is used.
/// </summary>
internal class SafeRssParser : RssParser
{
    private readonly ILogger _logger;

    public SafeRssParser(ILogger logger)
    {
        _logger = logger;
    }

    // Right now this is mainly for DateTimes, since Unity feed doesn't use proper RFC3339 format.
    // We don't use dates so right now we just ignore them.
    // TODO: Parse date properly even if doesn't conform to RFC3339.
    public override bool TryParseValue<T>(string value, out T result)
    {
        try
        {
            return base.TryParseValue(value, out result);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to parse RSS value {Value}: {Message}", value, e.Message);
            result = default;
            return false;
        }
    }
}