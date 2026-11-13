# Exports every "<Map> (blender 4.2).blend" in a folder to MapExports/<Map> for the Unity map builder
# (ValoReplay > Maps > Build All Exported Maps).
#
#   powershell -File Tools/MapExport/export_all.ps1 -MapsDir "C:\Users\Admin\Downloads\shade_s val maps!"
param(
    [Parameter(Mandatory = $true)][string]$MapsDir,
    [string]$Blender = "C:\Program Files\Blender Foundation\Blender 4.2\blender.exe",
    [string]$OutDir = (Join-Path $PSScriptRoot "..\..\MapExports")
)

$script = Join-Path $PSScriptRoot "export_map.py"
Get-ChildItem -LiteralPath $MapsDir -Filter *.blend | ForEach-Object {
    $map = ($_.BaseName -replace '\s*\(.*\)\s*$', '').Trim()     # "Ascent (blender 4.2)" -> "Ascent"
    Write-Host "== $map"
    & $Blender -b $_.FullName --python $script -- --out (Join-Path $OutDir $map) 2>&1 |
        Select-String -Pattern 'EXPORTED|Error|Traceback' | ForEach-Object { $_.Line }
}
