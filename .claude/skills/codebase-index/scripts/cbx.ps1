# Windows PowerShell wrapper around the installed `codebase-index` CLI.
# Mirrors scripts/cbx: whitelist, index gate, then PATH CLI, then py -3.12, then
# python -m. The gate is gate-index.py under the first Python 3.12 found; a
# refusal is its exit and no such Python is exit 4, as cbx has it.
# Python fallbacks pass -P / PYTHONSAFEPATH so a checkout codebase_index.py
# is not imported.
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Subcommand,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = "Stop"
$allowed = @(
    "search", "explain", "architecture", "symbol", "refs", "impact", "diff-impact",
    "path", "describe", "verify", "stats", "doctor", "update", "index"
)

if ($allowed -notcontains $Subcommand) {
    [Console]::Error.WriteLine("cbx: refusing subcommand '$Subcommand'. Allowed: $($allowed -join ', ')")
    exit 2
}

# A script runs in its caller's process, so both variables are put back in the
# `finally`, and the one exit after it is the only way out: an exit inside the
# `try` would leave them set for every later `python` the caller starts.
$savedSafePath = $env:PYTHONSAFEPATH
$savedAutoUpdate = $env:CBX_NO_SKILL_AUTO_UPDATE
$saved = $ErrorActionPreference
$code = $null
try {
    $env:CBX_NO_SKILL_AUTO_UPDATE = "1"
    $env:PYTHONSAFEPATH = "1"
    $ErrorActionPreference = "Continue"
    $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
    $py = Get-Command python -ErrorAction SilentlyContinue
    $gateProbe = "import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 12) else 1)"
    $gateRun = $null
    $gateArgs = @()
    if ($pyLauncher) {
        & $pyLauncher.Source -3.12 -I -c $gateProbe 2>$null
        if ($LASTEXITCODE -eq 0) { $gateRun = $pyLauncher.Source; $gateArgs = @("-3.12") }
    }
    if ($null -eq $gateRun -and $py) {
        & $py.Source -I -c $gateProbe 2>$null
        if ($LASTEXITCODE -eq 0) { $gateRun = $py.Source }
    }
    $ErrorActionPreference = $saved
    if ($null -eq $gateRun) {
        [Console]::Error.WriteLine("gate-index: no Python 3.12, so the index cannot be judged; refusing to open it")
        $code = 4
    } else {
        & $gateRun @gateArgs -I (Join-Path $PSScriptRoot "gate-index.py")
        if ($LASTEXITCODE -ne 0) { $code = $LASTEXITCODE }
    }
    $bin = Get-Command codebase-index -ErrorAction SilentlyContinue
    if ($null -eq $code -and $bin) {
        & $bin.Source $Subcommand @Rest
        $code = $LASTEXITCODE
    }
    $ErrorActionPreference = "Continue"
    if ($null -eq $code -and $pyLauncher) {
        & $pyLauncher.Source -3.12 -P -c "import codebase_index" 2>$null
        if ($LASTEXITCODE -eq 0) {
            $ErrorActionPreference = $saved
            & $pyLauncher.Source -3.12 -P -m codebase_index $Subcommand @Rest
            $code = $LASTEXITCODE
        }
    }
    if ($null -eq $code -and $py) {
        & $py.Source -P -c "import sys, codebase_index; raise SystemExit(0 if sys.version_info[:2] == (3, 12) else 1)" 2>$null
        if ($LASTEXITCODE -eq 0) {
            $ErrorActionPreference = $saved
            & $py.Source -P -m codebase_index $Subcommand @Rest
            $code = $LASTEXITCODE
        }
    }
} finally {
    $env:PYTHONSAFEPATH = $savedSafePath
    $env:CBX_NO_SKILL_AUTO_UPDATE = $savedAutoUpdate
    $ErrorActionPreference = $saved
}
if ($null -eq $code) {
    [Console]::Error.WriteLine("cbx: codebase-index CLI not on PATH and no Python 3.12 environment can import codebase_index")
    $code = 127
}
exit $code
