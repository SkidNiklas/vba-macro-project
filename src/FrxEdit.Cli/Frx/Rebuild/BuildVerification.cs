/// <summary>
/// Read-back checks run after a form has been rebuilt. The expected control set is derived from the
/// source form and the patch, not from the patched layout, so a bug in the patch applier is caught too.
/// </summary>
internal static partial class BuildVerification
{
    private static readonly string[] LayoutProperties = ["left", "top", "width", "height"];

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
