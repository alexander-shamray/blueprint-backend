# Windows PowerShell wrapper around the installed `codebase-index` CLI.
# Mirrors scripts/cbx: whitelist, then PATH CLI, then py -3.12, then python -m.
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Subcommand,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = "Stop"
$env:CBX_NO_SKILL_AUTO_UPDATE = "1"
$allowed = @(
    "search", "explain", "architecture", "symbol", "refs", "impact", "diff-impact",
    "path", "describe", "verify", "stats", "doctor", "update", "index"
)

if ($allowed -notcontains $Subcommand) {
    [Console]::Error.WriteLine("cbx: refusing subcommand '$Subcommand'. Allowed: $($allowed -join ', ')")
    exit 2
}

$bin = Get-Command codebase-index -ErrorAction SilentlyContinue
if ($bin) {
    & $bin.Source $Subcommand @Rest
    exit $LASTEXITCODE
}
$saved = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$pyLauncher = Get-Command py -ErrorAction SilentlyContinue
if ($pyLauncher) {
    & $pyLauncher.Source -3.12 -c "import codebase_index" 2>$null
    if ($LASTEXITCODE -eq 0) {
        $ErrorActionPreference = $saved
        & $pyLauncher.Source -3.12 -m codebase_index $Subcommand @Rest
        exit $LASTEXITCODE
    }
}
$py = Get-Command python -ErrorAction SilentlyContinue
if ($py) {
    & $py.Source -c "import sys, codebase_index; raise SystemExit(0 if sys.version_info[:2] == (3, 12) else 1)" 2>$null
    if ($LASTEXITCODE -eq 0) {
        $ErrorActionPreference = $saved
        & $py.Source -m codebase_index $Subcommand @Rest
        exit $LASTEXITCODE
    }
}
$ErrorActionPreference = $saved
[Console]::Error.WriteLine("cbx: codebase-index CLI not on PATH and no Python 3.12 environment can import codebase_index")
exit 127
