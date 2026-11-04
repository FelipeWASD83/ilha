# Sends the third party images (headphone photos and the Claude/GPT logos and animation strips) to the PRIVATE
# repository FelipeWASD83/ilha-imagens, where the "Download images" button of the public build gets them.
# They never go to the public ilha repository (brands of other companies).
#
# Usage: powershell -ExecutionPolicy Bypass -File .\ferramentas\publicar-imagens.ps1 [-Simular]
# -Simular: shows what would change, commits and pushes nothing.
# Works in a temporary clone; nothing is left in the project folder. Creates the repository (private) if it does not
# exist yet.
param([switch]$Simular)

$ErrorActionPreference = 'Stop'
$repo = 'FelipeWASD83/ilha-imagens'
$raiz = Split-Path $PSScriptRoot -Parent
$origem = Join-Path $raiz 'src\Ilha\imagens'
foreach ($p in 'fones', 'logos') {
    if (-not (Test-Path (Join-Path $origem $p))) { throw "missing $origem\$p" }
}

$trabalho = Join-Path $env:TEMP ('ilha-imagens-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $trabalho | Out-Null
try {
    # gh writes "Could not resolve" to stderr when it does not exist; in PowerShell 5.1 that would stop the script
    $ErrorActionPreference = 'Continue'
    & gh repo view $repo --json name 2>&1 | Out-Null
    $existe = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'

    $clone = Join-Path $trabalho 'repo'
    if ($existe) {
        & gh repo clone $repo $clone -- --quiet
        if ($LASTEXITCODE -ne 0) { throw "could not clone $repo" }
    } else {
        New-Item -ItemType Directory $clone | Out-Null
        & git -C $clone init -q -b main
    }

    foreach ($p in 'fones', 'logos') {
        # mirror only inside fones\ and logos\ (the clone's root holds .git)
        & robocopy (Join-Path $origem $p) (Join-Path $clone $p) /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed on $p ($LASTEXITCODE)" }
    }
    $leia = @"
# ilha-imagens (private)

Third party images used by the island (FelipeWASD83/ilha): official headphone photos in ``fones/`` (sources in
``fones/FONTES.md``) and the Claude and ChatGPT logos and animation strips in ``logos/``. They stay out of the public
repository because they are other companies' brands.

The public build of the island has a "Download images" button in Settings > General that downloads ``imagens.zip``
from the release ``imagens`` of this repository with the GitHub CLI (``gh``) of the PC, so it only works for an account
with access to it.

Updated with ``ferramentas\publicar-imagens.ps1`` in the ilha project.
"@
    [IO.File]::WriteAllText((Join-Path $clone 'README.md'), $leia.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))

    & git -C $clone add -A
    $mudancas = & git -C $clone status --porcelain
    $n = @($mudancas | Where-Object { $_ }).Count
    # the island downloads imagens.zip from the fixed release "imagens" (a release file comes from GitHub's CDN in
    # seconds; the repository's zipball came at ~200 KB/s, 107 s for 21 MB on 04/11/2026)
    $ErrorActionPreference = 'Continue'
    $temRelease = $existe -and ((& gh release view imagens -R $repo --json assets 2>&1 | Out-String) -match 'imagens\.zip')
    $ErrorActionPreference = 'Stop'
    if ($n -eq 0 -and $temRelease) { Write-Host "Nothing changed: $repo already has these images."; return }
    Write-Host "$n file(s) changed; release file present: $temRelease"
    if ($Simular) { @($mudancas) | Select-Object -First 30 | ForEach-Object { Write-Host "  $_" }; return }

    if ($n -gt 0) {
        $msg = Join-Path $trabalho 'msg.txt'
        [IO.File]::WriteAllText($msg, "Imagens da ilha atualizadas ($n arquivo(s))`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`n", (New-Object Text.UTF8Encoding $false))
        & git -C $clone commit -q -F $msg
        if ($LASTEXITCODE -ne 0) { throw 'commit failed' }
        if ($existe) {
            & git -C $clone push -q origin main
        } else {
            Push-Location $clone
            try { & gh repo create $repo --private --source . --remote origin --push --description 'Third party images of the ilha (private)' }
            finally { Pop-Location }
        }
        if ($LASTEXITCODE -ne 0) { throw 'push failed' }
    }

    # imagens.zip: fones\ and logos\ at its root
    $zip = Join-Path $trabalho 'imagens.zip'
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($p in 'fones', 'logos') {
            Get-ChildItem (Join-Path $clone $p) -File | ForEach-Object {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, "$p/$($_.Name)", 'Optimal') | Out-Null
            }
        }
    } finally { $z.Dispose() }
    $ErrorActionPreference = 'Continue'
    & gh release view imagens -R $repo 2>&1 | Out-Null
    $existeRelease = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    if ($existeRelease) {
        & gh release upload imagens $zip -R $repo --clobber
    } else {
        & gh release create imagens $zip -R $repo --title 'Imagens da ilha' --notes 'imagens.zip: fones/ and logos/. Updated by ferramentas\publicar-imagens.ps1; the island downloads it from Settings > General.'
    }
    if ($LASTEXITCODE -ne 0) { throw 'release upload failed' }
    Write-Host ("Done: https://github.com/$repo (imagens.zip {0:N1} MB)" -f ((Get-Item $zip).Length / 1MB))
}
finally {
    Remove-Item $trabalho -Recurse -Force -ErrorAction SilentlyContinue
}
