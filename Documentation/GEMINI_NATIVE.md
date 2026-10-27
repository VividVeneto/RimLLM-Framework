# Gemini 原生 API：RimWorld 1.7 / .NET 10

本更新基于 1.7 主分支 `0bc2258882401b90150ede55d703240fc5b0e72a`，将 1.6 分支的原生 Gemini 对话功能移植到 .NET 10。版本标记为 `2026.10.27.2`。

## 与 1.6 补丁的差异

| 项目 | 1.6 分支 | 1.7 主分支 |
|---|---|---|
| 游戏及运行时 | RimWorld 1.6 / .NET Framework 4.7.2 | RimWorld 1.7 / .NET 10 |
| 异步流 | 带别名的 BCL 接口及手写枚举器 | 原生 `IAsyncEnumerable`、异步迭代器和可取消的读取 |
| Gemini 默认模式 | 原生 REST | 保留已有 `/openai` 兼容模式；自定义原生地址时切换协议 |
| 原生功能 | 文本、JSON Schema、JSONL/SSE、模型列表 | 保留这些功能，并支持方法及枚举器两条取消路径 |
| 既有 Gemini 工具及图片 | 最小原生适配器未包含 | `/openai` 模式继续使用原有 SDK；原生文本模式不提供 |
| 1.7 决策、预算、安全存储及用量处理 | 不包含 1.7 后续改动 | 沿用现有 1.7 实现 |

`gemini-native-rimworld-1.6` 分支继续用于 1.6。不要把 1.7 的 DLL 安装到 1.6 游戏中。

## 配置

启用 Harmony 3、RimTalk 与 RimLLM Framework，并将框架排在 RimTalk 后面。进入「模组设置 → RimLLM Framework → 供应商设置 → Google Gemini」。

| 地址形式 | 对话协议及功能 |
|---|---|
| `https://generativelanguage.googleapis.com/v1beta/openai` | 默认的 OpenAI 兼容模式，保留 SDK、工具和图片支持 |
| `https://your-gemini-proxy.example/v1beta` | Gemini 原生 REST：文本、JSON 模式、JSON Schema、增量 SSE |

兼容模式由结尾的 `/openai` 识别；完整的 `/openai/chat/completions` 也会先正规化。其他地址按 Gemini 原生 Base URL 处理。如果服务只提供普通 OpenAI 兼容地址，请使用框架独立的 `OpenAI Compatible` 供应商。

原生中转的设置步骤：

1. 启用 Gemini，填写自己的中转密钥和原生 Base URL（通常到 `/v1beta`）。
2. 获取模型列表，将目标模型加入「模型设置 → 备援链」。
3. 执行连接测试与对话测试。
4. 在「第三方整合」启用「强制 RimTalk 改用 RimLLM」。

框架自动使用 `generateContent` / `streamGenerateContent?alt=sse`，并通过 `x-goog-api-key` 请求头鉴权。无需在地址里附加模型或方法，也无需把连通性测试的 JSON 请求体粘贴到 RimTalk。

原生适配器保留 JSONL 换行、过滤思考文本，支持 `responseJsonSchema`、token 用量、分页模型列表、错误类型和 `Retry-After`。Gemini 3 的关闭思考请求使用兼容的 `low` 等级；Gemini 2.5 Pro 使用最低 128 token 思考预算。原生模式暂不支持图片或工具调用，能力声明会相应关闭工具调用。

该新增原生传输仅用于对话。Embedding 使用独立设置，继续要求其原有的 OpenAI 兼容端点。

## 验证范围

- 使用 `Krafs.Rimworld.Ref 1.7.4982-unstable` 和 Harmony 3.0 完成 .NET 10 Release 构建，0 警告、0 错误。
- `Tests/NativeTests.csproj` 的 17 项独立测试通过，覆盖原生请求、Schema、增量 JSONL、限流等待、取消、断流、分页、首次连接、兼容模式工具、协议切换和错误正文读取。
- 同一 1.7 构建已通过真实中转的模型列表、结构化 JSON、JSONL 流及 `gemini-3.8-flash` 连接测试。
- 原有 NUnit 测试在相同环境中执行 559 项：修改前后均为 393 项通过、166 项失败，逐项比较无新增失败。失败主要源于本机只有 1.7 编译参考程序集，缺少可执行的 1.7 游戏运行程序集；这些参考程序集不能代替实际游戏运行。
- 尚未启动 RimWorld 1.7 验证 Harmony 接管和角色对话。

### 运行独立协议测试

仓库包含预编译 DLL。安装 .NET 10 SDK 后，在仓库根目录执行：

```powershell
dotnet run --project Tests/NativeTests.csproj -c Release
```

测试使用本地 HTTP 服务，不需要真实 API 密钥或游戏进程。可选的 `--live` 模式从标准输入读取密钥，通过 `GEMINI_BASE_URL` / `GEMINI_MODEL` 环境变量指定实际服务；它会产生真实 API 调用。

重新构建框架时使用原项目 `Source/RimLLM Framework/RimLLM Framework.csproj`，按现有构建文档设置 1.7 游戏程序集与 Harmony 路径，并使用 Release 配置。全量游戏相关测试仍需要 1.7 的实际运行程序集。

原项目与依赖的许可证继续适用，见根目录 `LICENSE`。
