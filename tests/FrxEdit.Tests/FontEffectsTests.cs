using Xunit;

namespace FrxEdit.Tests;

/// <summary>FontEffects holds bits that frxedit doesn't interpret, so they must survive a rebuild. Needs no external forms.</summary>
public class FontEffectsTests
{
    private const uint BoldAndUnknownBits = 0x4000_2001;
    private const uint Bold = 1u << 0;
    private const uint Italic = 1u << 1;

    [Theory]
    [InlineData(0x4000_0001u, 700)]
    [InlineData(0x4000_0001u, 600)]
    [InlineData(0x4000_2001u, 700)]
    [InlineData(0x4000_2000u, null)]
    [InlineData(0xC000_2001u, 700)]
    [InlineData(0u, null)]
    public void Unchanged_effects_are_written_back(uint effects, int? weight)
    {
        var properties = Read(Build(new() { ["fontEffects"] = effects, ["fontWeight"] = weight }));

        Assert.Equal(effects, properties["fontEffects"]);
    }

    [Fact]
    public void Patched_italic_changes_only_its_own_bit()
    {
        var properties = Read(Build(new() { ["fontEffects"] = BoldAndUnknownBits, ["fontWeight"] = 700, ["fontItalic"] = true }));

        Assert.Equal(BoldAndUnknownBits | Italic, properties["fontEffects"]);
    }

    [Fact]
    public void Patched_italic_can_be_cleared()
    {
        var properties = Read(Build(new() { ["fontEffects"] = BoldAndUnknownBits | Italic, ["fontWeight"] = 700, ["fontItalic"] = false }));

        Assert.Equal(BoldAndUnknownBits, properties["fontEffects"]);
    }

    [Fact]
    public void Patched_normal_weight_clears_the_bold_bit()
    {
        var properties = Read(Build(new() { ["fontEffects"] = BoldAndUnknownBits, ["fontWeight"] = 400 }));

        Assert.Equal(BoldAndUnknownBits & ~Bold, properties["fontEffects"]);
    }

    [Fact]
    public void Patched_bold_weight_sets_the_bold_bit()
    {
        var properties = Read(Build(new() { ["fontEffects"] = 0x4000_2000u, ["fontWeight"] = 700 }));

        Assert.Equal(0x4000_2000u | Bold, properties["fontEffects"]);
    }

    [Fact]
    public void A_control_without_effects_gets_no_field()
    {
        var properties = Read(Build(new() { ["fontName"] = "Tahoma" }));

        Assert.False(properties.ContainsKey("fontEffects"));
    }

    [Fact]
    public void Build_keeps_effects_from_one_rebuild_to_the_next()
    {
        using var workspace = new Workspace();
        var first = workspace.PathOf("first/sample.frm");
        var patch = workspace.WritePatch("patch.json", """{ "properties": { "CommandButton1": { "fontItalic": true } } }""");
        var patched = workspace.Run("build", VerificationTests.SampleForm(), patch, "--out", first, "--mode", "tolerant");
        Assert.True(patched.ExitCode == 0, patched.Describe());

        var second = workspace.PathOf("second/sample.frm");
        var rebuilt = workspace.Run("build", first, "--out", second, "--mode", "tolerant");

        Assert.True(rebuilt.ExitCode == 0, rebuilt.Describe());
        var properties = workspace.ReadControls(second).Single(control => control.Name == "CommandButton1").Properties!;
        Assert.True(properties["fontItalic"].GetBoolean());
        Assert.Equal(Italic, properties["fontEffects"].GetUInt32());
    }

    private static byte[] Build(Dictionary<string, object?> properties) =>
        TextPropsFactory.Build(properties, TextPropsFactory.StandardMask);

    private static Dictionary<string, object?> Read(byte[] data)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Assert.True(TextPropsParser.TryRead(data, new int[data.Length], 0, properties, out _));
        return properties;
    }
}
