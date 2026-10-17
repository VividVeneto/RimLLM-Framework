#Requires -Version 7.0
<#
以獨立設定啟動 DevTools；不修改玩家的 ModsConfig、設定或存檔。
範例：pwsh -File ./launch-tests.ps1 -Where 'class == StartupTests'
先儲存遊戲並關閉 RimWorld，再執行本腳本。
#>
param(
    [string]$Where = 'namespace == RimLLM_Framework.InGameTests',
    [string]$RimWorldDir = (Join-Path $PSScriptRoot '../../../..'),
    [string[]]$AdditionalMods = @(),
    [string]$Language = 'English',
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 600,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$RimWorldDir = [IO.Path]::GetFullPath($RimWorldDir)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$hostMod = Join-Path $PSScriptRoot 'Mod'
$gameExe = Join-Path $RimWorldDir 'RimWorld.exe'
if (!(Test-Path -LiteralPath $gameExe)) { throw "找不到遊戲：$gameExe" }
if (Get-Process RimWorld -ErrorAction SilentlyContinue) { throw '請先儲存並關閉 RimWorld。' }

$resultRoot = Join-Path $repoRoot 'TestResults/devtools'
[IO.Directory]::CreateDirectory($resultRoot) | Out-Null
# 避免兩次測試同時改寫伴隨模組的日誌設定。
$runLock = [IO.File]::Open((Join-Path $resultRoot 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$process = $null
try {
    if (!$SkipBuild) {
        & dotnet build (Join-Path $repoRoot 'Source/RimLLM Framework/RimLLM Framework.csproj') -c Release "-p:RimWorldDir=$RimWorldDir"
        if ($LASTEXITCODE) { throw 'Release 建置失敗。' }
        & dotnet build (Join-Path $PSScriptRoot 'RimLLM Framework.InGameTests.csproj') "-p:RimWorldDir=$RimWorldDir"
        if ($LASTEXITCODE) { throw 'DevTools 測試組建失敗。' }
    }
    foreach ($dll in 'DevTools.dll', 'RimLLM Framework.InGameTests.dll') {
        if (!(Test-Path -LiteralPath (Join-Path $hostMod "Assemblies/$dll"))) { throw "缺少測試組件：$dll" }
    }

    $link = Join-Path $RimWorldDir 'Mods/RimLLM Framework.InGameTests'
    if (Test-Path -LiteralPath $link) {
        $item = Get-Item -LiteralPath $link
        if ($item.LinkType -ne 'Junction' -or [IO.Path]::GetFullPath($item.Target) -ne [IO.Path]::GetFullPath($hostMod)) {
            throw "測試模組位置已存在且不指向此工作樹：$link"
        }
    } else {
        New-Item -ItemType Junction -Path $link -Value $hostMod | Out-Null
    }

    $runDir = Join-Path $resultRoot (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    [IO.Directory]::CreateDirectory((Join-Path $runDir 'Config')) | Out-Null
    $mods = [xml]'<ModsConfigData><version>1.7</version><activeMods /></ModsConfigData>'
    foreach ($id in @('brrainz.harmony', 'ludeon.rimworld', 'greenmushroom.rimllmframework') + $AdditionalMods + @('greenmushroom.rimllmframework.ingametests')) {
        $node = $mods.CreateElement('li')
        $node.InnerText = $id
        $mods.SelectSingleNode('/ModsConfigData/activeMods').AppendChild($node) | Out-Null
    }
    $mods.Save((Join-Path $runDir 'Config/ModsConfig.xml'))
    $prefs = [xml]'<Prefs><langFolderName>English</langFolderName><devMode>True</devMode></Prefs>'
    $prefs.Prefs.langFolderName = $Language
    $prefs.Save((Join-Path $runDir 'Config/Prefs.xml'))
    $testConfig = [xml]'<TestConfig><log><folder /><verboseLogging>true</verboseLogging><report>DevTools.Testing.MarkdownReport</report></log><failOnWarnings>true</failOnWarnings><failOnErrors>true</failOnErrors><randSeed>12345</randSeed></TestConfig>'
    $testConfig.TestConfig.log.folder = $runDir.Replace('\', '/')
    [IO.Directory]::CreateDirectory((Join-Path $hostMod 'Configs')) | Out-Null
    $testConfig.Save((Join-Path $hostMod 'Configs/UnitTestConfig.xml'))

    $start = [Diagnostics.ProcessStartInfo]::new($gameExe)
    $start.WorkingDirectory = $RimWorldDir
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in @('--pid', 'GreenMushroom.RimLLMFramework.InGameTests', '--test', '--where', $Where, '--exit', '-NoWindow', "-savedatafolder=$runDir")) {
        $start.ArgumentList.Add($arg)
    }
    Write-Host "DevTools filter: $Where"
    Write-Host "Results: $runDir"
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    [IO.File]::WriteAllText((Join-Path $runDir 'stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $runDir 'stderr.log'), $stderr.GetAwaiter().GetResult())
    if ($timedOut) { throw "測試逾時，屬於中斷執行：$runDir" }
    $logPath = Join-Path $runDir 'Test.log'
    if (!(Test-Path -LiteralPath $logPath)) { throw "沒有 Test.log，不算通過：$runDir" }
    $log = Get-Content -LiteralPath $logPath -Raw
    $cases = [regex]::Matches($log, '(?m)^--\t\[(Passed|Failed|Skipped|Canceled|NotRun|Pending)\]')
    foreach ($status in 'Passed', 'Failed', 'Skipped', 'Canceled', 'NotRun', 'Pending') {
        Write-Host "${status}: $(($cases | Where-Object { $_.Groups[1].Value -eq $status }).Count)"
    }
    if (!($cases | Where-Object { $_.Groups[1].Value -in 'Passed', 'Failed' })) {
        throw "零案例執行，不算通過：$logPath"
    }
    if ($process.ExitCode -ne 0 -or $log -notmatch 'Test runner finished\. Result: Passed') {
        throw "測試失敗或未正常結束：$logPath"
    }
} finally {
    if ($process) { $process.Dispose() }
    $runLock.Dispose()
}
