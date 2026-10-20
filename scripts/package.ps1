param(
    [Parameter(Mandatory = $true)]
    [string] $Tag
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Tag -cnotmatch '\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z') {
    throw 'Expected a version tag such as v1.0.0 or v1.0.0-rc.1.'
}

$root = Split-Path -Parent $PSScriptRoot
# Verify the dependency again before putting it in a release archive.
& (Join-Path $PSScriptRoot 'restore-deps.ps1')
$files = @('patch.exe', 'dnlib.dll', 'README.md', 'LICENSE', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md')
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)) { throw "Missing release file: $file" }
}
$dist = Join-Path $root 'dist'
[void][IO.Directory]::CreateDirectory($dist)
$archiveName = "pcl-patcher-$Tag-windows.zip"
$archivePath = Join-Path $dist $archiveName
if (Test-Path -LiteralPath $archivePath) { throw "Archive already exists: $archivePath" }
Compress-Archive -LiteralPath @($files | ForEach-Object { Join-Path $root $_ }) -DestinationPath $archivePath
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), "$hash  $archiveName`n", [Text.Encoding]::ASCII)
Write-Host "Created $archivePath and SHA256SUMS.txt"
