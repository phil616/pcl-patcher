$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root ('obj/release-tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$tag = 'v0.0.0-regression.1'
$output = Join-Path $work 'release'
$checks = 0

function Assert-Release([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "FAILED: $Message" }
    $script:checks++
}
function Get-EntryHash($Entry, [string] $Algorithm) {
    $stream = $Entry.Open()
    $hasher = [Security.Cryptography.HashAlgorithm]::Create($Algorithm)
    try { return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose(); $stream.Dispose() }
}

# Keep generated test artifacts under obj for inspection; never alter the real dependency.
& (Join-Path $PSScriptRoot 'package.ps1') -Tag $tag -OutputDirectory $output
$archivePath = Join-Path $output "pcl-patcher-$tag-windows.zip"
$sbomPath = Join-Path $output "pcl-patcher-$tag.spdx.json"
$sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
Assert-Release ($sbom.spdxVersion -ceq 'SPDX-2.3' -and $sbom.dataLicense -ceq 'CC0-1.0') 'SPDX version and data license'
Assert-Release ($sbom.packages.Count -eq 2 -and $sbom.files.Count -eq 8) 'complete component and file inventory'
$component = @($sbom.packages | Where-Object name -eq 'dnlib')[0]
$dependency = Get-Content -LiteralPath (Join-Path $root 'dependencies.lock.json') -Raw | ConvertFrom-Json
Assert-Release ($component.versionInfo -ceq $dependency.dnlib.version) 'SBOM dependency version matches lock'
Assert-Release ($component.checksums[0].checksumValue -ceq $dependency.dnlib.packageSha256) 'SBOM upstream package checksum'
Assert-Release ($component.externalRefs[0].referenceLocator -ceq "pkg:nuget/dnlib@$($dependency.dnlib.version)") 'dependency package URL'
Assert-Release (@($sbom.relationships | Where-Object {
    $_.spdxElementId -ceq 'SPDXRef-pcl-patcher' -and $_.relationshipType -ceq 'DEPENDS_ON' -and $_.relatedSpdxElement -ceq 'SPDXRef-dnlib'
}).Count -eq 1) 'root dependency relationship'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $expected = @('patch.exe', 'dnlib.dll', 'README.md', 'LICENSE', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md', 'packages.config', 'dependencies.lock.json', 'SBOM.spdx.json')
    Assert-Release ($archive.Entries.Count -eq $expected.Count) 'ZIP contains only intended release files'
    foreach ($name in $expected) { Assert-Release ($null -ne $archive.GetEntry($name)) "ZIP includes $name" }
    $reader = [IO.StreamReader]::new($archive.GetEntry('SBOM.spdx.json').Open())
    try { Assert-Release ($reader.ReadToEnd() -ceq [IO.File]::ReadAllText($sbomPath)) 'embedded SBOM exactly matches signed sidecar' }
    finally { $reader.Dispose() }
    foreach ($record in $sbom.files) {
        $name = $record.fileName.Substring(2)
        $entry = $archive.GetEntry($name)
        Assert-Release ($null -ne $entry) "SBOM file exists in ZIP: $name"
        foreach ($checksum in $record.checksums) {
            Assert-Release ((Get-EntryHash $entry $checksum.algorithm) -ceq $checksum.checksumValue) "SBOM $($checksum.algorithm) matches shipped $name"
        }
        Assert-Release (@($sbom.relationships | Where-Object { $_.relationshipType -ceq 'CONTAINS' -and $_.relatedSpdxElement -ceq $record.SPDXID }).Count -eq 1) "file has one package owner: $name"
    }
    foreach ($package in $sbom.packages) {
        $members = @($sbom.relationships | Where-Object { $_.relationshipType -ceq 'CONTAINS' -and $_.spdxElementId -ceq $package.SPDXID } | ForEach-Object { $_.relatedSpdxElement })
        $hashes = @($sbom.files | Where-Object { $_.SPDXID -cin $members } | ForEach-Object { Get-EntryHash ($archive.GetEntry($_.fileName.Substring(2))) 'SHA1' } | Sort-Object)
        $sha = [Security.Cryptography.SHA1]::Create()
        try { $code = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes(($hashes -join '')))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        Assert-Release ($code -ceq $package.packageVerificationCode.packageVerificationCodeValue) "package verification code: $($package.name)"
    }
}
finally { $archive.Dispose() }

$lines = @(Get-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt'))
Assert-Release ($lines.Count -eq 2) 'ZIP and SBOM checksums published'
foreach ($line in $lines) {
    Assert-Release ($line -cmatch '\A([a-f0-9]{64})  ([a-zA-Z0-9.-]+)\z') 'checksum manifest format'
    $hash = (Get-FileHash -LiteralPath (Join-Path $output $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Release ($hash -ceq $Matches[1]) 'release file matches checksum manifest'
}
$before = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$rejected = $false
try { & (Join-Path $PSScriptRoot 'package.ps1') -Tag $tag -OutputDirectory $output }
catch { $rejected = $_.Exception.Message -match 'already exists' }
Assert-Release ($rejected -and (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ceq $before) 'repeat packaging preserves existing release'
$rejected = $false
try { & (Join-Path $PSScriptRoot 'package.ps1') -Tag '../unsafe' -OutputDirectory (Join-Path $work 'invalid') }
catch { $rejected = $_.Exception.Message -match 'Expected a version tag' }
Assert-Release ($rejected -and -not (Test-Path -LiteralPath (Join-Path $work 'invalid'))) 'invalid tag rejected before writing'

$isolated = Join-Path $work 'dependency'
$isolatedScripts = Join-Path $isolated 'scripts'
[void][IO.Directory]::CreateDirectory($isolatedScripts)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dependencies.ps1') -Destination $isolatedScripts
Copy-Item -LiteralPath (Join-Path $root 'dependencies.lock.json') -Destination $isolated
[xml] $manifest = Get-Content -LiteralPath (Join-Path $root 'packages.config') -Raw
$manifest.packages.package.version = '99.0.0'
$manifest.Save((Join-Path $isolated 'packages.config'))
$rejected = $false
try { & { . (Join-Path $isolatedScripts 'dependencies.ps1'); Get-DnlibDependency } }
catch { $rejected = $_.Exception.Message -match 'manifest and hash lock must agree' }
Assert-Release $rejected 'dependency manifest update requires reviewed hashes'

$tampered = Join-Path $work 'tampered'
[void][IO.Directory]::CreateDirectory($tampered)
[IO.File]::WriteAllText((Join-Path $tampered 'dnlib.dll'), 'untrusted bytes')
$rejected = $false
try { & (Join-Path $PSScriptRoot 'sbom.ps1') -Tag $tag -ContentDirectory $tampered -Destination (Join-Path $tampered 'SBOM.spdx.json') }
catch { $rejected = $_.Exception.Message -match 'reviewed dependency hash' }
Assert-Release ($rejected -and -not (Test-Path -LiteralPath (Join-Path $tampered 'SBOM.spdx.json'))) 'tampered DLL cannot receive a generated SBOM'
Write-Host "PASS: $checks release package and SBOM regression checks."
