<#
.SYNOPSIS
    Fails if a branch touches files that belong only to this fork.

.DESCRIPTION
    This fork carries two kinds of change: fixes that upstream would want, and
    customisations that only make sense here - the installers pointing at this
    repository, the ghcr.io/icelogw image namespace, and the SilverLake banner
    in the sidebar. Opening an upstream pull request from a branch containing
    the second kind sends this fork's identity into someone else's project.

    Run this before opening a pull request against upstream. It compares the
    branch to the given base and reports any fork-only path it touches.

    Note: this file is deliberately ASCII-only. Windows PowerShell reads .ps1 as
    ANSI unless the file has a BOM, so a stray Unicode character here becomes a
    parse error rather than a script.

.EXAMPLE
    ./scripts/check-upstream-safe.ps1
    ./scripts/check-upstream-safe.ps1 -Base upstream/vibing
#>
param(
    [string]$Base = "upstream/vibing"
)

$ErrorActionPreference = "Stop"

# Paths that exist only to make this a fork. Anything here is local identity,
# not a fix, and must not appear in an upstream pull request.
$ForkOnlyPaths = @(
    'install.sh',
    'install.ps1',
    'docker-compose.build.yml'
)

# Files shared with upstream that contain fork-specific lines. Listed separately
# because changing the file is fine - only this content must not travel.
$ForkOnlyPatterns = [ordered]@{
    'ghcr.io/icelogw'        = "this fork's container registry namespace"
    'MineOS-SilverLake-Fork' = "this fork's repository name"
    'mineos-silverlake'      = "this fork's local image tags"
    'fork-tag'               = 'the SilverLake banner in the sidebar'
    'SilverLake'             = "this fork's branding"
}

git rev-parse --verify --quiet $Base > $null 2>&1
if (-not $?) {
    Write-Host "[ERR] Base ref '$Base' not found. Fetch it first: git fetch upstream" -ForegroundColor Red
    exit 2
}

$changed = @(git diff --name-only "$Base...HEAD")
if ($changed.Count -eq 0) {
    Write-Host "No changes against $Base." -ForegroundColor Yellow
    exit 0
}

$problems = @()

foreach ($file in $changed) {
    if ($ForkOnlyPaths -contains $file) {
        $problems += "  $file  (fork-only file)"
    }
}

# Only added lines matter: a fork-only string that was already upstream is not
# something this branch is introducing.
$added = git diff "$Base...HEAD" --unified=0 |
    Where-Object { $_.StartsWith('+') -and -not $_.StartsWith('+++') }

foreach ($pattern in $ForkOnlyPatterns.Keys) {
    $hits = @($added | Where-Object { $_ -match [regex]::Escape($pattern) })
    if ($hits.Count -gt 0) {
        $why = $ForkOnlyPatterns[$pattern]
        $problems += ("  adds '{0}' ({1}) - {2} line(s)" -f $pattern, $why, $hits.Count)
    }
}

Write-Host ""
if ($problems.Count -gt 0) {
    Write-Host "NOT SAFE for an upstream pull request." -ForegroundColor Red
    Write-Host "This branch carries fork-only changes:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ""
    Write-Host "Build the PR branch from $Base and cherry-pick only portable commits:" -ForegroundColor Yellow
    Write-Host "  git checkout -b pr/topic $Base" -ForegroundColor Yellow
    Write-Host "  git cherry-pick COMMIT [COMMIT...]" -ForegroundColor Yellow
    Write-Host "  ./scripts/check-upstream-safe.ps1" -ForegroundColor Yellow
    exit 1
}

Write-Host "Safe for an upstream pull request." -ForegroundColor Green
Write-Host ("  {0} file(s) changed against {1}, none fork-only." -f $changed.Count, $Base)
exit 0
