# InterviewPracticeDesktop

Windows 桌面面试练习工具。程序显示名为 Wallpaper Engine，与同名商业软件无关联。

- 点击开始后识别默认扬声器播放的内容，不采集麦克风。
- 本地 Whisper GPU 转写，支持中英文混合内容和临时预览。
- 自动识别问题，通过 DeepSeek 流式生成口语参考答案，上限 700 字。
- 支持透明度调节、窗口置顶和拖动。

## 开始使用

需要 Windows 64 位和 .NET Framework 4.8。取得完整运行包后，完整解压，运行 `Wallpaper Engine.exe` 或 `启动.cmd`，在设置中填写自己的 DeepSeek API Key，再点击“开始”。不要只复制 EXE。

GitHub 的 Code → Download ZIP 仅提供源码，不包含模型、运行库和 EXE，不能直接运行。完整运行包需要单独取得，具体见[使用与分享指南](docs/usage.md)。

## 文档导航

- [使用与分享](docs/usage.md)：启动、识别、设置和如何传给别人。
- [开发与更新](docs/development.md)：依赖准备、测试、构建、Git 工作流和语音模型选择。
- [性能验证](docs/benchmarks.md)：历史样本对比、测试方法与结果局限。
- [回答规则来源](answer-templates/来源说明.md)：模板快照和口语化规则的参考依据。
- [第三方依赖许可](licenses/SOURCES.md)：依赖及其许可证来源。

## 隐私

语音在本地转写。自动回答会将确认后的转写文字及填写的岗位 JD、简历摘要发送给 DeepSeek。API Key 和个人设置保存在本机 `MockInterviewAI-data`，不纳入 Git，也不包含在干净分享包中。每位使用者自行配置密钥。
