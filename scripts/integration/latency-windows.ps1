<#
.SYNOPSIS
    Opt-in Windows integration harness for the latency subsystem.

.DESCRIPTION
    The unit tests prove the decision logic over test doubles. They cannot prove anything
    about Windows: whether a driver honours a keyword without a restart, whether a QoS
    throttle applies per flow or per application, or whether a policy attaches to a
    connection that already existed. Those questions have one answer each and it is on a
    real machine.

    So this is deliberately separate from the unit tests and never runs as part of them.
    It records what it observed into artifacts/latency-lab/*.json, including the things it
    could not establish, so a claim in the audit can point at a file rather than at a
    hope.

    Nothing here is destructive by default. Every step that touches the machine - an
    adapter restart, a QoS policy, a load measurement - is behind an explicit switch, and
    the QoS half only ever creates and removes policies in this application's own
    DPIBypass.Latency. namespace.

.PARAMETER AdapterName
    The adapter to inspect. Defaults to the connected physical adapter with a gateway.

.PARAMETER Keyword
    The advanced keyword to exercise, e.g. *RscIPv4. Only read unless -AllowWrite.

.PARAMETER AllowWrite
    Permits writing the keyword and putting it back. Without this the run is read-only.

.PARAMETER AllowRestart
    Permits one controlled adapter restart, which drops every connection for a few
    seconds. Refused outright in a Remote Desktop session.

.PARAMETER AllowQos
    Permits creating and removing one QoS policy in this application's namespace.

.PARAMETER BulkApplication
    The executable the QoS policy matches, e.g. steam.exe. Required with -AllowQos.

.PARAMETER Target
    The address to measure. Defaults to 1.1.1.1.

.EXAMPLE
    pwsh -NoProfile -File scripts/integration/latency-windows.ps1

    Read-only: adapter, driver, operational state, and an idle measurement.

.EXAMPLE
    pwsh -NoProfile -File scripts/integration/latency-windows.ps1 -Keyword '*RscIPv4' -AllowWrite

    Also writes the keyword with -NoRestart and reports whether the stack picked it up,
    which is the question the whole apply path turns on.
#>
[CmdletBinding()]
param(
    [string]$AdapterName,
    [string]$Keyword,
    [switch]$AllowWrite,
    [switch]$AllowRestart,
    [switch]$AllowQos,
    [string]$BulkApplication,
    [string]$Target = '1.1.1.1',
    [int]$ProbeCount = 60,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Every policy this harness may create or remove. Anything outside it is somebody else's
# and is only ever counted, never touched - the same rule the application itself follows.
$script:PolicyPrefix = 'DPIBypass.Latency.'
$script:PolicyName = "${script:PolicyPrefix}lab.harness"

function Write-Step {
    param([string]$Message)
    Write-Host "--- $Message"
}

function Test-RemoteSession {
    # Restarting the adapter carrying a Remote Desktop session takes the session away.
    try {
        return [bool]([System.Windows.Forms.SystemInformation]::TerminalServerSession)
    } catch {
        return [bool]$env:SESSIONNAME -and $env:SESSIONNAME -like 'RDP-*'
    }
}

function Get-LabAdapter {
    param([string]$Name)

    if ($Name) {
        return Get-NetAdapter -Name $Name -ErrorAction Stop
    }

    $candidates = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.Status -eq 'Up' })
    foreach ($candidate in $candidates) {
        $routes = @(Get-NetRoute -InterfaceIndex $candidate.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' })
        if ($routes.Count -gt 0) { return $candidate }
    }

    throw 'No connected physical adapter with a default gateway was found.'
}

function Get-OperationalState {
    param($Adapter)

    $state = [ordered]@{
        rscIPv4Operational = $null
        rscIPv6Operational = $null
        rssEnabled         = $null
        lsoV2IPv4Enabled   = $null
        lsoV2IPv6Enabled   = $null
        linkStatus         = [string]$Adapter.Status
        hasIPv4Address     = $false
        hasDefaultRoute    = $false
    }

    try {
        $rsc = Get-NetAdapterRsc -Name $Adapter.Name -ErrorAction Stop | Select-Object -First 1
        if ($rsc) {
            $state.rscIPv4Operational = [bool]$rsc.IPv4Operational
            $state.rscIPv6Operational = [bool]$rsc.IPv6Operational
        }
    } catch { }

    try {
        $rss = Get-NetAdapterRss -Name $Adapter.Name -ErrorAction Stop | Select-Object -First 1
        if ($rss) { $state.rssEnabled = [bool]$rss.Enabled }
    } catch { }

    try {
        $lso = Get-NetAdapterLso -Name $Adapter.Name -ErrorAction Stop | Select-Object -First 1
        if ($lso) {
            $state.lsoV2IPv4Enabled = [bool]$lso.V2IPv4Enabled
            $state.lsoV2IPv6Enabled = [bool]$lso.V2IPv6Enabled
        }
    } catch { }

    try {
        $live = Get-NetAdapter -Name $Adapter.Name -ErrorAction Stop
        $state.linkStatus = [string]$live.Status
        $state.hasIPv4Address = @(Get-NetIPAddress -InterfaceIndex $live.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '169.254.*' }).Count -gt 0
        $state.hasDefaultRoute = @(Get-NetRoute -InterfaceIndex $live.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' }).Count -gt 0
    } catch { }

    return $state
}

function Measure-Latency {
    param([string]$Address, [int]$Count)

    $samples = New-Object System.Collections.Generic.List[double]
    $sent = 0

    for ($index = 0; $index -lt $Count; $index++) {
        $sent++
        try {
            $reply = Test-Connection -TargetName $Address -Count 1 -TimeoutSeconds 1 -ErrorAction Stop |
                Select-Object -First 1
            if ($null -ne $reply -and $null -ne $reply.Latency) {
                $samples.Add([double]$reply.Latency)
            }
        } catch { }

        Start-Sleep -Milliseconds 45
    }

    if ($samples.Count -eq 0) {
        return [ordered]@{
            attempts = $sent; replies = 0
            medianMs = $null; p95Ms = $null; p99Ms = $null; jitterMs = $null
            lossPercent = 100.0
            # Test-Connection reports whole milliseconds, so nothing below this is
            # resolvable and no difference smaller than it is a result.
            clockResolutionMs = 1.0
        }
    }

    $ordered = @($samples | Sort-Object)
    $jitterTotal = 0.0
    for ($index = 1; $index -lt $samples.Count; $index++) {
        $jitterTotal += [Math]::Abs($samples[$index] - $samples[$index - 1])
    }

    function Percentile($sorted, [double]$share) {
        $rank = [Math]::Ceiling($share * $sorted.Count) - 1
        if ($rank -lt 0) { $rank = 0 }
        if ($rank -ge $sorted.Count) { $rank = $sorted.Count - 1 }
        return [double]$sorted[$rank]
    }

    return [ordered]@{
        attempts          = $sent
        replies           = $samples.Count
        medianMs          = Percentile $ordered 0.50
        p95Ms             = Percentile $ordered 0.95
        # Reported, but a p99 from fewer than a hundred replies is the worst sample
        # wearing a percentile's name and is recorded as null instead.
        p99Ms             = $(if ($samples.Count -ge 100) { Percentile $ordered 0.99 } else { $null })
        jitterMs          = $(if ($samples.Count -gt 1) { $jitterTotal / ($samples.Count - 1) } else { 0.0 })
        lossPercent       = [Math]::Round(100.0 * ($sent - $samples.Count) / $sent, 2)
        clockResolutionMs = 1.0
    }
}

function Get-AdapterBytes {
    param($Adapter)
    try {
        $statistics = Get-NetAdapterStatistics -Name $Adapter.Name -ErrorAction Stop
        return [ordered]@{ sent = [long]$statistics.SentBytes; received = [long]$statistics.ReceivedBytes }
    } catch {
        return [ordered]@{ sent = 0; received = 0 }
    }
}

# --- environment -------------------------------------------------------------------
$report = [ordered]@{
    schemaVersion = 1
    startedAt     = (Get-Date).ToUniversalTime().ToString('o')
    host          = [ordered]@{
        windows       = (Get-CimInstance Win32_OperatingSystem).Caption
        build         = (Get-CimInstance Win32_OperatingSystem).BuildNumber
        powerShell    = $PSVersionTable.PSVersion.ToString()
        remoteSession = (Test-RemoteSession)
        processors    = [Environment]::ProcessorCount
    }
    steps         = New-Object System.Collections.Generic.List[object]
    notRun        = New-Object System.Collections.Generic.List[string]
}

Write-Step "Windows $($report.host.windows) build $($report.host.build)"

$adapter = Get-LabAdapter -Name $AdapterName
$report.adapter = [ordered]@{
    name                 = [string]$adapter.Name
    interfaceDescription = [string]$adapter.InterfaceDescription
    interfaceGuid        = [string]$adapter.InterfaceGuid
    driverVersion        = [string]$adapter.DriverVersion
    driverDate           = [string]$adapter.DriverDate
    linkSpeed            = [string]$adapter.LinkSpeed
    mediaType            = [string]$adapter.MediaType
}

Write-Step "Adapter $($adapter.Name) · $($adapter.InterfaceDescription) · driver $($adapter.DriverVersion)"

$report.advancedProperties = @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -AllProperties -ErrorAction SilentlyContinue |
    ForEach-Object {
        [ordered]@{
            keyword     = [string]$_.RegistryKeyword
            value       = @($_.RegistryValue | ForEach-Object { [string]$_ })
            validValues = @($_.ValidRegistryValues | ForEach-Object { [string]$_ })
        }
    })

$report.operationalBefore = Get-OperationalState -Adapter $adapter

# --- idle baseline -----------------------------------------------------------------
Write-Step "Measuring idle latency to $Target ($ProbeCount probes)"
$bytesBefore = Get-AdapterBytes -Adapter $adapter
$report.idle = Measure-Latency -Address $Target -Count $ProbeCount

# --- the keyword question ----------------------------------------------------------
if ($Keyword -and $AllowWrite) {
    $property = Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $Keyword -AllProperties -ErrorAction Stop
    $original = @($property.RegistryValue | ForEach-Object { [string]$_ })
    $wanted = @('0')
    if ($original -contains '0') { $wanted = @('1') }

    if (@($wanted | Where-Object { $_ -notin @($property.ValidRegistryValues | ForEach-Object { [string]$_ }) }).Count -gt 0) {
        $report.notRun.Add("Keyword $Keyword does not accept $($wanted -join ',')")
    }
    else {
        Write-Step "Writing $Keyword = $($wanted -join ',') with -NoRestart"

        $step = [ordered]@{
            keyword          = $Keyword
            originalValue    = $original
            requestedValue   = $wanted
            restartPerformed = $false
        }

        try {
            Set-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $Keyword -RegistryValue $wanted -NoRestart -Confirm:$false -ErrorAction Stop

            $step.storedValue = @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $Keyword -AllProperties -ErrorAction Stop |
                Select-Object -ExpandProperty RegistryValue | ForEach-Object { [string]$_ })

            # This is the whole question: the registry has the new value, but does the
            # stack report the feature in the new state without a restart?
            $step.operationalAfterWrite = Get-OperationalState -Adapter $adapter

            if ($AllowRestart) {
                if ($report.host.remoteSession) {
                    $report.notRun.Add('Adapter restart refused: this is a Remote Desktop session.')
                }
                else {
                    Write-Step 'Restarting the adapter (connections will drop)'
                    Restart-NetAdapter -Name $adapter.Name -Confirm:$false -ErrorAction Stop
                    $step.restartPerformed = $true

                    $deadline = (Get-Date).AddSeconds(45)
                    while ((Get-Date) -lt $deadline) {
                        Start-Sleep -Milliseconds 750
                        $back = Get-OperationalState -Adapter $adapter
                        if ($back.linkStatus -eq 'Up' -and $back.hasIPv4Address -and $back.hasDefaultRoute) { break }
                    }

                    $step.operationalAfterRestart = Get-OperationalState -Adapter $adapter
                    $step.loadedAfterRestart = Measure-Latency -Address $Target -Count $ProbeCount
                }
            }
            else {
                $report.notRun.Add('Adapter restart not attempted: -AllowRestart was not given.')
            }
        }
        finally {
            Write-Step "Restoring $Keyword to $($original -join ',')"
            Set-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $Keyword -RegistryValue $original -NoRestart -Confirm:$false -ErrorAction SilentlyContinue
            $step.restoredValue = @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $Keyword -AllProperties -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty RegistryValue | ForEach-Object { [string]$_ })
            $step.restoreMatchedOriginal = (@(Compare-Object -ReferenceObject $original -DifferenceObject $step.restoredValue).Count -eq 0)
        }

        $report.steps.Add($step)
    }
}
elseif ($Keyword) {
    $report.notRun.Add("Keyword $Keyword not written: -AllowWrite was not given.")
}

# --- the QoS question --------------------------------------------------------------
if ($AllowQos) {
    if (-not $BulkApplication) {
        throw '-AllowQos requires -BulkApplication, e.g. -BulkApplication steam.exe'
    }

    $existing = @(Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue)
    $foreign = @($existing | Where-Object { -not ([string]$_.Name).StartsWith($script:PolicyPrefix, [System.StringComparison]::Ordinal) })

    $qos = [ordered]@{
        foreignPolicyCount = $foreign.Count
        foreignPolicyNames = @($foreign | ForEach-Object { [string]$_.Name })
        application        = $BulkApplication
    }

    Write-Step "Creating $($script:PolicyName) for $BulkApplication (ActiveStore, 8 Mbit/s)"

    try {
        New-NetQosPolicy -Name $script:PolicyName `
                         -PolicyStore ActiveStore `
                         -AppPathNameMatchCondition $BulkApplication `
                         -ThrottleRateActionBitsPerSecond 8000000 `
                         -Precedence 127 `
                         -Confirm:$false -ErrorAction Stop | Out-Null

        $written = Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction Stop |
            Where-Object { $_.Name -eq $script:PolicyName } | Select-Object -First 1

        # Read back every condition and every action, not just the name: a policy of the
        # right name carrying the wrong rate looks exactly like one that works.
        $qos.readBack = [ordered]@{
            name         = [string]$written.Name
            owner        = [string]$written.Owner
            appPathName  = [string]$written.AppPathName
            throttleRate = $(if ($null -ne $written.ThrottleRateAction) { [uint64]$written.ThrottleRateAction } else { $null })
            precedence   = $(if ($null -ne $written.Precedence) { [int]$written.Precedence } else { $null })
            ipProtocol   = [string]$written.IPProtocol
        }

        $qos.readBackMatched = ($qos.readBack.appPathName -eq $BulkApplication -and $qos.readBack.throttleRate -eq 8000000)

        # Both of these need a human with a transfer running, so they are recorded as not
        # run rather than guessed at. They are the two questions Microsoft's reference
        # does not answer: whether the throttle is per flow or per application, and
        # whether a connection that predates the policy is governed by it.
        $report.notRun.Add('Single vs multiple TCP flow throttle behaviour: needs a real transfer; not measured by this run.')
        $report.notRun.Add('Whether a connection opened before the policy is governed by it: needs a real transfer; not measured by this run.')
    }
    finally {
        Write-Step "Removing $($script:PolicyName)"
        Remove-NetQosPolicy -Name $script:PolicyName -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue

        $remaining = @(Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $script:PolicyName })
        $qos.removed = ($remaining.Count -eq 0)

        $stillForeign = @(Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
            Where-Object { -not ([string]$_.Name).StartsWith($script:PolicyPrefix, [System.StringComparison]::Ordinal) })
        $qos.foreignPoliciesUntouched = ($stillForeign.Count -eq $foreign.Count)
    }

    $report.qos = $qos
}
else {
    $report.notRun.Add('QoS policy behaviour not exercised: -AllowQos was not given.')
}

# --- close out ---------------------------------------------------------------------
$report.operationalAfter = Get-OperationalState -Adapter $adapter
$bytesAfter = Get-AdapterBytes -Adapter $adapter
$report.dataUsedBytes = [ordered]@{
    sent     = [long]($bytesAfter.sent - $bytesBefore.sent)
    received = [long]($bytesAfter.received - $bytesBefore.received)
}
$report.finishedAt = (Get-Date).ToUniversalTime().ToString('o')

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts/latency-lab'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$file = Join-Path $OutputDirectory ("latency-lab-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $file -Encoding utf8

Write-Step "Wrote $file"

foreach ($skipped in $report.notRun) {
    Write-Host "NOT RUN: $skipped"
}
