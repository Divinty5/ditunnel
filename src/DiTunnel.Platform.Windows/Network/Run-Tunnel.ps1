param(
    [Parameter(Mandatory=$true)][string]$RuntimePath,
    [Parameter(Mandatory=$true)][string]$ConfigurationPath,
    [Parameter(Mandatory=$true)][string]$ServerAddress,
    [Parameter(Mandatory=$true)][int]$OwnerProcessId,
    [Parameter(Mandatory=$true)][string]$TunnelName,
    [ValidateSet('ProxyAll','BypassSelected','ProxySelected')][string]$SplitTunnelMode = 'ProxyAll',
    [string]$SplitAddresses = '',
    [string]$SplitDomains = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ownedRoutes = [System.Collections.Generic.List[object]]::new()
$dnsRule = $null
$xrayProcess = $null
$mutex = [Threading.Mutex]::new($false, 'Global\DiTunnel.NetworkHost.v1')
$locked = $false
$cleanupFailed = $false
$splitRoutes = @{}
$nextSplitRefresh = [DateTime]::MinValue
$dnsComment = 'DiTunnel managed DNS v1'
$netshPath = Join-Path $env:SystemRoot 'System32\netsh.exe'
$routePath = Join-Path $env:SystemRoot 'System32\route.exe'
$xrayDiagnosticPath = Join-Path (Join-Path $env:LOCALAPPDATA 'DiTunnel') 'last-xray.log'
$netshRunner = {
    param([string[]]$arguments)
    & $netshPath @arguments 2>&1 | Out-Null
    return $LASTEXITCODE
}
$stopPath = $ConfigurationPath + '.stop'
$stage = 'PRECHECK'
function Test-StopRequested { return (Test-Path -LiteralPath $stopPath) -or $owner.HasExited }
function Enter-Stage([string]$name) {
    $script:stage = $name
    Emit ('STAGE_' + $name)
    if (Test-StopRequested) { throw [OperationCanceledException]::new() }
}
function Emit([string]$state) { [Console]::Out.WriteLine($state); [Console]::Out.Flush() }
function Save-XrayDiagnostics([string]$text) {
    try {
        $safe = $text -split "`r?`n" | Where-Object { $_ -match '(?i)(error|failed|fatal|panic)' } | Select-Object -Last 10 | ForEach-Object {
            $line = $_ -replace '(?i)\b(vless|vmess|trojan|ss|hy2|hysteria2)://\S+', '[profile]'
            $line = $line -replace '(?i)(password|token|secret|privatekey|uuid|auth)\s*[:=]\s*[^\s,}"\\]+', '$1=[redacted]'
            $line = $line -replace '\b(?:\d{1,3}\.){3}\d{1,3}\b', '[address]'
            if ($line.Length -gt 400) { $line.Substring(0, 400) } else { $line }
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($xrayDiagnosticPath)) | Out-Null
        [IO.File]::WriteAllLines($xrayDiagnosticPath, @($safe))
    } catch { }
}
function Test-OtherVpnActive {
    $hiddify = Get-Process -Name 'Hiddify' -ErrorAction SilentlyContinue
    if ($hiddify) {
        $internetSettings = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
        if ($internetSettings.ProxyEnable -eq 1) { return $true }
    }
    $vpnAdapter = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -ne $TunnelName -and $_.Status -eq 'Up' -and
        (($_.Name + ' ' + $_.InterfaceDescription) -match '(?i)(hiddify|wintun|wireguard|\btun\b|\btap\b|\bvpn\b)')
    } | Select-Object -First 1
    return $null -ne $vpnAdapter
}
function Add-TunnelAddress([string]$address, [byte]$prefixLength, [string]$addressFamily) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $lastExitCode = 0
    do {
        if ($xrayProcess.HasExited -or (Test-StopRequested)) { throw [OperationCanceledException]::new() }
        $adapter = Get-NetAdapter -Name $TunnelName -ErrorAction SilentlyContinue
        if ($adapter) {
            $script:index = $adapter.InterfaceIndex
            $existingAddress = Get-NetIPAddress -InterfaceIndex $index -IPAddress $address -ErrorAction SilentlyContinue
            if ($existingAddress) { return }
            $arguments = if ($addressFamily -eq 'IPv4') {
                @('interface', 'ipv4', 'set', 'address', "name=$TunnelName", 'source=static', "address=$address/$prefixLength", 'gateway=none', 'store=active')
            } else {
                @('interface', 'ipv6', 'add', 'address', "interface=$TunnelName", "address=$address/$prefixLength", 'store=active')
            }
            $lastExitCode = & $netshRunner $arguments
            if ($lastExitCode -eq 0) { return }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    Emit ('ERROR_NETSH_' + $lastExitCode)
    throw "The $addressFamily interface did not become ready"
}
function Add-OwnedRoute([string]$prefix, [object]$routeInterface, [string]$nextHop) {
    $family = if ($prefix.Contains(':')) { 'ipv6' } else { 'ipv4' }
    $routeKind = switch ($prefix) {
        '0.0.0.0/1' { 'IPV4_LOW' }
        '128.0.0.0/1' { 'IPV4_HIGH' }
        '::/1' { 'IPV6_LOW' }
        '8000::/1' { 'IPV6_HIGH' }
        default { 'HOST' }
    }
    if ($family -eq 'ipv4') {
        $destination, $length = $prefix -split '/', 2
        $mask = if ($length -eq '1') { '128.0.0.0' } else { '255.255.255.255' }
        $arguments = @('ADD', $destination, 'MASK', $mask, $nextHop, 'METRIC', '1', 'IF', $routeInterface)
    } else {
        $arguments = @('-6', 'ADD', $prefix, $nextHop, 'METRIC', '1', 'IF', $routeInterface)
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $netshOutput = ''
    do {
        $previousErrorAction = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'SilentlyContinue'
            $netshOutput = (& $routePath @arguments 2>&1) -join "`n"
        } finally { $ErrorActionPreference = $previousErrorAction }
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0) { break }
        if ($xrayProcess -and $xrayProcess.HasExited) { throw 'Xray stopped while adding a route' }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($exitCode -ne 0) {
        $reason = if ($netshOutput -match '(?i)already exists') { 'DUPLICATE' }
            elseif ($netshOutput -match '(?i)element not found') { 'NOT_FOUND' }
            elseif ($netshOutput -match '(?i)access is denied') { 'ACCESS_DENIED' }
            elseif ($netshOutput -match '(?i)parameter is incorrect') { 'INVALID_PARAMETER' }
            else { 'UNKNOWN' }
        Emit ('ERROR_ROUTE_' + $routeKind)
        Emit ('ERROR_ROUTE_REASON_' + $reason)
        $detail = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($netshOutput)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        Emit ('ERROR_ROUTE_DETAIL_' + $detail)
        Emit ('ERROR_ROUTE_CODE_' + $exitCode)
        throw "route.exe could not add the $family route"
    }
    $ownedRoutes.Add([pscustomobject]@{ DestinationPrefix = $prefix; RouteInterface = $routeInterface; NextHop = $nextHop; Family = $family })
}
function Remove-OwnedRoute([object]$route) {
    if ($route.Family -eq 'ipv4') {
        $destination, $length = $route.DestinationPrefix -split '/', 2
        $mask = if ($length -eq '1') { '128.0.0.0' } else { '255.255.255.255' }
        $arguments = @('DELETE', $destination, 'MASK', $mask, $route.NextHop, 'IF', $route.RouteInterface)
    } else {
        $arguments = @('-6', 'DELETE', $route.DestinationPrefix, $route.NextHop, 'IF', $route.RouteInterface)
    }
    & $routePath @arguments 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "route.exe could not remove the $($route.Family) route" }
    [void]$ownedRoutes.Remove($route)
}
function Update-SplitRoutes([string[]]$addresses) {
    if ($SplitTunnelMode -eq 'ProxyAll') { return }
    $desired = @{}
    foreach ($address in $addresses) {
        if ($address -and $address -ne $ServerAddress) { $desired[$address] = $true }
    }
    if ($desired.Count -eq 0) { return }
    foreach ($address in @($splitRoutes.Keys)) {
        if (-not $desired.ContainsKey($address)) {
            try { Remove-OwnedRoute $splitRoutes[$address]; $splitRoutes.Remove($address) }
            catch { }
        }
    }
    foreach ($address in $desired.Keys) {
        if ($splitRoutes.ContainsKey($address)) { continue }
        if ($SplitTunnelMode -eq 'BypassSelected') {
            Add-OwnedRoute "$address/32" $upstream.InterfaceIndex $upstream.NextHop
        } else {
            Add-OwnedRoute "$address/32" $index '0.0.0.0'
        }
        $splitRoutes[$address] = $ownedRoutes[$ownedRoutes.Count - 1]
    }
}
function Refresh-SplitRoutesIfDue {
    if ($SplitTunnelMode -eq 'ProxyAll' -or [DateTime]::UtcNow -lt $nextSplitRefresh) { return }
    $script:nextSplitRefresh = [DateTime]::UtcNow.AddSeconds(45)
    $addresses = [System.Collections.Generic.List[string]]::new()
    foreach ($domain in ($SplitDomains -split ';' | Where-Object { $_ })) {
        try {
            [Net.Dns]::GetHostAddresses($domain) |
                Where-Object { $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork } |
                ForEach-Object { $addresses.Add($_.ToString()) }
        } catch { }
    }
    if ($addresses.Count -gt 0) { Update-SplitRoutes @($addresses | Sort-Object -Unique) }
}
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another host is active' }
    # Remove only our own stale DNS policy (e.g. after power loss). ActiveStore routes do not survive reboot.
    Get-DnsClientNrptRule | Where-Object { $_.Comment -eq $dnsComment } | Remove-DnsClientNrptRule -Force
    if (@(Get-DnsClientNrptRule).Count -gt 0) { Emit 'ERROR_DNS_POLICY'; throw 'Existing DNS policy' }
    $owner = Get-Process -Id $OwnerProcessId
    $ownerStart = $owner.StartTime
    Enter-Stage 'PRECHECK'
    if (Test-OtherVpnActive) { Emit 'ERROR_OTHER_VPN'; throw 'Disconnect other VPN first' }
    $ip = [Net.IPAddress]::Parse($ServerAddress)
    if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'IPv4 upstream required' }
    $path = @(Find-NetRoute -RemoteIPAddress $ServerAddress)
    $upstream = $path | Where-Object { $_.PSObject.Properties.Name -contains 'NextHop' } | Select-Object -First 1
    if (-not $upstream -or $upstream.InterfaceAlias -eq $TunnelName) { throw 'No physical upstream route' }
    $physicalAdapter = Get-NetAdapter -InterfaceIndex $upstream.InterfaceIndex
    if (-not $physicalAdapter.HardwareInterface) { Emit 'ERROR_OTHER_VPN'; throw 'Disconnect other VPN first' }
    $defaultPrefixes = @('0.0.0.0/0','0.0.0.0/1','128.0.0.0/1','::/0','::/1','8000::/1')
    foreach ($otherRoute in (Get-NetRoute | Where-Object { $_.DestinationPrefix -in $defaultPrefixes })) {
        $otherAdapter = Get-NetAdapter -InterfaceIndex $otherRoute.InterfaceIndex -ErrorAction SilentlyContinue
        if ($otherAdapter -and -not $otherAdapter.HardwareInterface -and $otherAdapter.Status -eq 'Up') {
            Emit 'ERROR_OTHER_VPN'; throw 'Another tunnel has a default route'
        }
    }
    if (Get-NetAdapter -Name $TunnelName -ErrorAction SilentlyContinue) { throw 'Tunnel already exists' }
    # A /32 prevents the proxy connection from being captured by the new split default routes.
    $existing = @(Get-NetRoute -DestinationPrefix "$ServerAddress/32" -ErrorAction SilentlyContinue)
    if ($existing.Count -eq 0) { Add-OwnedRoute "$ServerAddress/32" $upstream.InterfaceIndex $upstream.NextHop }
    Enter-Stage 'XRAY'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $RuntimePath
    $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($RuntimePath)
    $startInfo.Arguments = 'run -config "' + $ConfigurationPath + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $xrayProcess = [Diagnostics.Process]::Start($startInfo)
    $discardOutput = $xrayProcess.StandardOutput.ReadToEndAsync()
    $discardError = $xrayProcess.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($xrayProcess.HasExited -or $owner.HasExited -or (Test-StopRequested)) { throw 'Startup interrupted' }
        $adapter = Get-NetAdapter -Name $TunnelName -ErrorAction SilentlyContinue
        if ($adapter) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $adapter) { throw 'Adapter unavailable' }
    Enter-Stage 'ADDRESSES'
    Add-TunnelAddress '172.31.255.1' 30 'IPv4'
    Add-TunnelAddress 'fd52:d17::1' 64 'IPv6'
    Enter-Stage 'ROUTES'
    $splitIps = @($SplitAddresses -split ',' | Where-Object { $_ -and $_ -ne $ServerAddress } | Sort-Object -Unique)
    if ($SplitTunnelMode -eq 'ProxySelected' -and $splitIps.Count -eq 0) { throw 'No selected domain addresses' }
    if ($SplitTunnelMode -ne 'ProxySelected') {
        Add-OwnedRoute '0.0.0.0/1' $index '0.0.0.0'
        Add-OwnedRoute '128.0.0.0/1' $index '0.0.0.0'
        Add-OwnedRoute '::/1' $index '::'
        Add-OwnedRoute '8000::/1' $index '::'
    }
    Update-SplitRoutes $splitIps
    if ($SplitTunnelMode -ne 'ProxySelected') {
        Enter-Stage 'DNS_RULE'
        $dnsRule = Add-DnsClientNrptRule -Namespace '.' -NameServers '1.1.1.1','1.0.0.1' -Comment $dnsComment -PassThru
        Enter-Stage 'DNS_CACHE'
        Clear-DnsClientCache
    }
    if ($SplitTunnelMode -eq 'ProxySelected') {
        Emit 'CONNECTED'
        while (-not (Test-StopRequested)) {
            $owner.Refresh(); $xrayProcess.Refresh()
            if ($owner.HasExited -or $owner.StartTime -ne $ownerStart) { break }
            if ($xrayProcess.HasExited) { Emit ('ERROR_XRAY_EXIT_' + $xrayProcess.ExitCode); break }
            if ((Get-NetAdapter -InterfaceIndex $upstream.InterfaceIndex).Status -ne 'Up') { Emit 'ERROR_NETWORK_CHANGED'; break }
            Refresh-SplitRoutesIfDue
            Start-Sleep -Milliseconds 500
        }
        return
    }
    Enter-Stage 'PROBE'
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    try {
        $probeCancellation = [Threading.CancellationTokenSource]::new()
        $probeTask = $client.GetAsync('https://www.cloudflare.com/cdn-cgi/trace', $probeCancellation.Token)
        while (-not $probeTask.IsCompleted) {
            if (Test-StopRequested) { $probeCancellation.Cancel(); throw [OperationCanceledException]::new() }
            Start-Sleep -Milliseconds 100
        }
        $probe = $probeTask.GetAwaiter().GetResult()
        $probe.EnsureSuccessStatusCode() | Out-Null
        $body = $probe.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($body -notmatch '(?m)^ip=') { throw 'Probe response invalid' }
        $probe.Dispose()
    } catch {
        # Routing is already installed. A failed optional HTTPS probe must not tear down a working tunnel.
        Emit 'PROBE_WARNING'
    } finally { $client.Dispose(); $handler.Dispose(); if ($probeCancellation) { $probeCancellation.Dispose() } }
    Emit 'CONNECTED'
    while (-not (Test-StopRequested)) {
        $owner.Refresh(); $xrayProcess.Refresh()
        if ($owner.HasExited -or $owner.StartTime -ne $ownerStart) { break }
        if ($xrayProcess.HasExited) { Emit ('ERROR_XRAY_EXIT_' + $xrayProcess.ExitCode); break }
        if ((Get-NetAdapter -InterfaceIndex $upstream.InterfaceIndex).Status -ne 'Up') { Emit 'ERROR_NETWORK_CHANGED'; break }
        Refresh-SplitRoutesIfDue
        Start-Sleep -Milliseconds 500
    }
} catch [OperationCanceledException] { Emit 'CANCELLED' }
catch {
    # Send only allowlisted stage/type information, never command arguments or server credentials.
    Emit ('ERROR_STAGE_' + $stage)
    if ($stage -eq 'XRAY' -and (Test-OtherVpnActive)) { Emit 'ERROR_OTHER_VPN' }
    Emit ('ERROR_CODE_' + $_.Exception.GetType().Name + '_' + $_.Exception.HResult)
    Emit ('ERROR_CATEGORY_' + $_.CategoryInfo.Category)
    $failedCommand = $_.InvocationInfo.MyCommand.Name
    if ($failedCommand -match '^(Add-DnsClientNrptRule|Clear-DnsClientCache|Find-NetRoute|Get-NetIPAddress)$') {
        Emit ('ERROR_COMMAND_' + $failedCommand)
    }
    if ($_.Exception.PSObject.Properties.Name -contains 'NativeErrorCode') { Emit ('ERROR_NATIVE_' + [int]$_.Exception.NativeErrorCode) }
    Emit 'ERROR_TUN'
}
finally {
    Emit 'STAGE_CLEANUP'
    # Keep the independent host alive through rollback even if the UI disappeared.
    if ($locked) {
        $routesToRemove = @($ownedRoutes)
        [array]::Reverse($routesToRemove)
        foreach ($route in $routesToRemove) {
            try { Remove-OwnedRoute $route }
            catch {
                # TUN routes are removed automatically together with the Wintun adapter below.
                # The physical /32 route to the proxy server still requires explicit cleanup.
                if ($route.RouteInterface -ne $index) { $cleanupFailed = $true }
            }
        }
        try {
            Get-DnsClientNrptRule | Where-Object { $_.Comment -eq $dnsComment } | Remove-DnsClientNrptRule -Force -ErrorAction Stop
            Clear-DnsClientCache
        } catch { $cleanupFailed = $true }
        try { if ($xrayProcess -and -not $xrayProcess.HasExited) { $xrayProcess.Kill(); $xrayProcess.WaitForExit() } } catch { $cleanupFailed = $true }
        try { Save-XrayDiagnostics (($discardOutput.GetAwaiter().GetResult()) + "`n" + ($discardError.GetAwaiter().GetResult())) } catch { }
        if ($xrayProcess) { $xrayProcess.Dispose() }
        try { Remove-Item -LiteralPath $ConfigurationPath -Force -ErrorAction Stop } catch { $cleanupFailed = $true }
        Remove-Item -LiteralPath $stopPath -Force -ErrorAction SilentlyContinue
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
    if ($cleanupFailed) { Emit 'ERROR_CLEANUP' } else { Emit 'STOPPED' }
}
