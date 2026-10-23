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
