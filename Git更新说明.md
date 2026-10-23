# Git 更新约定

按照用户要求，今后在这个现有仓库中修改和提交代码，更新同一个 `Wallpaper Engine.exe`，不再生成多个版本的程序和压缩包。保留用户设置和本机依赖。

仓库位置：

```powershell
Set-Location -LiteralPath 'C:\Users\56526\Documents\Codex\2026-10-21\android-studio-mockinterviewai-zip-open-mockinterviewai\outputs\MockInterviewAI-Desktop'
git status --short
git log --oneline -5
```

每次修改前先检查已有改动，避免覆盖。修改后检查差异，退出正在运行的应用，执行适当验证和构建：

```powershell
git diff --check
git diff --stat
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
```

通过验证后，用明确的文件路径暂存本次修改并提交。例如：

```powershell
git add -- source tests 使用说明.md Git更新说明.md
git diff --cached --stat
git commit -m "fix: describe this update"
git status --short
.\启动.cmd
```

新增或删除资源时也用 `git add -- <具体路径>` 或 `git rm -- <具体路径>` 纳入提交。每次更新说明需写明提交号、修改内容和验证结果。不要把 API Key、个人设置、模型、运行库或 EXE 加入 Git。

已上传公开仓库 https://github.com/bitshowmaker-rgb/InterviewPracticeDesktop ，本地 `main` 跟踪 `origin/main`。原有本地提交保存在 `local-history-before-github` 分支，仅作历史回溯。拉取更新使用 `git pull --ff-only`，再验证和构建。修改并提交后用 `git push origin main` 上传；本机需登录具有写权限的 GitHub 账号。代码历史可以用 `git show <提交号>` 查看，需要回退时优先用 `git revert <提交号>` 保留历史。

仅恢复 Git 源码到新电脑时，还需按 `依赖来源.json` 和 `licenses/SOURCES.md` 准备本地依赖。此电脑上的模型与运行库已保留，可直接构建。
