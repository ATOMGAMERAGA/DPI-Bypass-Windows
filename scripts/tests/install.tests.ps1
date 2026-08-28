<#
.SYNOPSIS
    Checks the decisions scripts/install.ps1 makes before it touches the machine.

.DESCRIPTION
    The one line installer is also the updater: running it again has to install a
    newer release, say so and stop when the newest one is already there, and never
    tear down a working installation for nothing. That logic lives in two pure
    functions, and this pulls them out of the script - without running the script -
    and puts every case through them.

    Run it with:
      pwsh -File scripts/tests/install.tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) 'install.ps1'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$installerPath = Join-Path $root 'installer/DpiBypass.iss'
if (-not (Test-Path $scriptPath)) {
    throw "install.ps1 not found at $scriptPath"
}

# Parse rather than dot-source: the script installs things when it runs.
$errors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)

if ($errors -and $errors.Count -gt 0) {
    foreach ($parseError in $errors) {
        Write-Host "install.ps1($($parseError.Extent.StartLineNumber)): $($parseError.Message)" -ForegroundColor Red
    }

    throw 'install.ps1 does not parse.'
}

foreach ($name in @('ConvertTo-ComparableVersion', 'Get-UpdateDecision', 'Get-InstalledRelease', 'Get-SafeWorkingDirectory', 'Get-SetupExitReason')) {
    $definition = $ast.Find(
        [Func[System.Management.Automation.Language.Ast, bool]] {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        },
        $true)

    if (-not $definition) {
        throw "install.ps1 no longer defines $name"
    }

    # Bring the function into this session exactly as the script declares it.
    . ([scriptblock]::Create($definition.Extent.Text))
}

$failures = New-Object System.Collections.Generic.List[string]

function Test-Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        Write-Host "  ok   $Name" -ForegroundColor Green
    }
    catch {
        Write-Host "  FAIL $Name" -ForegroundColor Red
        Write-Host "       $($_.Exception.Message)" -ForegroundColor Red
        $script:failures.Add($Name)
    }
}

function Assert-Equal($Expected, $Actual, [string]$Because) {
    if ("$Expected" -ne "$Actual") {
        throw "expected '$Expected' but got '$Actual'$(if ($Because) { " - $Because" })"
    }
}

<#
    The [Code] section of the Inno Setup script, split into one entry per routine.

    Two of the tests below are about which code runs when, and the difference between
    something Setup calls before the wizard exists and something it calls afterwards
    is invisible to a flat search of the file.
#>
function Get-InstallerRoutines([string]$Path) {
    if (-not (Test-Path $Path)) {
        throw "installer script not found at $Path"
    }

    $text = Get-Content -Path $Path -Raw
    $section = $text.IndexOf("`n[Code]", [StringComparison]::Ordinal)
    if ($section -lt 0) { throw 'the installer script has no [Code] section' }

    $code = $text.Substring($section)
    $declarations = [regex]::Matches($code, '(?m)^(?:procedure|function)\s+(?<name>\w+)')
    if ($declarations.Count -eq 0) { throw 'no routines were found in the [Code] section' }

    $routines = @{}
    for ($index = 0; $index -lt $declarations.Count; $index++) {
        $from = $declarations[$index].Index
        $to = if ($index + 1 -lt $declarations.Count) { $declarations[$index + 1].Index } else { $code.Length }
        $routines[$declarations[$index].Groups['name'].Value] = $code.Substring($from, $to - $from)
    }

    return $routines
}

<#
    Every routine Setup can reach from InitializeSetup, including the ones it only
    reaches through another routine.
#>
function Get-ReachableRoutines([hashtable]$Routines, [string]$Entry) {
    $reachable = New-Object System.Collections.Generic.HashSet[string]
    $pending = New-Object System.Collections.Generic.Queue[string]
    [void]$pending.Enqueue($Entry)

    while ($pending.Count -gt 0) {
        $name = $pending.Dequeue()
        if (-not $Routines.ContainsKey($name)) { continue }
        if (-not $reachable.Add($name)) { continue }

        foreach ($candidate in $Routines.Keys) {
            if ($candidate -ne $name -and $Routines[$name] -match "\b$candidate\b") {
                [void]$pending.Enqueue($candidate)
            }
        }
    }

    return $reachable
}

Write-Host 'install.ps1' -ForegroundColor Cyan

Test-Case 'a tag with the v prefix parses' {
    Assert-Equal ([version]'1.0.0.28') (ConvertTo-ComparableVersion 'v1.0.0.28')
}

Test-Case 'a bare version parses' {
    Assert-Equal ([version]'1.0.0.28') (ConvertTo-ComparableVersion '1.0.0.28')
}

Test-Case 'a three part version parses' {
    Assert-Equal ([version]'1.2.3') (ConvertTo-ComparableVersion '1.2.3')
}

Test-Case 'trailing text is ignored' {
    Assert-Equal ([version]'1.0.0.28') (ConvertTo-ComparableVersion '1.0.0.28-beta')
}

Test-Case 'nonsense is not a version' {
    Assert-Equal $null (ConvertTo-ComparableVersion 'sürüm yok')
}

Test-Case 'nothing is not a version' {
    Assert-Equal $null (ConvertTo-ComparableVersion '')
}

Test-Case 'the same version installed means there is nothing to do' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.28' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'up-to-date' $decision.Action
}

Test-Case 'a newer release is an update' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.25' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'update' $decision.Action
}

Test-Case 'the build number alone is enough to see an update' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.28' -LatestVersion 'v1.0.0.29'
    Assert-Equal 'update' $decision.Action
}

Test-Case 'a locally built copy ahead of the release is left alone' {
    $decision = Get-UpdateDecision -InstalledVersion '1.1.0.0' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'newer-installed' $decision.Action
}

Test-Case 'nothing installed means install' {
    $decision = Get-UpdateDecision -InstalledVersion '' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'install' $decision.Action
}

Test-Case 'an unreadable installed version means install rather than refuse' {
    $decision = Get-UpdateDecision -InstalledVersion 'bilinmiyor' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'install' $decision.Action
}

Test-Case 'force reinstalls the version that is already there' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.28' -LatestVersion 'v1.0.0.28' -Force
    Assert-Equal 'install' $decision.Action
}

Test-Case 'version ordering is numeric, not alphabetical' {
    # "1.0.0.9" sorts after "1.0.0.10" as text, which would hide every tenth release.
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.9' -LatestVersion 'v1.0.0.10'
    Assert-Equal 'update' $decision.Action
}

Test-Case 'the decision carries the versions it compared' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.25' -LatestVersion 'v1.0.0.28'
    Assert-Equal ([version]'1.0.0.25') $decision.Installed
    Assert-Equal ([version]'1.0.0.28') $decision.Latest
}

Test-Case 'the working directory handed to Start-Process is a real folder' {
    # Start-Process passes this straight to CreateProcess, and a PowerShell location
    # that is a registry key, a mapped drive or an unreachable share fails the launch
    # with "the system cannot find the path specified" before the install has done
    # anything at all.
    $directory = Get-SafeWorkingDirectory

    if (-not $directory) {
        throw 'no working directory was chosen'
    }

    if (-not [System.IO.Directory]::Exists($directory)) {
        throw "'$directory' is not a directory"
    }
}

Test-Case 'the working directory is never the caller''s own location' {
    # Deliberately not $PWD: the point of the helper is that the location this script
    # happens to be run from has no say, because that location is the thing that
    # breaks - a registry drive, a mapped drive the elevated token does not have, or
    # a folder that has since been deleted.
    $elsewhere = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

    Push-Location $elsewhere
    try {
        $directory = (Get-SafeWorkingDirectory).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        $here = (Get-Location).ProviderPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar)

        Assert-Equal $false ($directory -eq $here) 'picked the current location'
    }
    finally {
        Pop-Location
    }
}

Test-Case 'nothing installed is reported as nothing installed' {
    # Runs against this machine: on a build agent there is no DPI Bypass, and the
    # function has to say so rather than throw.
    $installed = Get-InstalledRelease
    if ($null -ne $installed -and -not $installed.RegistryPath) {
        throw 'an installation was reported without a registry path'
    }
}

Test-Case 'uninstall restores latency before DNS and the driver are removed' {
    if (-not (Test-Path $installerPath)) {
        throw "installer script not found at $installerPath"
    }

    $installer = Get-Content -Path $installerPath -Raw
    $latency = $installer.IndexOf('Parameters: "latency restore"', [StringComparison]::Ordinal)
    $dns = $installer.IndexOf('Parameters: "--restore-dns"', [StringComparison]::Ordinal)
    $driver = $installer.IndexOf('Parameters: "stop WinDivert"', [StringComparison]::Ordinal)

    if ($latency -lt 0) { throw 'latency restore command is missing from UninstallRun' }
    if ($dns -lt 0) { throw 'DNS restore command is missing from UninstallRun' }
    if ($driver -lt 0) { throw 'WinDivert stop command is missing from UninstallRun' }
    if (-not ($latency -lt $dns -and $dns -lt $driver)) {
        throw 'restore commands are not ordered before driver removal'
    }
}

Test-Case 'setup restores DNS before force-killing a running app' {
    $routines = Get-InstallerRoutines $installerPath
    if (-not $routines.ContainsKey('StopRunningInstance')) { throw 'StopRunningInstance was not found' }

    $body = $routines['StopRunningInstance']
    $restore = $body.IndexOf("'--restore-dns'", [StringComparison]::Ordinal)
    $kill = $body.IndexOf("'/IM {#AppExeName} /F'", [StringComparison]::Ordinal)

    if ($restore -lt 0) { throw 'pre-kill DNS restore is missing' }
    if ($kill -lt 0) { throw 'application taskkill is missing' }
    if ($restore -ge $kill) { throw 'the app is killed before DNS is restored' }
}

Test-Case 'nothing InitializeSetup reaches expands a constant Setup has not set yet' {
    # This is the whole reason the install folder is read out of the registry there.
    # {app} exists only once the wizard has settled on a directory: asking for it
    # before that is an internal error which ends Setup during initialisation, before
    # a single file is written. With /SUPPRESSMSGBOXES - which the one line installer
    # passes - the error is never printed either, so the entire failed installation
    # reaches the user as nothing but "exit code 1".
    $routines = Get-InstallerRoutines $installerPath
    if (-not $routines.ContainsKey('InitializeSetup')) { throw 'InitializeSetup is missing' }

    $reachable = @(Get-ReachableRoutines $routines 'InitializeSetup')
    if ($reachable.Count -lt 2) { throw 'InitializeSetup appears to call nothing at all' }

    foreach ($name in $reachable) {
        foreach ($constant in @('app', 'group')) {
            if ($routines[$name] -match "ExpandConstant\(\s*'\{$constant\}") {
                throw "$name expands {$constant}, which Setup has not set when InitializeSetup runs"
            }
        }
    }
}

Test-Case 'the running instance is swept again once the install folder is known' {
    # InitializeSetup can only work from the previous installation's uninstall key. A
    # folder typed into the wizard, or one no uninstall key points at, is covered by
    # the second sweep - which is also the last thing to run before the first file is
    # replaced.
    $routines = Get-InstallerRoutines $installerPath
    if (-not $routines.ContainsKey('CurStepChanged')) { throw 'CurStepChanged is missing' }

    $body = $routines['CurStepChanged']
    if ($body -notmatch 'ssInstall') { throw 'the second sweep is not tied to the install step' }
    if ($body -notmatch 'SweepRunningInstance') { throw 'the install step does not stop the running app' }
}

Test-Case 'a previous installation is found through its uninstall key' {
    $routines = Get-InstallerRoutines $installerPath
    if (-not $routines.ContainsKey('PreviousInstallDir')) { throw 'PreviousInstallDir is missing' }

    $body = $routines['PreviousInstallDir'] + $routines['UninstallKeyPath']
    foreach ($needle in @('HKLM64', 'HKLM32', 'HKCU')) {
        if ($body -notmatch $needle) { throw "the $needle registry view is not consulted" }
    }

    # The same AppId the [Setup] section registers, or the key read here belongs to
    # some other product.
    $installer = Get-Content -Path $installerPath -Raw
    if ($installer -notmatch '#define AppIdGuid') { throw 'the AppId is no longer defined once' }
    if ($routines['UninstallKeyPath'] -notmatch '\{#AppIdGuid\}_is1') {
        throw 'the uninstall key is not built from the AppId'
    }
}

Test-Case 'the install command verifies a visible window before reporting success' {
    $script = Get-Content -Path $scriptPath -Raw
    $health = $script.IndexOf("ArgumentList     = @('--health-check'", [StringComparison]::Ordinal)
    $success = $script.IndexOf("Write-Ok 'Uygulama açıldı ve pencere doğrulandı.'", [StringComparison]::Ordinal)

    if ($health -lt 0) { throw 'the post-install health check is missing' }
    if ($success -lt 0) { throw 'the verified startup message is missing' }
    if ($health -ge $success) { throw 'startup is reported before the health check' }
}

Test-Case 'a failed post-install health check restores DNS before stopping the app' {
    $script = Get-Content -Path $scriptPath -Raw
    $failure = $script.IndexOf("if (-not `$healthy)", [StringComparison]::Ordinal)
    $restore = $script.IndexOf("ArgumentList     = @('--restore-dns')", $failure, [StringComparison]::Ordinal)
    $stop = $script.IndexOf('Stop-Process -Id $appProcess.Id', $failure, [StringComparison]::Ordinal)

    if ($failure -lt 0) { throw 'startup failure branch is missing' }
    if ($restore -lt 0) { throw 'DNS restore is missing from the startup failure branch' }
    if ($stop -lt 0) { throw 'failed application cleanup is missing' }
    if ($restore -ge $stop) { throw 'the failed app is stopped before DNS is restored' }
}

Test-Case 'a failed installation says what the exit code meant' {
    # The installer is silent and its message boxes are suppressed, so unless this
    # script explains the code, a failure is a bare number and a stack trace.
    $reason = Get-SetupExitReason 1
    if (-not $reason) { throw 'exit code 1 has no explanation' }
    if ($reason -notmatch 'başlatılamadı') { throw "exit code 1 is explained as '$reason'" }

    $unknown = Get-SetupExitReason 99
    if ($unknown -notmatch '99') { throw 'an unknown exit code loses the code itself' }
}

Test-Case 'the installer is asked for a log the failure path can quote' {
    $script = Get-Content -Path $scriptPath -Raw
    if ($script -notmatch '/LOG=\$setupLog') { throw 'the installer is run without a log file' }

    $explain = $script.IndexOf('Get-SetupExitReason $process.ExitCode', [StringComparison]::Ordinal)
    $fail = $script.IndexOf('throw "Kurulum $($process.ExitCode) kodu ile sonlandı."', [StringComparison]::Ordinal)

    if ($explain -lt 0) { throw 'a failed install does not explain the exit code' }
    if ($fail -lt 0) { throw 'the failure is no longer reported' }
    if ($explain -ge $fail) { throw 'the explanation is printed after the script has thrown' }
}

Test-Case 'the post-install check does not treat a hand-off as a failure' {
    # The installer's own last step launches the app, so the copy this script starts
    # normally hands its request over and exits within a second. Reading that exit as
    # "the app did not start" reported a healthy installation as broken.
    $script = Get-Content -Path $scriptPath -Raw
    $loop = $script.IndexOf('for ($attempt = 1; $attempt -le', [StringComparison]::Ordinal)
    $failure = $script.IndexOf('if (-not $healthy) {', [StringComparison]::Ordinal)

    if ($loop -lt 0) { throw 'the health check loop is missing' }
    if ($failure -lt 0) { throw 'the health check failure branch is missing' }

    $body = $script.Substring($loop, $failure - $loop)
    if ($body -match 'HasExited') {
        throw 'the health check loop gives up when the launched process exits'
    }
}

Test-Case 'the install command does not launch a second copy over the installer' {
    <#
        This is the failure the whole handover exists to prevent, seen from the
        script's side. The installer's [Run] section starts the app on a silent
        install; this script starting it again a moment later produced two copies,
        and the second one - finding a first copy that was still loading its runtime
        and could not answer for a window yet - killed it. From the user's chair the
        window appeared and vanished again the instant the install finished.

        So the launch has to be conditional on the health check having found nothing,
        and it has to come after the check rather than before it.
    #>
    $script = Get-Content -Path $scriptPath -Raw

    $check = $script.IndexOf("ArgumentList     = @('--health-check'", [StringComparison]::Ordinal)
    if ($check -lt 0) { throw 'the health check is missing' }

    $launches = [regex]::Matches($script, "ArgumentList '--show'")
    if ($launches.Count -ne 1) {
        throw "expected exactly one --show launch but found $($launches.Count)"
    }

    if ($launches[0].Index -lt $check) {
        throw 'the app is launched before the health check has looked for a running copy'
    }

    # The guard itself: without it the launch is unconditional again.
    $guard = $script.IndexOf('if (-not $healthy -and -not $appProcess) {', [StringComparison]::Ordinal)
    if ($guard -lt 0) { throw 'the --show launch is no longer guarded by the health check result' }
    if ($guard -gt $launches[0].Index) { throw 'the guard does not cover the launch' }
}

Test-Case 'a failed check never stops a copy this script did not start' {
    # The instance the installer launched may simply be slower than the budget above.
    # Ending it would leave the machine carrying the DNS redirect with none of the
    # protection that justifies it, so only a copy started here is ever stopped.
    $script = Get-Content -Path $scriptPath -Raw
    $stop = $script.IndexOf('Stop-Process -Id $appProcess.Id', [StringComparison]::Ordinal)
    if ($stop -lt 0) { throw 'the failure path no longer stops the app it started' }

    $guard = $script.LastIndexOf('if ($appProcess) {', $stop, [StringComparison]::Ordinal)
    if ($guard -lt 0) { throw 'the stop is not guarded by this script having started a copy' }
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) test failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All install script tests passed.' -ForegroundColor Green

# Said out loud, because a script that just ends leaves $LASTEXITCODE at whatever
# the caller had before it - which is not an answer.
exit 0
