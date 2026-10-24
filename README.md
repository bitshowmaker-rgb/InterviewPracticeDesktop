# InterviewPracticeDesktop

Windows 桌面面试练习工具。程序显示名为 **Wallpaper Engine**，与同名商业软件无关联。

- 只采集**默认扬声器播放的声音**（WASAPI 回路），不采集麦克风。
- 本地 Whisper（whisper.cpp，large-v3-turbo **F16 全精度**）离线转写，固定中文识别，支持 NVIDIA GPU 加速。
- 自动识别问题，通过 OpenAI 兼容接口（Chat Completions，SSE 流式）生成口语化参考答案，全文上限 700 字（约 2 分钟口述）。
- **暂停/继续**不卸载模型（点继续立即恢复），只有「终止」才真正卸载模型。
- **一键截屏识图**：把屏幕上的题目/代码/白板截下来，交给多模态模型读图并直接给出参考答案。
- 转写文本可直接编辑，再点「使用选中 / 最新文字」生成答案。
- 窗口可拖动、可任意缩放、可置顶、可调透明度，尺寸自动记忆。
- 系统提示词可在设置中完全自定义。

## 目录结构

```
InterviewPracticeDesktop-main/
├─ Wallpaper Engine.exe      # 主程序（构建产物）
├─ 启动.cmd                   # 双击启动
├─ source/                    # C# / WPF 源码与构建脚本
│  ├─ build.ps1               # 一键编译（csc，无需 Visual Studio）
│  ├─ Create-DesktopShortcut.ps1
│  └─ *.cs / MainWindow.xaml / app.manifest
├─ assets/                    # 图标
├─ answer-templates/          # 内置答题模板（编译时嵌入 exe）
├─ lib/                       # NAudio 2.2.1 两个 DLL（构建依赖）
├─ models/                    # Whisper 模型（已随交付部署）
├─ runtime/
│  ├─ whisper/                # CPU 运行库（whisper.cpp v1.9.2，已部署）
│  └─ whisper-gpu/            # GPU（CUDA 11.8 + cuBLAS）运行库（已部署）
└─ MockInterviewAI-data/      # 首次运行后生成：设置、加密保存的 API Key
```

> 不能只把 exe 复制到桌面：必须保留同级的 `models/`、`runtime/` 和 DLL。需要桌面入口请运行
> `powershell -NoProfile -ExecutionPolicy Bypass -File .\source\Create-DesktopShortcut.ps1` 创建快捷方式。

## 运行环境

- Windows 10/11 64 位，已安装 **.NET Framework 4.8**（Win10/11 一般自带）。
- 麦克风不需要；需要能正常播放声音的扬声器/耳机输出设备。
- GPU 加速为可选项：有 NVIDIA 显卡（RTX 40 系等，驱动支持 CUDA 11.8）时程序自动使用 `runtime/whisper-gpu/`；检测失败或不存在则自动回退 CPU。

## 使用步骤

1. 双击 `Wallpaper Engine.exe`（或 `启动.cmd`）。
2. 进入「设置」，填写 API 地址、模型 ID 和 API Key，保存：
   - DeepSeek：`https://api.deepseek.com`
   - OpenAI：`https://api.openai.com/v1`
   - 其他兼容服务或本机服务：按服务商提供的地址填写（本机服务允许 HTTP，其余要求 HTTPS）。
   - 服务商不支持 `response_format` 时取消「自动回答使用 JSON 模式」；模型要求新参数时勾选「使用 max_completion_tokens」。
3. 回到主界面点「开始」，播放面试音频即可持续转写；勾选「自动识别问题并回答」后，停顿即自动生成答案。
4. **暂停 / 继续**：暂停只是停止采集，模型仍驻留内存，点「继续」几乎立即恢复；只有点「终止」才会卸载模型（下次开始重新加载）。
5. **截屏识图**：点右侧「📷 截屏识图」（或全局快捷键 **Ctrl+Alt+S**），程序会先把自己隐藏，截取整个屏幕（含题目/代码/白板），再让多模态模型读图并把答案追加到参考回答区。设置里可单独填「识图模型 ID」（如 qwen-vl-max、gpt-4o），留空则使用主模型；「识图提示词」可自定义发给视觉模型的指令，留空用默认。
6. **一键清空**：答案区右侧「清空」按钮一键清掉当前参考答案（不影响识别与转写）。
7. 转写框里的文字可以直接改，改完点「使用选中 / 最新文字」把修正后的题目送进答案生成。生成中可取消，答案区可一键复制。

## 窗口缩放与提示词自定义

- **缩放窗口**：拖动窗口四周边缘，或拖动右下角的缩放手柄；标题栏新增最大化/还原按钮（双击标题栏同样可最大化/还原）；关闭时窗口尺寸会被记住。
- **自定义系统提示词**：在「设置 → 系统提示词」中直接改写，最长 8000 字，保存后对新生成的回答生效。
  - 留空或保持默认文本时，使用内置「模拟面试教练 + 题型模板」。
  - 自动识别回答需要解析固定 JSON 结构，程序会在你的提示词后**强制追加 JSON 输出格式要求**，不影响你写的主体指令。
  - 点「恢复默认提示词」可随时还原。

## 语音识别模型与运行库

**本交付已部署并实测通过**（CPU 与 GPU 两条链路均已完成中文识别验证：GPU 启动日志确认 F16 模型加载到 RTX 4060 Laptop 的 CUDA0，约 1.5 GB；固定 `-l zh` 避免背景音乐下误判语言）。若需重新下载或迁移到别的机器，按下表准备（文件名与位置固定，zip 内均为 `Release/` 目录，解压时把其内容放到目标目录）：

| 位置 | 内容 | 来源 |
| --- | --- | --- |
| `models/ggml-large-v3-turbo.bin` | Whisper large-v3-turbo **F16 全精度**模型（约 1.5 GB；比 Q5 量化版中文准确率更高） | huggingface.co/ggerganov/whisper.cpp；国内镜像 `modelscope.cn/models/shanmengqi/ggml-large-v3-turbo` |
| `runtime/whisper/` | whisper.cpp v1.9.2 `whisper-blas-bin-x64.zip` 的 `Release/` 内容（约 21 MB） | github.com/ggml-org/whisper.cpp/releases/tag/v1.9.2（国内可在 URL 前加 `https://gh-proxy.com/` 代理） |
| `runtime/whisper-gpu/` | `whisper-cublas-11.8.0-bin-x64.zip` 的 `Release/` 内容（约 270 MB） | 同上 |
| `runtime/whisper-gpu/` | 额外放入 cuBLAS 11.11.3.6 的 `cublas64_11.dll`、`cublasLt64_11.dll` | NVIDIA CUDA 官方重分发包 `libcublas-windows-x86_64-11.11.3.6-archive.zip`（developer.download.nvidia.com，约 420 MB），取 `bin/` 下两个 DLL |
| `lib/` | `NAudio.Core.dll`、`NAudio.Wasapi.dll`（2.2.1） | nuget.org 对应 nupkg 内 `lib/netstandard2.0/` |

下载后请核对 SHA-256（本次部署文件均已逐一校验通过）：

- 模型（F16）：`1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69`
- CPU 包：`ffe5b47ca8e53a7677949f23a9c4641bbec4eee8a5714c3d14b67bb8d7b24a78`
- GPU 包：`1776668730f5594a0b15f930225779e863dd8280397f9ee7c6e47ccf82bbb203`
- cuBLAS 包：`67b0934a6359e4ee26fff823c356021589d392c4fd49ca12624f570edc08e2b9`
- NAudio.Core 2.2.1：`794645dbfd30e4880663d52d9d9224d55c301fa54228ff623c95d572c7887347`
- NAudio.Wasapi 2.2.1：`c8396b8dbab86a2619edfd9158beaf18c21cb86677099b5594950fd8651b9d1a`

## 源码构建

需要 Windows 64 位、.NET Framework 4.8（使用系统自带的 `csc.exe`，不需要安装 Visual Studio）。先按上表放好 `lib/` 依赖，然后在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
```

输出为根目录的 `Wallpaper Engine.exe`；图标、XAML、两份答题模板会嵌入 exe。

## 隐私说明

- 语音始终在本机识别，音频不会离开本机。
- 只有在开启自动回答（或手动生成）时，确认后的转写文字、JD、简历摘要和提示词才会发送到你自己配置的 API。
- API Key 通过 Windows DPAPI 加密后保存在 `MockInterviewAI-data/`，只能由本机当前用户解密；分享整个文件夹前请删除该目录。

## 开源许可

- [whisper.cpp](https://github.com/ggml-org/whisper.cpp)：MIT（v1.9.2）；Whisper 模型：MIT（OpenAI / ggerganov）。
- [NAudio](https://github.com/naudio/NAudio)：MIT（2.2.1）。
- OpenBLAS 0.3.29：BSD-3-Clause；SDL2 2.28.5：zlib；均随 whisper.cpp 官方发布包分发。
- CUDA / cuBLAS：© NVIDIA，遵循 [CUDA 11.8 EULA](https://docs.nvidia.com/cuda/archive/11.8.0/eula/index.html)。
