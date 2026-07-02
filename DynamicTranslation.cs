using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WKTranslator;

// The code for this is imported over from the UltrakULL ReFORKED mod for ULTRAKILL,
// if it looks sloppy it is because
// I wasn't as experienced as I am now when I wrote this but it still works

public static class DynamicTranslation
{
    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static readonly List<(Regex Matcher, string Template)> _entries = new();

    public static int Count => _entries.Count;

    public static void Clear() => _entries.Clear();

    public static bool TryRegister(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || !PlaceholderPattern.IsMatch(key)) return false;

        var pattern = new StringBuilder("^");
        int lastIndex = 0;

        foreach (Match m in PlaceholderPattern.Matches(key))
        {
            pattern.Append(Regex.Escape(key.Substring(lastIndex, m.Index - lastIndex)));

            int argIndex = int.Parse(m.Groups[1].Value);
            pattern.Append($"(?<arg{argIndex}>.+?)");

            lastIndex = m.Index + m.Length;
        }

        pattern.Append(Regex.Escape(key.Substring(lastIndex)));
        pattern.Append('$');

        Regex matcher;
        try
        {
            matcher = new Regex(pattern.ToString(), RegexOptions.Compiled | RegexOptions.Singleline);
        }
        catch (Exception e)
        {
            LogManager.Error($"Failed to build dynamic translation pattern for '{key}': {e.Message}");
            return false;
        }

        _entries.Add((matcher, value));
        return true;
    }

    public static bool TryTranslate(string original, out string translated)
    {
        translated = null;
        if (string.IsNullOrEmpty(original)) return false;

        foreach (var (matcher, template) in _entries)
        {
            var match = matcher.Match(original);
            if (!match.Success) continue;

            translated = PlaceholderPattern.Replace(template, m =>
            {
                var group = match.Groups[$"arg{m.Groups[1].Value}"];
                return group.Success ? group.Value : m.Value;
            });

            return true;
        }

        return false;
    }
}