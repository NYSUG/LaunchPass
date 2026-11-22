[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (!$git) {
    $git = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe'
}
if (!(Test-Path -LiteralPath $git)) { throw 'Git executable not found.' }
$files = @(& $git -c "safe.directory=$root" -C $root ls-files -- mp3)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect tracked music.' }
if (!$files.Count) { Write-Host 'No tracked mp3/ files remain.'; return }
$before = @{}
foreach ($file in $files) {
    if (![IO.Path]::GetExtension($file).Equals('.mp3', [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected tracked file: $file" }
    $before[$file] = (Get-FileHash -LiteralPath (Join-Path $root $file) -Algorithm SHA256).Hash
}
Write-Host "Verified $($files.Count) local MP3 files. This only removes index entries; it does not erase history."
if (!$Apply) { Write-Host 'Preview only. Re-run with -Apply to stage untracking.'; return }
& $git -c "safe.directory=$root" -C $root rm --cached -- @files
if ($LASTEXITCODE -ne 0) { throw 'Git refused the index update. Local MP3s were not deleted.' }
foreach ($file in $files) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $file) -Algorithm SHA256).Hash -ne $before[$file]) { throw "Local music changed: $file" }
}
Write-Host 'Untracking staged; all local SHA-256 hashes unchanged. Nothing committed or pushed.'
