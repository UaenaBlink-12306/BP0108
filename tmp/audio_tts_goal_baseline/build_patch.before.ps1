$ErrorActionPreference = "Stop"

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $workspaceRoot "tools"
$gameFolderName = if ($env:BP0108_BUILD_FOLDER) { $env:BP0108_BUILD_FOLDER } else { "BP0108" }
$managedRoot = Join-Path $workspaceRoot "$gameFolderName\BP0108_Data\Managed"
$runtimeSource = Join-Path $toolsRoot "QuestionPatchRuntime.cs"
$matcherSource = Join-Path $toolsRoot "AnswerMatcher.cs"
$runtimeAssembly = Join-Path $managedRoot "CodexRuntimePatch.dll"
$targetAssembly = Join-Path $managedRoot "Assembly-CSharp.dll"
$backupAssembly = Join-Path $managedRoot "Assembly-CSharp.codex-backup.dll"
$cecilPackageRoot = Join-Path $toolsRoot ".vendor\Mono.Cecil"
$cecilNupkg = Join-Path $toolsRoot ".vendor\Mono.Cecil.nupkg"
$cecilZip = Join-Path $toolsRoot ".vendor\Mono.Cecil.zip"
$cecilDll = Join-Path $cecilPackageRoot "lib\net40\Mono.Cecil.dll"
$cscPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

function Ensure-Directory {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

Ensure-Directory (Join-Path $toolsRoot ".vendor")
Ensure-Directory $cecilPackageRoot

if (-not (Test-Path -LiteralPath $cecilDll)) {
    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Mono.Cecil/0.11.5" -OutFile $cecilNupkg
    Copy-Item -LiteralPath $cecilNupkg -Destination $cecilZip -Force
    Expand-Archive -LiteralPath $cecilZip -DestinationPath $cecilPackageRoot -Force
}

$references = @(
    (Join-Path $managedRoot "netstandard.dll"),
    (Join-Path $managedRoot "UnityEngine.CoreModule.dll"),
    (Join-Path $managedRoot "UnityEngine.InputLegacyModule.dll"),
    (Join-Path $managedRoot "UnityEngine.IMGUIModule.dll"),
    (Join-Path $managedRoot "UnityEngine.TextRenderingModule.dll"),
    (Join-Path $managedRoot "UnityEngine.UIModule.dll"),
    (Join-Path $managedRoot "UnityEngine.UI.dll"),
    (Join-Path $managedRoot "UnityEngine.ImageConversionModule.dll"),
    (Join-Path $managedRoot "UnityEngine.ScreenCaptureModule.dll"),
    (Join-Path $managedRoot "Newtonsoft.Json.dll")
)

if (Test-Path -LiteralPath $runtimeAssembly) {
    Remove-Item -LiteralPath $runtimeAssembly -Force
}

$referenceArgs = $references | ForEach-Object { '/reference:"{0}"' -f $_ }
$outArg = '/out:"{0}"' -f $runtimeAssembly
$sourceArg = '"{0}"' -f $runtimeSource
$matcherSourceArg = '"{0}"' -f $matcherSource
$compileArgs = @(
    '/nologo',
    '/target:library',
    '/optimize+',
    $outArg
) + $referenceArgs + @(
    $sourceArg,
    $matcherSourceArg
)

$compileOutput = & $cscPath @compileArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    throw ("Runtime patch compilation failed:`n" + ($compileOutput -join [Environment]::NewLine))
}

if (-not (Test-Path -LiteralPath $backupAssembly)) {
    Copy-Item -LiteralPath $targetAssembly -Destination $backupAssembly -Force
}

[Reflection.Assembly]::LoadFrom($cecilDll) | Out-Null

$readerParameters = New-Object Mono.Cecil.ReaderParameters
$readerParameters.ReadWrite = $false
$readerParameters.InMemory = $true

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($targetAssembly, $readerParameters)
$runtime = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($runtimeAssembly)
$module = $assembly.MainModule
$runtimeMethod = $runtime.MainModule.Types `
    | Where-Object { $_.FullName -eq "CodexRuntimePatch.QuestionPatch" } `
    | ForEach-Object { $_.Methods } `
    | Where-Object { $_.Name -eq "PrepareRoundQuestions" } `
    | Select-Object -First 1

$bootstrapMethod = $runtime.MainModule.Types `
    | Where-Object { $_.FullName -eq "CodexRuntimePatch.QuestionPatch" } `
    | ForEach-Object { $_.Methods } `
    | Where-Object { $_.Name -eq "Bootstrap" } `
    | Select-Object -First 1

$personalAnswerMethod = $runtime.MainModule.Types `
    | Where-Object { $_.FullName -eq "CodexRuntimePatch.QuestionPatch" } `
    | ForEach-Object { $_.Methods } `
    | Where-Object { $_.Name -eq "HandlePersonalAnswerResult" } `
    | Select-Object -First 1

$personalCheckGameOverMethod = $runtime.MainModule.Types `
    | Where-Object { $_.FullName -eq "CodexRuntimePatch.QuestionPatch" } `
    | ForEach-Object { $_.Methods } `
    | Where-Object { $_.Name -eq "HandlePersonalCheckGameOver" } `
    | Select-Object -First 1

$personalLeaderboardMethod = $runtime.MainModule.Types `
    | Where-Object { $_.FullName -eq "CodexRuntimePatch.QuestionPatch" } `
    | ForEach-Object { $_.Methods } `
    | Where-Object { $_.Name -eq "HandlePersonalLeaderboardJson" } `
    | Select-Object -First 1

$livestreamChatMethod = $runtime.MainModule.Types `
    | Where-Object { $_.FullName -eq "CodexRuntimePatch.QuestionPatch" } `
    | ForEach-Object { $_.Methods } `
    | Where-Object { $_.Name -eq "HandleLivestreamChatMessage" } `
    | Select-Object -First 1

$livestreamUserJoinedMethod = $runtime.MainModule.Types `
    | Where-Object { $_.FullName -eq "CodexRuntimePatch.QuestionPatch" } `
    | ForEach-Object { $_.Methods } `
    | Where-Object { $_.Name -eq "HandleLivestreamUserJoined" } `
    | Select-Object -First 1

if (-not $runtimeMethod) {
    throw "PrepareRoundQuestions method not found in CodexRuntimePatch.dll."
}

if (-not $bootstrapMethod) {
    throw "Bootstrap method not found in CodexRuntimePatch.dll."
}

if (-not $personalAnswerMethod) {
    throw "HandlePersonalAnswerResult method not found in CodexRuntimePatch.dll."
}

if (-not $personalCheckGameOverMethod) {
    throw "HandlePersonalCheckGameOver method not found in CodexRuntimePatch.dll."
}

if (-not $personalLeaderboardMethod) {
    throw "HandlePersonalLeaderboardJson method not found in CodexRuntimePatch.dll."
}

if (-not $livestreamChatMethod) {
    throw "HandleLivestreamChatMessage method not found in CodexRuntimePatch.dll."
}

if (-not $livestreamUserJoinedMethod) {
    throw "HandleLivestreamUserJoined method not found in CodexRuntimePatch.dll."
}

$importedRuntimeMethod = $module.ImportReference($runtimeMethod)
$importedBootstrapMethod = $module.ImportReference($bootstrapMethod)
$importedPersonalAnswerMethod = $module.ImportReference($personalAnswerMethod)
$importedPersonalCheckGameOverMethod = $module.ImportReference($personalCheckGameOverMethod)
$importedPersonalLeaderboardMethod = $module.ImportReference($personalLeaderboardMethod)
$importedLivestreamChatMethod = $module.ImportReference($livestreamChatMethod)
$importedLivestreamUserJoinedMethod = $module.ImportReference($livestreamUserJoinedMethod)
$targetType = $module.Types | Where-Object { $_.Name -eq "GameApiDemoUI" } | Select-Object -First 1
if (-not $targetType) {
    throw "GameApiDemoUI type not found."
}

$targetMethod = $targetType.Methods | Where-Object { $_.Name -eq "<OnStartGame>b__33_0" } | Select-Object -First 1
if (-not $targetMethod) {
    throw "GameApiDemoUI.<OnStartGame>b__33_0 was not found."
}

$alreadyPatched = $false
foreach ($instruction in $targetMethod.Body.Instructions) {
    if ($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $instruction.Operand.FullName -like "*CodexRuntimePatch.QuestionPatch::PrepareRoundQuestions*") {
        $alreadyPatched = $true
        break
    }
}

if (-not $alreadyPatched) {
    $processor = $targetMethod.Body.GetILProcessor()
    $callToOnGetQuestions = $targetMethod.Body.Instructions `
        | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $_.Operand.Name -eq "OnGetQuestions" } `
        | Select-Object -First 1

    if (-not $callToOnGetQuestions) {
        throw "Could not find the OnGetQuestions call site to patch."
    }

    $processor.InsertBefore($callToOnGetQuestions, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $importedRuntimeMethod))
}

$awakeMethod = $targetType.Methods | Where-Object { $_.Name -eq "Awake" } | Select-Object -First 1
if (-not $awakeMethod) {
    throw "GameApiDemoUI.Awake was not found."
}

$awakePatched = $false
foreach ($instruction in $awakeMethod.Body.Instructions) {
    if ($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $instruction.Operand.FullName -like "*CodexRuntimePatch.QuestionPatch::Bootstrap*") {
        $awakePatched = $true
        break
    }
}

if (-not $awakePatched) {
    $awakeProcessor = $awakeMethod.Body.GetILProcessor()
    $firstInstruction = $awakeMethod.Body.Instructions | Select-Object -First 1
    $awakeProcessor.InsertBefore($firstInstruction, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $importedBootstrapMethod))
}

# The original build immediately starts a round at the end of Start(). The stream
# lobby owns that action now, so neutralize both the receiver load and call.
$startMethod = $targetType.Methods | Where-Object { $_.Name -eq "Start" } | Select-Object -First 1
if (-not $startMethod) {
    throw "GameApiDemoUI.Start was not found."
}

$automaticStartCall = $startMethod.Body.Instructions `
    | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $_.Operand.Name -eq "OnStartGame" } `
    | Select-Object -Last 1

if ($automaticStartCall) {
    $automaticStartIndex = $startMethod.Body.Instructions.IndexOf($automaticStartCall)
    if ($automaticStartIndex -gt 0) {
        $automaticStartArgument = $startMethod.Body.Instructions[$automaticStartIndex - 1]
        if ($automaticStartArgument.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldarg_0) {
            $automaticStartArgument.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
            $automaticStartArgument.Operand = $null
        }
    }
    $automaticStartCall.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    $automaticStartCall.Operand = $null
}

$chatMessageMethod = $targetType.Methods | Where-Object { $_.Name -eq "HandleChatMessage" } | Select-Object -First 1
if (-not $chatMessageMethod) {
    throw "GameApiDemoUI.HandleChatMessage was not found."
}

$chatAlreadyPatched = $false
foreach ($instruction in $chatMessageMethod.Body.Instructions) {
    if ($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $instruction.Operand.FullName -like "*CodexRuntimePatch.QuestionPatch::HandleLivestreamChatMessage*") {
        $chatAlreadyPatched = $true
        break
    }
}

if (-not $chatAlreadyPatched) {
    $chatProcessor = $chatMessageMethod.Body.GetILProcessor()
    $chatFirstInstruction = $chatMessageMethod.Body.Instructions | Select-Object -First 1
    $chatProcessor.InsertBefore($chatFirstInstruction, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
    $chatProcessor.InsertBefore($chatFirstInstruction, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $importedLivestreamChatMethod))
}

$userJoinedMethod = $targetType.Methods | Where-Object { $_.Name -eq "HandleUserJoined" } | Select-Object -First 1
if (-not $userJoinedMethod) {
    throw "GameApiDemoUI.HandleUserJoined was not found."
}

$userJoinedAlreadyPatched = $false
foreach ($instruction in $userJoinedMethod.Body.Instructions) {
    if ($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $instruction.Operand.FullName -like "*CodexRuntimePatch.QuestionPatch::HandleLivestreamUserJoined*") {
        $userJoinedAlreadyPatched = $true
        break
    }
}

if (-not $userJoinedAlreadyPatched) {
    $userJoinedProcessor = $userJoinedMethod.Body.GetILProcessor()
    $userJoinedFirstInstruction = $userJoinedMethod.Body.Instructions | Select-Object -First 1
    $userJoinedProcessor.InsertBefore($userJoinedFirstInstruction, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
    $userJoinedProcessor.InsertBefore($userJoinedFirstInstruction, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $importedLivestreamUserJoinedMethod))
}

$gameFlowType = $module.Types | Where-Object { $_.Name -eq "GameFlowManager" } | Select-Object -First 1
if (-not $gameFlowType) {
    throw "GameFlowManager type not found."
}

$answerResultMethod = $gameFlowType.Methods | Where-Object { $_.Name -eq "OnAnswerResult" } | Select-Object -First 1
if (-not $answerResultMethod) {
    throw "GameFlowManager.OnAnswerResult was not found."
}

$answerAlreadyPatched = $false
foreach ($instruction in $answerResultMethod.Body.Instructions) {
    if ($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $instruction.Operand.FullName -like "*CodexRuntimePatch.QuestionPatch::HandlePersonalAnswerResult*") {
        $answerAlreadyPatched = $true
        break
    }
}

if (-not $answerAlreadyPatched) {
    $answerResultMethod.Body.Instructions.Clear()
    $answerResultMethod.Body.Variables.Clear()
    $answerResultMethod.Body.ExceptionHandlers.Clear()
    $answerResultMethod.Body.InitLocals = $false
    $answerProcessor = $answerResultMethod.Body.GetILProcessor()
    $answerProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $answerProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
    $answerProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $importedPersonalAnswerMethod))
    $answerProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
}

$leaderboardJsonMethod = $gameFlowType.Methods | Where-Object { $_.Name -eq "UpdateLeaderboardFromJson" } | Select-Object -First 1
if (-not $leaderboardJsonMethod) {
    throw "GameFlowManager.UpdateLeaderboardFromJson was not found."
}

$leaderboardAlreadyPatched = $false
foreach ($instruction in $leaderboardJsonMethod.Body.Instructions) {
    if ($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $instruction.Operand.FullName -like "*CodexRuntimePatch.QuestionPatch::HandlePersonalLeaderboardJson*") {
        $leaderboardAlreadyPatched = $true
        break
    }
}

if (-not $leaderboardAlreadyPatched) {
    $leaderboardJsonMethod.Body.Instructions.Clear()
    $leaderboardJsonMethod.Body.Variables.Clear()
    $leaderboardJsonMethod.Body.ExceptionHandlers.Clear()
    $leaderboardJsonMethod.Body.InitLocals = $false
    $leaderboardProcessor = $leaderboardJsonMethod.Body.GetILProcessor()
    $leaderboardProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $leaderboardProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
    $leaderboardProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $importedPersonalLeaderboardMethod))
    $leaderboardProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
}

$checkGameOverMethod = $gameFlowType.Methods | Where-Object { $_.Name -eq "CheckGameOver" } | Select-Object -First 1
if (-not $checkGameOverMethod) {
    throw "GameFlowManager.CheckGameOver was not found."
}

$checkAlreadyPatched = $false
foreach ($instruction in $checkGameOverMethod.Body.Instructions) {
    if ($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $instruction.Operand.FullName -like "*CodexRuntimePatch.QuestionPatch::HandlePersonalCheckGameOver*") {
        $checkAlreadyPatched = $true
        break
    }
}

if (-not $checkAlreadyPatched) {
    $checkGameOverMethod.Body.Instructions.Clear()
    $checkGameOverMethod.Body.Variables.Clear()
    $checkGameOverMethod.Body.ExceptionHandlers.Clear()
    $checkGameOverMethod.Body.InitLocals = $false
    $checkProcessor = $checkGameOverMethod.Body.GetILProcessor()
    $checkProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $checkProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $importedPersonalCheckGameOverMethod))
    $checkProcessor.Append([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
}

$writerParameters = New-Object Mono.Cecil.WriterParameters
$assembly.Write($targetAssembly, $writerParameters)

Write-Host "Built runtime patch assembly at $runtimeAssembly"
Write-Host "Patched $targetAssembly"
