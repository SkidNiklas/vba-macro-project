using Xunit;

namespace FrxEdit.Tests;

/// <summary>What a rebuild must keep. Needs no external forms: uses the sample form and payloads built here.</summary>
public class FidelityTests
{
    private const uint SpecDefaultVariousBits = 0x2C80_081B;

    [Fact]
    public void Build_without_changes_keeps_every_stream_of_the_sample()
    {
        using var workspace = new Workspace();
        var output = workspace.PathOf("out/sample.frm");

        var result = workspace.Run("build", VerificationTests.SampleForm(), "--out", output, "--mode", "tolerant");

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.Empty(StreamComparer.Differences(VerificationTests.SampleForm(), output));
    }

    [Fact]
    public void Patching_one_control_keeps_the_bytes_of_the_others()
    {
        using var workspace = new Workspace();
        var output = workspace.PathOf("out/sample.frm");
        var patch = workspace.WritePatch("patch.json", """{ "properties": { "CommandButton1": { "caption": "Ny text" } } }""");

        var result = workspace.Run("build", VerificationTests.SampleForm(), patch, "--out", output, "--mode", "tolerant");

        Assert.True(result.ExitCode == 0, result.Describe());
        var changed = StreamComparer.Differences(VerificationTests.SampleForm(), output);
        Assert.Equal(["Root Entry/f", "Root Entry/o"], changed);
    }

    [Fact]
    public void Changing_only_the_tab_index_keeps_the_object_data()
    {
        using var workspace = new Workspace();
        var output = workspace.PathOf("out/sample.frm");
        var patch = workspace.WritePatch("patch.json", """{ "properties": { "CommandButton1": { "tabIndex": 3 } } }""");

        var result = workspace.Run("build", VerificationTests.SampleForm(), patch, "--out", output, "--mode", "tolerant");

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.Equal(["Root Entry/f"], StreamComparer.Differences(VerificationTests.SampleForm(), output));
    }

    [Theory]
    [InlineData(false, 400)]
    [InlineData(true, 700)]
    public void Patched_fontBold_decides_the_weight(bool bold, int expected)
    {
        using var workspace = new Workspace();
        var heavy = workspace.PathOf("heavy/sample.frm");
        var first = workspace.WritePatch("first.json", """{ "properties": { "CommandButton1": { "fontWeight": 700 } } }""");
        Assert.Equal(0, workspace.Run("build", VerificationTests.SampleForm(), first, "--out", heavy, "--mode", "tolerant").ExitCode);

        var output = workspace.PathOf("out/sample.frm");
        var second = workspace.WritePatch("second.json", $$"""{ "properties": { "CommandButton1": { "fontBold": {{bold.ToString().ToLowerInvariant()}} } } }""");
        var result = workspace.Run("build", heavy, second, "--out", output, "--mode", "tolerant");

        Assert.True(result.ExitCode == 0, result.Describe());
        var properties = workspace.ReadControls(output).Single(control => control.Name == "CommandButton1").Properties!;
        var weight = properties.TryGetValue("fontWeight", out var value) ? value.GetInt32() : 400;
        Assert.Equal(expected, weight);
    }

    [Fact]
    public void A_flat_check_box_stays_flat()
    {
        var properties = ReadMorph(new CheckBoxControlSchema(), "CheckBox", new() { ["specialEffect"] = 0 });

        Assert.Equal(0u, properties["specialEffect"]);
    }

    [Fact]
    public void A_check_box_with_the_default_effect_stores_no_effect()
    {
        var properties = ReadMorph(new CheckBoxControlSchema(), "CheckBox", new());

        Assert.False(properties.ContainsKey("specialEffect"));
    }

    [Theory]
    [InlineData("ComboBox")]
    [InlineData("ListBox")]
    public void A_list_that_stored_no_various_bits_stores_none_after_a_rebuild(string type)
    {
        var properties = ReadMorph(ListSchema(type), type, new() { ["propMask"] = "0x0000000080000100" });

        Assert.False(properties.ContainsKey("variousPropertyBitsRaw"));
    }

    [Theory]
    [InlineData("ComboBox")]
    [InlineData("ListBox")]
    public void A_list_without_various_bits_starts_from_the_documented_default(string type)
    {
        var properties = ReadMorph(ListSchema(type), type, new() { ["propMask"] = "0x0000000080000100", ["enabled"] = false });

        Assert.Equal(SpecDefaultVariousBits & ~(1u << 1), properties["variousPropertyBitsRaw"]);
    }

    [Theory]
    [InlineData("ComboBox", 0x2C80_081Bu)]
    [InlineData("ListBox", 0x0000_001Bu)]
    [InlineData("ComboBox", 0x8000_081Bu)]
    public void Stored_various_bits_are_kept(string type, uint stored)
    {
        var properties = ReadMorph(ListSchema(type), type, new() { ["propMask"] = "0x0000000080000101", ["variousPropertyBitsRaw"] = stored });

        Assert.Equal(stored, properties["variousPropertyBitsRaw"]);
    }

    [Fact]
    public void Column_count_field_is_written_for_a_list_with_column_info()
    {
        var properties = ReadMorph(new ListBoxControlSchema(), "ListBox", new() { ["cColumnInfo"] = 2, ["columnCount"] = 2 });

        Assert.Equal((ushort)2, properties["cColumnInfo"]);
    }

    [Fact]
    public void Unrequested_change_is_reported()
    {
        var layout = VerificationTests.SampleLayout();
        var control = layout.Controls.First(candidate => candidate.Name == "CommandButton1");
        var changed = new Dictionary<string, object?>(control.Properties!, StringComparer.OrdinalIgnoreCase) { ["foreColor"] = "&H00000001&" };
        var rebuilt = layout with { Controls = layout.Controls.Select(other => other == control ? other with { Properties = changed } : other).ToList() };

        var found = BuildVerification.FindUnrequestedChanges(layout, rebuilt, patch: null);

        Assert.Contains(found, difference => difference.Contains("CommandButton1.foreColor"));
    }

    [Fact]
    public void A_requested_change_is_not_reported()
    {
        var layout = VerificationTests.SampleLayout();
        var control = layout.Controls.First(candidate => candidate.Name == "CommandButton1");
        var changed = new Dictionary<string, object?>(control.Properties!, StringComparer.OrdinalIgnoreCase) { ["foreColor"] = "&H00000001&" };
        var rebuilt = layout with { Controls = layout.Controls.Select(other => other == control ? other with { Properties = changed } : other).ToList() };
        var patch = new PatchDocument
        {
            Properties = new(StringComparer.OrdinalIgnoreCase)
            {
                ["CommandButton1"] = new(StringComparer.OrdinalIgnoreCase) { ["foreColor"] = System.Text.Json.JsonDocument.Parse("\"#000001\"").RootElement },
            },
        };

        Assert.Empty(BuildVerification.FindUnrequestedChanges(layout, rebuilt, patch));
    }

    [Fact]
    public void Offsets_are_not_reported_as_changes()
    {
        var layout = VerificationTests.SampleLayout();
        var control = layout.Controls.First(candidate => candidate.Name == "CommandButton1");
        var changed = new Dictionary<string, object?>(control.Properties!, StringComparer.OrdinalIgnoreCase) { ["tabIndexOffset"] = 999999 };
        var rebuilt = layout with { Controls = layout.Controls.Select(other => other == control ? other with { Properties = changed } : other).ToList() };

        Assert.Empty(BuildVerification.FindUnrequestedChanges(layout, rebuilt, patch: null));
    }

    private static IGeneratedControlSchema ListSchema(string type) =>
        type == "ComboBox" ? new ComboBoxControlSchema() : new ListBoxControlSchema();

    private static Dictionary<string, object?> ReadMorph(IGeneratedControlSchema schema, string type, Dictionary<string, object?> properties)
    {
        var all = new Dictionary<string, object?>(properties, StringComparer.OrdinalIgnoreCase);
        var request = new GeneratedControlRequest(type, "Ctl", 1, 0, 0, 0, 1000, 500, null, null, all);
        var payload = schema.BuildObjectPayload(request);
        var stream = StorageEntryDump.CreateSegment(payload, new int[payload.Length]);
        return ObjectStreamParser.Read(stream, type).Properties;
    }
}

internal static class StreamComparer
{
    /// <summary>Paths of the streams that differ between two forms.</summary>
    public static List<string> Differences(string frmA, string frmB)
    {
        var a = Streams(frmA);
        var b = Streams(frmB);
        return a.Keys.Union(b.Keys)
            .Where(path => !a.TryGetValue(path, out var left) || !b.TryGetValue(path, out var right) || !left.AsSpan().SequenceEqual(right))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, byte[]> Streams(string frm)
    {
        var binary = FrxBinary.Read(Path.ChangeExtension(frm, ".frx"));
        return CompoundStorageInspector.Inspect(binary.Bytes, binary.OleOffset).Streams
            .Where(stream => stream.Kind == "Stream" && !string.IsNullOrEmpty(stream.Path))
            .ToDictionary(stream => stream.Path!, stream => stream.Data, StringComparer.OrdinalIgnoreCase);
    }
}
