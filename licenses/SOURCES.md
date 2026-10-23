# 第三方组件许可来源

- `NAudio.txt`：NAudio 2.2.1，https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/license.txt
- `whisper.cpp.txt`：whisper.cpp v1.9.2，https://raw.githubusercontent.com/ggml-org/whisper.cpp/v1.9.2/LICENSE
- `whisper-model.txt`：OpenAI Whisper 模型原始 MIT 许可，https://raw.githubusercontent.com/openai/whisper/main/LICENSE 。ggml 转换模型仓库 https://huggingface.co/ggerganov/whisper.cpp 同样标注 MIT。
- `OpenBLAS.txt`：OpenBLAS 0.3.29，https://raw.githubusercontent.com/OpenMathLib/OpenBLAS/v0.3.29/LICENSE
- `SDL2.txt`：SDL 2.28.5，https://raw.githubusercontent.com/libsdl-org/SDL/release-2.28.5/LICENSE.txt
- `CUDA-11.8-EULA.html`：NVIDIA CUDA Toolkit 11.8 许可全文，https://docs.nvidia.com/cuda/archive/11.8.0/eula/index.html
- `cuBLAS-11.11.3.6-LICENSE.txt`：NVIDIA CUDA 11.8 随附 cuBLAS 11.11.3.6 官方重分发包内的原始 LICENSE。

OpenBLAS 与 SDL2 的版本按 whisper.cpp v1.9.2 官方发布工作流确认：
https://github.com/ggml-org/whisper.cpp/blob/v1.9.2/.github/workflows/release.yml

部分 GitHub 原文因网络访问缓慢，通过 jsDelivr 对应固定版本的缓存下载；原始许可证内容保留于上述文件。

语音识别运行包来自 whisper.cpp 官方 v1.9.2 发布文件，通过 SourceForge 镜像下载，并按官方 GitHub API 发布资产的 SHA-256 完整校验：

- CPU：`whisper-blas-bin-x64.zip`，`ffe5b47ca8e53a7677949f23a9c4641bbec4eee8a5714c3d14b67bb8d7b24a78`
- GPU：`whisper-cublas-11.8.0-bin-x64.zip`，`1776668730f5594a0b15f930225779e863dd8280397f9ee7c6e47ccf82bbb203`

GPU 包缺少必需的 cuBLAS 动态链接库，因此另外从 NVIDIA 官方重分发包补齐 `cublas64_11.dll` 和 `cublasLt64_11.dll`。官方版本、大小和校验值见：
https://developer.download.nvidia.com/compute/cuda/redist/redistrib_11.8.0.json

下载包：
https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-11.11.3.6-archive.zip

完整 ZIP 为 420850025 字节，SHA-256 为 `67b0934a6359e4ee26fff823c356021589d392c4fd49ca12624f570edc08e2b9`；校验匹配后提取所需 DLL 和原始许可。

## 回答规则来源

### 用户提供的模板

原始 skill 名称为 `interview-answer-templates`，包含题型路由和 T1–T17 答题骨架。answer-templates/ 保留两份未修改原件的快照：

- [interview-answer-templates.md](../answer-templates/interview-answer-templates.md)：原 `SKILL.md` 快照。
- [templates.md](../answer-templates/templates.md)：原 `references/templates.md` 快照。

两份快照由现有构建脚本分别嵌入为 `AnswerSkill.md`、`AnswerTemplates.md`。当前回答策略优先从代码中配置的本机 `SourceRoot` 读取 `references/templates.md`，不可读时回退到嵌入的 `AnswerTemplates.md`。原 skill 引用的第三方题库与 STAR 范例未复制到本仓库，当前回答策略不自动拉取这些外部文件。

### 口语化规则的参考

比较了以下 GitHub 项目，再独立编写本应用的中文口述规则，没有导入整套模拟面试流程或复制其正文：

- [techbelly/star-interview-prep](https://github.com/techbelly/star-interview-prep/blob/main/star-interview-prep/SKILL.md)：参考行为题中个人行动、结果及按题型使用 STAR 的方法，不要求技术题硬套 STAR。
- [atenreiro/ChatGPT_Interviews](https://github.com/atenreiro/ChatGPT_Interviews)：参考按岗位调整回答、避免生搬硬背的原则。

本应用先回应主问，再用短句、具体例子和自然衔接展开；行为题少讲背景，多讲个人行动和取舍，技术题解释机制和边界。缺失经历继续保留必要的真实事实占位，不编造项目或指标。

### 本应用的输出约束

模板快照中的旧格式仅作来源存档，不等同于当前程序的输出要求。实际策略由 `AnswerTemplatePolicy` 和 `InterviewCore` 实现：只生成一个口语正文版本，通常 450～550 字，硬上限 700 字（含标点），保留流式显示，不输出额外清单、点评或其他版本。原用户 skill 文件及上述两份嵌入快照均未因文档整理而改动。

运行、分享和构建步骤见 [README](../README.md)。
