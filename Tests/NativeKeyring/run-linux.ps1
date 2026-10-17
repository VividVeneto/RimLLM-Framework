#Requires -Version 7.0
# 以 Docker 驗證真正的 Secret Service；來源以外不掛載任何主機路徑，測試結束即刪除容器。
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$stage = Join-Path $repo 'TestResults/native-keyring-build'
[IO.Directory]::CreateDirectory((Join-Path $stage 'KeyStore')) | Out-Null
foreach ($name in 'Dockerfile', '.dockerignore', 'Program.cs', 'GameStubs.cs', 'run.sh', 'Probe.csproj') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $stage $name)
}
foreach ($name in 'EncryptionUtility.cs', 'RimLLMSecureKeyStore.cs', 'LinuxSecretStore.cs', 'MacOSSecretStore.cs') {
    Copy-Item -LiteralPath (Join-Path $repo "Source/RimLLM Framework/Core/Security/$name") -Destination (Join-Path $stage "KeyStore/$name")
}
# 原始專案直接連結原始碼；容器使用這份相同內容的暫存副本。
$project = [xml](Get-Content -LiteralPath (Join-Path $stage 'Probe.csproj') -Raw)
foreach ($node in @($project.SelectNodes('//Compile'))) { $node.ParentNode.RemoveChild($node) | Out-Null }
$project.Save((Join-Path $stage 'Probe.csproj'))
$buildLog = Join-Path $stage 'build.log'
& docker build -t rimllm-keyring-test:local $stage *> $buildLog
if ($LASTEXITCODE) { throw "Linux 測試映像建置失敗，請查看 $buildLog" }
& docker run --rm --network none rimllm-keyring-test:local 2>&1 | Tee-Object -FilePath (Join-Path $stage 'test.log')
if ($LASTEXITCODE) { throw "Linux 鑰匙圈測試失敗，請查看 $stage/test.log" }
