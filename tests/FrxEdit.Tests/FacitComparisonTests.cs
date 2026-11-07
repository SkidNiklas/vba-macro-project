using System.Text.Json;
using Xunit;

namespace FrxEdit.Tests;

/// <summary>The comparison itself, so that a form test can't pass because it compared nothing. Needs no forms.</summary>
public class FacitComparisonTests
{
    private static FacitControl Excel(params (string Name, string Value)[] values)
    {
        var properties = Facit.PropertyNames.ToDictionary(name => name, _ => string.Empty);
        foreach (var (name, value) in values)
        {
            properties[name] = value;
        }

        return new FacitControl("Ctl", "Label", null, 0, 0, 0, 0, properties);
    }

    private static Dictionary<string, JsonElement> Stored(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Matching_values_give_no_differences_and_are_counted()
    {
        var excel = Excel(("caption", "Text"), ("tabIndex", "3"), ("fontName", "Tahoma"), ("fontSize", "8.25"), ("fontBold", "True"));
        var stored = Stored("""{ "caption": "Text", "tabIndex": 3, "fontName": "Tahoma", "fontSize": 8.25, "fontEffects": 1 }""");

        var (differences, compared) = FacitComparison.Compare(excel, "Label", stored);

        Assert.Empty(differences);
        Assert.True(compared >= 5, $"compared {compared}");
    }

    [Theory]
    [InlineData("caption", "Excel", """{ "caption": "frxedit" }""")]
    [InlineData("tabIndex", "1", """{ "tabIndex": 2 }""")]
    [InlineData("visible", "True", """{ "visible": false }""")]
    [InlineData("foreColor", "192", """{ "foreColor": "#0000C0" }""")]
    [InlineData("fontName", "Tahoma", """{ "fontName": "Arial" }""")]
    [InlineData("fontSize", "9", """{ "fontSize": 9.75 }""")]
    [InlineData("fontBold", "False", """{ "fontEffects": 1 }""")]
    [InlineData("fontBold", "True", """{ "fontEffects": 8192 }""")]
    [InlineData("fontItalic", "False", """{ "fontItalic": true }""")]
    [InlineData("effect", "0", """{ "specialEffect": 2 }""")]
    public void A_different_value_is_reported(string property, string excelValue, string stored)
    {
        var (differences, _) = FacitComparison.Compare(Excel((property, excelValue)), "Label", Stored(stored));

        Assert.Contains(differences, difference => difference.StartsWith($"Ctl.{property}:"));
    }

    [Fact]
    public void A_font_frxedit_did_not_read_is_reported()
    {
        var (differences, _) = FacitComparison.Compare(Excel(("fontName", "Tahoma")), "Label", Stored("{}"));

        Assert.Contains(differences, difference => difference.Contains("not read"));
    }

    [Theory]
    [InlineData("TextBox", "effect", "2")]
    [InlineData("Label", "effect", "0")]
    [InlineData("TextBox", "foreColor", "-2147483640")]
    [InlineData("Label", "foreColor", "-2147483630")]
    [InlineData("ListBox", "backColor", "-2147483643")]
    [InlineData("CommandButton", "backColor", "-2147483633")]
    [InlineData("CommandButton", "wordWrap", "False")]
    [InlineData("Label", "wordWrap", "True")]
    [InlineData("Label", "visible", "True")]
    [InlineData("Label", "textAlign", "1")]
    public void A_value_the_form_does_not_store_is_the_default(string type, string property, string excelValue)
    {
        var (differences, compared) = FacitComparison.Compare(Excel((property, excelValue)), type, Stored("{}"));

        Assert.Empty(differences);
        Assert.True(compared >= 1);
    }

    [Theory]
    [InlineData("-2147483630", "systemButtonText")]
    [InlineData("2147483666", "systemButtonText")]
    [InlineData("192", "#C00000")]
    public void Colors_are_compared_by_value(string excelValue, string stored)
    {
        var json = JsonSerializer.Serialize(new { foreColor = stored });

        Assert.Empty(FacitComparison.Compare(Excel(("foreColor", excelValue)), "Label", Stored(json)).Differences);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(none)")]
    public void A_property_the_type_does_not_have_is_not_compared(string excelValue)
    {
        var (differences, compared) = FacitComparison.Compare(Excel(("locked", excelValue)), "Label", Stored("""{ "locked": true }"""));

        Assert.DoesNotContain(differences, difference => difference.StartsWith("Ctl.locked"));
        Assert.Equal(4, compared); // caption, value, tag and tip: text can really be empty.
    }

    [Fact]
    public void An_empty_text_is_compared()
    {
        var (differences, _) = FacitComparison.Compare(Excel(("caption", "")), "Label", Stored("""{ "caption": "Text" }"""));

        Assert.Contains(differences, difference => difference.StartsWith("Ctl.caption"));
    }

    [Theory]
    [InlineData("False", "0")]
    [InlineData("True", "1")]
    public void A_check_box_value_is_read_as_a_flag(string excelValue, string stored)
    {
        var json = JsonSerializer.Serialize(new { value = stored });

        Assert.Empty(FacitComparison.Compare(Excel(("value", excelValue)), "CheckBox", Stored(json)).Differences);
    }

    [Fact]
    public void A_frame_is_read_from_its_form_fields()
    {
        var excel = Excel(("caption", "Rubrik"), ("enabled", "False"), ("effect", "3"));
        var stored = Stored("""{ "formCaption": "Rubrik", "formBooleanPropertiesRaw": 49152, "formSpecialEffect": 3 }""");

        var (differences, compared) = FacitComparison.Compare(excel, "Frame", stored);

        Assert.Empty(differences);
        Assert.True(compared >= 3);
    }

    [Fact]
    public void A_frame_that_differs_from_Excel_is_reported()
    {
        var stored = Stored("""{ "formCaption": "Annan", "formBooleanPropertiesRaw": 49156, "formSpecialEffect": 1 }""");

        var (differences, _) = FacitComparison.Compare(Excel(("caption", "Rubrik"), ("enabled", "False"), ("effect", "3")), "Frame", stored);

        Assert.Equal(3, differences.Count);
    }

    [Theory]
    [InlineData("Frame")]
    [InlineData("MultiPage")]
    [InlineData("ActiveX")]
    public void Fonts_are_not_compared_where_frxedit_does_not_read_them(string type)
    {
        Assert.Empty(FacitComparison.ComparedProperties(type).Intersect(["fontName", "fontSize", "fontBold", "fontItalic"]));
    }

    [Fact]
    public void Controls_frxedit_reads_compare_every_property_in_the_facit()
    {
        Assert.Equal(Facit.PropertyNames, FacitComparison.ComparedProperties("Label"));
    }

    [Fact]
    public void A_facit_without_the_properties_is_refused()
    {
        using var document = JsonDocument.Parse("""{ "name": "Ctl", "type": "ILabelControl", "parent": "", "left": 0, "top": 0, "width": 1, "height": 1 }""");

        var error = Assert.Throws<InvalidDataException>(() => Facit.ReadControl(document.RootElement));

        Assert.Contains("Export-DesignerControls", error.Message);
    }
}
