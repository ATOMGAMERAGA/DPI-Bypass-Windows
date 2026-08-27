using System.Net.NetworkInformation;
using System.Text.Json;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Network;

public sealed record LatencyApplyResult(bool Applied, string? Reason = null);

public interface ILatencyAdapterController
{
    Task<AdapterLatencyCapability?> DetectAsync(
        NetworkFingerprint network,
        CancellationToken cancellationToken = default);

    Task<LatencyApplyResult> ApplyAsync(
        AdapterLatencyCapability adapter,
        LatencyOptimizationCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<LatencyRestoreOutcome> RestoreAsync(
        LatencySettingSnapshot setting,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses the Windows NetAdapter cmdlets without interpolating adapter data into code.
/// </summary>
public sealed class WindowsLatencyAdapterController : ILatencyAdapterController
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly Action<string>? _log;

    public WindowsLatencyAdapterController(Action<string>? log = null) => _log = log;

    public async Task<AdapterLatencyCapability?> DetectAsync(
        NetworkFingerprint network,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(network.AdapterId))
        {
            return null;
        }

        var result = await RunAsync(
            DetectScript,
            new Dictionary<string, string?> { ["DPI_BYPASS_ADAPTER_ID"] = network.AdapterId },
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Success)
        {
            _log?.Invoke($"NIC düşük gecikme yetenekleri okunamadı: {DescribeFailure(result)}");
            return null;
        }

        var dto = Deserialize<CapabilityDto>(result.StandardOutput);
        if (dto is not { Found: true })
        {
            return null;
        }

        return new AdapterLatencyCapability
        {
            AdapterId = dto.AdapterId ?? network.AdapterId,
            AdapterName = dto.AdapterName ?? network.AdapterName ?? network.DisplayName,
            InterfaceDescription = dto.InterfaceDescription ?? string.Empty,
            AdapterType = network.AdapterType,
            IsPhysical = dto.HardwareInterface,
            IsVirtual = dto.Virtual,
            // NetworkFingerprint got here from the live .NET adapter and gateway,
            // so eligibility does not depend on the localised text form of Status.
            IsUp = network.IsOnline,
            PowerManagement = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["SelectiveSuspend"] = dto.Power?.SelectiveSuspend ?? 0,
                ["DeviceSleepOnDisconnect"] = dto.Power?.DeviceSleepOnDisconnect ?? 0,
                ["D0PacketCoalescing"] = dto.Power?.D0PacketCoalescing ?? 0,
            },
            AdvancedProperties = dto.AdvancedProperties.Select(property => new AdapterAdvancedPropertyCapability
            {
                RegistryKeyword = property.RegistryKeyword ?? string.Empty,
                RegistryValues = property.RegistryValues,
                ValidRegistryValues = property.ValidRegistryValues,
            }).ToList(),
        };
    }

    public async Task<LatencyApplyResult> ApplyAsync(
        AdapterLatencyCapability adapter,
        LatencyOptimizationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var environment = BuildEnvironment(adapter.AdapterId, candidate.PropertyName);
        environment["DPI_BYPASS_POWER_VALUE"] = candidate.DesiredPowerValue?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["DPI_BYPASS_REGISTRY_VALUES"] = JsonSerializer.Serialize(candidate.DesiredValues);

        var result = await RunAsync(ApplyScript, environment, TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Success)
        {
            return new LatencyApplyResult(false, DescribeFailure(result));
        }

        var dto = Deserialize<ApplyDto>(result.StandardOutput);
        return dto is null
            ? new LatencyApplyResult(false, "Windows geçerli bir uygulama sonucu döndürmedi.")
            : new LatencyApplyResult(dto.Applied, dto.Reason);
    }

    public async Task<LatencyRestoreOutcome> RestoreAsync(
        LatencySettingSnapshot setting,
        CancellationToken cancellationToken = default)
    {
        var environment = BuildEnvironment(setting.AdapterId, setting.PropertyName);
        environment["DPI_BYPASS_SETTING_KIND"] = setting.Kind.ToString();
        environment["DPI_BYPASS_POWER_VALUE"] = setting.OriginalPowerValue?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["DPI_BYPASS_REGISTRY_VALUES"] = JsonSerializer.Serialize(setting.OriginalValues);

        var result = await RunAsync(RestoreScript, environment, TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Success)
        {
            _log?.Invoke($"'{setting.PropertyName}' geri yüklenemedi: {DescribeFailure(result)}");
            return LatencyRestoreOutcome.Failed;
        }

        var dto = Deserialize<RestoreDto>(result.StandardOutput);
        if (dto is null || !Enum.TryParse<LatencyRestoreOutcome>(dto.Outcome, ignoreCase: true, out var outcome))
        {
            return LatencyRestoreOutcome.Failed;
        }

        if (!string.IsNullOrWhiteSpace(dto.Reason))
        {
            _log?.Invoke($"'{setting.PropertyName}' geri yükleme sonucu: {dto.Reason}");
        }

        return outcome;
    }

    private static Dictionary<string, string?> BuildEnvironment(string adapterId, string propertyName) => new()
    {
        ["DPI_BYPASS_ADAPTER_ID"] = adapterId,
        ["DPI_BYPASS_PROPERTY"] = propertyName,
    };

    private static Task<ProcessResult> RunAsync(
        string script,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => ProcessRunner.PowerShellWithEnvironmentAsync(script, environment, timeout, cancellationToken);

    private static T? Deserialize<T>(string json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json.Trim(), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeFailure(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(text) ? $"PowerShell çıkış kodu {result.ExitCode}" : text.Trim();
    }

    private sealed record CapabilityDto
    {
        public bool Found { get; init; }
        public string? AdapterId { get; init; }
        public string? AdapterName { get; init; }
        public string? InterfaceDescription { get; init; }
        public bool HardwareInterface { get; init; }
        public bool Virtual { get; init; }
        public PowerDto? Power { get; init; }
        public List<AdvancedPropertyDto> AdvancedProperties { get; init; } = [];
    }

    private sealed record PowerDto
    {
        public int SelectiveSuspend { get; init; }
        public int DeviceSleepOnDisconnect { get; init; }
        public int D0PacketCoalescing { get; init; }
    }

    private sealed record AdvancedPropertyDto
    {
        public string? RegistryKeyword { get; init; }
        public List<string> RegistryValues { get; init; } = [];
        public List<string> ValidRegistryValues { get; init; } = [];
    }

    private sealed record ApplyDto
    {
        public bool Applied { get; init; }
        public string? Reason { get; init; }
    }

    private sealed record RestoreDto
    {
        public string? Outcome { get; init; }
        public string? Reason { get; init; }
    }

    private const string DetectScript = """
        $ErrorActionPreference = 'Stop'

        function Find-DpiAdapter {
            $wanted = ($env:DPI_BYPASS_ADAPTER_ID -replace '[{}]', '').Trim()
            @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
                (([string]$_.InterfaceGuid) -replace '[{}]', '').Trim() -eq $wanted
            }) | Select-Object -First 1
        }

        function Read-PowerState($instance, [string]$name) {
            if ($null -eq $instance) { return 0 }
            $property = $instance.PSObject.Properties[$name]
            if ($null -eq $property -or $null -eq $property.Value) { return 0 }
            try { return [int]$property.Value } catch { return 0 }
        }

        $adapter = Find-DpiAdapter
        if ($null -eq $adapter) {
            [pscustomobject]@{ Found = $false } | ConvertTo-Json -Compress
            return
        }

        $power = $null
        try {
            $power = Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop
        } catch { }

        $advanced = @()
        try {
            $advanced = @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -AllProperties -ErrorAction Stop |
                Where-Object { $_.RegistryKeyword -eq '*InterruptModeration' } |
                ForEach-Object {
                    [pscustomobject]@{
                        RegistryKeyword = [string]$_.RegistryKeyword
                        RegistryValues = @($_.RegistryValue | ForEach-Object { [string]$_ })
                        ValidRegistryValues = @($_.ValidRegistryValues | ForEach-Object { [string]$_ })
                    }
                })
        } catch { }

        [pscustomobject]@{
            Found = $true
            AdapterId = [string]$adapter.InterfaceGuid
            AdapterName = [string]$adapter.Name
            InterfaceDescription = [string]$adapter.InterfaceDescription
            HardwareInterface = [bool]$adapter.HardwareInterface
            Virtual = [bool]$adapter.Virtual
            Power = [pscustomobject]@{
                SelectiveSuspend = Read-PowerState $power 'SelectiveSuspend'
                DeviceSleepOnDisconnect = Read-PowerState $power 'DeviceSleepOnDisconnect'
                D0PacketCoalescing = Read-PowerState $power 'D0PacketCoalescing'
            }
            AdvancedProperties = $advanced
        } | ConvertTo-Json -Depth 6 -Compress
        """;

    private const string ApplyScript = """
        $ErrorActionPreference = 'Stop'

        function Find-DpiAdapter {
            $wanted = ($env:DPI_BYPASS_ADAPTER_ID -replace '[{}]', '').Trim()
            @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
                (([string]$_.InterfaceGuid) -replace '[{}]', '').Trim() -eq $wanted
            }) | Select-Object -First 1
        }

        function Result([bool]$applied, [string]$reason) {
            [pscustomobject]@{ Applied = $applied; Reason = $reason } | ConvertTo-Json -Compress
        }

        try {
            $adapter = Find-DpiAdapter
            if ($null -eq $adapter -or -not [bool]$adapter.HardwareInterface -or [bool]$adapter.Virtual) {
                Result $false 'Aktif fiziksel bağdaştırıcı artık bulunamadı.'
                return
            }

            $property = $env:DPI_BYPASS_PROPERTY
            if ($property -in @('SelectiveSuspend', 'DeviceSleepOnDisconnect', 'D0PacketCoalescing')) {
                $value = [int]$env:DPI_BYPASS_POWER_VALUE
                switch ($property) {
                    'SelectiveSuspend' {
                        Set-NetAdapterPowerManagement -Name $adapter.Name -SelectiveSuspend $value -NoRestart -Confirm:$false -ErrorAction Stop
                    }
                    'DeviceSleepOnDisconnect' {
                        Set-NetAdapterPowerManagement -Name $adapter.Name -DeviceSleepOnDisconnect $value -NoRestart -Confirm:$false -ErrorAction Stop
                    }
                    'D0PacketCoalescing' {
                        Set-NetAdapterPowerManagement -Name $adapter.Name -D0PacketCoalescing $value -NoRestart -Confirm:$false -ErrorAction Stop
                    }
                }

                $current = Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop
                $actual = [int]$current.PSObject.Properties[$property].Value
                Result ($actual -eq $value) ($(if ($actual -eq $value) { $null } else { 'Sürücü değeri canlı olarak uygulamadı; yeniden başlatma gerektiren ayar atlandı.' }))
                return
            }

            if ($property -eq '*InterruptModeration') {
                $current = Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -AllProperties -ErrorAction Stop
                $values = @($env:DPI_BYPASS_REGISTRY_VALUES | ConvertFrom-Json | ForEach-Object { [string]$_ })
                $valid = @($current.ValidRegistryValues | ForEach-Object { [string]$_ })
                if (@($values | Where-Object { $_ -notin $valid }).Count -gt 0) {
                    Result $false 'Sürücü istenen RegistryValue değerini desteklemiyor.'
                    return
                }

                Set-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -RegistryValue $values -NoRestart -Confirm:$false -ErrorAction Stop
                $actual = @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -AllProperties -ErrorAction Stop |
                    Select-Object -ExpandProperty RegistryValue | ForEach-Object { [string]$_ })
                $same = (@(Compare-Object -ReferenceObject $values -DifferenceObject $actual).Count -eq 0)
                Result $same ($(if ($same) { $null } else { 'Sürücü değeri canlı olarak uygulamadı; yeniden başlatma gerektiren ayar atlandı.' }))
                return
            }

            Result $false 'İzin verilmeyen NIC özelliği reddedildi.'
        } catch {
            Result $false $_.Exception.Message
        }
        """;

    private const string RestoreScript = """
        $ErrorActionPreference = 'Stop'

        function Find-DpiAdapter {
            $wanted = ($env:DPI_BYPASS_ADAPTER_ID -replace '[{}]', '').Trim()
            @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
                (([string]$_.InterfaceGuid) -replace '[{}]', '').Trim() -eq $wanted
            }) | Select-Object -First 1
        }

        function Result([string]$outcome, [string]$reason) {
            [pscustomobject]@{ Outcome = $outcome; Reason = $reason } | ConvertTo-Json -Compress
        }

        try {
            $adapter = Find-DpiAdapter
            if ($null -eq $adapter) {
                Result 'MissingAdapter' 'Bağdaştırıcı artık sistemde yok; anlık görüntü korundu.'
                return
            }

            $property = $env:DPI_BYPASS_PROPERTY
            if ($env:DPI_BYPASS_SETTING_KIND -eq 'PowerManagement') {
                if ($property -notin @('SelectiveSuspend', 'DeviceSleepOnDisconnect', 'D0PacketCoalescing')) {
                    Result 'MissingProperty' 'Snapshot içindeki özellik bu sürüm tarafından yönetilmiyor.'
                    return
                }

                $power = Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop
                $member = $power.PSObject.Properties[$property]
                if ($null -eq $member -or [int]$member.Value -eq 0) {
                    Result 'MissingProperty' 'Sürücü güncellemesinden sonra güç özelliği bulunamadı.'
                    return
                }

                $original = [int]$env:DPI_BYPASS_POWER_VALUE
                if ([int]$member.Value -eq $original) {
                    Result 'AlreadyOriginal' $null
                    return
                }

                switch ($property) {
                    'SelectiveSuspend' {
                        Set-NetAdapterPowerManagement -Name $adapter.Name -SelectiveSuspend $original -NoRestart -Confirm:$false -ErrorAction Stop
                    }
                    'DeviceSleepOnDisconnect' {
                        Set-NetAdapterPowerManagement -Name $adapter.Name -DeviceSleepOnDisconnect $original -NoRestart -Confirm:$false -ErrorAction Stop
                    }
                    'D0PacketCoalescing' {
                        Set-NetAdapterPowerManagement -Name $adapter.Name -D0PacketCoalescing $original -NoRestart -Confirm:$false -ErrorAction Stop
                    }
                }

                $actual = Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop
                if ([int]$actual.PSObject.Properties[$property].Value -eq $original) {
                    Result 'Restored' $null
                } else {
                    Result 'Failed' 'Sürücü özgün güç değerini canlı olarak geri almadı.'
                }
                return
            }

            if ($env:DPI_BYPASS_SETTING_KIND -eq 'AdvancedProperty') {
                $current = Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -AllProperties -ErrorAction SilentlyContinue
                if ($null -eq $current) {
                    Result 'MissingProperty' 'Sürücü güncellemesinden sonra gelişmiş özellik bulunamadı.'
                    return
                }

                $original = @($env:DPI_BYPASS_REGISTRY_VALUES | ConvertFrom-Json | ForEach-Object { [string]$_ })
                $actualBefore = @($current.RegistryValue | ForEach-Object { [string]$_ })
                if (@(Compare-Object -ReferenceObject $original -DifferenceObject $actualBefore).Count -eq 0) {
                    Result 'AlreadyOriginal' $null
                    return
                }

                Set-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -RegistryValue $original -NoRestart -Confirm:$false -ErrorAction Stop
                $actual = @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -AllProperties -ErrorAction Stop |
                    Select-Object -ExpandProperty RegistryValue | ForEach-Object { [string]$_ })
                if (@(Compare-Object -ReferenceObject $original -DifferenceObject $actual).Count -eq 0) {
                    Result 'Restored' $null
                } else {
                    Result 'Failed' 'Sürücü özgün RegistryValue değerini canlı olarak geri almadı.'
                }
                return
            }

            Result 'MissingProperty' 'Bilinmeyen snapshot ayar türü atlandı.'
        } catch {
            Result 'Failed' $_.Exception.Message
        }
        """;
}
