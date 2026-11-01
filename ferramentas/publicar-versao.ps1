# Publica a versão atual da ilha como release no GitHub: é ela que o botão "Atualizar" da ilha baixa.
# Quem roda é o Felipe (publicar é decisão dele). Precisa do gh logado (gh auth login) com permissão no repositório.
#
# Antes: subir a versão no src\Ilha\Ilha.csproj (<Version>) e no topo do CHANGELOG.md, commitar e dar push.
#
# O que faz: confere que a versão do csproj bate com o topo do CHANGELOG.md, que não há nada sem commit e que o
# commit já está no GitHub; publica pelo ferramentas\compilar.ps1 -Publicar (com a checagem do Smart App Control);
# tira do pacote as logos e as fotos de fones (marcas de terceiros) e qualquer carimbo; grava o pacote.json (versão
# e lista de arquivos, que o atualizador usa para saber o que é dele); zipa como Ilha-X.Y.Z.zip e cria a release
# vX.Y.Z com o zip e as notas da seção da versão no CHANGELOG. O GitHub calcula o SHA-256 do zip sozinho, e o
# atualizador confere por ele.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\ferramentas\publicar-versao.ps1
#   powershell -ExecutionPolicy Bypass -File .\ferramentas\publicar-versao.ps1 -SoGerar <folder>
#     (only builds the zip in the folder, without git and without gh: to check the package before publishing)

param(
    [string]$SoGerar = "",
    [string]$Repositorio = "FelipeWASD83/ilha"
)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz

function Rodar([string]$exe, [string[]]$argumentos) {
    # No PowerShell 5.1, com 'Stop', qualquer linha no stderr vira erro fatal mesmo quando deu certo.
    $ErrorActionPreference = 'Continue'
    $saida = & $exe @argumentos 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ codigo = $LASTEXITCODE; saida = (($saida -join "`n").Trim()) }
}

# --- versão: csproj e topo do CHANGELOG
$csproj = [IO.File]::ReadAllText((Join-Path $raiz 'src\Ilha\Ilha.csproj'))
if ($csproj -notmatch '<Version>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</Version>') { throw 'Could not find <Version> in src\Ilha\Ilha.csproj.' }
$versao = $Matches[1]
$changelog = [IO.File]::ReadAllText((Join-Path $raiz 'CHANGELOG.md'))
$linhas = $changelog -split "`r?`n"
$topo = $linhas | Where-Object { $_ -match '^## ' } | Select-Object -First 1
if (-not $topo -or $topo -notmatch '^## ([0-9]+\.[0-9]+\.[0-9]+)') { throw 'CHANGELOG.md does not have a "## X.Y.Z" section at the top.' }
if ($Matches[1] -ne $versao) { throw "The csproj says $versao and the top of CHANGELOG.md says $($Matches[1]). Fix that first." }
Write-Host "Version $versao"

# --- notas: só a seção desta versão
$inicio = [array]::IndexOf($linhas, $topo)
$notas = @()
for ($i = $inicio + 1; $i -lt $linhas.Length; $i++) {
    if ($linhas[$i] -match '^## ') { break }
    $notas += $linhas[$i]
}
$textoNotas = ($notas -join "`n").Trim()
if (-not $textoNotas) { throw "The $versao section of CHANGELOG.md is empty." }

# --- git e gh (não no modo de só gerar)
$local = ''
if (-not $SoGerar) {
    if ((Rodar git @('status', '--porcelain')).saida) { throw 'There are uncommitted changes. Commit before publishing.' }
    $r = Rodar git @('fetch', '-q', 'origin')
    if ($r.codigo -ne 0) { throw "Could not reach GitHub (git fetch): $($r.saida)" }
    $local = (Rodar git @('rev-parse', 'HEAD')).saida
    $r = Rodar git @('rev-parse', '@{u}')
    if ($r.codigo -ne 0) { throw 'This branch is not linked to GitHub. Run: git push -u origin main' }
    if ($local -ne $r.saida) { throw 'The current commit has not been pushed yet (git push) or is behind GitHub.' }
    if ((Rodar gh @('auth', 'status')).codigo -ne 0) { throw 'gh is not logged in. Run: gh auth login' }
    if ((Rodar gh @('release', 'view', "v$versao", '--repo', $Repositorio)).codigo -eq 0) { throw "Release v$versao already exists in $Repositorio." }
}

# --- pastas de trabalho
$trabalho = if ($SoGerar) { [IO.Path]::GetFullPath($SoGerar) } else { Join-Path $env:TEMP "ilha-release-$versao" }
$pub = Join-Path $trabalho 'Ilha'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
New-Item -ItemType Directory -Force $trabalho | Out-Null

Write-Host 'Building (with the Smart App Control check)...'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'compilar.ps1') -Publicar $pub -Publico
if ($LASTEXITCODE -ne 0) { throw 'The build failed or Smart App Control blocked every attempt (see above).' }

# --- fora do pacote: marcas de terceiros e carimbo
foreach ($p in @('imagens\logos', 'imagens\fones')) {
    $alvo = Join-Path $pub $p
    if (Test-Path $alvo) { Remove-Item $alvo -Recurse -Force }
}
Get-ChildItem $pub -Recurse -File | Where-Object { $_.Name -like 'Carimbo*' } | Remove-Item -Force
# pastas que ficaram vazias (imagens\ sem as logos)
Get-ChildItem $pub -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending |
    Where-Object { -not (Get-ChildItem $_.FullName -Force) } | Remove-Item -Force

foreach ($f in @('Ilha.exe', 'Ilha.dll', 'animacoes.json', 'icone.ico')) {
    if (-not (Test-Path (Join-Path $pub $f))) { throw "$f is missing from the published files." }
}
if (-not (Get-ChildItem (Join-Path $pub 'sons') -Filter *.wav -ErrorAction SilentlyContinue)) { throw 'The sounds were not included in the published files.' }
$v = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $pub 'Ilha.dll')).Version
if ("$($v.Major).$($v.Minor).$($v.Build)" -ne $versao) { throw "The published Ilha.dll is version $($v.Major).$($v.Minor).$($v.Build), not $versao." }

# --- pacote.json: versão e todos os arquivos (ele mesmo incluído), com "/"
$base = (Resolve-Path $pub).Path.TrimEnd('\') + '\'
$arquivos = @(Get-ChildItem $pub -Recurse -File | ForEach-Object { $_.FullName.Substring($base.Length).Replace('\', '/') } | Where-Object { $_ -ne 'pacote.json' } | Sort-Object)
$arquivos += 'pacote.json'
$json = [ordered]@{ versao = $versao; arquivos = $arquivos } | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText((Join-Path $pub 'pacote.json'), $json, (New-Object Text.UTF8Encoding($false)))

# --- zip com os arquivos na raiz (o atualizador também aceita uma pasta dentro)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $trabalho "Ilha-$versao.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($pub, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

# --- confere o que entrou
$z = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $nomes = @($z.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
} finally { $z.Dispose() }
$proibidos = @($nomes | Where-Object { $_ -match '^imagens/(logos|fones)/' -or $_ -match 'Carimbo' })
if ($proibidos.Count -gt 0) { throw "The zip contains files that must not ship: $($proibidos -join ', ')" }
if ($nomes -notcontains 'pacote.json' -or $nomes -notcontains 'Ilha.exe') { throw 'The zip is missing pacote.json or Ilha.exe.' }
$faltam = @($arquivos | Where-Object { $nomes -notcontains $_ })
if ($faltam.Count -gt 0) { throw "The zip does not have everything on the list: $($faltam -join ', ')" }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$tamanho = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "Package: $zip ($tamanho MB, $($arquivos.Count) files)"
Write-Host "SHA-256: $hash"

if ($SoGerar) {
    Write-Host 'Only built (without git and without gh). To check it with the island''s own updater:'
    Write-Host "  `$env:ILHA_TESTE_PACOTE = '$zip'; .\src\Ilha\bin\Release\net10.0-windows10.0.26100.0\win-x64\Ilha.exe --testar atualizador"
    exit 0
}

$arqNotas = Join-Path $trabalho 'notas.md'
[IO.File]::WriteAllText($arqNotas, $textoNotas, (New-Object Text.UTF8Encoding($false)))
Write-Host "Publishing v$versao to $Repositorio..."
$r = Rodar gh @('release', 'create', "v$versao", $zip, '--repo', $Repositorio, '--title', "Ilha $versao", '--notes-file', $arqNotas, '--target', $local)
if ($r.codigo -ne 0) { throw "gh did not create the release: $($r.saida)" }
Write-Host $r.saida
Remove-Item $trabalho -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Done. The installed island sees this version in "Check for updates" (or by itself, at most once a day).'
