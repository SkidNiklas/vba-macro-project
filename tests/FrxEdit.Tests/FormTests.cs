using Xunit;

namespace FrxEdit.Tests;

/// <summary>Runs against every form in FRXEDIT_TESTFORMS. Skipped when the variable is not set.</summary>
public class FormTests
{
    /// <summary>Excel and frxedit round positions differently, so points are compared with this margin.</summary>
    private const double PointTolerance = 0.1;

    private const string AddedButton = "CmdFrxEditTest";

    [Theory]
    [MemberData(nameof(TestForms.All), MemberType = typeof(TestForms))]
    public void RoundTrip_without_changes_keeps_layout_and_code(string form)
    {
        using var workspace = new Workspace();
        var source = workspace.CopyForm(TestForms.PathOf(form));
        var output = workspace.PathOf($"out/{form}.frm");

        var result = workspace.Run("build", source, "--out", output, "--mode", "tolerant");

        Assert.True(result.ExitCode == 0, result.Describe());
        AssertNoDifferences(CompareLayouts(workspace.ReadControls(source), workspace.ReadControls(output)));
        Assert.Equal(Workspace.ReadCode(source), Workspace.ReadCode(output));
    }

    [Theory]
    [MemberData(nameof(TestForms.All), MemberType = typeof(TestForms))]
    public void AddCommandButton_adds_it_and_keeps_the_rest(string form)
    {
        using var workspace = new Workspace();
        var source = workspace.CopyForm(TestForms.PathOf(form));
        var output = workspace.PathOf($"out/{form}.frm");
        var patch = workspace.WritePatch("patch.json", $$"""
            { "properties": { "{{AddedButton}}": { "$action": "add", "type": "CommandButton", "caption": "Test", "leftPt": 2, "topPt": 2, "widthPt": 60, "heightPt": 20 } } }
            """);

        var result = workspace.Run("build", source, patch, "--out", output, "--mode", "tolerant");

        Assert.True(result.ExitCode == 0, result.Describe());
        var rebuilt = workspace.ReadControls(output);
        var button = Assert.Single(rebuilt, control => control.Name == AddedButton);
        Assert.Equal("CommandButton", button.Type);
        Assert.Null(button.Parent);
        Assert.Equal(60, button.WidthPt!.Value, PointTolerance);
        Assert.Equal(20, button.HeightPt!.Value, PointTolerance);
        AssertNoDifferences(CompareLayouts(workspace.ReadControls(source), rebuilt.Where(control => control.Name != AddedButton).ToList()));
        Assert.Equal(Workspace.ReadCode(source), Workspace.ReadCode(output));
    }

    [Theory]
    [MemberData(nameof(TestForms.All), MemberType = typeof(TestForms))]
    public void Inspect_output_is_accepted_as_a_patch(string form)
    {
        using var workspace = new Workspace();
        var source = workspace.CopyForm(TestForms.PathOf(form));
        var inspected = workspace.PathOf("inspect/layout.json");
        Directory.CreateDirectory(Path.GetDirectoryName(inspected)!);
        Assert.Equal(0, workspace.Run("inspect", source, "--as-patch", "--mode", "tolerant", "--out", inspected).ExitCode);

        var result = workspace.Run("build", source, inspected, "--out", workspace.PathOf($"out/{form}.frm"), "--mode", "tolerant");

        Assert.DoesNotContain("Unknown or unsupported property", result.Error);
    }

    [Theory]
    [MemberData(nameof(TestForms.All), MemberType = typeof(TestForms))]
    public void Inspect_matches_what_Excel_reports(string form)
    {
        var formPath = TestForms.PathOf(form);
        var expected = Facit.ControlsOf(form);
        if (expected is null)
        {
            Assert.Skip($"{Facit.FacitVariable} is not set, or has no entry for this form.");
        }

        using var workspace = new Workspace();
        var actual = workspace.ReadControls(workspace.CopyForm(formPath));

        AssertNoDifferences(CompareWithFacit(expected, actual));
    }

    private static List<string> CompareLayouts(IReadOnlyList<ControlRecord> expected, IReadOnlyList<ControlRecord> actual)
    {
        var differences = NameDifferences(expected.Select(control => control.Name), actual.Select(control => control.Name));
        foreach (var control in expected)
        {
            var other = actual.FirstOrDefault(candidate => candidate.Name.Equals(control.Name, StringComparison.OrdinalIgnoreCase));
            if (other is null)
            {
                continue;
            }

            AddIfDifferent(differences, control.Name, "type", control.Type, other.Type);
            AddIfDifferent(differences, control.Name, "parent", control.Parent, other.Parent);
            AddIfDifferent(differences, control.Name, "left", control.Left, other.Left);
            AddIfDifferent(differences, control.Name, "top", control.Top, other.Top);
            AddIfDifferent(differences, control.Name, "width", control.RawWidth, other.RawWidth);
            AddIfDifferent(differences, control.Name, "height", control.RawHeight, other.RawHeight);
        }

        return differences;
    }

    private static List<string> CompareWithFacit(IReadOnlyList<FacitControl> expected, IReadOnlyList<ControlRecord> actual)
    {
        var differences = NameDifferences(expected.Select(control => control.Name), actual.Select(control => control.Name));
        foreach (var control in expected)
        {
            var other = actual.FirstOrDefault(candidate => candidate.Name.Equals(control.Name, StringComparison.OrdinalIgnoreCase));
            if (other is null)
            {
                continue;
            }

            if (!control.Type.Equals(other.Type, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add($"{control.Name}.type: Excel {control.Type}, frxedit {other.Type}");
            }

            if (!string.Equals(control.Parent, other.Parent, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add($"{control.Name}.parent: Excel {control.Parent ?? "(form)"}, frxedit {other.Parent ?? "(form)"}");
            }

            AddIfOutsideTolerance(differences, control.Name, "left", control.Left, other.LeftPt);
            AddIfOutsideTolerance(differences, control.Name, "top", control.Top, other.TopPt);
            AddIfOutsideTolerance(differences, control.Name, "width", control.Width, other.WidthPt);
            AddIfOutsideTolerance(differences, control.Name, "height", control.Height, other.HeightPt);
        }

        return differences;
    }

    private static List<string> NameDifferences(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedNames = expected.ToList();
        var actualNames = actual.ToList();
        var differences = new List<string>();
        differences.AddRange(expectedNames.Except(actualNames, StringComparer.OrdinalIgnoreCase).Select(name => $"missing: {name}"));
        differences.AddRange(actualNames.Except(expectedNames, StringComparer.OrdinalIgnoreCase).Select(name => $"unexpected: {name}"));
        if (expectedNames.Count != actualNames.Count)
        {
            differences.Add($"count: expected {expectedNames.Count}, got {actualNames.Count}");
        }

        return differences;
    }

    private static void AddIfDifferent<T>(List<string> differences, string control, string property, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            differences.Add($"{control}.{property}: expected {expected}, got {actual}");
        }
    }

    private static void AddIfOutsideTolerance(List<string> differences, string control, string property, double expected, double? actual)
    {
        if (actual is null || Math.Abs(expected - actual.Value) > PointTolerance)
        {
            differences.Add($"{control}.{property}: Excel {expected}, frxedit {actual?.ToString() ?? "unknown"}");
        }
    }

    private static void AssertNoDifferences(List<string> differences) =>
        Assert.True(differences.Count == 0, $"{differences.Count} differences:{Environment.NewLine}{string.Join(Environment.NewLine, differences)}");
}
