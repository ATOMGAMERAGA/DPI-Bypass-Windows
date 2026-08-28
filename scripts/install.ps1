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
    A directory every Start-Process in this script can safely be handed.

    Start-Process gives the working directory straight to CreateProcess, and a
    PowerShell session's location is not necessarily one CreateProcess will take: it
    can be a registry key or a certificate store rather than a folder, a mapped drive
    the elevated token does not have, or a network share that needs credentials the
    new process will not carry. Any of those fails the launch outright with "the
    system cannot find the path specified" - a path error, reported before the
    install has done anything, about a path the user never chose. The Windows
    directory is the one answer that is a real folder in every context this runs in,
    and nothing here cares what the working directory actually is.

    Returns null only if none of the candidates exist, in which case the callers
    leave the parameter off and get the old behaviour.
#>
function Get-SafeWorkingDirectory {
    $candidates = @($env:SystemRoot, $env:windir, 'C:\Windows', $env:TEMP)

    try { $candidates += [System.IO.Path]::GetTempPath() } catch { }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }

        try {
            if ([System.IO.Directory]::Exists($candidate)) { return $candidate }
        }
        catch {
            # Not reachable from here; try the next one.
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

<#
    What an Inno Setup exit code means, in a sentence the person who ran the one liner
    can do something with.

    The installer is run with /SUPPRESSMSGBOXES, which is what makes it silent - and
    also what stops it from ever printing why it stopped. Without this the whole of a
    failed installation is a number.
#>
function Get-SetupExitReason([int]$ExitCode) {
    switch ($ExitCode) {
        1 { 'Kurulum başlatılamadı (geçersiz parametre ya da kurulum betiği hatası).' }
        2 { 'Kurulum, dosyalar kopyalanmadan önce iptal edildi.' }
        3 { 'Kuruluma hazırlanırken önemli bir hata oluştu.' }
        4 { 'Dosyalar kurulurken önemli bir hata oluştu.' }
        5 { 'Kurulum, dosyalar kopyalanırken iptal edildi.' }
        6 { 'Kurulum işlemi dışarıdan sonlandırıldı.' }
        7 { 'Hazırlık aşaması kurulumun sürdürülemeyeceğine karar verdi.' }
        8 { 'Hazırlık aşaması bilgisayarın yeniden başlatılmasını istiyor.' }
        default { "Kurulum bilinmeyen bir hatayla ($ExitCode) sonlandı." }
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

    $startArgs = @{
        FilePath     = 'powershell.exe'
        Verb         = 'RunAs'
        ArgumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-Command', $command)
    }

    # Never the caller's location: see Get-SafeWorkingDirectory. The elevated shell
    # downloads the script again, so it needs nothing from the folder this ran in.
    $safeDirectory = Get-SafeWorkingDirectory
    if ($safeDirectory) { $startArgs['WorkingDirectory'] = $safeDirectory }

    try {
        Start-Process @startArgs | Out-Null
    }
    catch {
        Write-Host ''
        Write-Warn "Yükseltilmiş pencere açılamadı: $($_.Exception.Message)"
        Write-Note 'Başlat menüsünden PowerShell''i sağ tıklayıp "Yönetici olarak çalıştır"'
        Write-Note 'seçeneğiyle açın ve aynı komutu orada çalıştırın.'
        exit 1
    }

    return
}

# Used for every process this script starts from here on, for the reason above.
$SafeWorkingDirectory = Get-SafeWorkingDirectory

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

# Windows PowerShell redraws a progress bar for every chunk Invoke-WebRequest reads,
# and that redrawing is where most of the time in a 50 MB download goes. It is turned
# off for the transfers and put back exactly as it was found.
$previousProgress = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'

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
                $uninstallStart = @{ FilePath = $exe; ArgumentList = $uninstallArgs; Wait = $true }
                if ($SafeWorkingDirectory) { $uninstallStart['WorkingDirectory'] = $SafeWorkingDirectory }
                Start-Process @uninstallStart | Out-Null

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

    # Kept outside the temporary folder deliberately: that folder is deleted on the way
    # out of this script, and when the installation fails this file is the only account
    # of what it was doing when it stopped.
    $setupLog = Join-Path $env:TEMP ('dpibypass-setup-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))

    # Quoted here, not by Start-Process: it joins ArgumentList with spaces and adds
    # nothing of its own, so an unquoted path through "C:\Users\Ad Soyad\..." would
    # reach the installer as a truncated switch followed by junk it never asked for.
    $mode = if ($Quiet) { '/VERYSILENT' } else { '/SILENT' }
    $arguments = @($mode, '/NORESTART', '/SUPPRESSMSGBOXES', "`"/LOG=$setupLog`"")

    $setupStart = @{ FilePath = $setupPath; ArgumentList = $arguments; Wait = $true; PassThru = $true }
    if ($SafeWorkingDirectory) { $setupStart['WorkingDirectory'] = $SafeWorkingDirectory }

    $process = Start-Process @setupStart
    if ($process.ExitCode -ne 0) {
        Write-Host ''
        Write-Warn (Get-SetupExitReason $process.ExitCode)

        if (Test-Path $setupLog) {
            Write-Note "Kurulum günlüğü: $setupLog"
            Write-Note 'Günlüğün son satırları:'
            foreach ($line in @(Get-Content -Path $setupLog -Tail 12 -ErrorAction SilentlyContinue)) {
                Write-Note "  $line"
            }
        }

        Write-Host ''
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

        # Started in its own folder, not in this script's temporary one: that folder
        # is deleted in the finally block below, and a running process whose working
        # directory has been removed cannot launch a child of its own - which is how
        # the very first run after an install ends up unable to register its logon
        # task or configure DNS, reporting a path error about neither.
        $appProcess = Start-Process -FilePath $appExe -ArgumentList '--show' `
            -WorkingDirectory $now.InstallLocation -PassThru

        # Starting a process only proves CreateProcess accepted the file. It does not
        # prove WPF built a window, the dispatcher is alive, or a tray icon exists. The
        # internal health check asks the running instance to show a real window and only
        # returns zero after that instance acknowledges it. Do not print a success-shaped
        # message while the app is dead or stuck behind an unresponsive old copy.
        #
        # Whether the process started just above is still running says nothing about
        # that. The installer's own last step already launched the app, so this one
        # normally finds an instance holding the single instance lock, hands its request
        # to it and exits within a second - by design, and long before a slow first
        # launch has a window up. Only the health check is allowed to answer the
        # question, and it is asked on every attempt.
        Write-Step 'Pencere ve başlangıç durumu doğrulanıyor...'
        $healthy = $false
        $healthExitCode = $null

        for ($attempt = 1; $attempt -le 4 -and -not $healthy; $attempt++) {
            Start-Sleep -Milliseconds 750

            $healthStart = @{
                FilePath         = $appExe
                ArgumentList     = @('--health-check')
                WorkingDirectory = $now.InstallLocation
                Wait             = $true
                PassThru         = $true
                WindowStyle      = 'Hidden'
            }

            try {
                $health = Start-Process @healthStart
                $healthExitCode = $health.ExitCode
                $healthy = $healthExitCode -eq 0
            }
            catch {
                $healthExitCode = -1
            }
        }

        if (-not $healthy) {
            Write-Warn 'Uygulama işlemi pencere açtığını doğrulamadı; DNS güvenli biçimde geri alınıyor.'

            # The failed copy may have redirected DNS before it became unreachable.
            # Restore from a separate helper before ending it, then let the external
            # watchdog make the same idempotent check when the owner disappears.
            try {
                $restoreStart = @{
                    FilePath         = $appExe
                    ArgumentList     = @('--restore-dns')
                    WorkingDirectory = $now.InstallLocation
                    Wait             = $true
                    WindowStyle      = 'Hidden'
                }
                Start-Process @restoreStart | Out-Null
            }
            catch {
                Write-Warn "DNS kurtarma yardımcısı çalıştırılamadı: $($_.Exception.Message)"
            }

            $appProcess.Refresh()
            if (-not $appProcess.HasExited) {
                Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
            }

            $logPath = Join-Path $env:ProgramData 'DPI Bypass\logs\dpibypass.log'
            throw "DPI Bypass kuruldu ancak görünür bir pencere başlatamadı (sağlık kodu: $healthExitCode). " +
                "İnternet ayarları geri alma işlemine alındı. Ayrıntılar: $logPath"
        }

        Write-Ok 'Uygulama açıldı ve pencere doğrulandı.'
    }
    else {
        throw 'Kurulum tamamlandı ancak DpiBypass.exe kurulum klasöründe bulunamadı.'
    }

    Write-Host ''
    Write-Note 'Uygulama Başlat menüsünde "DPI Bypass" adıyla yer alıyor ve her'
    Write-Note 'Windows açılışında kendiliğinden başlar. Pencereyi kapattığınızda'
    Write-Note 'saatin yanındaki ok (^) altındaki simgeden geri açabilirsiniz.'
    Write-Note 'Durum sekmesindeki "discord.com testi" düğmesiyle çalıştığını'
    Write-Note 'doğrulayabilirsiniz.'
}
finally {
    $ProgressPreference = $previousProgress
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
