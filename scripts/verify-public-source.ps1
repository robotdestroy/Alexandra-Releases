[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$files = @(git -C $root ls-files)
if ($LASTEXITCODE -ne 0 -or $files.Count -lt 10) { throw 'Stage the reviewed source files before running this check.' }
$problems = [Collections.Generic.List[string]]::new()
foreach ($relative in $files) {
    if ($relative -match '(?i)(^|/)Session Logs/|(^|/)Session-\d{4}-\d{2}-\d{2}-\d{6}-[A-F0-9]+\.md(?:\.tmp)?$') {
        $problems.Add("Personal session journal in public source: $relative")
    }
    if ($relative -match '(?i)(^|/)(bin|obj|outputs|tmp|logs|captures|screenshots|browser-profile)/|(^|/)(settings\.json|appsettings\.json|openai\.key|Journal\.[^/]+\.log|Status\.json|Cargo\.json|NavRoute\.json|memory-[^/]+\.json(?:\.tmp)?|session-[^/]+\.jsonl|\.env(?:\..*)?)$|\.(pdb|wav|pcm|mp3|mp4|ogg|webm|pfx|p12|pem|key|alexandra-access|dmp|nupkg|exe|dll|zip|ttf|otf)$') {
        $problems.Add("Forbidden public-source file: $relative")
    }
    if ($relative.EndsWith('.jsonl') -and -not $relative.StartsWith('tests/Alexandra.Tests/Fixtures/')) {
        $problems.Add("Unreviewed record file outside synthetic fixtures: $relative")
    }
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $problems.Add("Missing tracked file: $relative"); continue }
    $bytes = [IO.File]::ReadAllBytes($path)
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $text = $encoding.GetString($bytes)
        if ($text -match '\b(sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{24,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[A-Z0-9]{16})\b|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') {
            $problems.Add("Possible credential in $relative (value not displayed)")
        }
        if ($text -match '(?i)[A-Z]:[\\/][U]sers[\\/][^\\/\s]+|/[U]sers/[^/\s]+/') {
            $problems.Add("Hard-coded personal home path in $relative")
        }
    }
}
$email = (git -C $root config user.email)
if ($email -notmatch '^[^@\s]+@users\.noreply\.github\.com$') {
    $problems.Add('Configure a GitHub no-reply email in this checkout before committing public source.')
}
if ($problems.Count -gt 0) { throw (($problems | Select-Object -Unique) -join [Environment]::NewLine) }
Write-Host "Public source check passed for $($files.Count) tracked files. Review new content and binary metadata manually as well."
