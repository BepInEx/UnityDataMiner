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

    public override bool TryParseValue<T>(string value, out T result)
    {
        try
        {
            if (typeof(T) == typeof(DateTimeOffset))
            {
                result = (T)(object)DateTimeOffset.Parse(value);
                return true;
            }
            
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