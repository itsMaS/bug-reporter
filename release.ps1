<#
.SYNOPSIS
  Cuts a bug-reporter release end to end.

.DESCRIPTION
  .\release.ps1 1.4.0 [-Notes "..."]

  1. Verifies: on main, clean working tree, tag vX.Y.Z does not exist yet.
  2. Bumps <Version> in bug-reporter.csproj.
  3. Wipes publish-selfcontained\, publishes self-contained single-file.
  4. Verifies the exe carries the new version and the folder has no stray *.zip / *.log.
  5. Zips publish-selfcontained\* to .\bug-reporter-win.zip and verifies it (no nested zip).
  6. Commits the version bump, tags vX.Y.Z, pushes main and the tag.
  7. Creates the GitHub release with the zip, verifies the uploaded asset size.
  8. Deletes the local zip.

  The in-app updater reads releases/latest and compares the tag against the assembly
  version, so the tag and the csproj <Version> must match, and the asset must be named
  bug-reporter-win.zip. This script guarantees both.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Notes,

    [string]$Repo = 'itsMaS/bug-reporter'
)

$ErrorActionPreference = 'Stop'
$root      = $PSScriptRoot
$csproj    = Join-Path $root 'bug-reporter.csproj'
$publish   = Join-Path $root 'publish-selfcontained'
$zip       = Join-Path $root 'bug-reporter-win.zip'
$tag       = "v$Version"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

Set-Location $root
$env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')

# ── 1. Preconditions ──────────────────────────────────────────────────────────
Step "Checking preconditions for $tag"
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { Fail "Must release from main (currently on '$branch')." }
$dirty = git status --porcelain
if ($dirty) { Fail "Working tree has uncommitted changes:`n$dirty" }
if (git tag -l $tag) { Fail "Tag $tag already exists." }
git fetch origin main --quiet
$behind = (git rev-list --count "HEAD..origin/main").Trim()
if ($behind -ne '0') { Fail "Local main is $behind commit(s) behind origin/main. Pull first." }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail "gh CLI not found on PATH." }

# ── 2. Release notes (commits since last tag, before we add the bump commit) ──
if (-not $Notes) {
    $lastTag = git describe --tags --abbrev=0 2>$null
    if ($lastTag) {
        $lines = git log "$lastTag..HEAD" --pretty=format:'- %s'
        $Notes = ($lines -join "`n")
    }
    if (-not $Notes) { $Notes = "Release $tag" }
}

# ── 3. Bump version ───────────────────────────────────────────────────────────
Step "Setting <Version> to $Version"
$content = [IO.File]::ReadAllText($csproj)
if ($content -notmatch '<Version>[^<]+</Version>') { Fail "No <Version> element in bug-reporter.csproj." }
$content = $content -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText($csproj, $content, (New-Object System.Text.UTF8Encoding($true)))

function Revert-Bump { git checkout -- $csproj | Out-Null }

# ── 4. Build ──────────────────────────────────────────────────────────────────
Step "Publishing self-contained build"
Stop-Process -Name 'bug-reporter' -ErrorAction SilentlyContinue
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zip -Force -ErrorAction SilentlyContinue
dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Revert-Bump; Fail "dotnet publish failed." }

$exe = Join-Path $publish 'bug-reporter.exe'
if (-not (Test-Path $exe)) { Revert-Bump; Fail "bug-reporter.exe missing from publish output." }
$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
if (-not $fileVersion.StartsWith("$Version.")) { Revert-Bump; Fail "Built exe reports version '$fileVersion', expected '$Version.0'." }

$stray = Get-ChildItem $publish -Recurse -Include *.zip, *.log
if ($stray) { Revert-Bump; Fail "Stray files in publish folder:`n$($stray.FullName -join "`n")" }

# ── 5. Zip + verify ───────────────────────────────────────────────────────────
Step "Creating bug-reporter-win.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries   = $archive.Entries.Count
    $nested    = @($archive.Entries | Where-Object { $_.Name -like '*.zip' }).Count
    $hasExe    = @($archive.Entries | Where-Object { $_.FullName -eq 'bug-reporter.exe' }).Count -eq 1
} finally { $archive.Dispose() }
$zipMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "    $entries entries, $zipMB MB, nested zips: $nested"
if ($nested -ne 0) { Revert-Bump; Fail "Zip contains a nested zip." }
if (-not $hasExe)  { Revert-Bump; Fail "Zip does not contain bug-reporter.exe at its root." }
if ($zipMB -lt 100 -or $zipMB -gt 300) { Revert-Bump; Fail "Zip is $zipMB MB; expected roughly 200 MB. Check the publish output." }

# ── 6. Commit, tag, push ──────────────────────────────────────────────────────
Step "Committing version bump and tagging $tag"
git add $csproj
git commit -q -m "Release $tag"
git tag $tag
git push origin main
git push origin $tag

# ── 7. GitHub release ─────────────────────────────────────────────────────────
Step "Creating GitHub release $tag"
gh release create $tag $zip --repo $Repo --target main --title $tag --notes $Notes
if ($LASTEXITCODE -ne 0) { Fail "gh release create failed. Tag and commit are pushed; fix and run: gh release create $tag $zip --repo $Repo --title $tag" }

$localSize = (Get-Item $zip).Length
# Parse JSON in PowerShell rather than passing a jq filter with embedded quotes; PowerShell 5.1
# strips inner double quotes from native-command arguments.
function Get-RemoteAssetSize {
    $releaseId = (gh api "repos/$Repo/releases/tags/$tag" --jq '.id' | Out-String).Trim()
    if (-not $releaseId) { return $null }
    $assets = gh api "repos/$Repo/releases/$releaseId/assets" | ConvertFrom-Json
    $asset = $assets | Where-Object { $_.name -eq 'bug-reporter-win.zip' } | Select-Object -First 1
    if ($asset) { return [int64]$asset.size } else { return $null }
}
$remoteSize = Get-RemoteAssetSize
if ($null -eq $remoteSize -or $remoteSize -ne $localSize) {
    Write-Host "    Asset listing lagged or mismatched (remote '$remoteSize' vs local $localSize); retrying in 5s"
    Start-Sleep -Seconds 5
    $remoteSize = Get-RemoteAssetSize
}
if ($null -eq $remoteSize -or $remoteSize -ne $localSize) { Fail "Uploaded asset size ($remoteSize) does not match local zip ($localSize). Re-upload with: gh release upload $tag $zip --repo $Repo --clobber" }

# ── 8. Cleanup ────────────────────────────────────────────────────────────────
Remove-Item $zip -Force
Step "Released $tag ($zipMB MB): https://github.com/$Repo/releases/tag/$tag"
