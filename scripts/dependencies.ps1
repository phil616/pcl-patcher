function Get-DnlibDependency {
    $root = Split-Path -Parent $PSScriptRoot
    [xml] $manifest = Get-Content -LiteralPath (Join-Path $root 'packages.config') -Raw
    $packages = @($manifest.packages.package)
    $lock = Get-Content -LiteralPath (Join-Path $root 'dependencies.lock.json') -Raw | ConvertFrom-Json
    if ($packages.Count -ne 1 -or $packages[0].id -cne 'dnlib' -or $packages[0].targetFramework -cne 'net45' -or
        $lock.formatVersion -ne 1 -or $packages[0].version -cne $lock.dnlib.version -or
        $lock.dnlib.version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z' -or
        $lock.dnlib.packageSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $lock.dnlib.dllSha256 -cnotmatch '\A[a-f0-9]{64}\z') {
        throw 'Dependency manifest and hash lock must agree. Review and update both when upgrading dnlib.'
    }
    return $lock.dnlib
}

function Get-Sha256([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}
