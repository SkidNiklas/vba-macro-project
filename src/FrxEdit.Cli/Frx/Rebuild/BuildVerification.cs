/// <summary>
/// Read-back checks run after a form has been rebuilt. The expected control set is derived from the
/// source form and the patch, not from the patched layout, so a bug in the patch applier is caught too.
/// </summary>
internal static partial class BuildVerification
{
    private static readonly string[] LayoutProperties = ["left", "top", "width", "height"];

    private static readonly HashSet<string> LocationProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "recordIndex", "recordDelta", "recordBlock", "objectStreamLength", "objectStreamConsumedBytes", "OleObjectBlob", "recordMarkerHex",
    };

    /// <summary>Totals for a whole container, which change when a control in it does.</summary>
    private static readonly HashSet<string> ContainerProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "nextAvailableId", "siteDataCountOfSites", "siteDataCountOfBytes", "classTableCount",
    };

    private static readonly string[] BookkeepingPrefixes = ["cb", "propMask", "textProps", "objectStream", "siteObjectStream", "variousPropertyBits", "sitePropMask", "siteBitFlags", "siteName", "name"];

    private static readonly string[] BookkeepingSuffixes = ["ByteCount", "Compressed", "CountRaw"];

    private static readonly Dictionary<string, bool> SiteFlagDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tabStop"] = true,
        ["visible"] = true,
        ["default"] = false,
        ["cancel"] = false,
    };

    public static void ThrowIfControlsDiffer(
        IEnumerable<string> sourceNames,
        PatchDocument? patch,
        IEnumerable<string> rebuiltNames)
    {
        var expected = ExpectedControlNames(sourceNames, patch);
        var expectedCounts = CountNames(expected);
        var rebuilt = rebuiltNames.ToList();
        var rebuiltCounts = CountNames(rebuilt);

        var missing = ExcessNames(expectedCounts, rebuiltCounts);
        var unexpected = ExcessNames(rebuiltCounts, expectedCounts);
        if (missing.Count == 0 && unexpected.Count == 0)
        {
            return;
        }

        throw new CliException(
            $"Verification failed: expected {expected.Count} controls, read back {rebuilt.Count}. " +
            $"Missing: [{string.Join(", ", missing)}]. Unexpected: [{string.Join(", ", unexpected)}]. No output was written.");
    }

    /// <summary>Controls found by guessing names from raw bytes, because the parser could not read their stream.</summary>
    public static IReadOnlyList<string> FindGuessedControls(LayoutInspection layout) =>
        layout.Controls
            .Where(control => control.Properties is null || control.Properties.ContainsKey("legacyScanner"))
            .Select(control => control.Name)
            .ToList();

    /// <summary>Guessed names and sizes can't be trusted, so a form that needs them must not be rebuilt.</summary>
    public static void ThrowIfControlsAreGuessed(LayoutInspection layout)
    {
        var guessed = FindGuessedControls(layout);
        if (guessed.Count > 0)
        {
            throw new CliException(
                $"This form can't be rebuilt safely: the parser could not read {guessed.Count} controls, and their names were guessed from raw bytes: " +
                $"[{string.Join(", ", guessed)}]. No output was written.");
        }
    }

    /// <summary>Differences that mean a patched value, or a control's place in the form, did not survive the rebuild.</summary>
    public static IReadOnlyList<string> FindFailedChanges(PatchDocument? patch, IEnumerable<string> differences)
    {
        var failed = new List<string>();
        foreach (var difference in differences)
        {
            if (difference.StartsWith("missing rebuilt control:", StringComparison.Ordinal))
            {
                failed.Add(difference);
                continue;
            }

            var match = PropertyDifferenceRegex().Match(difference);
            if (match.Success && patch is not null && IsPatched(patch, match.Groups["name"].Value, match.Groups["property"].Value))
            {
                failed.Add(difference);
            }
        }

        return failed;
    }

    /// <summary>
    /// Flags in a patch that the rebuilt form doesn't have. A site without a BitFlags field has the default flags,
    /// and the rewriter can only write to a field that exists, so a change to such a site would otherwise be lost.
    /// </summary>
    public static IReadOnlyList<string> FindLostSiteFlags(PatchDocument? patch, IEnumerable<ControlInfo> rebuilt)
    {
        var lost = new List<string>();
        if (patch?.Properties is null)
        {
            return lost;
        }

        var controls = rebuilt.ToLookup(control => control.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, properties) in patch.Properties)
        {
            var finalName = patch.Renames is not null && patch.Renames.TryGetValue(name, out var newName) ? newName : name;
            var control = controls[finalName].FirstOrDefault();
            if (control is null)
            {
                continue;
            }

            foreach (var (property, value) in properties)
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !SiteFlagDefaults.TryGetValue(property, out var defaultValue))
                {
                    continue;
                }

                var actual = control.Properties?.TryGetValue(property, out var read) is true && read is bool flag ? flag : defaultValue;
                if (actual != value.GetBoolean())
                {
                    lost.Add($"{finalName}.{property}: asked for {value.GetBoolean()}, read back {actual}. This control has no BitFlags field to write to.");
                }
            }
        }

        return lost;
    }

    /// <summary>
    /// Properties that were read from the source but differ in the rebuilt form, without the patch asking for it.
    /// Nothing may differ in a build without changes. A patched control may differ only in what it was patched
    /// with and in the bookkeeping that follows from that.
    /// </summary>
    public static IReadOnlyList<string> FindUnrequestedChanges(LayoutInspection target, LayoutInspection rebuilt, PatchDocument? patch)
    {
        var differences = new List<string>();
        var rebuiltByName = rebuilt.Controls.ToLookup(control => control.Name, StringComparer.OrdinalIgnoreCase);
        var added = (patch?.Add ?? []).Select(control => control.Name ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = patch?.Move?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var control in target.Controls)
        {
            var other = rebuiltByName[control.Name].FirstOrDefault();
            if (other is null || added.Contains(control.Name) || moved.Contains(control.Name))
            {
                continue;
            }

            AddPropertyDifferences(control.Name, control.Properties, other.Properties, patch, RequestedProperties(patch, control.Name), differences);
        }

        if (patch is null)
        {
            AddPropertyDifferences("<form>", target.FrxFormControl, rebuilt.FrxFormControl, patch, null, differences);
        }

        return differences;
    }

    private static HashSet<string>? RequestedProperties(PatchDocument? patch, string name)
    {
        if (patch is null)
        {
            return null;
        }

        var originalName = patch.Renames?.FirstOrDefault(pair => pair.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Key ?? name;
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isPatched = patch.Layout?.ContainsKey(originalName) == true || patch.Renames?.ContainsKey(originalName) == true;
        foreach (var key in new[] { originalName, name })
        {
            if (patch.Properties?.TryGetValue(key, out var properties) == true)
            {
                requested.UnionWith(properties.Keys);
                isPatched = true;
            }
        }

        return isPatched ? requested : null;
    }

    private static void AddPropertyDifferences(
        string name,
        Dictionary<string, object?>? expected,
        Dictionary<string, object?>? actual,
        PatchDocument? patch,
        HashSet<string>? requested,
        List<string> differences)
    {
        var keys = (expected?.Keys ?? Enumerable.Empty<string>()).Union(actual?.Keys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (IsLocation(key) || (patch is not null && ContainerProperties.Contains(key)) || FollowsFromPatch(key, requested))
            {
                continue;
            }

            var expectedJson = ComparableJson(expected?.GetValueOrDefault(key));
            var actualJson = ComparableJson(actual?.GetValueOrDefault(key));
            if (expectedJson != actualJson)
            {
                differences.Add($"{name}.{key}: {expectedJson} != {actualJson}");
            }
        }
    }

    /// <summary>Where a value lies in the file. It moves when anything before it changes.</summary>
    private static bool IsLocation(string key) =>
        key.Contains("offset", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("Span", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("StreamIndex", StringComparison.OrdinalIgnoreCase) ||
        LocationProperties.Contains(key);

    private static bool FollowsFromPatch(string key, HashSet<string>? requested)
    {
        if (requested is null)
        {
            return false;
        }

        if (BookkeepingPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            BookkeepingSuffixes.Any(suffix => key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // A patched caption also changes captionCount, and a patched font bit changes FontEffects.
        return requested.Any(property => key.StartsWith(property, StringComparison.OrdinalIgnoreCase)) ||
               (key.StartsWith("font", StringComparison.OrdinalIgnoreCase) &&
                requested.Any(property => property.StartsWith("font", StringComparison.OrdinalIgnoreCase)));
    }

    private static string ComparableJson(object? value) =>
        JsonSerializer.Serialize(WithoutLocations(value), FrxEditApp.JsonOptions);

    private static object? WithoutLocations(object? value) => value switch
    {
        IDictionary<string, object?> dictionary => dictionary
            .Where(pair => !IsLocation(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => WithoutLocations(pair.Value)),
        string => value,
        byte[] => value,
        System.Collections.IEnumerable items => items.Cast<object?>().Select(WithoutLocations).ToList(),
        _ => value,
    };

    private static List<string> ExpectedControlNames(IEnumerable<string> sourceNames, PatchDocument? patch)
    {
        var removed = new HashSet<string>(patch?.Remove ?? [], StringComparer.OrdinalIgnoreCase);
        var expected = new List<string>();
        foreach (var name in sourceNames)
        {
            if (removed.Contains(name))
            {
                continue;
            }

            expected.Add(patch?.Renames is not null && patch.Renames.TryGetValue(name, out var newName) ? newName : name);
        }

        expected.AddRange((patch?.Add ?? []).Select(control => control.Name ?? string.Empty));
        return expected;
    }

    private static bool IsPatched(PatchDocument patch, string finalName, string property)
    {
        var originalName = patch.Renames?
            .FirstOrDefault(pair => pair.Value.Equals(finalName, StringComparison.OrdinalIgnoreCase)).Key ?? finalName;

        if (LayoutProperties.Contains(property, StringComparer.OrdinalIgnoreCase) &&
            patch.Layout?.ContainsKey(originalName) is true)
        {
            return true;
        }

        return patch.Properties?.TryGetValue(originalName, out var properties) is true &&
               properties.ContainsKey(property);
    }

    private static Dictionary<string, int> CountNames(IEnumerable<string> names)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        return counts;
    }

    private static List<string> ExcessNames(Dictionary<string, int> counts, Dictionary<string, int> other)
    {
        var excess = new List<string>();
        foreach (var (name, count) in counts)
        {
            excess.AddRange(Enumerable.Repeat(name, Math.Max(0, count - other.GetValueOrDefault(name))));
        }

        excess.Sort(StringComparer.OrdinalIgnoreCase);
        return excess;
    }

    // Format from RebuildComparison: "<parent>/<name>:<type>.<property>: <expected> != <actual>"
    [GeneratedRegex(@"^.*?/(?<name>[^/:]+):[^.]+\.(?<property>\w+):")]
    private static partial Regex PropertyDifferenceRegex();
}
