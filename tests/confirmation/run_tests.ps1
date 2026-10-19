param([switch]$SkipBuild)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$qa=Join-Path $repo 'tmp/unified-qa'
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$classic=Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
if (-not $SkipBuild) {
    & (Join-Path $repo 'build.ps1') -LicenseApiBaseUrl 'https://auth.viewonly.cloud' -LicensePublicKeyPath (Join-Path $repo 'build_public_key.xml') -DirectoryTrial
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}
New-Item -ItemType Directory -Path $qa -Force | Out-Null
& $classic -STA -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tests/run_global_info_regression.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Batch global/personnel regression failed' }
& $classic -STA -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tests/plain_failure_regression_utf8.ps1') -OutputDirectory (Join-Path $qa 'batch_messages')
if ($LASTEXITCODE -ne 0) { throw 'Batch explanation regression failed' }
$dll=Join-Path $repo 'dist/CAD-100/Cad100.DirectoryChecking.dll'
Copy-Item -LiteralPath $dll -Destination (Join-Path $qa 'Cad100.DirectoryChecking.dll') -Force
foreach ($name in @('Stage2Tests','AdapterTests','RuleTests')) {
    $source=if($name -eq 'RuleTests'){Join-Path $repo 'tests/directory_rules/RuleTests.cs'}else{Join-Path $repo ('tests/stage2/'+$name+'.cs')}
    & $csc /nologo /codepage:65001 /r:System.Web.Extensions.dll "/r:$dll" "/out:$qa/$name.exe" $source
    if ($LASTEXITCODE -ne 0){throw "$name compilation failed"}
}
& (Join-Path $qa 'RuleTests.exe') (Join-Path $repo 'tests/directory_rules/native_sample.json') (Join-Path $qa 'rules')
if ($LASTEXITCODE -ne 0){throw 'Rule regression failed'}
& (Join-Path $qa 'Stage2Tests.exe') (Join-Path $repo 'tests/stage2/native_input.json') (Join-Path $qa 'stage2')
if ($LASTEXITCODE -ne 0){throw 'Stage2 regression failed'}
$entries=@(Get-Content -LiteralPath (Join-Path $repo 'tests/stage2/native_replay/manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json)
foreach($entry in $entries){$entry.tsv=Join-Path $repo ('tests/stage2/native_replay/'+$entry.tsv)}
$entries|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $qa 'manifest.json') -Encoding UTF8
& (Join-Path $qa 'AdapterTests.exe') (Join-Path $qa 'manifest.json') (Join-Path $repo 'tests/stage2/native_expected.json') $qa
if($LASTEXITCODE -ne 0){throw 'Adapter regression failed'}
function Gac([string]$name){return Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET/assembly') -Recurse -Filter ($name+'.dll')|Select-Object -First 1 -ExpandProperty FullName}
$pf=Gac 'PresentationFramework';$pc=Gac 'PresentationCore';$wb=Gac 'WindowsBase';$sx=Gac 'System.Xaml'
& $csc /nologo /codepage:65001 /r:System.Web.Extensions.dll "/r:$pf" "/r:$pc" "/r:$wb" "/r:$sx" "/r:$dll" "/out:$qa/ConfirmationTests.exe" (Join-Path $PSScriptRoot 'ConfirmationTests.cs')
if($LASTEXITCODE -ne 0){throw 'UI compilation failed'}
& (Join-Path $qa 'ConfirmationTests.exe') (Join-Path $repo 'tmp/global-info-qa/application/CAD-100.exe') (Join-Path $repo 'tests/stage2/native_input.json') (Join-Path $qa 'ui')
if($LASTEXITCODE -ne 0){throw 'Unified UI regression failed'}
$node=Get-Command node -ErrorAction SilentlyContinue
if($node){& $node.Source (Join-Path $repo 'tests/title_transform_regression.mjs');if($LASTEXITCODE -ne 0){throw 'Title transform regression failed'}}else{Write-Warning 'Node unavailable: title transform replay skipped; requires Node 20.11 or later.'}
Write-Host "Tests complete. Results: $qa. No live CAD scan or DWG processing executed."
