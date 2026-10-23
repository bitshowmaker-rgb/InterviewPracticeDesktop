# InterviewPracticeDesktop

Windows 桌面面试练习工具。程序显示名为 Wallpaper Engine，与同名商业软件无关联。

- 点击开始后识别默认扬声器播放的内容，不采集麦克风。
- 本地 Whisper GPU 转写，支持中英文混合内容和临时预览。
- 自动识别问题，通过 DeepSeek 流式生成口语参考答案，上限 700 字。
- 支持透明度调节、窗口置顶和拖动。

## 直接运行与分享

普通使用者需要完整的 Windows 运行包，解压后运行 `Wallpaper Engine.exe` 或 `启动.cmd`，再在设置里填写自己的 DeepSeek API Key。不要只复制 EXE。完整操作见 [分享给别人.md](分享给别人.md)。

GitHub 的 Code → Download ZIP 只包含源码，不包含运行所需模型、运行库和 EXE。完整运行包应单独通过网盘或 GitHub Release 分享。

## 源码构建

需要 Windows 64 位、.NET Framework 4.8，以及 `依赖来源.json` 列出的 NAudio DLL、Whisper 运行库与模型。准备 `lib`、`runtime`、`models` 目录后运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
```

API Key 和本地设置保存在 `MockInterviewAI-data`，该目录不纳入 Git，也不包含在干净分享包中。每位使用者自行配置密钥，自动回答会向 DeepSeek 发送转写文字与其填写的背景资料。

第三方依赖的许可证见 `licenses`，回答规则的参考来源见 [回答与模型升级说明.md](回答与模型升级说明.md)。
