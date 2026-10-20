$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}

# The package and DLL hashes are pinned together. Review both when updating dnlib.
$version = '4.5.0'
$packageHash = '63bc2f9579568204cc8b30fa9f6700a231bcf868a8032e09118887af3eafee58'
$dllHash = '97ef5328d1d1703924c48d9cd1d45b7227fe3836e116dcded623b6a0dbd52c23'
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root 'dnlib.dll'

if (Test-Path -LiteralPath $destination) {
    if ((Get-Sha256 $destination) -ne $dllHash) {
        throw 'Existing dnlib.dll does not match the pinned version. Remove it and run build.cmd again.'
    }
    Write-Host "Verified dnlib $version (net45)."
    return
}

$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pcl-patcher-deps-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tempDirectory)
$packagePath = Join-Path $tempDirectory 'dnlib.nupkg'
$dllPath = Join-Path $tempDirectory 'dnlib.dll'
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/dnlib/$version/dnlib.$version.nupkg" -OutFile $packagePath
    if ((Get-Sha256 $packagePath) -ne $packageHash) {
        throw 'dnlib NuGet package SHA256 mismatch.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entry = $archive.GetEntry('lib/net45/dnlib.dll')
        if ($null -eq $entry) { throw 'dnlib net45 DLL is missing from the package.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dllPath)
    }
    finally { $archive.Dispose() }
    if ((Get-Sha256 $dllPath) -ne $dllHash) {
        throw 'dnlib DLL SHA256 mismatch.'
    }
    [IO.File]::Copy($dllPath, $destination, $false)
    Write-Host "Restored and verified dnlib $version (net45)."
}
finally {
    foreach ($file in @($packagePath, $dllPath)) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    Remove-Item -LiteralPath $tempDirectory -Force
}
