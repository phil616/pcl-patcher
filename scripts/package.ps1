param(
    [Parameter(Mandatory = $true)]
    [string] $Tag,
    [string] $OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Tag -cnotmatch '\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z') {
    throw 'Expected a version tag such as v1.0.0 or v1.0.0-rc.1.'
}

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dependencies.ps1')
# Verify the dependency again before putting it in a release archive.
& (Join-Path $PSScriptRoot 'restore-deps.ps1')
$files = @('patch.exe', 'dnlib.dll', 'README.md', 'LICENSE', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md', 'packages.config', 'dependencies.lock.json')
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)) { throw "Missing release file: $file" }
}
$dist = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($dist)
$archiveName = "pcl-patcher-$Tag-windows.zip"
$archivePath = Join-Path $dist $archiveName
$sbomName = "pcl-patcher-$Tag.spdx.json"
$sbomPath = Join-Path $dist $sbomName
$checksumPath = Join-Path $dist 'SHA256SUMS.txt'
foreach ($path in @($archivePath, $sbomPath, $checksumPath)) {
    if (Test-Path -LiteralPath $path) { throw "Release output already exists: $path" }
}
$staging = Join-Path $root ('obj/package-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($staging)
try {
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $staging }
    & (Join-Path $PSScriptRoot 'sbom.ps1') -Tag $Tag -ContentDirectory $staging -Destination $sbomPath
    Copy-Item -LiteralPath $sbomPath -Destination (Join-Path $staging 'SBOM.spdx.json')
    Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $staging -File | Select-Object -ExpandProperty FullName) -DestinationPath $archivePath
    $lines = @($archiveName, $sbomName) | ForEach-Object { (Get-Sha256 (Join-Path $dist $_)) + '  ' + $_ }
    [IO.File]::WriteAllText($checksumPath, (($lines -join "`n") + "`n"), [Text.Encoding]::ASCII)
}
finally {
    # Only delete direct files in the unique staging directory created above.
    foreach ($file in Get-ChildItem -LiteralPath $staging -File) { Remove-Item -LiteralPath $file.FullName -Force }
    Remove-Item -LiteralPath $staging -Force
}
Write-Host "Created $archivePath, $sbomName and SHA256SUMS.txt"
