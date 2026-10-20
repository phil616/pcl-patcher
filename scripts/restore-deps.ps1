$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'dependencies.ps1')
$dependency = Get-DnlibDependency
$version = $dependency.version
$packageHash = $dependency.packageSha256
$dllHash = $dependency.dllSha256
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
