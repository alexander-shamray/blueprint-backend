# What stack.ps1 and generator.ps1 share, the stress run's two machines
# (ADR-172): the repository's paths, this machine's LAN address, and the
# line protocol on which the generator asks for the run and hands its
# results back.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$script:StressDir = Join-Path $script:RepoRoot 'deploy\observability\stress'
$script:ComposeDir = Join-Path $script:RepoRoot 'deploy\compose'
$script:RemoteOverride = Join-Path $script:ComposeDir 'docker-compose.stress-remote.yml'

# The port the generator asks for the run on and returns raw.json to, apart
# from every port Compose publishes.
$script:ResultsPort = 5299

# The requests, each a first line followed by the run's token: RESULT is
# followed by raw.json, and ABORT ends a run that will send none, so the
# stack's ports close with it.
$script:PlanRequest = 'PLAN'
$script:ResultRequest = 'RESULT'
$script:AbortRequest = 'ABORT'

# A request line is a word and a token; raw.json is a few hundred KiB, so
# anything past these is not one. A connection gets the deadline whole, so a
# peer that trickles bytes cannot hold the stack's one listener.
$script:RequestLineLimit = 256
$script:ResultLimitChars = 64MB
$script:RequestDeadline = [TimeSpan]::FromMinutes(2)

$script:Utf8 = [System.Text.UTF8Encoding]::new($false)

# A native command's failure is an exit code, which PowerShell does not throw
# on. -Stream shows a long command's output as it comes instead of returning it.
function Invoke-Native {
    param(
        [Parameter(Mandatory)][scriptblock]$Command,
        [Parameter(Mandatory)][string]$What,
        [switch]$Stream
    )

    $output = if ($Stream) { & $Command | Out-Host } else { & $Command }
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
    return $output
}

# Enough of a failed service's log for an init script's error or the head of a stack trace.
$script:FailedLogLines = 30

# Each container of the stack that exited non-zero, is unhealthy or was never started, one row a service.
function Get-FailedServices {
    param([Parameter(Mandatory)][string[]]$Compose)

    $lines = Invoke-Native { docker compose @Compose ps -a --format json } 'Listing the stack'
    $failed = foreach ($line in @($lines | Where-Object { $_ -and $_.Trim() })) {
        foreach ($container in @($line | ConvertFrom-Json)) {
            $reason = if ($container.State -eq 'exited' -and $container.ExitCode -ne 0) {
                "exited with code $($container.ExitCode)"
            } elseif ($container.Health -eq 'unhealthy') {
                'unhealthy'
            } elseif ($container.State -eq 'created') {
                'never started'
            }
            if ($reason) {
                [pscustomobject]@{
                    Service = $container.Service
                    Reason = $reason
                    Started = $container.State -ne 'created'
                }
            }
        }
    }
    # A service that never started was waiting on one that failed, so the failed ones come first.
    @($failed | Sort-Object Service -Unique | Sort-Object @{ Expression = 'Started'; Descending = $true }, Service)
}

# Compose's up names the first service it gave up on and not why, which is in that container's log; so a failed
# up lists every service that failed and shows each one's log, and the exception names them.
function Invoke-ComposeUp {
    param(
        [Parameter(Mandatory)][string[]]$Files,
        [Parameter(Mandatory)][string]$What,
        [string[]]$Arguments = @()
    )

    $compose = @($Files | ForEach-Object { '-f', $_ })
    try {
        Invoke-Native { docker compose @compose up -d @Arguments --wait } $What -Stream
    } catch {
        $upError = $_
        # Listing the stack can fail as the up did, and then the up's own error is the one to keep.
        $failed = @(try { Get-FailedServices $compose } catch { })
        if ($failed.Count -eq 0) {
            throw $upError
        }
        foreach ($service in @($failed | Where-Object Started)) {
            Write-Host ''
            Write-Host "$($service.Service), $($service.Reason). Its log's last $FailedLogLines lines:"
            & docker compose @compose logs --no-color --no-log-prefix --tail $FailedLogLines $service.Service |
                Out-Host
        }
        $names = ($failed | ForEach-Object { "$($_.Service) $($_.Reason)" }) -join '; '
        throw "$($upError.Exception.Message) Failed: $names. Each started one's log is above."
    }
}

# The IPv4 address of the interface holding the default route, the one the
# LAN reaches: WSL's and Hyper-V's switches hold none. A link-local address
# is a missing DHCP lease, not a LAN.
function Get-LanAddress {
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object {
            $_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric
        } |
        Select-Object -First 1
    if (-not $route) {
        throw 'No interface holds a default route, so this machine has no LAN address to use.'
    }

    $ip = Get-NetIPAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -notlike '169.254.*' } |
        Select-Object -First 1
    if (-not $ip) {
        throw "Interface $($route.InterfaceAlias) holds the default route but no IPv4 address."
    }

    $connection = Get-NetConnectionProfile -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue
    return [pscustomobject]@{
        Address = $ip.IPAddress
        PrefixLength = [int]$ip.PrefixLength
        Interface = $ip.InterfaceAlias
        Category = if ($connection) { [string]$connection.NetworkCategory } else { 'Unknown' }
    }
}

# Whether two IPv4 addresses share the network a prefix length draws, in
# 64-bit arithmetic, since a 32-bit shift in PowerShell can widen or sign.
function Test-SameSubnet {
    param([string]$A, [string]$B, [int]$PrefixLength)

    $all = [uint64]4294967295
    $mask = ($all -shl (32 - $PrefixLength)) -band $all
    $toNumber = {
        param($text)
        $bytes = [System.Net.IPAddress]::Parse($text).GetAddressBytes()
        [Array]::Reverse($bytes)
        [uint64][BitConverter]::ToUInt32($bytes, 0)
    }
    return ((& $toNumber $A) -band $mask) -eq ((& $toNumber $B) -band $mask)
}

# The host port docker-compose.stress-remote.yml publishes a service on, so
# the file stays the one copy of each.
function Get-RemotePort {
    param([Parameter(Mandatory)][string]$Service)

    $text = Get-Content -Raw $script:RemoteOverride
    $match = [regex]::Match($text, "(?ms)^  $([regex]::Escape($Service)):\r?\n.*?\}:(\d+):\d+`"")
    if (-not $match.Success) {
        throw "docker-compose.stress-remote.yml publishes no LAN port for $Service."
    }
    return [int]$match.Groups[1].Value
}

function Assert-Deadline {
    param([System.Diagnostics.Stopwatch]$Clock)

    if ($Clock.Elapsed -gt $script:RequestDeadline) {
        throw "The peer took longer than $($script:RequestDeadline.TotalSeconds) s."
    }
}

# The request's first line, refused past RequestLineLimit rather than read whole.
function Read-RequestLine {
    param([System.IO.StreamReader]$Reader, [System.Diagnostics.Stopwatch]$Clock)

    $line = [System.Text.StringBuilder]::new()
    while (($c = $Reader.Read()) -ge 0 -and $c -ne 10) {
        Assert-Deadline $Clock
        if ($line.Length -ge $script:RequestLineLimit) {
            throw "The peer's request line runs past $($script:RequestLineLimit) characters."
        }
        [void]$line.Append([char]$c)
    }
    return $line.ToString().TrimEnd("`r")
}

# Reads what is left of a stream as text, refusing more than the limit.
function Read-Bounded {
    param([System.IO.StreamReader]$Reader, [int]$Limit, [System.Diagnostics.Stopwatch]$Clock)

    $text = [System.Text.StringBuilder]::new()
    $buffer = [char[]]::new(65536)
    while (($read = $Reader.Read($buffer, 0, $buffer.Length)) -gt 0) {
        Assert-Deadline $Clock
        if ($text.Length + $read -gt $Limit) {
            throw "The peer sent more than $Limit characters."
        }
        [void]$text.Append($buffer, 0, $read)
    }
    return $text.ToString()
}

# The run's token against the one a request carries, in constant time.
function Test-Token {
    param([string]$Expected, [string]$Offered)

    return [System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
        $script:Utf8.GetBytes($Expected), $script:Utf8.GetBytes($Offered))
}
