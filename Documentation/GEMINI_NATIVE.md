# Gemini 原生 API：RimWorld 1.6 分支

此分支基于上游提交 `75212ec22b3af66729b1d6caa48847122c91b798`（RimLLM Framework `2026.10.11.2`），为 RimWorld 1.6 提供一个小范围的 Gemini 原生协议补丁。fork 版本号为 `2026.10.27.1`。它与上游面向 RimWorld 1.7 的 `main` 分开维护。

## 安装及配置

将本分支的模组文件放入 `RimWorld/Mods/RimLLM Framework`。仓库包含预编译的 `Assemblies/RimLLM Framework.dll` 及依赖，无需自行编译。启用 Harmony、RimTalk 和本框架，将框架排列在 RimTalk 后面。

进入「模组设置 → RimLLM Framework → 供应商设置 → Google Gemini」：

1. 启用供应商并填写自己的 API 密钥。
2. 在新增的 API 服务端点输入框填写 Gemini 原生 Base URL，例如 `https://your-gemini-proxy.example/v1beta`。官方默认地址是 `https://generativelanguage.googleapis.com/v1beta`。
3. 获取模型列表，在「模型设置」将目标模型加入备援链。
4. 先执行连接测试和对话测试，再到「第三方整合」开启「强制 RimTalk 改用 RimLLM」。

Base URL 填写到 `/v1beta` 即可，不要附加模型及 `:generateContent` 方法。接管开启后，供应商、模型和密钥以框架设置为准；无需把独立连通性测试的 JSON 请求体粘贴到 RimTalk。

## 协议与范围

- 原生 `generateContent` 和 `streamGenerateContent?alt=sse`。
- `x-goog-api-key` 请求头鉴权；密钥不写入请求 URL。
- 文本对话、系统提示、JSON 模式和 `responseJsonSchema`。
- 逐段读取 SSE，保留 RimTalk JSONL 的换行，并过滤原生思考文本。
- 原生模型列表、分页、上下文上限和 token 用量。
- 复用框架的备援链、第三方接管与记账；保留 HTTP 错误和 `Retry-After` 信息。
- 修复已有流式适配器丢失方法取消令牌的问题。

此最小适配器不包含 Gemini 图片输入或工具调用，能力声明会相应关闭原生工具调用。其他供应商仍使用原有协议。

Gemini 3 无法完全关闭思考；RimTalk 要求关闭思考时使用兼容的 `low` 等级，避免部分中转拒绝 `minimal`。Gemini 2.5 Pro 使用最低 128 token 思考预算，旧版无思考模型不发送该字段。

## 验证

已完成 .NET Framework 4.7.2 Release 构建，以及 `tests/NativeTests.csproj` 的 13 项回归测试，覆盖原生请求、Schema、JSONL 增量流、限流等待、取消、断流、模型分页、首次连接和其他供应商的协议。

同一 DLL 还实际通过 Gemini 中转的模型列表、结构化 JSON、JSONL 流和 `gemini-3.8-flash` 连接测试。游戏进程内的 Harmony 接管及角色对话仍需启动游戏确认。

### 运行无凭据回归测试

在 Windows 上安装 .NET SDK 和 .NET Framework 4.8 Developer Pack，然后从仓库根目录执行：

```powershell
dotnet build .\tests\NativeTests.csproj -c Release
& '.\tests\bin\Release\net48\RimLLM Framework.Tests.exe'
```

这些测试直接运行仓库内的框架 DLL，通过本地 HTTP 服务验证协议行为，不需要外部 API、游戏进程或真实密钥。

如需从源码构建框架，请使用原项目 `Source/RimLLM Framework/RimLLM Framework.csproj`，并按原文档准备依赖。更新出货 DLL 需使用 Release 构建。

### 可选真实接口测试

测试程序的 `--live` 模式从标准输入读取密钥，通过 `GEMINI_BASE_URL` 和 `GEMINI_MODEL` 环境变量选择端点与模型。这会调用实际服务并可能产生费用。不要将真实密钥写入源码、测试夹具或提交的配置文件。

原项目及依赖的许可证继续适用，见根目录 `LICENSE`。
