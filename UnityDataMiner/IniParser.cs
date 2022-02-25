using System;
using System.Collections.Generic;

namespace UnityDataMiner;

public static class IniParser
{
    public static Dictionary<string, Dictionary<string, string>> Parse(string ini)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>();
        string? sectionName = null;
        Dictionary<string, string>? section = null;

        foreach (var line in ini.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line == string.Empty || line.StartsWith("Note:"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                if (sectionName != null && section != null)
                {
                    sections.Add(sectionName, section);
                }

                sectionName = line[1..^1];
                section = new Dictionary<string, string>();
            }
            else
            {
                var indexOf = line.IndexOf('=');
                if (indexOf == -1)
                {
                    throw new IniException($"Failed to parse line {line}");
                }

                if (section == null)
                {
                    throw new IniException($"Unexpected {line} without a section");
                }

                var name = line[..indexOf];
                var value = line[(indexOf + 1)..];

                section.Add(name, value);
            }
        }

        if (sectionName != null && section != null)
        {
            sections.Add(sectionName, section);
        }

        return sections;
    }

    public class IniException : Exception
    {
        public IniException(string? message) : base(message)
        {
        }
    }
}
