[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$AllowUnsignedTesterRelease
)
$ErrorActionPreference='Stop'
if (-not $AllowUnsignedTesterRelease) { throw 'This beta is unsigned. Pass -AllowUnsignedTesterRelease explicitly; Windows may display Unknown publisher warnings.' }
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-source.ps1')
$source=Get-ReleaseSource -Root $root -Version $Version
& (Join-Path $PSScriptRoot 'verify-public-source.ps1')
Assert-PublishedReleaseSource $source
$outputRoot=[IO.Path]::GetFullPath((Join-Path $root 'outputs'))
$release=[IO.Path]::GetFullPath((Join-Path $outputRoot "releases\$Version"))
$publish=[IO.Path]::GetFullPath((Join-Path $outputRoot ("publish\$Version-"+[Guid]::NewGuid().ToString('N'))))
foreach($path in @($release,$publish)) {
    if(-not $path.StartsWith($outputRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid output path.' }
}
if(Test-Path -LiteralPath $release) { throw 'This release folder already exists. Use a new version; never overwrite a distributed release.' }
$snapshot=Join-Path $outputRoot ('source\'+[Guid]::NewGuid().ToString('N'))
$archive=$snapshot+'.zip'
New-Item -ItemType Directory -Path (Split-Path -Parent $snapshot) -Force | Out-Null
& git -C $root archive --format=zip --output=$archive $source.Commit
if($LASTEXITCODE -ne 0) { throw 'Could not export the committed public source.' }
Expand-Archive -LiteralPath $archive -DestinationPath $snapshot
function Invoke-ReleaseDotNet([string[]]$Arguments) { & dotnet.exe @Arguments; if($LASTEXITCODE -ne 0) { throw 'A .NET build/test/package command failed.' } }
# Build only tracked files from the verified commit, never local/ignored files.
Push-Location $snapshot
try {
    Invoke-ReleaseDotNet @('restore', '.\Alexandra.sln')
    Invoke-ReleaseDotNet @('build', '.\Alexandra.sln','-c','Release','--no-restore')
    Invoke-ReleaseDotNet @('test','.\Alexandra.sln','-c','Release','--no-build')
    Invoke-ReleaseDotNet @('run','--project','tools\Alexandra.AppearanceProbe','-c','Release')
    Invoke-ReleaseDotNet @('tool','restore')
    New-Item -ItemType Directory -Path $publish,$release -Force | Out-Null
    Invoke-ReleaseDotNet @('publish','src\Alexandra.App\Alexandra.App.csproj','-c','Release','-r','win-x64','--self-contained','true',
        "-p:Version=$Version",'-p:AlexandraUpdateFeedUrl=https://github.com/robotdestroy/Alexandra-Releases',
        '-p:DebugType=None','-p:DebugSymbols=false','-p:PublishSingleFile=false','-o',$publish)
    $source | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'source.json') -Encoding utf8
    Invoke-ReleaseDotNet @('tool','run','vpk','--','pack','--packId','Alexandra','--packVersion',$Version,'--packDir',$publish,
        '--mainExe','Alexandra.App.exe','--packTitle','Alexandra','--packAuthors','Alexandra',
        '--runtime','win-x64','--icon',(Join-Path $snapshot 'src\Alexandra.App\Assets\alexandra-icon.ico'),'--outputDir',$release)
    Copy-Item -LiteralPath (Join-Path $publish 'README.md') -Destination (Join-Path $release 'README.md')
    Copy-Item -LiteralPath (Join-Path $publish 'Alexandra-Read-Me.pdf') -Destination (Join-Path $release 'Alexandra-Read-Me.pdf')
    Copy-Item -LiteralPath (Join-Path $publish 'source.json') -Destination (Join-Path $release 'source.json')
    & (Join-Path $PSScriptRoot 'verify-release.ps1') -Version $Version
    $probe=Join-Path $outputRoot ('update-probe-'+[Guid]::NewGuid().ToString('N'))
    Invoke-ReleaseDotNet @('run','--project','tools\Alexandra.ReleaseProbe','-c','Release','--',$release,'0.0.0',$Version,$probe)
    Write-Host "Installer: $release\Alexandra-win-Setup.exe"
    Write-Host "Public source: $($source.Repository)/tree/$($source.Commit)"
    Write-Host 'No OpenAI keys, user settings or journal data are included.'
} finally { Pop-Location }
