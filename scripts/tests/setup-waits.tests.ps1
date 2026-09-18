<#
.SYNOPSIS
    Checks that nothing the installer waits for can wait for ever.

.DESCRIPTION
    Setup runs this application several times while installing it - to put DNS and
    the network card back before it replaces the files, to register or remove the
    logon task, to undo the hosts file entry on the way out - and it waits for each
    of those runs to finish. It starts them hidden, and on a silent install Setup
    itself is no more than a progress bar.

    So a window on that path is not a prompt. It is an installation that stops at
    nought per cent on a bar that never moves, with a dialog behind it that nobody
    can see and nobody can click. That is the failure this file exists to prevent,
    and it has three parts:

      1. every verb the command line answers is recognised as headless, or the
         application opens its main window instead of doing the job;
      2. nothing on that path puts up a dialog;
      3. Setup's own waits have deadlines, because during an upgrade the copy it
         runs is the build already on the machine - a build a fix here cannot reach.

    Run it with:
      pwsh -File scripts/tests/setup-waits.tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$commandLinePath = Join-Path $root 'src/DpiBypass.App/CommandLineTasks.cs'
$appPath = Join-Path $root 'src/DpiBypass.App/App.xaml.cs'
$installerPath = Join-Path $root 'installer/DpiBypass.iss'

foreach ($path in @($commandLinePath, $appPath, $installerPath)) {
    if (-not (Test-Path $path)) { throw "not found: $path" }
}

$commandLine = Get-Content -Path $commandLinePath -Raw
$app = Get-Content -Path $appPath -Raw
$installer = Get-Content -Path $installerPath -Raw

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

<#
    The body of one C# method, from its signature to the start of the next one.

    The verbs are dispatched by a switch, and the sub-verb switches further down the
    same file use the same shape - so a flat search over the file would collect
    "restore", "on" and "off" as though they were things the command line could be
    started with.
#>
function Get-MethodBody([string]$Text, [string]$Signature) {
    $start = $Text.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "$Signature was not found" }

    $brace = $Text.IndexOf('{', $start)
    if ($brace -lt 0) { throw "$Signature has no body" }

    $depth = 0
    for ($index = $brace; $index -lt $Text.Length; $index++) {
        switch ($Text[$index]) {
            '{' { $depth++ }
            '}' {
                $depth--
                if ($depth -eq 0) {
                    return $Text.Substring($brace, $index - $brace + 1)
                }
            }
        }
    }

    throw "$Signature is not closed"
}

<#
    One routine of the installer's [Code] section, from its declaration to the start
    of whichever routine is declared next.
#>
function Get-PascalRoutine([string]$Text, [string]$Declaration) {
    $section = $Text.IndexOf("`n[Code]", [StringComparison]::Ordinal)
    if ($section -lt 0) { throw 'the installer script has no [Code] section' }

    $code = $Text.Substring($section)
    $start = $code.IndexOf($Declaration, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "$Declaration was not found" }

    $end = $code.Length
    foreach ($keyword in @("`nprocedure ", "`nfunction ")) {
        $next = $code.IndexOf($keyword, $start + 1, [StringComparison]::Ordinal)
        if ($next -ge 0 -and $next -lt $end) { $end = $next }
    }

    return $code.Substring($start, $end - $start)
}

Write-Host 'Setup waits' -ForegroundColor Cyan

Test-Case 'every verb the command line dispatches is recognised as headless' {
    <#
        The bug this catches, exactly as it shipped: --restore-hosts was added to the
        dispatcher and not to the set the launch path tests against. So the verb
        worked when it was reached, and it was never reached - IsHeadlessVerb said no,
        the application built its main window instead, and the uninstaller waited for
        that window to be closed by a user who could not see it.
    #>
    $set = [regex]::Match($commandLine, 'HeadlessVerbs\s*=\s*new\([^)]*\)\s*\{(?<body>[^}]*)\}')
    if (-not $set.Success) { throw 'the HeadlessVerbs set was not found' }

    $known = New-Object System.Collections.Generic.HashSet[string]
    foreach ($entry in [regex]::Matches($set.Groups['body'].Value, '"(?<verb>[^"]+)"')) {
        [void]$known.Add($entry.Groups['verb'].Value)
    }

    if ($known.Count -lt 10) { throw "only $($known.Count) headless verb(s) were parsed" }

    $body = Get-MethodBody $commandLine 'public static async Task<bool> TryRunAsync'
    $dispatched = @([regex]::Matches($body, '(?m)^\s*case\s+"(?<verb>[^"]+)"\s*:') |
        ForEach-Object { $_.Groups['verb'].Value })

    if ($dispatched.Count -lt 10) { throw "only $($dispatched.Count) dispatched verb(s) were parsed" }

    $missing = @($dispatched | Where-Object { -not $known.Contains($_) })
    if ($missing.Count -gt 0) {
        throw "dispatched but not headless: $($missing -join ', ') - these open the main window instead"
    }
}

Test-Case 'the verbs the installer runs are marked unattended and carry a deadline' {
    foreach ($verb in @('install-autostart', 'uninstall-autostart', 'restore-dns', 'restore-hosts')) {
        if ($commandLine -notmatch "UnattendedVerbs[\s\S]{0,400}""$([regex]::Escape($verb))""") {
            throw "$verb is not in the unattended set"
        }
    }

    $begin = Get-MethodBody $commandLine 'public static void BeginUnattended'
    if ($begin -notmatch 'Unattended = true') { throw 'BeginUnattended does not mark the run' }
    if ($begin -notmatch 'StartDeadline') { throw 'an unattended run is started without a deadline' }

    # "latency restore" is the one unattended job whose first argument alone does not
    # say so: latency status and latency test are things a person runs and reads.
    if ($begin -notmatch '"restore"') { throw 'the latency restore sub-verb is not covered' }

    $deadline = Get-MethodBody $commandLine 'private static void StartDeadline'
    if ($deadline -notmatch 'Kill\(\)') { throw 'the deadline does not actually end the process' }
}

Test-Case 'an unattended run is armed before anything that could block' {
    $start = Get-MethodBody $app 'private void Start(StartupEventArgs e)'
    $arm = $start.IndexOf('CommandLineTasks.BeginUnattended', [StringComparison]::Ordinal)
    $run = $start.IndexOf('RunHeadlessAsync', [StringComparison]::Ordinal)

    if ($arm -lt 0) { throw 'startup never marks an unattended run' }
    if ($run -lt 0) { throw 'the headless path was not found' }
    if ($arm -gt $run) { throw 'the deadline is armed after the work it is meant to bound' }
}

Test-Case 'nothing on the unattended path can stop on a dialog' {
    # Two places would otherwise: the command line's own output, when there is no
    # console to attach to, and the startup crash report.
    $write = Get-MethodBody $commandLine 'private static void WriteConsole'
    $guard = $write.IndexOf('Unattended', [StringComparison]::Ordinal)
    $dialog = $write.IndexOf('MessageBox.Show', [StringComparison]::Ordinal)

    if ($dialog -lt 0) { throw 'the dialog fallback was not found' }
    if ($guard -lt 0 -or $guard -gt $dialog) { throw 'the dialog fallback is not ruled out by an unattended run' }

    $fatal = Get-MethodBody $app 'private static void ReportFatal'
    $fatalGuard = $fatal.IndexOf('CommandLineTasks.Unattended', [StringComparison]::Ordinal)
    $fatalDialog = $fatal.IndexOf('MessageBox.Show', [StringComparison]::Ordinal)

    if ($fatalDialog -lt 0) { throw 'the crash dialog was not found' }
    if ($fatalGuard -lt 0 -or $fatalGuard -gt $fatalDialog) {
        throw 'the crash dialog is shown even when Setup is waiting on this process'
    }
}

Test-Case 'Setup never waits on the installed application without a deadline' {
    <#
        The sweep is the one place Setup runs a build other than the one it carries:
        during an upgrade it is the copy already on the machine, which may predate
        every fix above. Its waits are therefore Setup's to bound, not the
        application's - so ewWaitUntilTerminated must not appear there at all.
    #>
    $sweep = Get-PascalRoutine $installer 'procedure StopRunningInstance'

    if ($sweep -match 'ewWaitUntilTerminated') {
        throw 'the sweep still waits on a process with no time limit'
    }

    $bounded = [regex]::Matches($sweep, 'RunBounded\(')
    if ($bounded.Count -lt 5) {
        throw "only $($bounded.Count) command(s) in the sweep are bounded"
    }
}

Test-Case 'the bounded wait gives up and says so' {
    $body = Get-PascalRoutine $installer 'function RunBounded'

    if ($body -notmatch 'ewNoWait') { throw 'RunBounded still blocks on the process itself' }
    if ($body -notmatch 'Deadline') { throw 'RunBounded has no deadline' }
    if ($body -notmatch 'Log\(') { throw 'giving up leaves nothing in the setup log' }

    # A deadline that is shorter than the work is a different bug: abandoning a DNS
    # restore half way through leaves the machine resolving against a proxy that is
    # being uninstalled. The DNS write budget inside the app reaches two minutes.
    $dns = [regex]::Match($installer, 'RestoreDnsDeadline\s*=\s*(?<seconds>\d+)')
    if (-not $dns.Success) { throw 'the DNS restore deadline was not found' }
    if ([int]$dns.Groups['seconds'].Value -lt 130) {
        throw "the DNS restore deadline is $($dns.Groups['seconds'].Value)s, shorter than the work it covers"
    }
}

Test-Case 'the Installing page says what it is waiting for' {
    # Without this the sweep is a progress bar at nought per cent with nothing above
    # it, which is indistinguishable from an installer that has died.
    if ($installer -notmatch '(?m)^turkish\.ClosingPrevious=') { throw 'the Turkish status line is missing' }
    if ($installer -notmatch '(?m)^english\.ClosingPrevious=') { throw 'the English status line is missing' }

    $sweep = Get-PascalRoutine $installer 'procedure StopRunningInstance'
    if ($sweep -notmatch "SayStep\('ClosingPrevious'\)") { throw 'the sweep never says what it is doing' }

    $body = Get-PascalRoutine $installer 'procedure SayStep'

    # There is no wizard form during InitializeSetup and none at all during uninstall,
    # and a status line is never worth an installation.
    if ($body -notmatch 'try') { throw 'SayStep is not guarded against there being no form' }
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) test failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All setup wait tests passed.' -ForegroundColor Green

# Said out loud, because a script that just ends leaves $LASTEXITCODE at whatever the
# caller had before it - which is not an answer.
exit 0
