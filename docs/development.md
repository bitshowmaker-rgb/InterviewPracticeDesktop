# 开发与更新

以下命令均在仓库根目录执行。沿用现有项目位置和 `source/`、`tests/` 目录，不需要迁移应用或本机设置。

## 依赖准备

需要 Windows 64 位和 .NET Framework 4.8。仓库没有打包模型、DLL 或运行库；从新克隆的源码构建时，先按[依赖清单](../依赖来源.json)准备文件，并按清单的 SHA-256 校验下载结果。版本、许可和来源见[第三方组件说明](../licenses/SOURCES.md)。

| 位置 | 所需内容 |
| --- | --- |
| `lib/` | NAudio 2.2.1 的 `NAudio.Core.dll`、`NAudio.Wasapi.dll` |
| `models/` | `ggml-large-v3-turbo-q5_0.bin` |
| `runtime/whisper/` | whisper.cpp CPU 发布包及其依赖，包含 `whisper-server.exe` |
| `runtime/whisper-gpu/` | 可选的 CUDA 发布包及依赖，包含 `whisper-server.exe`；cuBLAS DLL 的补充来源见组件说明 |

已有运行环境的电脑可继续使用现存依赖，无需重新下载。构建脚本将 NAudio DLL 从 `lib/` 复制到程序根目录。

## 测试和构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

测试源码保留在 `tests/`，覆盖请求与格式校验、流式回答、取消、语音分段和 700 字上限。测试程序及临时数据写入系统临时目录；默认使用模拟 HTTP、合成 PCM 等测试，不发送真实 DeepSeek 请求。运行耗时和识别效果的历史记录见[性能验证](benchmarks.md)。

退出正在运行的应用，再构建并启动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
.\启动.cmd
```

构建输出仍为根目录的 `Wallpaper Engine.exe`。图标、XAML 和两份模板快照由脚本按现有路径嵌入；不要仅因模板文件是 Markdown 就删除或重命名。资源说明见[回答规则来源](../answer-templates/来源说明.md)。运行和分享步骤见[使用指南](usage.md)。

## Git 工作流

修改前确认分支和已有改动，避免覆盖其他工作：

```powershell
git rev-parse --show-toplevel
git branch --show-current
git status --short
git log --oneline -5
```

在工作区干净时，用 `git pull --ff-only` 拉取更新，再修改和验证。只有文档变化时检查链接、资源路径和 Git 差异即可；代码行为变化时执行相关测试，构建前退出应用。

```powershell
git diff --check
git diff --stat
git diff
```

使用具体路径暂存本次修改，不一并暂存无关改动。例如本次只改 README 和文档目录时：

```powershell
git add -- README.md docs
git diff --cached --stat
git diff --cached
git commit -m "docs: clarify usage and development instructions"
git status --short
```

迁移、删除或新增其他文件时，分别检查并暂存对应的具体路径。Git 身份缺失时先由维护者配置自己的身份，再提交。每个验证通过的里程碑创建本地提交，并记录改动、验证结果和提交号。

本地 `main` 跟踪[公开仓库](https://github.com/bitshowmaker-rgb/InterviewPracticeDesktop)的 `origin/main`，迁移前的本地历史保留在 `local-history-before-github` 分支。远程推送需要明确授权；获得授权并登录有写权限的账号后执行 `git push origin main`。查看历史用 `git show <提交号>`，需要撤销已提交修改时优先用 `git revert <提交号>` 保留历史。

不要提交 API Key、个人设置、模型、运行库、EXE、ZIP、缓存或临时测试产物；这些本机文件继续由现有 `.gitignore` 排除。普通代码更新替换同一 EXE，保留 `MockInterviewAI-data` 和依赖，不累积多个版本的运行包。

## 语音模型选择

当前使用 Whisper large-v3-turbo Q5，保留 GPU 加速、完整语句校正和临时预览，兼顾速度与中英文识别准确度。完整 large-v3 与 Turbo 存在速度取舍，不应仅凭模型更大就替换。模型说明参考 [Whisper 官方项目](https://github.com/openai/whisper)。

候选模型需在本机用相同的中英文样本比较错误率、首次文字延迟、最终结果延迟和显存占用，再决定是否切换。本仓库的依赖清单和加载代码仍指向 Turbo Q5，不代表已安装或验证其他权重。
