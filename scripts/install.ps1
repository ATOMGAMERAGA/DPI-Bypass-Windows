<#
.SYNOPSIS
    One line installer for DPI Bypass.

.DESCRIPTION
    Fetches the newest published release, checks the installer against the
    checksum file published alongside it, and installs it silently.

    Run it with:
      irm https://raw.githubusercontent.com/ATOMGAMERAGA/DPI-Bypass-Windows/main/scripts/install.ps1 | iex
#>
[CmdletBinding()]
param(
    [string]$Repository = 'ATOMGAMERAGA/DPI-Bypass-Windows',
    [string]$Tag,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn([string]$Message) { Write-Host "    $Message" -ForegroundColor Yellow }

if ($PSVersionTable.PSVersion.Major -lt 5) {
    throw 'Windows PowerShell 5.1 veya daha yenisi gerekiyor.'
}

if (-not ($IsWindows -or $env:OS -eq 'Windows_NT')) {
    throw 'DPI Bypass yalnızca Windows üzerinde çalışır.'
}

# The installer writes to Program Files and registers a scheduled task, so it needs
# elevation. Re-run the same one liner in an elevated shell rather than failing.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Step 'Yönetici hakları gerekiyor, yükseltilmiş bir pencere açılıyor...'
    $url = "https://raw.githubusercontent.com/$Repository/main/scripts/install.ps1"
    $command = "irm $url | iex"
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $command
    ) | Out-Null
    return
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$headers = @{
    'User-Agent' = 'DpiBypass-Installer'
    'Accept'     = 'application/vnd.github+json'
}

Write-Step 'Son sürüm bilgisi alınıyor...'
$apiUrl = if ($Tag) {
    "https://api.github.com/repos/$Repository/releases/tags/$Tag"
} else {
    "https://api.github.com/repos/$Repository/releases/latest"
}

$release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -UseBasicParsing
Write-Ok "Sürüm: $($release.tag_name)"

$setupAsset = $release.assets | Where-Object { $_.name -like 'DpiBypass-Setup-*.exe' } | Select-Object -First 1
if (-not $setupAsset) {
    throw "Bu sürümde kurulum dosyası bulunamadı ($($release.tag_name))."
}

$checksumAsset = $release.assets | Where-Object { $_.name -like '*SHA256SUMS*' } | Select-Object -First 1

$work = Join-Path $env:TEMP ("dpibypass-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$setupPath = Join-Path $work $setupAsset.name

try {
    Write-Step "Kurulum dosyası indiriliyor ($([math]::Round($setupAsset.size / 1MB, 1)) MB)..."
    Invoke-WebRequest -Uri $setupAsset.browser_download_url -OutFile $setupPath -Headers $headers -UseBasicParsing
    $actual = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash

    if ($checksumAsset) {
        Write-Step 'Sağlama toplamı doğrulanıyor...'
        $sumsPath = Join-Path $work $checksumAsset.name
        Invoke-WebRequest -Uri $checksumAsset.browser_download_url -OutFile $sumsPath -Headers $headers -UseBasicParsing

        $expected = $null
        foreach ($line in Get-Content -Path $sumsPath) {
            if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and
                $Matches[2].Trim() -eq $setupAsset.name) {
                $expected = $Matches[1]
                break
            }
        }

        if (-not $expected) {
            throw "Sağlama listesinde $($setupAsset.name) için kayıt yok; kurulum durduruldu."
        }

        if ($expected -ne $actual) {
            throw "Sağlama toplamı uyuşmuyor. Beklenen $expected, bulunan $actual. Kurulum durduruldu."
        }

        Write-Ok 'Sağlama toplamı doğrulandı.'
    }
    else {
        Write-Warn "Sağlama listesi yayınlanmamış; indirilen dosyanın SHA256 değeri: $actual"
    }

    Write-Step 'Kurulum çalıştırılıyor...'
    $arguments = @('/SILENT', '/NORESTART', '/SUPPRESSMSGBOXES')
    if ($Quiet) { $arguments = @('/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES') }

    $process = Start-Process -FilePath $setupPath -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Kurulum $($process.ExitCode) kodu ile sonlandı."
    }

    Write-Ok 'DPI Bypass kuruldu.'
    Write-Host ''
    Write-Host 'Uygulama Başlat menüsünde "DPI Bypass" adıyla yer alıyor ve her' -ForegroundColor Gray
    Write-Host 'Windows açılışında kendiliğinden başlar. Durum sekmesindeki' -ForegroundColor Gray
    Write-Host '"discord.com testi" düğmesiyle çalıştığını doğrulayabilirsiniz.' -ForegroundColor Gray
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
