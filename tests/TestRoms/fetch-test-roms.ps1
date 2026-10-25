# Downloads the test ROMs listed in manifest.tsv into .cache/ and verifies their SHA-256 hashes.
# These suites have no explicit redistribution license, so they are never committed (SPEC §11.3).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$cache = Join-Path $here '.cache'

foreach ($line in Get-Content (Join-Path $here 'manifest.tsv')) {
    if ($line -match '^\s*(#|$)') { continue }
    $path, $sha, $url = $line -split "`t"
    $target = Join-Path $cache $path
    if ((Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash -eq $sha.ToUpperInvariant())) { continue }

    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Write-Host "Downloading $path"
    $tmp = "$target.tmp"
    Invoke-WebRequest -Uri $url -OutFile $tmp -MaximumRetryCount 3 -RetryIntervalSec 2
    if ((Get-FileHash $tmp -Algorithm SHA256).Hash -ne $sha.ToUpperInvariant()) {
        Remove-Item $tmp
        throw "SHA-256 mismatch for $path (from $url)"
    }
    Move-Item -Force $tmp $target
}
Write-Host "Test ROMs are ready in $cache"
