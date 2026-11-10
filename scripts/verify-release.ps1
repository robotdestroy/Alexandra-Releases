[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$release=Join-Path $root "outputs\releases\$Version"
$setup=Join-Path $release 'Alexandra-win-Setup.exe'
$package=Join-Path $release "Alexandra-$Version-full.nupkg"
$manifest=Join-Path $release 'releases.win.json'
$guide=Join-Path $release 'Alexandra-Read-Me.pdf'
$readme=Join-Path $release 'README.md'
foreach($path in @($setup,$package,$manifest,$guide,$readme)) { if(-not(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release artifact: $path" } }
if((Get-Item -LiteralPath $setup).VersionInfo.ProductVersion -ne $Version) { throw 'Installer version does not match.' }
$assets=(Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).Assets
$asset=@($assets | Where-Object { $_.PackageId -eq 'Alexandra' -and $_.Version -eq $Version -and $_.Type -eq 'Full' })
if($asset.Count -ne 1 -or $asset[0].FileName -ne "Alexandra-$Version-full.nupkg") { throw 'Unexpected update manifest.' }
if($asset[0].SHA256 -ne (Get-FileHash -LiteralPath $package).Hash) { throw 'Update package checksum does not match.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($package)
try {
    $names=@($zip.Entries | ForEach-Object FullName)
    foreach($name in $names) {
        if($name -match '(?i)(^|/)memory-[^/]+\.json(\.tmp)?$') { throw 'Private companion memory found in package.' }
        if($name -match '(?i)(^|/)(settings\.json|appsettings\.json|openai\.key|gateway-access\.dat|Journal\.[^/]+\.log|Status\.json|Cargo\.json|NavRoute\.json|session-[^/]+\.jsonl)$|\.(pdb|wav|pcm|pfx|p12|alexandra-access)$') {
            throw "Personal/development file found in package: $name"
        }
    }
    foreach($required in @('Alexandra.App.exe','Alexandra.App.dll','Alexandra.Core.dll','Alexandra.Infrastructure.dll',
        'Velopack.dll','coreclr.dll','PresentationFramework.dll','.playwright/node/win32_x64/node.exe','.playwright/package/package.json',
        'README.md','Alexandra-Read-Me.pdf')) {
        if(-not($names | Where-Object { $_.EndsWith('/'+$required,[StringComparison]::OrdinalIgnoreCase) })) { throw "Missing runtime payload: $required" }
    }
    # Downloadable guides must be exactly the same files included with the installed app.
    foreach($document in @($readme,$guide)) {
        $name=[IO.Path]::GetFileName($document)
        $entry=@($zip.Entries | Where-Object { $_.FullName -eq "lib/app/$name" })
        if($entry.Count -ne 1) { throw "Unexpected bundled guide path: $name" }
        $stream=$entry[0].Open(); $sha=[Security.Cryptography.SHA256]::Create()
        try { $hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
        finally { $stream.Dispose(); $sha.Dispose() }
        if($hash -ne (Get-FileHash -LiteralPath $document).Hash) { throw "Bundled/downloadable guide mismatch: $name" }
    }
    # Scan executable/text resources for recognizable real key/token formats, including UTF-16 strings.
    foreach($entry in $zip.Entries) {
        if($entry.FullName -notmatch '(?i)\.(dll|exe|json|config|txt|md|js|mjs|ps1)$') { continue }
        $stream=$entry.Open(); $memory=[IO.MemoryStream]::new()
        try { $stream.CopyTo($memory); $bytes=$memory.ToArray() } finally { $stream.Dispose(); $memory.Dispose() }
        foreach($encoding in @([Text.Encoding]::UTF8,[Text.Encoding]::Unicode)) {
            if($encoding.GetString($bytes) -match '\b(sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{24,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})') {
                throw "Possible credential in $($entry.FullName). Refusing release; secret value is not displayed."
            }
        }
    }
} finally { $zip.Dispose() }
Write-Host "Verified Alexandra ${Version}: self-contained, expected payloads, matching update checksum, no detected credentials/personal files."
Write-Host "Installer SHA256: $((Get-FileHash -LiteralPath $setup).Hash)"
