using System.Globalization;
using System.Text.Json;
using FrxEdit.Cli.MsForms;

namespace FrxEdit.Tests;

/// <summary>Compares what frxedit reads with the properties Excel reports. A value the form doesn't store counts as the control's default.</summary>
internal static class FacitComparison
{
    private const string NoSuchProperty = "(none)";

    private static readonly string[] TextProperties = ["caption", "value", "tag", "tip"];

    private static readonly string[] FontProperties = ["fontName", "fontSize", "fontBold", "fontItalic"];

    /// <summary>
    /// The properties frxedit reads for a control type. It doesn't read the font of a Frame, MultiPage or ActiveX
    /// control, nor anything of a Page, whose caption is in the MultiPage and whose site flags mean something else.
    /// </summary>
    public static IReadOnlyList<string> ComparedProperties(string type) => type switch
    {
        "Page" => [],
        "ActiveX" => ["tag", "visible", "tabIndex", "tabStop"],
        "MultiPage" => ["tag", "tip", "visible", "enabled", "tabIndex", "tabStop"],
        "Frame" => Facit.PropertyNames.Except(FontProperties).Except(["value"]).ToArray(),
        // A button's Value is not stored: Excel always reports False.
        "CommandButton" => Facit.PropertyNames.Except(["value"]).ToArray(),
        _ => Facit.PropertyNames,
    };

    public static (List<string> Differences, int Compared) Compare(FacitControl excel, string type, IReadOnlyDictionary<string, JsonElement>? stored)
    {
        var properties = stored ?? new Dictionary<string, JsonElement>();
        var differences = new List<string>();
        var compared = 0;
        foreach (var property in ComparedProperties(type))
        {
            var expected = excel.Properties[property];
            // Excel gives an empty value for a property the control type doesn't have.
            if (expected == NoSuchProperty || (expected.Length == 0 && !TextProperties.Contains(property)))
            {
                continue;
            }

            compared++;
            var actual = Read(property, type, properties);
            if (!Same(property, expected, actual))
            {
                differences.Add($"{excel.Name}.{property}: Excel '{expected}', frxedit '{actual ?? "not read"}'");
            }
        }

        return (differences, compared);
    }

    private static bool Same(string property, string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        return property switch
        {
            "foreColor" or "backColor" => ToOleColor(expected) == ToOleColor(actual),
            "fontSize" => Math.Abs(double.Parse(expected, CultureInfo.InvariantCulture) - double.Parse(actual, CultureInfo.InvariantCulture)) < 0.01,
            _ => expected == actual,
        };
    }

    private static string? Read(string property, string type, IReadOnlyDictionary<string, JsonElement> stored) => property switch
    {
        "caption" => Text(stored, type == "Frame" ? "formCaption" : "caption") ?? string.Empty,
        "value" => ReadValue(type, stored),
        "tag" => Text(stored, "tag") ?? string.Empty,
        "tip" => Text(stored, "controlTipText") ?? string.Empty,
        "enabled" when type is "Frame" or "MultiPage" => Flag(Number(stored, "formBooleanPropertiesRaw") is { } bits ? (bits & 4) != 0 : null),
        "enabled" => Flag(Bool(stored, "enabled") ?? true),
        "locked" => Flag(Bool(stored, "locked") ?? false),
        "visible" => Flag(Bool(stored, "visible") ?? true),
        "tabStop" => Flag(Bool(stored, "tabStop") ?? true),
        "wordWrap" => Flag(Bool(stored, "wordWrap") ?? type != "CommandButton"),
        "autoSize" => Flag(Bool(stored, "autoSize") ?? false),
        "tabIndex" => Text(stored, "tabIndex"),
        "foreColor" => Text(stored, "foreColor") ?? DefaultForeColor(type),
        "backColor" => Text(stored, "backColor") ?? DefaultBackColor(type),
        "backStyle" => Text(stored, "backStyle") ?? "1",
        "borderStyle" => Text(stored, "borderStyle") ?? "0",
        "effect" => Text(stored, type == "Frame" ? "formSpecialEffect" : "specialEffect") ?? DefaultEffect(type),
        "textAlign" => Text(stored, "textAlign") switch
        {
            null or "left" => "1",
            "center" => "2",
            "right" => "3",
            var other => other,
        },
        "fontName" => Text(stored, "fontName"),
        "fontSize" => Text(stored, "fontSize"),
        "fontBold" => Flag((Number(stored, "fontEffects") ?? 0) % 2 == 1),
        "fontItalic" => Flag(Bool(stored, "fontItalic") ?? false),
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
    };

    private static string? ReadValue(string type, IReadOnlyDictionary<string, JsonElement> stored)
    {
        var value = Text(stored, "value") ?? string.Empty;
        if (type is not ("CheckBox" or "OptionButton" or "ToggleButton"))
        {
            return value;
        }

        return value switch
        {
            "" or "0" => "False",
            "1" => "True",
            _ => value,
        };
    }

    private static bool IsTextEntry(string type) => type is "TextBox" or "ComboBox" or "ListBox";

    private static string DefaultForeColor(string type) => IsTextEntry(type) ? "-2147483640" : "-2147483630";

    private static string DefaultBackColor(string type) => IsTextEntry(type) ? "-2147483643" : "-2147483633";

    private static string DefaultEffect(string type) => type is "Label" or "Frame" ? "0" : "2";

    private static uint? ToOleColor(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            return unchecked((uint)number);
        }

        return OleColorConverter.TryParse(text, out var color) ? color : null;
    }

    private static string? Flag(bool? value) => value is null ? null : value.Value ? "True" : "False";

    private static string? Text(IReadOnlyDictionary<string, JsonElement> stored, string key)
    {
        if (!stored.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.GetRawText(),
        };
    }

    private static long? Number(IReadOnlyDictionary<string, JsonElement> stored, string key) =>
        stored.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static bool? Bool(IReadOnlyDictionary<string, JsonElement> stored, string key) =>
        stored.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
