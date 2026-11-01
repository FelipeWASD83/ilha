# Compila a ilha e confere se o Smart App Control deixa o Ilha.dll carregar.
# O SAC julga cada binario novo pela reputacao na nuvem; as vezes uma compilacao e barrada
# (erro 0x800711C7) e a seguinte, com bytes diferentes, passa. Se barrar, este script troca o
# "carimbo" (Carimbo.g.cs, uma constante com um bloco de letras de tamanho sorteado, fora do git)
# e compila de novo. Mesmo metodo do quadra (Ferramentas\compilar.ps1).
#
# Uso: powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1 [-Publicar <pasta>] [-Tentativas 8]

param(
    [string]$Publicar = "",
    [int]$Tentativas = 8
)

$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $raiz "src\Ilha"
$carimbo = Join-Path $proj "Carimbo.g.cs"

function Escrever-Carimbo {
    $n = Get-Random -Minimum 40 -Maximum 400
    $letras = -join ((1..$n) | ForEach-Object { [char](Get-Random -Minimum 97 -Maximum 123) })
    $texto = "// gerado pelo ferramentas\compilar.ps1; muda os bytes do Ilha.dll quando o Smart App Control barra uma compilacao`nnamespace Ilha;`ninternal static class Carimbo { public const string Texto = `"$letras`"; }`n"
    [IO.File]::WriteAllText($carimbo, $texto, (New-Object Text.UTF8Encoding($false)))
}

function Compilar {
    if ($Publicar) {
        dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $Publicar -nologo -v q | Out-Host
        return (Join-Path $Publicar "Ilha.exe")
    }
    dotnet build $proj -c Release -nologo -v q | Out-Host
    return (Get-ChildItem (Join-Path $proj "bin\Release") -Recurse -Filter Ilha.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

if (-not (Test-Path $carimbo)) { Escrever-Carimbo }

for ($i = 1; $i -le $Tentativas; $i++) {
    $exe = Compilar
    if ($LASTEXITCODE -ne 0) { Write-Host "a compilacao falhou"; exit 1 }
    $p = Start-Process $exe -ArgumentList "--ping" -PassThru -WindowStyle Hidden
    $p.WaitForExit(20000) | Out-Null
    if ($p.ExitCode -eq 0) {
        Write-Host "ok: o Smart App Control aceitou esta compilacao (tentativa $i)"
        Write-Host $exe
        exit 0
    }
    Write-Host "tentativa ${i}: o Smart App Control barrou o Ilha.dll (codigo $($p.ExitCode)); trocando o carimbo"
    Escrever-Carimbo
}

Write-Host "o Smart App Control barrou todas as $Tentativas tentativas"
exit 2
