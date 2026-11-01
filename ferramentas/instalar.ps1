# Instala (ou atualiza) a ilha em %LOCALAPPDATA%\Programs\Ilha, com atalho no Menu Iniciar.
# A copia instalada grava sozinha o "iniciar com o Windows" (HKCU\...\Run) quando abre.
# A configuracao fica em %USERPROFILE%\.ilha e nao e tocada.
#
# Uso (na pasta do projeto):
#   powershell -ExecutionPolicy Bypass -File .\ferramentas\instalar.ps1 [-NaoAbrir]
#   powershell -ExecutionPolicy Bypass -File .\ferramentas\instalar.ps1 -Desinstalar
#
# Rodar num terminal normal, nao de dentro do app do Claude: processo aberto pelo app (pacote MSIX) enxerga um
# AppData e um registro virtuais, e a instalacao iria para a copia virtual.

param([switch]$NaoAbrir, [switch]$Desinstalar)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$destino = Join-Path $env:LOCALAPPDATA 'Programs\Ilha'
$atalho = Join-Path ([Environment]::GetFolderPath('Programs')) 'Ilha.lnk'
$temp = Join-Path $env:TEMP 'ilha-instalar'

function Fechar-Ilha {
    $abertas = Get-Process Ilha -ErrorAction SilentlyContinue
    if (-not $abertas) { return }
    Write-Host 'Fechando a ilha aberta...'
    $exeAberto = $abertas[0].Path
    if ($exeAberto) { Start-Process $exeAberto -ArgumentList '--sair' -Wait -ErrorAction SilentlyContinue }
    foreach ($p in $abertas) { $p.WaitForExit(10000) | Out-Null }
    Get-Process Ilha -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($Desinstalar) {
    Fechar-Ilha
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Ilha' -ErrorAction SilentlyContinue
    if (Test-Path $atalho) { Remove-Item $atalho -Force }
    if (Test-Path $destino) { Remove-Item $destino -Recurse -Force }
    Write-Host 'Ilha desinstalada (a configuracao em %USERPROFILE%\.ilha ficou).'
    exit 0
}

Write-Host 'Compilando (com a checagem do Smart App Control)...'
if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'compilar.ps1') -Publicar $temp
if ($LASTEXITCODE -ne 0) { throw 'A compilacao falhou ou o Smart App Control barrou todas as tentativas (veja acima).' }
if (-not (Test-Path (Join-Path $temp 'animacoes.json'))) { throw 'O animacoes.json nao foi junto na publicacao.' }

Fechar-Ilha

Write-Host "Instalando em $destino"
if (Test-Path $destino) { Get-ChildItem $destino -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $destino | Out-Null
Copy-Item (Join-Path $temp '*') $destino -Recurse -Force
Remove-Item $temp -Recurse -Force

$exe = Join-Path $destino 'Ilha.exe'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($atalho)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $destino
$lnk.IconLocation = "$(Join-Path $destino 'icone.ico'),0"
$lnk.Description = 'Dynamic Island para o Windows'
$lnk.Save()
Write-Host "Atalho no Menu Iniciar: $atalho"

if (-not $NaoAbrir) {
    Start-Process $exe
    Write-Host 'Ilha aberta (ela mesma grava o iniciar com o Windows).'
}
Write-Host 'Pronto.'
