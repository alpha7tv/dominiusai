# Gera os executáveis Windows x64 (autocontidos) e, se o Inno Setup estiver instalado, o instalador .exe.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
dotnet test "$root\tests\DominiusPlay.Tests" -c Release
dotnet publish "$root\src\DominiusPlay.Desktop" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root\artifacts\publish\desktop"
dotnet publish "$root\src\DominiusPlay.Server" -c Release -r win-x64 --self-contained true -o "$root\artifacts\publish\server"
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (Test-Path $iscc) { & $iscc "$root\installer\DominiusPlayCRM.iss" } else { Write-Warning "Inno Setup 6 não encontrado. Instale em https://jrsoftware.org/isinfo.php e rode novamente." }
