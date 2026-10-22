using System.Globalization;

namespace Qyl.Telemetry.AutoInstrumentation.Internal;

internal sealed class QylCapturedNameMap
{
    private static readonly QylCapturedNameMap Empty = new([], []);

    private readonly string[] _lookupNames;
    private readonly string[] _tagNames;

    private QylCapturedNameMap(string[] lookupNames, string[] tagNames)
    {
        _lookupNames = lookupNames;
        _tagNames = tagNames;
    }

    internal int Count => _lookupNames.Length;

    internal string GetLookupName(int index) => _lookupNames[index];

    internal string GetTagName(int index) => _tagNames[index];

    internal static QylCapturedNameMap Create(string prefix, string[] configuredNames, bool normalizeLookupName = false)
    {
        if (configuredNames.Length is 0)
            return Empty;

        var entries = new Dictionary<string, string>(configuredNames.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var configuredName in configuredNames)
        {
            var trimmedName = configuredName.Trim();
            if (trimmedName.Length is 0)
                continue;

            var normalizedName = NormalizeName(trimmedName);
            var lookupName = normalizeLookupName ? normalizedName : trimmedName;
            entries[lookupName] = prefix + normalizedName;
        }

        if (entries.Count is 0)
            return Empty;

        return new QylCapturedNameMap([.. entries.Keys], [.. entries.Values]);
    }

    private static string NormalizeName(string name)
        => name.ToLower(CultureInfo.InvariantCulture);
}
