param([switch]$SkipBuild,[string]$PythonExe='')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$qa=Join-Path $repo 'tmp/stage3-qa'
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if(-not $SkipBuild){
 & (Join-Path $repo 'build.ps1') -LicenseApiBaseUrl https://auth.viewonly.cloud -LicensePublicKeyPath (Join-Path $repo 'build_public_key.xml') -DirectoryTrial
 if($LASTEXITCODE -ne 0){throw 'Production build failed'}
}
New-Item -ItemType Directory -Path $qa -Force | Out-Null
# This QA library uses the SAME production source with an explicit isolated authorization test double.
# It is never copied into dist or the binary ZIP. No production source guard is removed or replaced.
$lib=Join-Path $qa 'Cad100.DirectoryChecking.dll'
$sources=@('Models.cs','RuleEngine.cs','BatchPreview.cs','DwgDirectoryScanner.cs','DwgDirectoryWriter.cs')|ForEach-Object{Join-Path $repo ('src/DirectoryChecking/'+$_)}
& $csc /nologo /codepage:65001 /target:library /reference:System.Web.Extensions.dll /out:$lib $sources (Join-Path $PSScriptRoot 'TestAuthorization.cs')
if($LASTEXITCODE -ne 0){throw 'QA library compilation failed'}
foreach($name in @('WriteTests','ProcessTests','FakeCore')){
 $exe=Join-Path $qa ($name+'.exe');$source=Join-Path $PSScriptRoot ($name+'.cs')
 & $csc /nologo /codepage:65001 /reference:System.Web.Extensions.dll /reference:$lib /out:$exe $source
 if($LASTEXITCODE -ne 0){throw "$name compilation failed"}
}
& (Join-Path $qa 'WriteTests.exe') (Join-Path $repo 'tests/stage2/native_input.json') (Join-Path $qa 'managed')
if($LASTEXITCODE -ne 0){throw 'Managed preflight/audit tests failed'}
& (Join-Path $qa 'ProcessTests.exe') (Join-Path $qa 'FakeCore.exe') (Join-Path $repo 'engine/scripts') (Join-Path $qa ('process_'+[guid]::NewGuid().ToString('N').Substring(0,6)))
if($LASTEXITCODE -ne 0){throw 'Process orchestration tests failed'}
# Result-state UI checks reuse the existing full regression's isolated application.
$uiApp=Join-Path $repo 'tmp/global-info-qa/application/CAD-100.exe'
if(Test-Path -LiteralPath $uiApp){
 $uiQa=Join-Path $qa 'ui';New-Item -ItemType Directory -Path $uiQa -Force|Out-Null
 $productionDll=Join-Path $repo 'dist/CAD-100/Cad100.DirectoryChecking.dll'
 Copy-Item -LiteralPath $productionDll -Destination (Join-Path $uiQa 'Cad100.DirectoryChecking.dll') -Force
 Copy-Item -LiteralPath (Join-Path $repo 'tmp/global-info-qa/application/CadProcessingCore.dll') -Destination (Join-Path $uiQa 'CadProcessingCore.dll') -Force
 $refs=@('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll','System.Xaml.dll')|ForEach-Object{'/reference:'+(Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET/assembly') -Recurse -Filter $_|Select-Object -First 1 -ExpandProperty FullName)}
 $exe=Join-Path $uiQa 'WriterUiTests.exe'
 & $csc /nologo /codepage:65001 /reference:System.Web.Extensions.dll /reference:$productionDll $refs /out:$exe (Join-Path $PSScriptRoot 'WriterUiTests.cs')
 if($LASTEXITCODE -ne 0){throw 'Writer UI tests compilation failed'}
 & $exe $uiApp (Join-Path $repo 'tests/stage2/native_input.json') $uiQa
 if($LASTEXITCODE -ne 0){throw 'Writer UI tests failed'}
}else{Write-Warning 'Run tests/confirmation/run_tests.ps1 first to prepare the isolated WPF UI harness.'}
if(-not $PythonExe){$python=Get-Command python -ErrorAction SilentlyContinue;if($python -and $python.Source -notmatch 'WindowsApps'){$PythonExe=$python.Source}}
if($PythonExe){& $PythonExe (Join-Path $PSScriptRoot 'lisp_whitebox.py');if($LASTEXITCODE -ne 0){throw 'LISP list editing tests failed'}}
else{Write-Warning 'Python 3 unavailable; run tests/stage3/lisp_whitebox.py separately.'}
Write-Host "Stage 3 tests complete: $qa. CAD engine and DWG persistence were NOT validated by these doubles."
