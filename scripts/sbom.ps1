param(
    [Parameter(Mandatory = $true)][string] $Tag,
    [Parameter(Mandatory = $true)][string] $ContentDirectory,
    [Parameter(Mandatory = $true)][string] $Destination
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'dependencies.ps1')
$dependency = Get-DnlibDependency
$files = @(Get-ChildItem -LiteralPath $ContentDirectory -File | Sort-Object Name)
if ($files.Count -eq 0 -or @($files | Where-Object Name -eq 'dnlib.dll').Count -ne 1) {
    throw 'Expected release files including dnlib.dll before creating the SBOM.'
}
if ((Get-Sha256 (Join-Path $ContentDirectory 'dnlib.dll')) -cne $dependency.dllSha256) {
    throw 'Release dnlib.dll does not match the reviewed dependency hash.'
}

function Get-VerificationCode([object[]] $Records) {
    $hashes = @($Records | ForEach-Object { $_.checksums[0].checksumValue } | Sort-Object)
    $sha = [Security.Cryptography.SHA1]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes(($hashes -join '')))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

$records = @()
$relationships = @([ordered]@{ spdxElementId='SPDXRef-DOCUMENT'; relationshipType='DESCRIBES'; relatedSpdxElement='SPDXRef-pcl-patcher' })
foreach ($file in $files) {
    $id = 'SPDXRef-File-' + $file.Name.Replace('.', '-')
    $record = [ordered]@{
        SPDXID = $id
        fileName = './' + $file.Name
        checksums = @(
            [ordered]@{ algorithm='SHA1'; checksumValue=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA1).Hash.ToLowerInvariant() },
            [ordered]@{ algorithm='SHA256'; checksumValue=(Get-Sha256 $file.FullName) }
        )
        licenseConcluded = 'NOASSERTION'
        licenseInfoInFiles = @('NOASSERTION')
        copyrightText = 'NOASSERTION'
    }
    $records += $record
    $owner = 'SPDXRef-pcl-patcher'
    if ($file.Name -eq 'dnlib.dll') { $owner = 'SPDXRef-dnlib' }
    $relationships += [ordered]@{ spdxElementId=$owner; relationshipType='CONTAINS'; relatedSpdxElement=$id }
}
$rootFiles = @($records | Where-Object fileName -ne './dnlib.dll')
$dnlibFiles = @($records | Where-Object fileName -eq './dnlib.dll')
$relationships += [ordered]@{ spdxElementId='SPDXRef-pcl-patcher'; relationshipType='DEPENDS_ON'; relatedSpdxElement='SPDXRef-dnlib' }
$version = $dependency.version
$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "pcl-patcher-$Tag-windows"
    documentNamespace = 'https://github.com/phil616/pcl-patcher/sbom/' + [Guid]::NewGuid().ToString('D')
    creationInfo = [ordered]@{
        creators = @('Tool: pcl-patcher-sbom-1.0')
        created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
    comment = 'Inventory of files shipped in this release. This SBOM excludes itself to avoid a self-referential checksum. dnlib is an upstream NuGet binary, not built by this project. NOASSERTION fields have not been independently determined.'
    packages = @(
        [ordered]@{
            SPDXID='SPDXRef-pcl-patcher'; name='pcl-patcher'; versionInfo=$Tag.Substring(1)
            downloadLocation='NOASSERTION'; filesAnalyzed=$true
            packageVerificationCode=[ordered]@{ packageVerificationCodeValue=(Get-VerificationCode $rootFiles) }
            licenseInfoFromFiles=@('NOASSERTION')
            licenseDeclared='MIT'; licenseConcluded='NOASSERTION'; copyrightText='NOASSERTION'
            homepage='https://github.com/phil616/pcl-patcher'
        },
        [ordered]@{
            SPDXID='SPDXRef-dnlib'; name='dnlib'; versionInfo=$version
            downloadLocation="https://api.nuget.org/v3-flatcontainer/dnlib/$version/dnlib.$version.nupkg"
            filesAnalyzed=$true
            packageVerificationCode=[ordered]@{ packageVerificationCodeValue=(Get-VerificationCode $dnlibFiles) }
            licenseInfoFromFiles=@('NOASSERTION')
            checksums=@([ordered]@{ algorithm='SHA256'; checksumValue=$dependency.packageSha256 })
            licenseDeclared='MIT'; licenseConcluded='NOASSERTION'; copyrightText='Copyright (C) 2012-2019 de4dot@gmail.com'
            externalRefs=@([ordered]@{ referenceCategory='PACKAGE-MANAGER'; referenceType='purl'; referenceLocator="pkg:nuget/dnlib@$version" })
            comment='Only lib/net45/dnlib.dll is redistributed. Package checksum identifies the upstream NuGet archive; file hashes and verification code cover the redistributed DLL.'
        }
    )
    files = $records
    relationships = $relationships
}
[IO.File]::WriteAllText($Destination, ($document | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
