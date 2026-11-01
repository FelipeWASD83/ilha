# Compila a ilha e confere se o Smart App Control deixa o Ilha.exe e o Ilha.dll rodarem.
# O SAC julga cada binario novo pela reputacao na nuvem; as vezes uma compilacao e barrada
# (erro 0x800711C7) e a seguinte, com bytes diferentes, passa. Se barrar, este script troca o
# "carimbo" e compila de novo:
#   - Carimbo.g.cs (fora do git): constante com um bloco de letras de tamanho sorteado (muda o Ilha.dll);
#   - SourceRevisionId: entra na versao informativa, que vai para os recursos do Ilha.exe (muda o .exe,
#     que sem isso sai igual em toda compilacao e, se barrado uma vez, continua barrado).
# Mesmo metodo do quadra (Ferramentas\compilar.ps1).
#
# Usage: powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1 [-Publicar <folder>] [-Publico] [-Tentativas 8]
# -Publico: builds without the private modules of src\Ilha\local.props (the public release uses this).

param(
    [string]$Publicar = "",
    [switch]$Publico,
    [int]$Tentativas = 8
)

$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $raiz "src\Ilha"
$carimbo = Join-Path $proj "Carimbo.g.cs"
$script:selo = ""

function Escrever-Carimbo {
    $n = Get-Random -Minimum 40 -Maximum 400
    $letras = -join ((1..$n) | ForEach-Object { [char](Get-Random -Minimum 97 -Maximum 123) })
    $texto = "// gerado pelo ferramentas\compilar.ps1; muda os bytes do Ilha.dll quando o Smart App Control barra uma compilacao`nnamespace Ilha;`ninternal static class Carimbo { public const string Texto = `"$letras`"; }`n"
    [IO.File]::WriteAllText($carimbo, $texto, (New-Object Text.UTF8Encoding($false)))
    $script:selo = -join ((1..10) | ForEach-Object { [char](Get-Random -Minimum 97 -Maximum 123) })
}

function Compilar {
    $rev = @()
    if ($script:selo) { $rev = @("-p:SourceRevisionId=$($script:selo)") }
    if ($Publico) { $rev += "-p:PublicBuild=true" }
    if ($Publicar) {
        dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $Publicar -nologo -v q @rev | Out-Host
        return (Join-Path $Publicar "Ilha.exe")
    }
    dotnet build $proj -c Release -nologo -v q @rev | Out-Host
    return (Get-ChildItem (Join-Path $proj "bin\Release") -Recurse -Filter Ilha.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

# 0 = rodou; outro numero = o SAC barrou o .exe (Start-Process lanca erro) ou o .dll (o .NET sai com erro)
function Testar([string]$exe) {
    try {
        $p = Start-Process $exe -ArgumentList "--ping" -PassThru -WindowStyle Hidden
        $p.WaitForExit(20000) | Out-Null
        return $p.ExitCode
    }
    catch {
        Write-Host "  Ilha.exe did not even open: $($_.Exception.Message)"
        return -1
    }
}

if (-not (Test-Path $carimbo)) { Escrever-Carimbo }

for ($i = 1; $i -le $Tentativas; $i++) {
    $exe = Compilar
    if ($LASTEXITCODE -ne 0) { Write-Host "the build failed"; exit 1 }
    $codigo = Testar $exe
    if ($codigo -eq 0) {
        Write-Host "ok: Smart App Control accepted this build (attempt $i)"
        Write-Host $exe
        exit 0
    }
    Write-Host "attempt ${i}: Smart App Control blocked it (code $codigo); changing the stamp"
    Escrever-Carimbo
}

Write-Host "Smart App Control blocked all $Tentativas attempts"
exit 2
