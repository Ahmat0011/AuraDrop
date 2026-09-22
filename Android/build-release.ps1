# AuraDrop - Android Release Build Script
$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "     AuraDrop - Android Release Build     " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$projectDir = $PSScriptRoot
$csproj = Join-Path $projectDir "AuraDrop.Android.csproj"
$releaseDir = Join-Path $projectDir "Release-Build"

if (-not (Test-Path $releaseDir)) {
    New-Item -ItemType Directory -Path $releaseDir | Out-Null
}

[xml]$csprojXml = Get-Content $csproj
$version = $csprojXml.Project.PropertyGroup.ApplicationDisplayVersion | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
if (-not $version) {
    $version = "1.0"
}

Write-Host "`n[1/3] Kompiliere und signiere APK für Android (Version $version)..." -ForegroundColor Yellow

# Vorherige APKs und Icon-Cache aufräumen
Get-ChildItem -Path (Join-Path $projectDir "bin\Release") -Filter "*.apk" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $projectDir "obj\Release\net10.0-android\resizetizer") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $projectDir "obj\Debug\net10.0-android\resizetizer") -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish $csproj `
    -f net10.0-android `
    -c Release `
    -p:AndroidPackageFormat=apk `
    -p:AndroidKeyStore=true `
    -p:AndroidSigningKeyStore=release.keystore `
    -p:AndroidSigningKeyAlias=omnicast `
    -p:AndroidSigningStorePass=OmniCast2026! `
    -p:AndroidSigningKeyPass=OmniCast2026!

if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[FEHLER] dotnet publish fehlgeschlagen mit Exit-Code $LASTEXITCODE!" -ForegroundColor Red
    exit 1
}

$publishApk = Join-Path $projectDir "bin\Release\net10.0-android\publish\com.auradrop.transfer-Signed.apk"

if (-not (Test-Path $publishApk)) {
    $publishApk = Join-Path $projectDir "bin\Release\net10.0-android\com.auradrop.transfer-Signed.apk"
}

if (-not (Test-Path $publishApk)) {
    # Suche jede neu erstellte signierte APK
    $found = Get-ChildItem -Path (Join-Path $projectDir "bin\Release\net10.0-android") -Filter "*Signed.apk" -Recurse | Select-Object -First 1
    if ($found) { $publishApk = $found.FullName }
}

if (Test-Path $publishApk) {
    Write-Host "`n[2/3] Bereitstellen der Release-APK..." -ForegroundColor Yellow

    Get-ChildItem -Path $releaseDir -Filter "*.apk" | Remove-Item -Force

    $apkName = "AuraDrop-v$version.apk"
    $destApk = Join-Path $releaseDir $apkName

    Copy-Item $publishApk $destApk -Force

    $fileSizeMB = [math]::Round(((Get-Item $destApk).Length / 1MB), 2)

    Write-Host "`n[3/3] Erfolgreich abgeschlossen!" -ForegroundColor Green
    Write-Host "--------------------------------------------------------" -ForegroundColor Green
    Write-Host "APK-Datei:      $destApk" -ForegroundColor White
    Write-Host "Dateigröße:     $fileSizeMB MB" -ForegroundColor White
    Write-Host "--------------------------------------------------------" -ForegroundColor Green
} else {
    Write-Host "`n[FEHLER] Signierte APK-Datei wurde nicht im Ausgabeverzeichnis gefunden!" -ForegroundColor Red
    exit 1
}
