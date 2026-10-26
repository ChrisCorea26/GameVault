# Builds docs/manual/GameVault-User-Manual.pdf from the Markdown chapters in docs/manual/.
# Needs pandoc (winget install JohnMacFarlane.Pandoc) and Microsoft Edge or Google Chrome (version 131 or later).
# Pass -Browser with the browser's path if it isn't found automatically.
param([string]$Browser)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$out = Join-Path $here 'GameVault-User-Manual.pdf'
$work = Join-Path ([IO.Path]::GetTempPath()) ("gv-manual-" + [Guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null

try {
    if (-not (Get-Command pandoc -ErrorAction SilentlyContinue)) {
        throw 'pandoc is required: winget install JohnMacFarlane.Pandoc'
    }

    if (-not $Browser) {
        $Browser = @(
            "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
            "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
            "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
            "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $Browser) { throw 'No Edge or Chrome found. Pass -Browser <path>.' }

    # Chapters are numbered files; the build order is their file-name order.
    $chapters = Get-ChildItem $here -Filter '[0-9][0-9]-*.md' | Sort-Object Name | ForEach-Object FullName

    & pandoc @chapters `
        --from markdown+pipe_tables+fenced_divs+bracketed_spans `
        --to html5 --standalone --toc --toc-depth=2 `
        --metadata title="GameVault User Manual" `
        --template (Join-Path $here 'template.html') `
        --css manual.css `
        --output (Join-Path $work 'manual.html')
    if ($LASTEXITCODE -ne 0) { throw 'pandoc failed.' }
    Copy-Item (Join-Path $here 'manual.css') $work

    $url = ([Uri](Join-Path $work 'manual.html')).AbsoluteUri
    & $Browser --headless --disable-gpu --no-pdf-header-footer "--print-to-pdf=$out" $url 2>$null | Out-Null
    # The browser returns before the file is flushed on some machines.
    for ($i = 0; $i -lt 50 -and -not (Test-Path $out); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $out)) { throw 'The browser did not write the PDF.' }
    Write-Host "Wrote $out"
}
finally {
    Remove-Item -Recurse -Force $work
}
