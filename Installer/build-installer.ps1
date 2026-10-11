# Сборка установщика «Экранного переводчика».
# 1) публикует single-file exe (bin\publish\ScreenTranslator.exe);
# 2) компилирует Installer\ScreenTranslator.iss через Inno Setup 6 (ISCC.exe).
# Результат: bin\installer\ScreenTranslatorSetup.exe
#
# Требуется: .NET SDK 8+ и Inno Setup 6 (winget install JRSoftware.InnoSetup).

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host '==> Публикация single-file exe' -ForegroundColor Cyan
dotnet publish "$root\ScreenTranslator.csproj" -c Release -p:PublishProfile=SingleFile
if ($LASTEXITCODE -ne 0) { throw 'Ошибка dotnet publish' }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw 'Не найден Inno Setup 6. Установите: winget install JRSoftware.InnoSetup'
}

Write-Host '==> Компиляция установщика' -ForegroundColor Cyan
& $iscc "$PSScriptRoot\ScreenTranslator.iss"
if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции установщика' }

$setup = Join-Path $root 'bin\installer\ScreenTranslatorSetup.exe'
Write-Host ("==> Готово: {0} ({1:N1} МБ)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
