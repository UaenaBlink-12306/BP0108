$ErrorActionPreference = "Stop"

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$policySource = Join-Path $PSScriptRoot "StreamAudioPolicy.cs"
$testSource = Join-Path $PSScriptRoot "StreamAudioPolicyTests.cs"
$outputRoot = Join-Path $workspaceRoot "tmp\stream_audio_policy_tests"
$testExe = Join-Path $outputRoot "StreamAudioPolicyTests.exe"
$cscPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $cscPath)) {
    throw "The .NET Framework C# compiler was not found at $cscPath"
}

if (-not (Test-Path -LiteralPath $outputRoot)) {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

$compileArgs = @(
    "/nologo",
    "/target:exe",
    "/optimize+",
    ("/out:{0}" -f $testExe),
    $policySource,
    $testSource
)

$compileOutput = & $cscPath @compileArgs 2>&1
if ($compileOutput) {
    $compileOutput | Write-Host
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "Stream audio policy test compilation failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

& $testExe
$testExitCode = $LASTEXITCODE
if ($testExitCode -ne 0) {
    Write-Error "Stream audio policy tests failed with exit code $testExitCode."
}

exit $testExitCode
