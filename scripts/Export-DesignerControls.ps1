<#
.SYNOPSIS
    Exports what Excel itself reports for every UserForm control to JSON, as a reference for frxedit tests.

.DESCRIPTION
    Copies the add-in to a temp folder and opens only the copy, read-only and with macros disabled, in a
    new hidden Excel. The original file and the user's add-in settings are not changed. The script stops
    without exporting if anything named like the add-in is already loaded in that Excel, because Excel
    could then read the wrong project.

    Requires "Trust access to the VBA project object model" in Excel's Trust Center.

.PARAMETER AddinPath
    The .xlam, .xlsm or .xlsb to read.

.PARAMETER OutputPath
    The JSON file to write. Keep it outside the repository: it contains form and control names.

.EXAMPLE
    .\Export-DesignerControls.ps1 -AddinPath C:\Work\MyAddin.xlam -OutputPath C:\Work\facit\controls.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AddinPath,
    [Parameter(Mandatory)] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type -Namespace FrxEditFacit -Name NativeMethods -MemberDefinition @'
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
'@

$vbextMsForm = 3
$msoAutomationSecurityForceDisable = 3
$xlDoNotUpdateLinks = 0

function Get-ControlRecord($control, [string] $typeName, [string] $parentName) {
    [ordered]@{
        name   = [string] $control.Name
        type   = $typeName
        parent = $parentName
        left   = [double] $control.Left
        top    = [double] $control.Top
        width  = [double] $control.Width
        height = [double] $control.Height
    }
}

function Get-ParentName($control, [string] $formName) {
    $parentName = [string] $control.Parent.Name
    if ($parentName -eq $formName) { return $null }
    return $parentName
}

function Get-InstalledAddinNames($excel) {
    @($excel.AddIns | Where-Object { $_.Installed } | ForEach-Object { $_.Name } | Sort-Object)
}

$source = (Resolve-Path -LiteralPath $AddinPath).Path
$baseName = [IO.Path]::GetFileNameWithoutExtension($source)
$tempDir = Join-Path ([IO.Path]::GetTempPath()) "frxedit-facit-$([Guid]::NewGuid().ToString('N'))"
$copy = Join-Path $tempDir "facit-copy$([IO.Path]::GetExtension($source))"
$excel = $null
[uint32] $excelPid = 0

New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    Copy-Item -LiteralPath $source -Destination $copy
    Set-ItemProperty -LiteralPath $copy -Name IsReadOnly -Value $true

    $excel = New-Object -ComObject Excel.Application
    [void] [FrxEditFacit.NativeMethods]::GetWindowThreadProcessId([IntPtr] $excel.Hwnd, [ref] $excelPid)
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.EnableEvents = $false
    $excel.AutomationSecurity = $msoAutomationSecurityForceDisable

    $loaded = @($excel.Workbooks | ForEach-Object { $_.Name } | Where-Object { $_ -like "$baseName*" })
    if ($loaded.Count -gt 0) {
        throw "Stopped: already loaded in the new Excel: $($loaded -join ', '). Nothing was exported or changed."
    }
    $addinsBefore = Get-InstalledAddinNames $excel

    $workbook = $excel.Workbooks.Open($copy, $xlDoNotUpdateLinks, $true)
    try {
        $project = $workbook.VBProject
        $null = $project.VBComponents.Count
    } catch {
        throw "Excel denied access to the VBA project. Turn on 'Trust access to the VBA project object model' in the Trust Center. ($($_.Exception.Message))"
    }

    $forms = [ordered]@{}
    foreach ($component in $project.VBComponents) {
        if ($component.Type -ne $vbextMsForm) { continue }

        $formName = [string] $component.Name
        $controls = New-Object System.Collections.ArrayList
        foreach ($control in $component.Designer.Controls) {
            $typeName = [Microsoft.VisualBasic.Information]::TypeName($control)
            [void] $controls.Add((Get-ControlRecord $control $typeName (Get-ParentName $control $formName)))

            if ($typeName -in 'MultiPage', 'IMultiPage') {
                foreach ($page in $control.Pages) {
                    [void] $controls.Add((Get-ControlRecord $page 'Page' ([string] $control.Name)))
                }
            }
        }
        $forms[$formName] = [ordered]@{ controls = @($controls) }
        Write-Host ("{0,-30} {1,4} controls" -f $formName, $controls.Count)
    }

    $addinsAfter = Get-InstalledAddinNames $excel
    if (($addinsBefore -join '|') -ne ($addinsAfter -join '|')) {
        Write-Warning "The installed add-ins changed during the export: before [$($addinsBefore -join ', ')], after [$($addinsAfter -join ', ')]."
    }

    $document = [ordered]@{
        source     = [IO.Path]::GetFileName($source)
        exportedAt = (Get-Date).ToString('s')
        excel      = [string] $excel.Version
        forms      = $forms
    }
    $outputFull = [IO.Path]::GetFullPath($OutputPath)
    [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFull))
    [IO.File]::WriteAllText($outputFull, ($document | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    Write-Host "Wrote $outputFull ($($forms.Count) forms)"

    $workbook.Close($false)
}
finally {
    if ($null -ne $excel) {
        try { $excel.Quit() } catch { }
        [void] [Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
    # Only this script's own Excel, found through its window handle. A hidden leftover would lock files.
    if ($excelPid -gt 0) {
        Start-Sleep -Seconds 2
        Get-Process -Id $excelPid -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    if (Test-Path -LiteralPath $copy) { Set-ItemProperty -LiteralPath $copy -Name IsReadOnly -Value $false }
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
