# Shared, read-only source checks and release-note formatting.
function Get-ReleaseSource([string]$Root, [string]$Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
    $commit = (& git -C $Root rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'A committed source checkout is required.' }
    $changes = @(& git -C $Root status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -gt 0) { throw 'Commit all source changes before building or publishing a release.' }
    $project = [xml](Get-Content -LiteralPath (Join-Path $Root 'src/Alexandra.App/Alexandra.App.csproj') -Raw)
    if ([string]$project.Project.PropertyGroup.Version -ne $Version) {
        throw 'The committed project version must match the requested release version.'
    }
    return [pscustomobject][ordered]@{
        SchemaVersion = 1
        Repository = 'https://github.com/robotdestroy/Alexandra-Releases'
        Version = $Version
        Commit = $commit.Trim()
    }
}

function Assert-ReleaseSourceMetadata($Metadata, [string]$Version, [string]$Commit) {
    if ($Metadata.SchemaVersion -ne 1 -or
        $Metadata.Repository -cne 'https://github.com/robotdestroy/Alexandra-Releases' -or
        $Metadata.Version -cne $Version -or $Metadata.Commit -cne $Commit -or
        $Commit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Release source metadata does not match the reviewed source revision.'
    }
}

function Assert-PublishedReleaseSource($Source) {
    # Require the audited public main revision, never a private local-only commit.
    $remote = @(& git ls-remote --exit-code ($Source.Repository + '.git') refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $remote.Count -ne 1 -or
        ($remote[0] -split '\s+')[0] -cne $Source.Commit) {
        throw 'Push this reviewed source commit to public main before building or publishing.'
    }
}

function Get-RemoteReleaseCommit([string]$Repository, [string]$Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
    $ref = "refs/tags/v$Version"
    $lines = @(& git ls-remote --exit-code ($Repository + '.git') $ref ($ref + '^{}'))
    if ($LASTEXITCODE -eq 2) { return $null }
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the remote release tag.' }
    $peeled = @($lines | Where-Object { ($_ -split '\s+')[1] -ceq ($ref + '^{}') })
    if ($peeled.Count -eq 1) { return ($peeled[0] -split '\s+')[0] }
    if ($lines.Count -ne 1) { throw 'Unexpected release tag response.' }
    return ($lines[0] -split '\s+')[0]
}

function Get-SourceReleaseNotes([string]$Body, [string]$Commit, [switch]$Historical) {
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid public source commit.' }
    $repo = 'https://github.com/robotdestroy/Alexandra-Releases'
    $start = '<!-- alexandra-source-start -->'
    $end = '<!-- alexandra-source-end -->'
    $bodyWithoutSection = [regex]::Replace($Body, '(?s)\r?\n*<!-- alexandra-source-start -->.*?<!-- alexandra-source-end -->', '').TrimEnd()
    $links = @(
        $start,
        '## Source code',
        '',
        "- [Browse public source]($repo/tree/$Commit)",
        "- [Download source ZIP]($repo/archive/$Commit.zip)",
        "- [MIT license]($repo/blob/$Commit/LICENSE)"
    )
    if ($Historical) {
        $links += @('', 'This is the audited public source snapshot based on 0.1.24, not an exact source archive for this historical binary. The public source uses Segoe UI instead of the separately supplied preview font. The automatic "Source code" downloads at the bottom of this historical release point to the earlier release-channel-only snapshot; use the links above to inspect the application source. Existing installer and update files have not changed.')
    } else {
        $links += @('', "This release was built from public commit ``$Commit``. The release tag and GitHub's automatic source archives point to that revision. The attached and bundled ``source.json`` identifies the same source. Third-party dependencies retain their own licenses.")
    }
    return $bodyWithoutSection + "`n`n" + (($links + $end) -join "`n") + "`n"
}
