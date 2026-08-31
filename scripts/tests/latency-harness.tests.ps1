<#
    The Windows latency harness cannot be run in CI: it needs a real adapter, a real
    driver and a person with a transfer running. What can be checked anywhere is that it
    stays safe to hand to somebody - that it parses, that nothing touches the machine
    without an explicit switch, and above all that it can only ever create and remove
    policies in this application's own namespace.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:Failures = 0

function Test-Case {
    param([string]$Name, [scriptblock]$Body)

    try {
        & $Body
        Write-Host "  ok   $Name"
    } catch {
        $script:Failures++
        Write-Host "  FAIL $Name"
        Write-Host "       $($_.Exception.Message)"
    }
}

function Should-Contain {
    param([string]$Haystack, [string]$Needle, [string]$Because)
    if ($Haystack -notmatch [regex]::Escape($Needle)) {
        throw "expected to find '$Needle' ($Because)"
    }
}

function Should-NotContain {
    param([string]$Haystack, [string]$Needle, [string]$Because)
    if ($Haystack -match [regex]::Escape($Needle)) {
        throw "did not expect to find '$Needle' ($Because)"
    }
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$harnessPath = Join-Path $root 'scripts/integration/latency-windows.ps1'

Write-Host 'Latency integration harness'

Test-Case 'the harness exists where the audit says it does' {
    if (-not (Test-Path $harnessPath)) { throw "missing $harnessPath" }
}

$harness = Get-Content $harnessPath -Raw

Test-Case 'the harness parses' {
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($harnessPath, [ref]$null, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        throw ($errors | ForEach-Object { $_.ToString() }) -join '; '
    }
}

Test-Case 'every write to the machine is behind an explicit switch' {
    Should-Contain $harness '[switch]$AllowWrite' 'writing a keyword must be opt-in'
    Should-Contain $harness '[switch]$AllowRestart' 'restarting the adapter must be opt-in'
    Should-Contain $harness '[switch]$AllowQos' 'creating a policy must be opt-in'
}

Test-Case 'the harness never removes a policy outside this application namespace' {
    # One name, built from the prefix constant, is the only thing Remove-NetQosPolicy is
    # ever given. A harness that could delete a corporate policy is not a harness.
    $removals = [regex]::Matches($harness, 'Remove-NetQosPolicy[^\r\n]*')
    if ($removals.Count -eq 0) { throw 'expected the harness to clean up after itself' }

    foreach ($removal in $removals) {
        Should-Contain $removal.Value '$script:PolicyName' 'only the harness policy may be removed'
    }

    Should-Contain $harness "PolicyPrefix = 'DPIBypass.Latency.'" 'the namespace has to be the application one'
}

Test-Case 'the policy it creates is its own, in the non-persistent store' {
    $creations = [regex]::Matches($harness, 'New-NetQosPolicy[^\r\n]*')
    if ($creations.Count -ne 1) { throw "expected exactly one policy creation, found $($creations.Count)" }

    Should-Contain $creations[0].Value '$script:PolicyName' 'the created policy must carry our name'
    Should-Contain $harness '-PolicyStore ActiveStore' 'a crash must not leave a policy across a reboot'
}

Test-Case 'the keyword is always put back, and the restore is verified' {
    Should-Contain $harness 'restoreMatchedOriginal' 'the run has to record whether the original came back'
    Should-Contain $harness 'finally' 'the restore must run on the failure path too'
}

Test-Case 'an adapter restart is refused in a remote session' {
    Should-Contain $harness 'Test-RemoteSession' 'the harness has to know whether it is remote'
    Should-Contain $harness 'Adapter restart refused: this is a Remote Desktop session.' 'and refuse'
}

Test-Case 'what it could not establish is recorded rather than omitted' {
    Should-Contain $harness 'notRun' 'the report has to carry its own gaps'
    Should-Contain $harness 'Single vs multiple TCP flow throttle behaviour' 'the open QoS question is named'
    Should-Contain $harness 'connection opened before the policy' 'so is the other one'
}

Test-Case 'a p99 is not reported from a sample too small to have one' {
    Should-Contain $harness '$samples.Count -ge 100' 'a p99 needs a hundred replies to mean anything'
}

Test-Case 'the clock resolution travels with the numbers' {
    Should-Contain $harness 'clockResolutionMs' 'a gain below the resolution is not a gain'
}

Test-Case 'the harness writes where the audit says it writes' {
    Should-Contain $harness 'artifacts/latency-lab' 'the audit points at this directory'
}

Test-Case 'nothing here disables a security or system feature' {
    foreach ($forbidden in @(
        'Set-MpPreference',
        'netsh advfirewall set',
        'Disable-NetAdapterChecksumOffload',
        'Set-NetTCPSetting',
        'Disable-NetAdapterBinding',
        'Stop-Service')) {
        Should-NotContain $harness $forbidden 'the harness only ever touches the latency surface'
    }
}

Write-Host ''

if ($script:Failures -gt 0) {
    Write-Host "$($script:Failures) harness test(s) failed."
    exit 1
}

Write-Host 'All latency harness tests passed.'
exit 0
