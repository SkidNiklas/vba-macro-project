using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FrxEdit.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void RegisterEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}

internal sealed record RunResult(int ExitCode, string Output, string Error)
{
    public string Describe() => $"exit code {ExitCode}{Environment.NewLine}{Error}{Output}";
}

internal sealed record ControlRecord(
    string Name,
    string Type,
    string? Parent,
    int? Left,
    int? Top,
    int? RawWidth,
    int? RawHeight,
    double? LeftPt,
    double? TopPt,
    double? WidthPt,
    double? HeightPt);

internal sealed record LayoutFile(List<ControlRecord> Controls);

/// <summary>A temporary folder for one test. Forms are always copied here, so their source is never written to.</summary>
internal sealed class Workspace : IDisposable
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("frxedit-tests-").FullName;

    public string PathOf(string relativePath) => Path.Combine(Directory, relativePath);

    public RunResult Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = new FrxEditApp(output, error).Run(args);
        return new RunResult(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Copies a form and marks it as UTF-8 if it holds non-ASCII text, because frxedit reads a file without
    /// a BOM as Windows-1252. Returns the path of the copy.
    /// </summary>
    public string CopyForm(string frmPath)
    {
        var frmCopy = PathOf(Path.GetFileName(frmPath));
        var bytes = File.ReadAllBytes(frmPath);
        var hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
        File.WriteAllBytes(frmCopy, hasBom || !IsUtf8WithNonAscii(bytes) ? bytes : [.. Utf8Bom, .. bytes]);
        File.Copy(Path.ChangeExtension(frmPath, ".frx"), Path.ChangeExtension(frmCopy, ".frx"));
        return frmCopy;
    }

    public string WritePatch(string name, string json)
    {
        var path = PathOf(name);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    public List<ControlRecord> ReadControls(string frmPath)
    {
        var folder = System.IO.Directory.CreateTempSubdirectory("inspect-").FullName;
        try
        {
            var rawPath = Path.Combine(folder, "raw.json");
            var result = Run("inspect", frmPath, "--mode", "tolerant", "--out", Path.Combine(folder, "layout.json"), "--raw-out", rawPath);
            Assert.True(result.ExitCode == 0, $"inspect failed: {result.Describe()}");
            return JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(rawPath), ReadOptions)!.Controls;
        }
        finally
        {
            System.IO.Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The form's text from its attributes on, which build must keep apart from the header.</summary>
    public static string ReadCode(string frmPath)
    {
        var text = File.ReadAllText(frmPath, Encoding.Latin1);
        var start = text.IndexOf("Attribute VB_Name", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{frmPath} has no attributes.");
        return text[start..];
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder must not fail a test.
        }
    }

    private static bool IsUtf8WithNonAscii(byte[] bytes)
    {
        if (bytes.All(b => b < 0x80))
        {
            return false;
        }

        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>Forms from FRXEDIT_TESTFORMS, a folder outside the repository. Without it, the form tests are skipped.</summary>
public static class TestForms
{
    public const string FormsVariable = "FRXEDIT_TESTFORMS";

    private const string NoForms = "(no forms)";

    public static TheoryData<string> All()
    {
        var data = new TheoryData<string>();
        var folder = Environment.GetEnvironmentVariable(FormsVariable);
        if (string.IsNullOrWhiteSpace(folder))
        {
            // One row that skips, because a theory without data is counted as failed.
            data.Add(NoForms);
            return data;
        }

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"{FormsVariable} points at a folder that does not exist: {folder}");
        }

        foreach (var frm in Directory.GetFiles(folder, "*.frm").Order(StringComparer.OrdinalIgnoreCase))
        {
            data.Add(Path.GetFileNameWithoutExtension(frm));
        }

        return data;
    }

    internal static string PathOf(string form)
    {
        if (form == NoForms)
        {
            Assert.Skip($"{FormsVariable} is not set.");
        }

        return Path.Combine(Environment.GetEnvironmentVariable(FormsVariable)!, form + ".frm");
    }
}

/// <summary>What Excel reports for each form, written by scripts/Export-DesignerControls.ps1 to the file in FRXEDIT_FACIT.</summary>
internal static class Facit
{
    public const string FacitVariable = "FRXEDIT_FACIT";

    public static IReadOnlyList<FacitControl>? ControlsOf(string form)
    {
        var path = Environment.GetEnvironmentVariable(FacitVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.GetProperty("forms").TryGetProperty(form, out var entry))
        {
            return null;
        }

        return entry.GetProperty("controls").EnumerateArray().Select(control => new FacitControl(
            control.GetProperty("name").GetString()!,
            NormalizeType(control.GetProperty("type").GetString()!),
            // The export writes an empty string for a control directly on the form.
            control.GetProperty("parent").GetString() is { Length: > 0 } parent ? parent : null,
            control.GetProperty("left").GetDouble(),
            control.GetProperty("top").GetDouble(),
            control.GetProperty("width").GetDouble(),
            control.GetProperty("height").GetDouble())).ToList();
    }

    /// <summary>Through COM, TypeName returns the interface name, such as IMdcText for a TextBox.</summary>
    private static string NormalizeType(string type) => type switch
    {
        "ICommandButton" => "CommandButton",
        "ILabelControl" => "Label",
        "IMdcText" => "TextBox",
        "IMdcCombo" => "ComboBox",
        "IMdcList" => "ListBox",
        "IMdcCheckBox" => "CheckBox",
        "IMdcOptionButton" => "OptionButton",
        "IMdcToggleButton" => "ToggleButton",
        "IOptionFrame" => "Frame",
        "IMultiPage" => "MultiPage",
        "IListView" or "ITreeView" or "IProgressBar" => "ActiveX",
        _ => type,
    };
}

internal sealed record FacitControl(string Name, string Type, string? Parent, double Left, double Top, double Width, double Height);
