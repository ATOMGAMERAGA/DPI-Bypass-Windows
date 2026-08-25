<#
.SYNOPSIS
    One line installer and updater for DPI Bypass.

.DESCRIPTION
    Compares what is installed against the newest published release. If they match
    it says so and stops; if the release is newer it downloads the installer,
    checks it against the published checksum, removes the old installation and
    installs the new one.

    Run it with:
      irm https://raw.githubusercontent.com/ATOMGAMERAGA/DPI-Bypass-Windows/main/scripts/install.ps1 | iex

    Running it again later is the supported way to update.

.PARAMETER Force
    Install even when the same version is already there.

.PARAMETER Tag
    Install a specific release (for example v1.0.0.30) instead of the newest one.
#>
[CmdletBinding()]
param(
    [string]$Repository = 'ATOMGAMERAGA/DPI-Bypass-Windows',
    [string]$Tag,
    [switch]$Force,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn([string]$Message) { Write-Host "    $Message" -ForegroundColor Yellow }
function Write-Note([string]$Message) { Write-Host "    $Message" -ForegroundColor Gray }

# The Inno Setup AppId. The uninstall key Windows writes is this plus "_is1", and it
# is the only reliable way to find an installation the user may have moved.
$AppId = '{9F4C1C3E-7B21-4C0A-9E52-6A2D5B71C4A8}_is1'

$UninstallKeys = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
)

<#
    Everything known about the copy that is already on this machine: the recorded
    version, where it lives, and how to remove it silently.
#>
function Get-InstalledRelease {
    foreach ($path in $UninstallKeys) {
        if (-not (Test-Path $path)) { continue }

        $key = Get-ItemProperty -Path $path -ErrorAction SilentlyContinue
        if (-not $key) { continue }

        $location = $key.InstallLocation
        $version = $null

        # The executable is the truth. The registry value is what the installer wrote
        # and survives a failed upgrade that never replaced the files.
        if ($location) {
            $exe = Join-Path $location 'DpiBypass.exe'
            if (Test-Path $exe) {
                try {
                    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
                    if ($info.FileVersion) { $version = $info.FileVersion.Trim() }
                }
                catch {
                    # Fall back to the registry value below.
                }
            }
        }

        if (-not $version -and $key.DisplayVersion) { $version = $key.DisplayVersion }

        return [pscustomobject]@{
            Version         = $version
            DisplayName     = $key.DisplayName
            InstallLocation = $location
            UninstallString = $key.UninstallString
            QuietUninstall  = $key.QuietUninstallString
            RegistryPath    = $path
        }
    }

    return $null
}

<#
    Turns "1.0.0.28", "v1.0.0.28" or "1.0.0" into something comparable. Returns null
    for anything that is not a version, so the caller can fall back to installing.
#>
function ConvertTo-ComparableVersion([string]$Text) {
    if (-not $Text) { return $null }

    $cleaned = $Text.Trim().TrimStart('v', 'V')
    $match = [regex]::Match($cleaned, '^\d+(\.\d+){0,3}')
    if (-not $match.Success) { return $null }

    try { return [version]$match.Value } catch { return $null }
}

<#
    What running this command should do, given what is installed and what is
    published. Kept as one function with no side effects because it is the whole
    point of the script - "update me if there is something newer, and tell me if
    there is not" - and because scripts/tests/install.tests.ps1 checks it.

    Returns one of:
      install         nothing installed, or nothing we can compare: install it
      update          the release is newer than what is here
      up-to-date      the newest release is already installed
      newer-installed the installed build is ahead of the published release
#>
function Get-UpdateDecision {
    param(
        [string]$InstalledVersion,
        [string]$LatestVersion,
        [switch]$Force
    )

    $installed = ConvertTo-ComparableVersion $InstalledVersion
    $latest = ConvertTo-ComparableVersion $LatestVersion

    $action = 'install'

    if (-not $Force -and $installed -and $latest) {
        if ($installed -eq $latest) { $action = 'up-to-date' }
        elseif ($installed -lt $latest) { $action = 'update' }
        else { $action = 'newer-installed' }
    }

    return [pscustomobject]@{
        Action    = $action
        Installed = $installed
        Latest    = $latest
    }
}

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

    # The elevated shell has to be told what this one was told, and it has to stay
    # open long enough to read: a window that closes on its own takes the result of
    # the update with it.
    $switches = ''
    if ($Force) { $switches += " -Force" }
    if ($Quiet) { $switches += " -Quiet" }
    if ($Tag) { $switches += " -Tag '$Tag'" }

    $command = "& ([scriptblock]::Create((irm $url)))$switches"
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-Command', $command
    ) | Out-Null
    return
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$headers = @{
    'User-Agent' = 'DpiBypass-Installer'
    'Accept'     = 'application/vnd.github+json'
}

$installed = Get-InstalledRelease
if ($installed -and $installed.Version) {
    Write-Note "Kurulu sürüm: $($installed.Version)"
}

Write-Step 'Son sürüm bilgisi alınıyor...'
$apiUrl = if ($Tag) {
    "https://api.github.com/repos/$Repository/releases/tags/$Tag"
} else {
    "https://api.github.com/repos/$Repository/releases/latest"
}

$release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -UseBasicParsing
Write-Ok "GitHub'daki sürüm: $($release.tag_name)"

$setupAsset = $release.assets | Where-Object { $_.name -like 'DpiBypass-Setup-*.exe' } | Select-Object -First 1
if (-not $setupAsset) {
    throw "Bu sürümde kurulum dosyası bulunamadı ($($release.tag_name))."
}

# The comparison is what makes running this command again an update rather than a
# blind reinstall - and what lets it answer "you already have the newest one".
$installedVersionText = ''
if ($installed) { $installedVersionText = $installed.Version }
$decision = Get-UpdateDecision -InstalledVersion $installedVersionText -LatestVersion $release.tag_name -Force:$Force

switch ($decision.Action) {
    'up-to-date' {
        Write-Host ''
        Write-Ok "GitHub'da olan zaten en güncel sürüm: $($decision.Installed) bilgisayarınızda kurulu."
        Write-Note 'Güncellenecek bir şey yok, hiçbir dosya indirilmedi.'
        Write-Note 'Aynı sürümü yeniden kurmak isterseniz komutu -Force ile çalıştırın.'
        Write-Host ''
        Write-Note 'Uygulama Başlat menüsünde "DPI Bypass" adıyla yer alır;'
        Write-Note 'pencereyi her koşulda açmak için: DpiBypass.exe --show'
        return
    }

    'newer-installed' {
        Write-Warn "Kurulu sürüm ($($decision.Installed)) yayınlanandan ($($decision.Latest)) yeni; bir şey yapılmadı."
        Write-Note 'Yine de kurmak için aynı komutu -Force ile çalıştırın.'
        return
    }

    'update' {
        Write-Step "Güncelleme bulundu: $($decision.Installed) -> $($decision.Latest)"
    }
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

    # Only now, with a verified installer on disk, is it safe to take the working
    # copy away. Removing it first and then failing to download would leave the user
    # with no protection at all.
    if ($installed) {
        $uninstaller = $installed.QuietUninstall
        if (-not $uninstaller) { $uninstaller = $installed.UninstallString }

        if ($uninstaller) {
            Write-Step 'Eski sürüm kaldırılıyor...'
            $exe = $uninstaller
            $uninstallArgs = @('/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES')

            # UninstallString is a quoted path, sometimes with arguments of its own.
            if ($uninstaller -match '^\s*"([^"]+)"\s*(.*)$') {
                $exe = $Matches[1]
                if ($Matches[2].Trim()) { $uninstallArgs = @($Matches[2].Trim().Split(' ')) + $uninstallArgs }
            }

            if (Test-Path $exe) {
                Start-Process -FilePath $exe -ArgumentList $uninstallArgs -Wait | Out-Null

                # Inno's uninstaller copies itself to the temp folder and the first
                # process exits immediately, so waiting on it proves nothing. The
                # registry key disappearing is what actually means "finished".
                $deadline = (Get-Date).AddMinutes(3)
                while ((Test-Path $installed.RegistryPath) -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 500
                }

                if (Test-Path $installed.RegistryPath) {
                    Write-Warn 'Eski sürüm kaldırılamadı; kurulum yine de üzerine yazacak.'
                }
                else {
                    Write-Ok 'Eski sürüm kaldırıldı.'
                }
            }
            else {
                Write-Warn 'Kaldırma aracı bulunamadı; kurulum üzerine yazacak.'
            }
        }
    }

    Write-Step 'Kurulum çalıştırılıyor...'
    $arguments = @('/SILENT', '/NORESTART', '/SUPPRESSMSGBOXES')
    if ($Quiet) { $arguments = @('/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES') }

    $process = Start-Process -FilePath $setupPath -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Kurulum $($process.ExitCode) kodu ile sonlandı."
    }

    $now = Get-InstalledRelease
    if ($now -and $now.Version) {
        Write-Ok "DPI Bypass $($now.Version) kuruldu."
    }
    else {
        Write-Ok 'DPI Bypass kuruldu.'
    }

    # A silent install never runs the installer's "launch now" checkbox, and the
    # logon task does not fire until the next sign-in. Without this the command
    # finishes having put nothing on screen, which is indistinguishable from a
    # failed installation.
    $appExe = $null
    if ($now -and $now.InstallLocation) { $appExe = Join-Path $now.InstallLocation 'DpiBypass.exe' }
    if ($appExe -and (Test-Path $appExe)) {
        Write-Step 'Uygulama başlatılıyor...'
        Start-Process -FilePath $appExe -ArgumentList '--show' | Out-Null
    }

    Write-Host ''
    Write-Note 'Uygulama Başlat menüsünde "DPI Bypass" adıyla yer alıyor ve her'
    Write-Note 'Windows açılışında kendiliğinden başlar. Pencereyi kapattığınızda'
    Write-Note 'saatin yanındaki ok (^) altındaki simgeden geri açabilirsiniz.'
    Write-Note 'Durum sekmesindeki "discord.com testi" düğmesiyle çalıştığını'
    Write-Note 'doğrulayabilirsiniz.'
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
