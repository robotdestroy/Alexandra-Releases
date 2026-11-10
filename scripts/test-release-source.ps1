[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-source.ps1')
$count = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:count++
    Write-Host "PASS: $Name"
}
function Reject([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected $Name
}
$sha = 'a' * 40
$metadata = [pscustomobject]@{SchemaVersion=1;Repository='https://github.com/robotdestroy/Alexandra-Releases';Version='1.2.3';Commit=$sha}
Assert-ReleaseSourceMetadata $metadata '1.2.3' $sha
Check $true 'matching source metadata accepted'
Reject { Assert-ReleaseSourceMetadata $metadata '1.2.4' $sha } 'different binary version rejected'
Reject { Assert-ReleaseSourceMetadata $metadata '1.2.3' ('b'*40) } 'different source commit rejected'
$metadata.Repository = 'https://example.com/unreviewed'
Reject { Assert-ReleaseSourceMetadata $metadata '1.2.3' $sha } 'different repository rejected'
$metadata.Repository = 'https://github.com/robotdestroy/Alexandra-Releases'
$metadata.SchemaVersion = 2
Reject { Assert-ReleaseSourceMetadata $metadata '1.2.3' $sha } 'unknown metadata schema rejected'
Reject { Get-SourceReleaseNotes 'Notes' '../main' } 'non-commit source link rejected'
$notes = Get-SourceReleaseNotes 'Original notes' $sha
Check ($notes.Contains("/tree/$sha") -and $notes.Contains("/archive/$sha.zip") -and $notes.Contains("/blob/$sha/LICENSE")) 'new notes pin browse, ZIP and MIT links'
Check ($notes.Contains('built from public commit') -and $notes.Contains('source.json')) 'new notes describe matching binary source'
$historical = Get-SourceReleaseNotes 'Original notes' $sha -Historical
Check ($historical.Contains('not an exact source archive') -and $historical.Contains('Segoe UI')) 'historical notes disclose snapshot and font differences'
Check ($historical.StartsWith('Original notes')) 'existing notes preserved'
$again = Get-SourceReleaseNotes $historical $sha -Historical
Check ($again -ceq $historical) 'source-note updates are idempotent'

# Exercise real local Git checks with synthetic files; no API, game data or app settings.
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('alexandra-release-source-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'src/Alexandra.App') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'src/Alexandra.App/Alexandra.App.csproj'), '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>')
& git -C $fixture init --quiet
& git -C $fixture -c user.name=Fixture -c user.email=fixture@users.noreply.github.com add -- .
& git -C $fixture -c user.name=Fixture -c user.email=fixture@users.noreply.github.com commit --quiet -m 'Synthetic release-source test'
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize synthetic fixture.' }
$source = Get-ReleaseSource $fixture '1.2.3'
Check ($source.Commit -match '^[0-9a-f]{40}$' -and $source.Version -eq '1.2.3') 'clean version-matched source accepted'
Reject { Get-ReleaseSource $fixture '1.2.4' } 'version override cannot mislabel the source'
[IO.File]::AppendAllText((Join-Path $fixture 'src/Alexandra.App/Alexandra.App.csproj'), "`n<!-- changed -->")
Reject { Get-ReleaseSource $fixture '1.2.3' } 'dirty tracked source rejected'
& git -C $fixture -c user.name=Fixture -c user.email=fixture@users.noreply.github.com commit --quiet -am 'Synthetic tracked change'
if ($LASTEXITCODE -ne 0) { throw 'Could not update synthetic fixture.' }
[IO.File]::WriteAllText((Join-Path $fixture 'unreviewed.cs'), '// synthetic untracked source')
Reject { Get-ReleaseSource $fixture '1.2.3' } 'untracked source rejected'
Write-Host "All $count release-source checks passed. Synthetic fixture retained at $fixture"
