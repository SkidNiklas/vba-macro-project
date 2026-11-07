# The control properties read from Excel's designer. Dot-sourced by Export-DesignerControls.ps1 and
# Test-ImportInExcel.ps1, so the reference data and the import test compare the same things.

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

function Get-DesignerProperties($control) {
    [ordered]@{
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
}
