$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
[void][IO.Directory]::CreateDirectory((Join-Path $root 'obj'))
$testExe = Join-Path $root 'obj/SecurityTests.exe'
& $compiler /nologo /target:exe "/out:$testExe" "/r:$(Join-Path $root 'dnlib.dll')" (Join-Path $root 'tests/SecurityTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'dnlib.dll') -Destination (Join-Path $root 'obj/dnlib.dll') -Force
& $testExe (Join-Path $root 'patch.exe')
if ($LASTEXITCODE -ne 0) { throw 'Security regression tests failed.' }
