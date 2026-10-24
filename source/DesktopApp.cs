using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MockInterviewAI
{
    public sealed class DesktopController : IDisposable
    {
        public Window Window { get; private set; }
        public CaptureState Protection { get; private set; }
        private readonly SettingsStore store;
        private readonly TextBox question, answer, job, resume, transcriptBox, vocabulary, apiEndpoint, apiModel, systemPrompt;
        private readonly TextBox visionModel, visionPrompt, normalTokens, visionTokens;
        private readonly PasswordBox key;
        private readonly TextBlock status, protectionTitle, protectionDetail, answerContext, speechStatus;
        private readonly TextBlock transcriptPreview;
        private readonly Button generate, start, stop, cancel, settingsButton, copy, pin, resetPrompt, terminate, screenshot, clearAnswer;
        private readonly System.Windows.Shapes.Path maximizeIcon;
        private readonly Border settingsPanel;
        private readonly Grid interviewPanel;
        private readonly CheckBox remember, topmost, hideTaskbar, hideTray, apiJsonMode, apiCompletionTokens;
        private readonly CheckBox automatic;
        private readonly Slider opacity;
        private readonly TextBlock opacityLabel;
        private readonly Forms.NotifyIcon tray;
        private readonly System.Drawing.Icon trayIcon;
        private readonly AutomaticQuestionBuffer automaticBuffer = new AutomaticQuestionBuffer();
        private readonly DispatcherTimer automaticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private CancellationTokenSource request;
        private SpeakerTranscriptionSession session;
        private string latestTranscript = "";
        private bool closed, busy, stopping, questionEdited, fillingQuestion, automaticRequest, loading = true, paused;
        private HwndSource hotkeySource;
        private bool restoreHotkeyRegistered;
        private const int RestoreHotkeyId = 0x4D49;
        private const int ScreenshotHotkeyId = 0x4D4A;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint keyCode);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private T Control<T>(string name) where T : class { return Window.FindName(name) as T; }

        public DesktopController(string dataDirectory)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(stream);
            store = new SettingsStore(dataDirectory);
            question = Control<TextBox>("QuestionBox"); answer = Control<TextBox>("AnswerBox");
            job = Control<TextBox>("JobBox"); resume = Control<TextBox>("ResumeBox");
            transcriptBox = Control<TextBox>("TranscriptBox"); vocabulary = Control<TextBox>("VocabularyBox");
            systemPrompt = Control<TextBox>("SystemPromptBox");
            transcriptPreview = Control<TextBlock>("TranscriptPreviewText");
            apiEndpoint = Control<TextBox>("ApiEndpointBox"); apiModel = Control<TextBox>("ApiModelBox");
            visionModel = Control<TextBox>("VisionModelBox");
            visionPrompt = Control<TextBox>("VisionPromptBox");
            normalTokens = Control<TextBox>("NormalTokensBox");
            visionTokens = Control<TextBox>("VisionTokensBox");
            apiJsonMode = Control<CheckBox>("ApiJsonModeBox"); apiCompletionTokens = Control<CheckBox>("ApiCompletionTokensBox");
            key = Control<PasswordBox>("ApiKeyBox"); status = Control<TextBlock>("StatusText");
            protectionTitle = Control<TextBlock>("ProtectionTitle"); protectionDetail = Control<TextBlock>("ProtectionDetail");
            answerContext = Control<TextBlock>("AnswerContext");
            generate = Control<Button>("GenerateButton"); start = Control<Button>("StartButton"); stop = Control<Button>("StopButton");
            speechStatus = Control<TextBlock>("SpeechStatus");
            cancel = Control<Button>("CancelButton"); settingsButton = Control<Button>("SettingsButton");
            copy = Control<Button>("CopyButton"); pin = Control<Button>("PinButton");
            resetPrompt = Control<Button>("ResetPromptButton");
            terminate = Control<Button>("TerminateButton"); screenshot = Control<Button>("ScreenshotButton");
            clearAnswer = Control<Button>("ClearAnswerButton");
            maximizeIcon = Control<System.Windows.Shapes.Path>("MaximizeIcon");
            settingsPanel = Control<Border>("SettingsPanel"); interviewPanel = Control<Grid>("InterviewPanel");
            remember = Control<CheckBox>("RememberKeyBox"); topmost = Control<CheckBox>("TopmostBox");
            hideTaskbar = Control<CheckBox>("HideTaskbarBox");
            hideTray = Control<CheckBox>("HideTrayBox");
            automatic = Control<CheckBox>("AutoAnswerBox");
            opacity = Control<Slider>("OpacitySlider"); opacityLabel = Control<TextBlock>("OpacityLabel");

            string loadedKey, notice;
            SavedSettings saved = store.Load(out loadedKey, out notice);
            apiEndpoint.Text = saved.ApiEndpoint ?? InterviewCore.Endpoint;
            apiModel.Text = saved.ApiModel ?? InterviewCore.Model;
            apiJsonMode.IsChecked = saved.ApiJsonMode ?? true; apiCompletionTokens.IsChecked = saved.ApiCompletionTokenLimit;
            key.Password = loadedKey;
            job.Text = saved.JobDescription ?? ""; resume.Text = saved.Resume ?? "";
            vocabulary.Text = saved.Vocabulary ?? "技术面试，API，Java，Python，JavaScript，TypeScript，React，Vue，Spring Boot，MySQL，Redis，Docker，Kubernetes，HTTP，TCP，Git，DeepSeek";
            systemPrompt.Text = String.IsNullOrWhiteSpace(saved.SystemPrompt) ? InterviewCore.DefaultManualSystemPrompt : saved.SystemPrompt;
            visionModel.Text = saved.VisionModel ?? "";
            visionPrompt.Text = saved.VisionPrompt ?? "";
            normalTokens.Text = saved.NormalMaxTokens > 0 ? saved.NormalMaxTokens.ToString() : "2000";
            visionTokens.Text = saved.VisionMaxTokens > 0 ? saved.VisionMaxTokens.ToString() : "1600";
            remember.IsChecked = saved.RememberKey;
            automatic.IsChecked = saved.AutoAnswerEnabled ?? true;
            opacity.Value = saved.OpacityPercent >= 40 && saved.OpacityPercent <= 100 ? saved.OpacityPercent : 86;
            if (saved.WindowWidth >= Window.MinWidth && saved.WindowWidth <= 4000) Window.Width = saved.WindowWidth;
            if (saved.WindowHeight >= Window.MinHeight && saved.WindowHeight <= 4000) Window.Height = saved.WindowHeight;
            topmost.IsChecked = saved.AlwaysOnTop; hideTaskbar.IsChecked = saved.HideTaskbar;
            hideTray.IsChecked = saved.HideTrayIcon ?? true;
            Window.Topmost = saved.AlwaysOnTop; Window.ShowInTaskbar = !saved.HideTaskbar;
            pin.Content = Window.Topmost ? "取消置顶" : "窗口置顶";
            status.Text = String.IsNullOrEmpty(notice) ? "就绪 · 点击开始后，持续识别电脑播放的声音。" : notice;

            using (var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WallpaperEngine.ico"))
                trayIcon = new System.Drawing.Icon(iconStream);
            using (var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WallpaperEngine.ico"))
            {
                var decoder = BitmapDecoder.Create(iconStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Window.Icon = decoder.Frames.OrderBy(frame => Math.Abs(frame.PixelWidth - 32)).First();
            }
            Control<Image>("HeaderIcon").Source = Window.Icon;
            tray = new Forms.NotifyIcon { Text = "Wallpaper Engine", Icon = trayIcon, Visible = false };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示窗口", null, delegate { Window.Dispatcher.BeginInvoke(new Action(Restore)); });
            menu.Items.Add("退出", null, delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { Window.Close(); })); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Window.Dispatcher.BeginInvoke(new Action(Restore)); };

            Window.SourceInitialized += delegate { WindowMovement.Attach(Window); RegisterRestoreHotkey(); ApplyAppearance(); ApplyProtection(); };
            Window.ContentRendered += delegate { ApplyProtection(); };
            Window.Closing += delegate { PersistWindowSize(); Dispose(); };
            settingsButton.Click += delegate { ToggleSettings(); };
            Control<Button>("SaveSettingsButton").Click += delegate { SaveSettings(); };
            Control<Button>("CheckProtectionButton").Click += delegate { ApplyProtection(); };
            resetPrompt.Click += delegate { systemPrompt.Text = InterviewCore.DefaultManualSystemPrompt; systemPrompt.Focus(); };
            pin.Click += delegate { topmost.IsChecked = !(topmost.IsChecked == true); ApplyWindowOptions(); };
            topmost.Click += delegate { ApplyWindowOptions(); };
            hideTaskbar.Click += delegate { ApplyWindowOptions(); };
            hideTray.Click += delegate { ApplyTrayVisibility(); };
            Control<Button>("MinimizeButton").Click += delegate { Window.WindowState = WindowState.Minimized; };
            Control<Button>("MaximizeButton").Click += delegate { ToggleMaximize(); };
            Control<Button>("CloseWindowButton").Click += delegate { Window.Close(); };
            opacity.ValueChanged += delegate { ApplyAppearance(); };
            automatic.Checked += delegate { AutomaticOptionChanged(); };
            automatic.Unchecked += delegate { AutomaticOptionChanged(); };
            automaticTimer.Tick += async delegate {
                if (session != null && !stopping) await ProcessAutomaticAsync(session.HasUnfinishedAudio);
            };
            generate.Click += async delegate { await GenerateAsync(); };
            cancel.Click += delegate { if (request != null) request.Cancel(); };
            start.Click += async delegate { await StartRecognitionAsync(); };
            stop.Click += delegate { TogglePause(); };
            terminate.Click += async delegate { await StopRecognitionAsync(); };
            screenshot.Click += async delegate { await ScreenshotAsync(); };
            clearAnswer.Click += delegate {
                answer.Text = "你的参考答案会显示在这里。";
                answerContext.Text = "已清空参考回答区。";
                copy.IsEnabled = false;
            };
            question.TextChanged += delegate { if (!loading && !fillingQuestion) questionEdited = !String.IsNullOrWhiteSpace(question.Text); };
            Control<Button>("UseTranscriptButton").Click += delegate {
                if (busy) return;
                string selected = transcriptBox.SelectedText;
                FillQuestion(String.IsNullOrWhiteSpace(selected) ? latestTranscript : selected);
                questionEdited = false;
            };
            copy.Click += delegate {
                try { Clipboard.SetText(answer.Text); SetStatus("参考答案已复制。", false); }
                catch { SetStatus("剪贴板暂时不可用，请选中文字后按 Ctrl+C。", true); }
            };
            Window.StateChanged += delegate {
                UpdateMaximizeIcon();
                if (Window.WindowState == WindowState.Minimized && !Window.ShowInTaskbar)
                {
                    Window.Hide();
                }
            };
            apiEndpoint.TextChanged += delegate {
                if (!loading) { key.Clear(); SetStatus("API 地址已改变，请填写对应服务商的 API Key 并保存。", false); }
            };
            loading = false;
            if (String.IsNullOrWhiteSpace(key.Password))
            {
                SetStatus("请在设置填写 API Key；开始后将自动识别问题并回答。", false);
            }
        }

        private string PromptText { get { return InterviewCore.IsDefaultSystemPrompt(systemPrompt.Text) ? null : systemPrompt.Text.Trim(); } }

        private void ToggleMaximize()
        {
            Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void UpdateMaximizeIcon()
        {
            if (maximizeIcon == null) return;
            maximizeIcon.Data = Window.WindowState == WindowState.Maximized
                ? Geometry.Parse("M0,3.5 H7.5 V11 H0 Z M3.5,0 H11 V7.5 H3.5 Z")
                : Geometry.Parse("M0,0 H11 V11 H0 Z");
        }

        private void PersistWindowSize()
        {
            if (closed || loading || Window.WindowState != WindowState.Normal) return;
            store.UpdateWindowSize(Window.Width, Window.Height);
        }

        private void ApplyAppearance()
        {
            opacityLabel.Text = "不透明度 " + Math.Round(opacity.Value) + "%";
            Window.Opacity = opacity.Value / 100.0;
            ApplyProtection();
        }

        private void AutomaticOptionChanged()
        {
            if (loading) return;
            automaticBuffer.Clear();
            if (automatic.IsChecked != true && automaticRequest && request != null) request.Cancel();
            SetStatus(automatic.IsChecked == true ? "自动回答已开启，识别到完整问题后自动生成。" : "自动回答已暂停，连续识别仍可使用。", false);
        }

        public void Restore()
        {
            Window.Show();
            Window.WindowState = WindowState.Normal;
            Window.Activate();
            ApplyProtection();
        }

        private void RegisterRestoreHotkey()
        {
            hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
            hotkeySource.AddHook(RestoreHotkeyHook);
            // MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, I = Ctrl+Alt+I 恢复窗口；S = Ctrl+Alt+S 截屏识图
            restoreHotkeyRegistered = RegisterHotKey(hotkeySource.Handle, RestoreHotkeyId, 0x4003, 0x49);
            RegisterHotKey(hotkeySource.Handle, ScreenshotHotkeyId, 0x4003, 0x53);
            ApplyTrayVisibility();
        }

        private IntPtr RestoreHotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312)
            {
                int id = wParam.ToInt32();
                if (id == RestoreHotkeyId)
                {
                    Restore(); handled = true;
                }
                else if (id == ScreenshotHotkeyId)
                {
                    OnUi(async delegate { await ScreenshotAsync(); });
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void ApplyTrayVisibility()
        {
            if (hideTray.IsChecked == true && !restoreHotkeyRegistered)
            {
                tray.Visible = true;
                SetStatus("Ctrl+Alt+I 快捷键被占用，托盘图标暂时保留，以便恢复窗口。", true);
                return;
            }
            tray.Visible = hideTray.IsChecked != true;
        }

        public void SetStatus(string message, bool error)
        {
            if (closed) return;
            status.Text = message;
            status.Foreground = (Brush)new BrushConverter().ConvertFromString(error ? "#AD3232" : "#61718A");
        }

        public void ApplyProtection()
        {
            IntPtr handle = new WindowInteropHelper(Window).Handle;
            if (handle == IntPtr.Zero) return;
            Protection = CaptureProtection.Apply(handle);
            if (Protection.Applied && Protection.Verified)
            {
                protectionTitle.Text = "共享保护已开启 · Windows 状态已确认";
                protectionTitle.Foreground = (Brush)new BrushConverter().ConvertFromString("#17684E");
                protectionDetail.Text = "腾讯会议接收端效果待验证。共享前，请通过另一台设备确认画面。";
            }
            else
            {
                protectionTitle.Text = "共享保护未生效 · 请勿依赖窗口隐藏";
                protectionTitle.Foreground = (Brush)new BrushConverter().ConvertFromString("#AD3232");
                protectionDetail.Text = "系统没有确认窗口排除状态（错误码 " + Protection.ErrorCode + "）。可在腾讯会议中只共享指定的其他窗口。";
            }
        }

        private void ApplyWindowOptions()
        {
            if (loading) return;
            Window.Topmost = topmost.IsChecked == true;
            Window.ShowInTaskbar = hideTaskbar.IsChecked != true;
            pin.Content = Window.Topmost ? "取消置顶" : "窗口置顶";
            ApplyAppearance();
            ApplyProtection();
        }

        private void ToggleSettings()
        {
            if (busy) return;
            bool show = settingsPanel.Visibility != Visibility.Visible;
            settingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            interviewPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            settingsButton.Content = show ? "返回" : "设置";
        }

        private ApiConfiguration ReadApiConfiguration()
        {
            int normal, vision;
            int.TryParse(normalTokens.Text.Trim(), out normal);
            int.TryParse(visionTokens.Text.Trim(), out vision);
            return new ApiConfiguration(apiEndpoint.Text, apiModel.Text, apiJsonMode.IsChecked == true, apiCompletionTokens.IsChecked == true, normal, vision);
        }

        private void SaveSettings()
        {
            try
            {
                ApiConfiguration api = ReadApiConfiguration();
                store.Save(new SavedSettings {
                    ApiEndpoint = api.EndpointUrl, ApiModel = api.ModelName, VisionModel = visionModel.Text.Trim(), VisionPrompt = visionPrompt.Text.Trim(), ApiJsonMode = api.JsonMode, ApiCompletionTokenLimit = api.CompletionTokenLimit,
                    NormalMaxTokens = api.NormalMaxTokens, VisionMaxTokens = api.VisionMaxTokens,
                    RememberKey = remember.IsChecked == true,
                    JobDescription = job.Text, Resume = resume.Text, Vocabulary = vocabulary.Text,
                    SystemPrompt = PromptText,
                    AlwaysOnTop = topmost.IsChecked == true, HideTaskbar = hideTaskbar.IsChecked == true,
                    HideTrayIcon = hideTray.IsChecked == true,
                    AutoAnswerEnabled = automatic.IsChecked == true, OpacityPercent = opacity.Value,
                    WindowWidth = Window.Width, WindowHeight = Window.Height
                }, key.Password);
                key.Password = key.Password.Trim();
                ApplyWindowOptions();
                ApplyTrayVisibility();
                settingsPanel.Visibility = Visibility.Collapsed;
                interviewPanel.Visibility = Visibility.Visible;
                settingsButton.Content = "设置";
                SetStatus("设置已保存。", false);
            }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, true); }
            catch { SetStatus("设置保存失败，请检查程序文件夹是否可写。", true); }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            generate.IsEnabled = !value;
            question.IsReadOnly = value;
            settingsButton.IsEnabled = !value;
            generate.Content = value ? "正在生成…" : "重新生成当前答案";
            cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task GenerateAsync()
        {
            if (busy) return;
            if (String.IsNullOrWhiteSpace(question.Text)) { SetStatus("请先输入或说出问题。", true); return; }
            if (String.IsNullOrWhiteSpace(key.Password))
            {
                ToggleSettings(); SetStatus("请先填写 API Key。", true); return;
            }
            var current = new CancellationTokenSource();
            request = current;
            string asked = question.Text.Trim();
            string prompt = PromptText;
            SetBusy(true);
            SetStatus("正在生成参考答案…", false);
            try
            {
                string result = await InterviewCore.AskStreamingAsync(key.Password, asked, job.Text, resume.Text,
                    text => UpdateAnswerProgress(current, text, false), current.Token, ReadApiConfiguration(), prompt);
                if (closed || request != current) return;
                answer.Text = result;
                answerContext.Text = "本次问题：" + (asked.Length > 60 ? asked.Substring(0, 60) + "…" : asked);
                copy.IsEnabled = true;
                SetStatus("参考答案已生成。", false);
            }
            catch (OperationCanceledException)
            {
                SetStatus(current.IsCancellationRequested ? "已取消生成。" : "请求超时，请检查网络后重试。", false);
            }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, true); }
            catch { SetStatus("网络请求失败，请检查互联网连接及代理设置后重试。", true); }
            finally
            {
                if (request == current) { MarkIncompleteAnswer(); request = null; if (!closed) SetBusy(false); }
                current.Dispose();
            }
        }

        private void FillQuestion(string text)
        {
            fillingQuestion = true;
            question.Text = (text ?? "").Length > 8000 ? text.Substring(0, 8000) : (text ?? "");
            fillingQuestion = false;
        }

        private void UpdateAnswerProgress(CancellationTokenSource current, string text, bool isAutomatic)
        {
            OnUi(delegate {
                if (closed || request != current || current.IsCancellationRequested || (isAutomatic && automatic.IsChecked != true)) return;
                if (String.IsNullOrWhiteSpace(text)) return;
                answer.Text = text;
                answer.ScrollToEnd();
                answerContext.Text = "正在生成两分钟参考答案…";
                copy.IsEnabled = true;
                SetStatus("正在生成，答案会逐步显示…", false);
            });
        }

        private void MarkIncompleteAnswer()
        {
            if (answerContext.Text == "正在生成两分钟参考答案…") answerContext.Text = "参考答案未完成，可重新生成。";
        }

        private async Task StartRecognitionAsync()
        {
            if (session != null || closed) return;
            var current = new SpeakerTranscriptionSession(AppDomain.CurrentDomain.BaseDirectory, vocabulary.Text);
            session = current;
            automaticBuffer.Clear();
            automaticTimer.Start();
            stopping = false;
            paused = false;
            start.IsEnabled = false;
            stop.IsEnabled = true;
            stop.Content = "⏸ 暂停";
            terminate.IsEnabled = false;
            current.StatusChanged += message => OnUi(delegate { if (session == current) speechStatus.Text = message; });
            current.PreviewReady += text => OnUi(delegate { if (session == current) AcceptPreview(text); });
            current.TranscriptReady += text => OnUi(delegate {
                if (session != current) return;
                AcceptTranscript(text, !stopping && current.IsCapturing);
            });
            current.Failed += error => OnUi(delegate {
                if (session != current) return;
                session = null;
                paused = false;
                AcceptPreview("");
                automaticTimer.Stop(); automaticBuffer.Clear();
                if (automaticRequest && request != null) request.Cancel();
                current.Dispose();
                start.IsEnabled = true; stop.IsEnabled = false; terminate.IsEnabled = false;
                stop.Content = "⏸ 暂停";
                speechStatus.Text = "识别已停止：" + RecognitionError(error);
            });
            try
            {
                await current.StartAsync();
            }
            catch (Exception error)
            {
                if (session == current && !stopping)
                {
                    session = null;
                    paused = false;
                    automaticTimer.Stop(); automaticBuffer.Clear();
                    current.Dispose();
                    start.IsEnabled = true; stop.IsEnabled = false; terminate.IsEnabled = false;
                    stop.Content = "⏸ 暂停";
                    speechStatus.Text = error is OperationCanceledException ? "已取消启动。" : "无法开始识别：" + RecognitionError(error);
                }
            }
        }

        private static string RecognitionError(Exception error)
        {
            return error is InvalidOperationException ? error.Message : "请确认输出设备可用，且模型和运行库完整；然后重新开始。";
        }

        private void AcceptTranscript(string text, bool allowAutomatic)
        {
            AcceptPreview("");
            latestTranscript = text;
            transcriptBox.AppendText((transcriptBox.Text.Length == 0 ? "" : "\n\n") + "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text);
            if (transcriptBox.Text.Length > 100000) transcriptBox.Text = transcriptBox.Text.Substring(transcriptBox.Text.Length - 80000);
            transcriptBox.ScrollToEnd();
            if (allowAutomatic && automatic.IsChecked == true)
            {
                automaticBuffer.Add(text, DateTime.UtcNow);
                if (!busy) SetStatus("正在合并识别文字，停顿后自动判断问题并回答…", false);
            }
            else if (!busy && !questionEdited && !question.IsKeyboardFocusWithin) FillQuestion(text);
        }

        private void AcceptPreview(string text)
        {
            transcriptPreview.Text = String.IsNullOrWhiteSpace(text) ? "" : "临时转写（停顿后校正）：" + text;
        }

        private async Task ProcessAutomaticAsync(bool audioOrAsrBusy)
        {
            if (closed || busy || automatic.IsChecked != true || !automaticBuffer.HasPending) return;
            if (String.IsNullOrWhiteSpace(key.Password))
            {
                SetStatus("自动回答需要 API Key，请到设置填写并保存。", true);
                return;
            }
            string text;
            try { text = automaticBuffer.TakeReady(DateTime.UtcNow, audioOrAsrBusy); }
            catch (InvalidOperationException error) { SetStatus(error.Message, true); return; }
            if (text == null) return;
            var current = new CancellationTokenSource();
            request = current;
            automaticRequest = true;
            string prompt = PromptText;
            SetBusy(true);
            SetStatus("正在自动提取问题并生成参考答案…", false);
            try
            {
                AutomaticAnswer result = await InterviewCore.DetectAndAnswerStreamingAsync(key.Password, text, job.Text, resume.Text,
                    partial => UpdateAnswerProgress(current, partial, true), current.Token, ReadApiConfiguration(), prompt);
                current.Token.ThrowIfCancellationRequested();
                if (closed || request != current || automatic.IsChecked != true) return;
                if (result.IsQuestion)
                {
                    FillQuestion(result.Question);
                    questionEdited = false;
                    answer.Text = InterviewCore.OnlyTwoMinuteAnswer(result.Answer);
                    answerContext.Text = "自动识别问题：" + (result.Question.Length > 70 ? result.Question.Substring(0, 70) + "…" : result.Question);
                    copy.IsEnabled = true;
                    SetStatus("已自动生成答案，继续等待下一个问题。", false);
                }
                else SetStatus("这段未检测到提问，继续识别…", false);
            }
            catch (OperationCanceledException)
            {
                automaticBuffer.AllowRetry(text);
                SetStatus(current.IsCancellationRequested ? "已取消自动回答。" : "自动回答超时，请检查网络。", false);
            }
            catch (Exception error)
            {
                automaticBuffer.AllowRetry(text);
                SetStatus(error is InvalidOperationException ? error.Message : "自动回答网络请求失败，请检查网络。", true);
            }
            finally
            {
                if (request == current)
                {
                    MarkIncompleteAnswer();
                    request = null; automaticRequest = false;
                    if (!closed) SetBusy(false);
                }
                current.Dispose();
            }
        }

        private void TogglePause()
        {
            var current = session;
            if (current == null || stopping || busy) return;
            try
            {
                if (!paused)
                {
                    current.Pause();
                    paused = true;
                    stop.Content = "▶ 继续";
                    terminate.IsEnabled = true;
                    SetStatus("已暂停 · 模型仍驻留内存，点“继续”立即恢复；点“终止”才会卸载模型。", false);
                }
                else
                {
                    current.Resume();
                    paused = false;
                    stop.Content = "⏸ 暂停";
                    terminate.IsEnabled = false;
                    SetStatus("已继续识别。", false);
                }
            }
            catch (Exception error)
            {
                SetStatus("暂停/继续失败：" + RecognitionError(error), true);
            }
        }

        private async Task ScreenshotAsync()
        {
            if (busy) return;
            if (String.IsNullOrWhiteSpace(key.Password)) { ToggleSettings(); SetStatus("请先填写 API Key。", true); return; }
            byte[] png;
            // Stealth capture: instead of Window.Hide() (which flashes the taskbar, drops focus,
            // and is visible in screen recordings), just set Opacity=0. A WPF layer window with
            // Opacity=0 is invisible to GDI CopyFromScreen, stays in the taskbar, and does not
            // fire focus/activation events — so screen-recording proctoring won't see it blink.
            double savedOpacity = Window.Opacity;
            Window.Opacity = 0;
            try
            {
                await Task.Delay(250);
                var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                using (var bmp = new System.Drawing.Bitmap(vs.Width, vs.Height))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(vs.Left, vs.Top, 0, 0, bmp.Size);
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        png = ms.ToArray();
                    }
                }
            }
            finally
            {
                try { Window.Opacity = savedOpacity; } catch { }
            }
            var current = new CancellationTokenSource();
            request = current;
            SetBusy(true);
            SetStatus("正在截取屏幕并识图解答…", false);
            try
            {
                string result = await InterviewCore.AskVisionAsync(key.Password, visionPrompt.Text.Trim(), png, current.Token, ReadApiConfiguration(), visionModel.Text, PromptText);
                if (closed || request != current) return;
                bool fresh = String.IsNullOrEmpty(answer.Text) || answer.Text.StartsWith("你的参考答案");
                answer.Text = fresh ? result : answer.Text + "\n\n【识图解答】\n" + result;
                answerContext.Text = "截屏识图 · " + DateTime.Now.ToString("HH:mm:ss");
                copy.IsEnabled = true;
                SetStatus("识图解答已生成。", false);
            }
            catch (OperationCanceledException)
            {
                SetStatus(current.IsCancellationRequested ? "已取消识图。" : "识图超时，请检查网络。", false);
            }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, true); }
            catch { SetStatus("识图请求失败，请检查网络，或确认模型 ID 支持图片输入。", true); }
            finally
            {
                if (request == current) { request = null; if (!closed) SetBusy(false); }
                current.Dispose();
            }
        }

        private async Task StopRecognitionAsync()
        {
            var current = session;
            if (current == null || stopping) return;
            stopping = true;
            paused = false;
            automaticTimer.Stop(); automaticBuffer.Clear();
            if (automaticRequest && request != null) request.Cancel();
            stop.IsEnabled = false; terminate.IsEnabled = false;
            speechStatus.Text = "正在终止：卸载识别模型并完成剩余转写…";
            try { await current.StopAsync(); }
            catch (Exception error) { if (!closed) speechStatus.Text = "终止识别时发生错误：" + RecognitionError(error); }
            finally
            {
                if (session == current)
                {
                    session = null;
                    AcceptPreview("");
                    current.Dispose();
                    if (!closed)
                    {
                        speechStatus.Text = "已终止 · 模型已卸载，点击“开始”会重新加载。";
                        start.IsEnabled = true; stop.IsEnabled = false; terminate.IsEnabled = false;
                        stop.Content = "⏸ 暂停";
                    }
                }
                stopping = false;
            }
        }

        private void OnUi(Action action)
        {
            if (!closed && !Window.Dispatcher.HasShutdownStarted)
                Window.Dispatcher.BeginInvoke(new Action(delegate { if (!closed) action(); }));
        }

        public void Dispose()
        {
            if (closed) return;
            closed = true;
            if (hotkeySource != null)
            {
                if (restoreHotkeyRegistered) UnregisterHotKey(hotkeySource.Handle, RestoreHotkeyId);
                UnregisterHotKey(hotkeySource.Handle, ScreenshotHotkeyId);
                hotkeySource.RemoveHook(RestoreHotkeyHook);
            }

            automaticTimer.Stop(); automaticBuffer.Clear();
            if (request != null) request.Cancel();
            if (session != null) { session.Dispose(); session = null; }
            tray.Visible = false;
            tray.Dispose();
            trayIcon.Dispose();
        }
    }

    public static class DesktopApp
    {
        [STAThread]
        public static int Main()
        {
            string data = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MockInterviewAI-data");
            try
            {
                var app = new Application();
                var controller = new DesktopController(data);
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                    controller.SetStatus("操作未完成，请重试。", true);
                    e.Handled = true;
                };
                return app.Run(controller.Window);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt"), "启动失败：" + ex.Message, Encoding.UTF8); } catch { }
                return 1;
            }
        }
    }
}
