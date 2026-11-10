[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$Publish
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$repo='robotdestroy/Alexandra-Releases'
$release=Join-Path $root "outputs\releases\$Version"
& (Join-Path $PSScriptRoot 'verify-release.ps1') -Version $Version
$gh=Get-Command gh -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if(-not $gh -and (Test-Path 'C:\Program Files\GitHub CLI\gh.exe')) { $gh='C:\Program Files\GitHub CLI\gh.exe' }
if(-not $gh) { throw 'Install GitHub CLI and sign in on the publishing PC. Never put a publishing token in the app.' }
$repository=& $gh repo view $repo --json isPrivate,isEmpty | ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or $repository.isPrivate -ne $false) { throw 'The approved public release repository is not available.' }
if($repository.isEmpty) {
    # GitHub's contents API otherwise uses the account's default author email.
    throw 'Initialize the repository with a reviewed source commit and a GitHub no-reply email before publishing releases.'
}
$existing=& $gh release view "v$Version" --repo $repo --json tagName,isDraft 2>$null
$resumeDraft=$LASTEXITCODE -eq 0
if($resumeDraft -and -not($existing | ConvertFrom-Json).isDraft) { throw 'This release is already public. Publish a new version rather than overwriting it.' }
$files=@(
    (Join-Path $release 'Alexandra-win-Setup.exe'),
    (Join-Path $release "Alexandra-$Version-full.nupkg"),
    (Join-Path $release 'releases.win.json'),
    (Join-Path $release 'RELEASES'),
    (Join-Path $release 'README.md'),
    (Join-Path $release 'Alexandra-Read-Me.pdf')
) | Where-Object { Test-Path -LiteralPath $_ }
$notes="Alexandra $Version - Windows x64 friends beta. Download Alexandra-win-Setup.exe. Self-contained: no separate .NET install. Read Me included and attached. One-time Setup: enter a separately supplied API key, select voice input, and review cloud consent. No API keys or personal data are bundled. Updates download automatically and install next launch. Unsigned test build: Windows may display Unknown publisher warnings."
$notesPath=Join-Path $root "docs\releases\$Version.md"
$notesArgs=if(Test-Path -LiteralPath $notesPath) { @('--notes-file',$notesPath) } else { @('--notes',$notes) }
if(-not $resumeDraft) {
    & $gh release create "v$Version" @files --repo $repo --title "Alexandra $Version" @notesArgs --draft
    if($LASTEXITCODE -ne 0) { throw 'Draft release upload failed.' }
}
$remote=& $gh release view "v$Version" --repo $repo --json assets | ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or @($remote.assets).Count -ne $files.Count) { throw 'Unexpected draft assets. Release remains a draft.' }
foreach($file in $files) {
    $local=Get-Item -LiteralPath $file
    $remoteAsset=@($remote.assets | Where-Object name -eq $local.Name)
    if($remoteAsset.Count -ne 1 -or $remoteAsset[0].size -ne $local.Length) { throw 'Remote asset verification failed; release remains a draft.' }
    if($remoteAsset[0].digest -and $remoteAsset[0].digest -ne ('sha256:'+(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant())) { throw 'Remote asset digest mismatch; release remains a draft.' }
}
if($Publish) {
    & $gh release edit "v$Version" --repo $repo --draft=false --latest
    if($LASTEXITCODE -ne 0) { throw 'Could not publish verified draft.' }
    Write-Host 'Published: https://github.com/robotdestroy/Alexandra-Releases/releases/latest'
} else { Write-Host "Verified draft uploaded. Review it, then publish v$Version in GitHub." }
