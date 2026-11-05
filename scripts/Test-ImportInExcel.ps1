<#
.SYNOPSIS
    Imports rebuilt UserForms into a temporary copy of an add-in and opens them in Excel's form designer.

.DESCRIPTION
    Copies the add-in to a temp folder and works only on the copy, which is never saved. Macros are disabled,
    so no form code runs. For each form the script compares Designer.Controls before and after the import,
    opens the designer and saves a picture of the VBA editor. It then leaves Excel open until you close it,
    or until -WaitSeconds has passed, and closes only the Excel it started.

    Requires "Trust access to the VBA project object model", and that no other Excel is running.

.PARAMETER AddinPath
    The add-in to copy.

.PARAMETER FormPaths
    The rebuilt .frm files (UTF-8), each with its .frx beside it.

.PARAMETER OutputFolder
    Where to write the pictures and result.json. Keep it outside the repository.

.PARAMETER WaitSeconds
    How long to keep Excel open. Use 0 to close it right away.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AddinPath,
    [Parameter(Mandatory)] [string[]] $FormPaths,
    [Parameter(Mandatory)] [string] $OutputFolder,
    [int] $WaitSeconds = 300
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace FrxEditImport -Name NativeMethods -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
'@

$vbextMsForm = 3
$msoAutomationSecurityForceDisable = 3
$xlDoNotUpdateLinks = 0

function Get-OptionalValue([scriptblock] $read) {
    # Not every control type has every property.
    try { return [string] (& $read) } catch { return '(none)' }
}

function Get-FontValue($control, [string] $property) {
    # $control.Font.Name reads as empty through PowerShell's COM adapter, so ask the objects directly.
    try {
        $getProperty = [Reflection.BindingFlags]::GetProperty
        $font = [System.__ComObject].InvokeMember('Font', $getProperty, $null, $control, $null)
        return [string] [System.__ComObject].InvokeMember($property, $getProperty, $null, $font, $null)
    } catch {
        return '(none)'
    }
}

function Get-DesignerControls($component) {
    @($component.Designer.Controls | ForEach-Object {
        $control = $_
        [ordered]@{
            name        = [string] $control.Name
            left        = [math]::Round([double] $control.Left, 2)
            top         = [math]::Round([double] $control.Top, 2)
            width       = [math]::Round([double] $control.Width, 2)
            height      = [math]::Round([double] $control.Height, 2)
            caption     = Get-OptionalValue { $control.Caption }
            value       = Get-OptionalValue { $control.Value }
            tag         = Get-OptionalValue { $control.Tag }
            tip         = Get-OptionalValue { $control.ControlTipText }
            visible     = Get-OptionalValue { $control.Visible }
            enabled     = Get-OptionalValue { $control.Enabled }
            locked      = Get-OptionalValue { $control.Locked }
            tabIndex    = Get-OptionalValue { $control.TabIndex }
            tabStop     = Get-OptionalValue { $control.TabStop }
            foreColor   = Get-OptionalValue { $control.ForeColor }
            backColor   = Get-OptionalValue { $control.BackColor }
            backStyle   = Get-OptionalValue { $control.BackStyle }
            borderStyle = Get-OptionalValue { $control.BorderStyle }
            effect      = Get-OptionalValue { $control.SpecialEffect }
            textAlign   = Get-OptionalValue { $control.TextAlign }
            wordWrap    = Get-OptionalValue { $control.WordWrap }
            autoSize    = Get-OptionalValue { $control.AutoSize }
            fontName    = Get-FontValue $control 'Name'
            fontSize    = Get-FontValue $control 'Size'
            fontBold    = Get-FontValue $control 'Bold'
            fontItalic  = Get-FontValue $control 'Italic'
        }
    })
}

function Compare-Controls($before, $after) {
    $differences = New-Object System.Collections.ArrayList
    $afterByName = @{}
    foreach ($control in $after) { $afterByName[$control.name] = $control }
    foreach ($control in $before) {
        $other = $afterByName[$control.name]
        if ($null -eq $other) { [void] $differences.Add("missing: $($control.name)"); continue }
        foreach ($property in @($control.Keys)) {
            $different = if ($property -in 'left', 'top', 'width', 'height') {
                [math]::Abs($control[$property] - $other[$property]) -gt 0.1
            } else {
                $control[$property] -cne $other[$property]
            }
            if ($different) {
                [void] $differences.Add("$($control.name).${property}: '$($control[$property])' -> '$($other[$property])'")
            }
        }
        $afterByName.Remove($control.name)
    }
    foreach ($name in $afterByName.Keys) { [void] $differences.Add("unexpected: $name") }
    return $differences
}

function Save-WindowPicture([IntPtr] $handle, [string] $path) {
    [void] [FrxEditImport.NativeMethods]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 800
    $rect = New-Object FrxEditImport.NativeMethods+RECT
    [void] [FrxEditImport.NativeMethods]::GetWindowRect($handle, [ref] $rect)
    $bitmap = New-Object System.Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
}

if (Get-Process EXCEL -ErrorAction SilentlyContinue) {
    throw 'Excel is running. Close it first, so this script can be sure which Excel it controls.'
}

$source = (Resolve-Path -LiteralPath $AddinPath).Path
$tempDir = Join-Path ([IO.Path]::GetTempPath()) "frxedit-import-$([Guid]::NewGuid().ToString('N'))"
$copy = Join-Path $tempDir "import-copy$([IO.Path]::GetExtension($source))"
$formsDir = Join-Path $tempDir 'forms'
$outputFull = [IO.Path]::GetFullPath($OutputFolder)
$excel = $null
[uint32] $excelPid = 0

[void] [IO.Directory]::CreateDirectory($outputFull)
[void] [IO.Directory]::CreateDirectory($formsDir)
try {
    Copy-Item -LiteralPath $source -Destination $copy

    $excel = New-Object -ComObject Excel.Application
    [void] [FrxEditImport.NativeMethods]::GetWindowThreadProcessId([IntPtr] $excel.Hwnd, [ref] $excelPid)
    $excel.DisplayAlerts = $false
    $excel.EnableEvents = $false
    $excel.AutomationSecurity = $msoAutomationSecurityForceDisable

    $workbook = $excel.Workbooks.Open($copy, $xlDoNotUpdateLinks, $false)
    $project = $workbook.VBProject
    $excel.Visible = $true
    $excel.VBE.MainWindow.Visible = $true

    $ansi = [Text.Encoding]::GetEncoding(1252)
    $results = New-Object System.Collections.ArrayList
    foreach ($formPath in $FormPaths) {
        $frm = (Resolve-Path -LiteralPath $formPath).Path
        $name = [IO.Path]::GetFileNameWithoutExtension($frm)
        $result = [ordered]@{ form = $name; imported = $false; differences = @(); error = $null }
        try {
            $before = $null
            $existing = $project.VBComponents | Where-Object { $_.Name -eq $name }
            if ($existing) {
                $before = Get-DesignerControls $existing
                $project.VBComponents.Remove($existing)
            }

            $tempFrm = Join-Path $formsDir "$name.frm"
            [IO.File]::WriteAllText($tempFrm, [IO.File]::ReadAllText($frm, [Text.Encoding]::UTF8), $ansi)
            Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($frm, '.frx')) -Destination (Join-Path $formsDir "$name.frx")

            $component = $project.VBComponents.Import($tempFrm)
            $result.imported = $component.Type -eq $vbextMsForm
            $after = Get-DesignerControls $component
            $result.controls = $after.Count
            if ($before) { $result.differences = @(Compare-Controls $before $after) }

            $component.Activate()
            $picture = Join-Path $outputFull "$name.png"
            Save-WindowPicture ([IntPtr] $excel.VBE.MainWindow.HWnd) $picture
            $result.picture = $picture
        } catch {
            $result.error = $_.Exception.Message
        }
        [void] $results.Add($result)
        Write-Host ("{0,-30} imported={1} controls={2} differences={3} {4}" -f $name, $result.imported, $result.controls, @($result.differences).Count, $result.error)
    }

    $resultPath = Join-Path $outputFull 'result.json'
    [IO.File]::WriteAllText($resultPath, (ConvertTo-Json -InputObject @($results) -Depth 5), (New-Object Text.UTF8Encoding($false)))
    Write-Host "Wrote $resultPath"

    if ($WaitSeconds -gt 0) {
        Write-Host "Excel stays open for up to $WaitSeconds seconds. Close it when you have looked. Don't save."
        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $excelPid -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 2
        }
    }
}
finally {
    if ($null -ne $excel) {
        try {
            $workbook.Saved = $true
            $excel.Quit()
        } catch { }
        [void] [Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
    # Only this script's own Excel, found through its window handle. A hidden leftover would lock files.
    if ($excelPid -gt 0) {
        Start-Sleep -Seconds 2
        Get-Process -Id $excelPid -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
