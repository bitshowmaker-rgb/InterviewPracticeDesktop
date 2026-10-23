# InterviewPracticeDesktop

Windows 桌面面试练习工具。程序显示名为 Wallpaper Engine，与同名商业软件无关联。

- 点击开始后识别默认扬声器播放的内容，不采集麦克风。
- 本地 Whisper GPU 转写，支持中英文混合内容和临时预览。
- 自动识别问题，通过可配置的 OpenAI 兼容 API 流式生成口语参考答案，上限 700 字。
- 支持透明度调节、窗口置顶和拖动。

## 使用与分享

### 安装和启动

使用 Windows 64 位电脑和 .NET Framework 4.8。取得维护者提供的完整自解压包 `WallpaperEngine-5.4.0-Setup.exe` 后，双击即可解压并启动，无需安装 Android Studio 或开发工具。首次解压需要等待，后续双击相同文件会直接启动。首次使用仍需填写自己的 API Key。

程序解压到 `%LOCALAPPDATA%\InterviewPracticeDesktop`，个人设置保存在该目录的 `MockInterviewAI-data`。运行包包含模型和运行库，体积约 1.25 GB。GitHub 的 Code → Download ZIP 仍然只是源码；可运行文件应通过 Releases 附件或维护者分享链接获取。

必须保留程序旁的 `models`、`runtime` 文件夹和 DLL，不能只把 EXE 复制到桌面。桌面入口应使用快捷方式；在应用根目录执行以下命令可创建它：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\Create-DesktopShortcut.ps1
```

### 识别与回答

1. 进入“设置”，填写 API 地址、模型 ID 和对应服务商的 API Key 并保存；根据需要补充岗位 JD、简历摘要和术语。
2. 点击“开始”，持续识别电脑默认扬声器播放的声音，不采集麦克风。启动程序本身不会开始识别；没有播放声音时不会产生转写。
3. 开启“自动识别问题并回答”后，确认的转写内容会自动用于判断问题和生成答案，无需再次点击生成。
4. 点击“停止”结束采集；需要重写当前答案时使用“重新生成当前答案”。生成中可以取消。

有兼容 NVIDIA 显卡和完整 GPU 运行库时优先使用 GPU；不可用时会尝试 CPU，速度可能较慢。持续讲话时，GPU 每约 4 秒提供一次临时转写，停顿后用完整语句校正。临时文字不会提前触发回答，CPU 模式跳过额外预览任务以避免积压。

回答使用自然口语，仅提供一个版本，全文上限 700 字（含标点），通常 450～550 字，简单问题可以更短。收到答案内容就逐步显示，不必等待整篇完成；取消或断流后已出现的片段会标记为未完成。若模型超长，程序在上限内优先保留完整句子，没有合适句末时以省略号结束。

### API 配置

支持 OpenAI 兼容的 Chat Completions 接口。设置中可填写 Base URL（程序自动补上 /chat/completions）或完整接口地址、模型 ID 和对应密钥。旧设置自动沿用现有 DeepSeek 地址和模型。切换地址会清空当前密钥；保存后才替换本机设置。

- DeepSeek：地址可填 https://api.deepseek.com，保留现有 deepseek-flash 模型 ID；是否可用以账号权限为准。
- OpenAI：地址可填 https://api.openai.com/v1，模型 ID 按账号可用的 Chat Completions 模型填写。
- 其他兼容服务或本机服务：按服务商提供的地址、模型填写。本机服务允许 HTTP，其余地址使用 HTTPS。

服务商不支持 response_format 时取消“自动回答使用 JSON 模式”；模型要求新 token 参数时勾选“使用 max_completion_tokens”。两项默认沿用现有行为。DeepSeek 的 thinking 扩展仅对官方主机发送，其他服务不携带该扩展。自动回答仍要求模型返回提示中指定的 JSON，并由程序校验。

这不包含 Claude Messages、Gemini generateContent 或 OpenAI Responses 原生协议；此类模型需要通过兼容网关提供 Chat Completions。模型须支持文本聊天及 SSE 流式响应，具体参数支持以所选服务商为准。协议依据：[OpenAI Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[流式事件](https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events)、[DeepSeek Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/)。

### 窗口与隐私

拖动标题或空白处移动窗口，右上角滑块调整透明度；默认隐藏托盘图标；按 `Ctrl+Alt+I` 可恢复窗口，在设置中取消“隐藏托盘图标”后可使用托盘菜单。快捷键被占用时会保留托盘入口。右上角 × 可退出程序。共享保护状态可在设置中检查，腾讯会议接收端的实际效果仍应通过另一台设备确认。

设置保存在程序旁的 `MockInterviewAI-data`。语音在本地识别，自动回答会向设置中配置的 API 服务发送确认后的转写文字和你填写的背景资料。请保留自己的设置目录，但不要把它随运行包转发。

### 分享给别人

直接使用者：通过网盘等方式发送整个干净运行包，让对方完整解压，再填写对方自己的 API Key。分享包应包含程序、模型、运行库和第三方许可证，排除发送者的密钥、简历、JD、本地设置、Git 仓库及测试记录。后续转发时继续使用干净分享包，不要将自己使用后的整个目录重新压缩发送。

开发者：发送[源码仓库链接](https://github.com/bitshowmaker-rgb/InterviewPracticeDesktop)。仓库不包含模型、运行库、DLL 和 EXE，Code → Download ZIP 得到的源码不能直接运行，需按下方的源码构建说明准备依赖并构建。完整运行包可以通过网盘提供，或另行发布为 GitHub Release 附件。


## 源码构建

以下命令均在仓库根目录执行。沿用现有项目位置和 `source/`、`tests/` 目录，不需要迁移应用或本机设置。

### 依赖准备

需要 Windows 64 位和 .NET Framework 4.8。仓库没有打包模型、DLL 或运行库；从新克隆的源码构建时，先按[依赖清单](依赖来源.json)准备文件，并按清单的 SHA-256 校验下载结果。版本、许可和来源见[第三方组件说明](licenses/SOURCES.md)。

| 位置 | 所需内容 |
| --- | --- |
| `lib/` | NAudio 2.2.1 的 `NAudio.Core.dll`、`NAudio.Wasapi.dll` |
| `models/` | `ggml-large-v3-turbo-q5_0.bin` |
| `runtime/whisper/` | whisper.cpp CPU 发布包及其依赖，包含 `whisper-server.exe` |
| `runtime/whisper-gpu/` | 可选的 CUDA 发布包及依赖，包含 `whisper-server.exe`；cuBLAS DLL 的补充来源见组件说明 |

已有运行环境的电脑可继续使用现存依赖，无需重新下载。构建脚本将 NAudio DLL 从 `lib/` 复制到程序根目录。

### 测试和构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

测试源码保留在 `tests/`，覆盖请求与格式校验、流式回答、取消、语音分段和 700 字上限。测试程序及临时数据写入系统临时目录；默认使用模拟 HTTP、合成 PCM 等测试，不发送真实 API 请求。运行耗时和识别效果的历史记录见[性能验证](docs/benchmarks.md)。

退出正在运行的应用，再构建并启动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
.\启动.cmd
```

构建输出仍为根目录的 `Wallpaper Engine.exe`。图标、XAML 和两份模板快照由脚本按现有路径嵌入；不要仅因模板文件是 Markdown 就删除或重命名。资源说明见[许可与规则来源](licenses/SOURCES.md)。

### 制作可直接运行的 EXE

在模型、运行库和主程序已准备好的仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\Build-PortableLauncher.ps1
```

默认输出到项目上一级目录的 `WallpaperEngine-5.4.0-Setup.exe`。构建采用文件白名单，只打包程序、依赖、模型、说明和许可证，不包含个人设置、密钥、简历、测试记录或 Git 历史。将该 EXE 上传到 GitHub Releases 的附件区或网盘，分享下载链接即可；不要放进 Git 源码目录。此启动包未做代码签名，Windows 可能提示未知发布者。

### 语音模型选择

当前使用 Whisper large-v3-turbo Q5，保留 GPU 加速、完整语句校正和临时预览，兼顾速度与中英文识别准确度。完整 large-v3 与 Turbo 存在速度取舍，不应仅凭模型更大就替换。模型说明参考 [Whisper 官方项目](https://github.com/openai/whisper)。

候选模型需在本机用相同的中英文样本比较错误率、首次文字延迟、最终结果延迟和显存占用，再决定是否切换。本仓库的依赖清单和加载代码仍指向 Turbo Q5，不代表已安装或验证其他权重。

## 其他资料

- [性能验证与局限](docs/benchmarks.md)
- [第三方许可与回答规则来源](licenses/SOURCES.md)
