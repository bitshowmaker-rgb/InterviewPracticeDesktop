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
        public string LastRecognitionBackend { get; private set; }
        public string LastOutputDevice { get; private set; }
        private readonly SettingsStore store;
        private readonly TextBox question, answer, job, resume, transcriptBox, vocabulary, apiEndpoint, apiModel;
        private readonly PasswordBox key;
        private readonly TextBlock status, protectionTitle, protectionDetail, answerContext, speechStatus;
        private readonly TextBlock transcriptPreview;
        private readonly Button generate, start, stop, cancel, settingsButton, copy, pin;
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
        private Func<string, string, string, string, Action<string>, CancellationToken, ApiConfiguration, Task<AutomaticAnswer>> automaticResponder = InterviewCore.DetectAndAnswerStreamingAsync;
        private CancellationTokenSource request;
        private SpeakerTranscriptionSession session;
        private string latestTranscript = "";
        private bool closed, busy, stopping, questionEdited, fillingQuestion, automaticRequest, loading = true;
        private HwndSource hotkeySource;
        private bool restoreHotkeyRegistered;
        private const int RestoreHotkeyId = 0x4D49;
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
            transcriptPreview = Control<TextBlock>("TranscriptPreviewText");
            apiEndpoint = Control<TextBox>("ApiEndpointBox"); apiModel = Control<TextBox>("ApiModelBox");
            apiJsonMode = Control<CheckBox>("ApiJsonModeBox"); apiCompletionTokens = Control<CheckBox>("ApiCompletionTokensBox");
            key = Control<PasswordBox>("ApiKeyBox"); status = Control<TextBlock>("StatusText");
            protectionTitle = Control<TextBlock>("ProtectionTitle"); protectionDetail = Control<TextBlock>("ProtectionDetail");
            answerContext = Control<TextBlock>("AnswerContext");
            generate = Control<Button>("GenerateButton"); start = Control<Button>("StartButton"); stop = Control<Button>("StopButton");
            speechStatus = Control<TextBlock>("SpeechStatus");
            cancel = Control<Button>("CancelButton"); settingsButton = Control<Button>("SettingsButton");
            copy = Control<Button>("CopyButton"); pin = Control<Button>("PinButton");
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
            remember.IsChecked = saved.RememberKey;
            automatic.IsChecked = saved.AutoAnswerEnabled ?? true;
            opacity.Value = saved.OpacityPercent >= 40 && saved.OpacityPercent <= 100 ? saved.OpacityPercent : 86;
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
            Window.Closing += delegate { Dispose(); };
            settingsButton.Click += delegate { ToggleSettings(); };
            Control<Button>("SaveSettingsButton").Click += delegate { SaveSettings(); };
            Control<Button>("CheckProtectionButton").Click += delegate { ApplyProtection(); };
            pin.Click += delegate { topmost.IsChecked = !(topmost.IsChecked == true); ApplyWindowOptions(); };
            topmost.Click += delegate { ApplyWindowOptions(); };
            hideTaskbar.Click += delegate { ApplyWindowOptions(); };
            hideTray.Click += delegate { ApplyTrayVisibility(); };
            Control<Button>("MinimizeButton").Click += delegate { Window.WindowState = WindowState.Minimized; };
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
            stop.Click += async delegate { await StopRecognitionAsync(); };
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
            // MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, I
            restoreHotkeyRegistered = RegisterHotKey(hotkeySource.Handle, RestoreHotkeyId, 0x4003, 0x49);
            ApplyTrayVisibility();
        }

        private IntPtr RestoreHotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312 && wParam.ToInt32() == RestoreHotkeyId)
            {
                Restore(); handled = true;
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
            return new ApiConfiguration(apiEndpoint.Text, apiModel.Text, apiJsonMode.IsChecked == true, apiCompletionTokens.IsChecked == true);
        }

        private void SaveSettings()
        {
            try
            {
                ApiConfiguration api = ReadApiConfiguration();
                store.Save(new SavedSettings {
                    ApiEndpoint = api.EndpointUrl, ApiModel = api.ModelName, ApiJsonMode = api.JsonMode, ApiCompletionTokenLimit = api.CompletionTokenLimit,
                    RememberKey = remember.IsChecked == true,
                    JobDescription = job.Text, Resume = resume.Text, Vocabulary = vocabulary.Text,
                    AlwaysOnTop = topmost.IsChecked == true, HideTaskbar = hideTaskbar.IsChecked == true,
                    HideTrayIcon = hideTray.IsChecked == true,
                    AutoAnswerEnabled = automatic.IsChecked == true, OpacityPercent = opacity.Value
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
            SetBusy(true);
            SetStatus("正在生成参考答案…", false);
            try
            {
                string result = await InterviewCore.AskStreamingAsync(key.Password, asked, job.Text, resume.Text,
                    text => UpdateAnswerProgress(current, text, false), current.Token, ReadApiConfiguration());
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
            start.IsEnabled = false;
            stop.IsEnabled = true;
            current.StatusChanged += message => OnUi(delegate { if (session == current) speechStatus.Text = message; });
            current.PreviewReady += text => OnUi(delegate { if (session == current) AcceptPreview(text); });
            current.TranscriptReady += text => OnUi(delegate {
                if (session != current) return;
                AcceptTranscript(text, !stopping && current.IsCapturing);
            });
            current.Failed += error => OnUi(delegate {
                if (session != current) return;
                session = null;
                AcceptPreview("");
                automaticTimer.Stop(); automaticBuffer.Clear();
                if (automaticRequest && request != null) request.Cancel();
                current.Dispose();
                start.IsEnabled = true; stop.IsEnabled = false;
                speechStatus.Text = "识别已停止：" + RecognitionError(error);
            });
            try
            {
                await current.StartAsync();
                if (session == current && !closed)
                {
                    LastRecognitionBackend = current.Backend;
                    LastOutputDevice = current.DeviceName;
                }
            }
            catch (Exception error)
            {
                if (session == current && !stopping)
                {
                    session = null;
                    automaticTimer.Stop(); automaticBuffer.Clear();
                    current.Dispose();
                    start.IsEnabled = true; stop.IsEnabled = false;
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
            SetBusy(true);
            SetStatus("正在自动提取问题并生成参考答案…", false);
            try
            {
                AutomaticAnswer result = await automaticResponder(key.Password, text, job.Text, resume.Text,
                    partial => UpdateAnswerProgress(current, partial, true), current.Token, ReadApiConfiguration());
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

        private async Task StopRecognitionAsync()
        {
            var current = session;
            if (current == null || stopping) return;
            stopping = true;
            automaticTimer.Stop(); automaticBuffer.Clear();
            if (automaticRequest && request != null) request.Cancel();
            stop.IsEnabled = false;
            speechStatus.Text = "正在停止采集并完成剩余转写…";
            try { await current.StopAsync(); }
            catch (Exception error) { if (!closed) speechStatus.Text = "停止识别时发生错误：" + RecognitionError(error); }
            finally
            {
                if (session == current)
                {
                    session = null;
                    AcceptPreview("");
                    current.Dispose();
                    if (!closed)
                    {
                        speechStatus.Text = "已停止 · 点击开始可继续；不会采集麦克风。";
                        start.IsEnabled = true; stop.IsEnabled = false;
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

        public bool RecognitionStartsOnlyOnClick { get { return session == null && start.IsEnabled && !stop.IsEnabled; } }
        public int WindowAlpha { get { return (int)Math.Round(Window.Opacity * 255); } }
        public bool NativeLayeredWindow { get { return WindowAppearance.IsLayered(new WindowInteropHelper(Window).Handle); } }

        public bool TestWindowMovementUi()
        {
            Window.UpdateLayout();
            if (!WindowMovement.CanStartDrag(Window, Control<StackPanel>("WindowDragRegion"))) return false;
            if (!WindowMovement.CanStartDrag(Window, status)) return false;
            foreach (FrameworkElement control in new FrameworkElement[] { start, settingsButton, question, answer, opacity, automatic })
                if (WindowMovement.CanStartDrag(Window, control) || WindowMovement.ReadHitTest(Window, control) != 1) return false;
            return true;
        }

        public async Task<bool> TestAutomaticUiAsync()
        {
            var original = automaticResponder;
            int calls = 0;
            bool streamed = false;
            Action<string> lateProgress = null;
            automatic.IsChecked = true;
            automaticBuffer.Clear();
            automaticResponder = async (secret, text, jd, cv, progress, cancellation, api) => {
                calls++;
                if (text.Contains("解释")) progress("依赖注入");
                await Task.Delay(30, cancellation);
                if (text.Contains("解释")) streamed = answer.Text == "依赖注入" && answerContext.Text.Contains("正在生成");
                return text.Contains("解释")
                    ? new AutomaticAnswer { IsQuestion = true, Question = "请解释 Spring Boot 依赖注入。", Answer = "**依赖注入**由容器提供依赖。" }
                    : new AutomaticAnswer { IsQuestion = false };
            };
            try
            {
                AcceptPreview("解释这段临时识别内容。");
                await Task.Delay(800);
                await ProcessAutomaticAsync(false);
                bool previewNotSubmitted = calls == 0 && !automaticBuffer.HasPending;
                AcceptTranscript("请解释 Spring Boot 依赖注入。", true);
                await Task.Delay(1600);
                await ProcessAutomaticAsync(false);
                bool first = calls == 1 && streamed && question.Text.Contains("依赖注入") && answer.Text == "依赖注入由容器提供依赖。";
                AcceptTranscript("请解释 Spring Boot 依赖注入。", true);
                await Task.Delay(1600);
                await ProcessAutomaticAsync(false);
                bool duplicate = calls == 1;
                AcceptTranscript("欢迎大家，我们今天准时开始。", true);
                await Task.Delay(1600);
                await ProcessAutomaticAsync(false);
                bool noQuestion = calls == 2 && answer.Text == "依赖注入由容器提供依赖。";
                automaticResponder = async (secret, text, jd, cv, progress, cancellation, api) => {
                    lateProgress = progress;
                    await Task.Delay(10000, cancellation);
                    return new AutomaticAnswer { IsQuestion = true, Question = "stale", Answer = "must not show" };
                };
                AcceptTranscript("解释 REST API 的用途。", true);
                await Task.Delay(1600);
                Task pending = ProcessAutomaticAsync(false);
                await Task.Delay(50);
                automatic.IsChecked = false;
                await pending;
                if (lateProgress != null) lateProgress("取消后的旧内容不得显示");
                await Task.Delay(30);
                bool cancelled = !busy && answer.Text == "依赖注入由容器提供依赖。";
                automatic.IsChecked = true;
                return previewNotSubmitted && first && duplicate && noQuestion && cancelled && transcriptPreview.Text == "";
            }
            finally { automaticResponder = original; automaticBuffer.Clear(); }
        }

        public async Task<bool> TestRecognitionUiAsync()
        {
            if (!RecognitionStartsOnlyOnClick) return false;
            bool autoWasEnabled = automatic.IsChecked == true;
            automatic.IsChecked = false; // Never submit real captured content during UI tests.
            try
            {
            Task starting = StartRecognitionAsync();
            await Task.Delay(100);
            await StopRecognitionAsync();
            await starting;
            if (!RecognitionStartsOnlyOnClick) return false;
            await StartRecognitionAsync();
            bool active = session != null && session.IsCapturing && !start.IsEnabled && stop.IsEnabled && generate.IsEnabled;
            settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            active = active && session != null && session.IsCapturing;
            settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(750);
            await StopRecognitionAsync();
            return active && RecognitionStartsOnlyOnClick;
            }
            finally { automatic.IsChecked = autoWasEnabled; }
        }

        public void PrepareUiTest()
        {
            settingsPanel.Visibility = Visibility.Collapsed;
            interviewPanel.Visibility = Visibility.Visible;
            settingsButton.Content = "设置";
            question.Text = "请介绍一下你做过的一个项目，以及你是如何解决其中的困难的。";
            answer.Text = "先给出项目目标与我的职责，再用一个具体问题说明解决过程。\n\n1. 背景：简要说明项目服务的对象，以及需要解决的实际问题。\n\n2. 行动：说明我负责的部分、遇到的困难，以及方案选择的依据。\n\n3. 结果：给出可以核实的结果；没有数据时，明确描述可观察的变化。\n\n4. 复盘：总结经验，以及下次会如何改进。";
            answerContext.Text = "界面测试示例 · 未调用 API";
            copy.IsEnabled = true;
            transcriptBox.Text = "[14:32:08] 请解释一下 Spring Boot 中的 dependency injection，以及 Redis 缓存的使用场景。";
            speechStatus.Text = "未开始 · 只采集默认输出设备，不采集麦克风。";
        }

        public void SavePreview(string file)
        {
            Window.UpdateLayout();
            double previewHeight = Window.WindowStyle == WindowStyle.None ? Window.ActualHeight : Window.ActualHeight - SystemParameters.CaptionHeight - 2 * SystemParameters.ResizeFrameVerticalBorderWidth;
            var image = new RenderTargetBitmap((int)Window.ActualWidth, (int)previewHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(Window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }

        public bool TestSettingsUi()
        {
            settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (settingsPanel.Visibility != Visibility.Visible) return false;
            key.Password = "old-provider-key";
            apiEndpoint.Text = "https://api.example.com/v1";
            if (key.Password.Length != 0) return false;
            apiModel.Text = "custom-model";
            apiJsonMode.IsChecked = false;
            apiCompletionTokens.IsChecked = true;
            key.Password = "ui-test-key-never-persist";
            remember.IsChecked = false;
            opacity.Value = 80;
            vocabulary.Text = "Redis, Spring Boot, dependency injection";
            job.Text = "研发岗位";
            resume.Text = "真实项目经历";
            Control<Button>("SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (settingsPanel.Visibility == Visibility.Visible) return false;
            string content = File.ReadAllText(Path.Combine(store.Folder, "settings.json"), Encoding.UTF8);
            if (content.Contains("ui-test-key-never-persist") || !content.Contains("dependency injection") || WindowAlpha != 204 || !Window.AllowsTransparency || !NativeLayeredWindow) return false;
            SavedSettings persisted = InterviewCore.Json().Deserialize<SavedSettings>(content);
            if (persisted.OpacityPercent != 80 || persisted.AutoAnswerEnabled != true) return false;
            if (persisted.ApiEndpoint != "https://api.example.com/v1/chat/completions" || persisted.ApiModel != "custom-model" || persisted.ApiJsonMode != false || !persisted.ApiCompletionTokenLimit) return false;
            if (!restoreHotkeyRegistered || tray.Visible || persisted.HideTrayIcon != true) return false;
            hideTray.IsChecked = false; ApplyTrayVisibility();
            if (!tray.Visible) return false;
            hideTray.IsChecked = true; ApplyTrayVisibility();
            Window.WindowState = WindowState.Minimized;
            bool handled = false;
            RestoreHotkeyHook(new WindowInteropHelper(Window).Handle, 0x0312, new IntPtr(RestoreHotkeyId), IntPtr.Zero, ref handled);
            if (!handled || !Window.IsVisible || Window.WindowState != WindowState.Normal || tray.Visible) return false;
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!Window.Topmost) return false;
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ApplyProtection();
            return !Window.Topmost && Protection.Verified;
        }

        public void Dispose()
        {
            if (closed) return;
            closed = true;
            if (hotkeySource != null)
            {
                if (restoreHotkeyRegistered) UnregisterHotKey(hotkeySource.Handle, RestoreHotkeyId);
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
        public static int Main(string[] args)
        {
            bool captureTest = args.Length >= 3 && args[0] == "--capture-ui-test";
            bool test = args.Length >= 3 && (args[0] == "--ui-test" || captureTest);
            string data = test ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "desktop-test-data") : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MockInterviewAI-data");
            try
            {
                var app = new Application();
                var controller = new DesktopController(data);
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                    controller.SetStatus("操作未完成，请重试。", true);
                    e.Handled = true;
                };
                if (test)
                {
                    controller.Window.ContentRendered += delegate {
                        controller.PrepareUiTest();
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
                        timer.Tick += async delegate {
                            timer.Stop();
                            bool uiChecks = controller.TestSettingsUi();
                            bool autoChecks = await controller.TestAutomaticUiAsync();
                            bool? captureChecks = null;
                            if (captureTest) captureChecks = await controller.TestRecognitionUiAsync();
                            bool dragChecks = controller.TestWindowMovementUi();
                            controller.ApplyProtection();
                            controller.SavePreview(Path.GetFullPath(args[2]));
                            File.WriteAllText(Path.GetFullPath(args[1]), InterviewCore.Json().Serialize(new {
                                WindowOpened = true, SettingsUiChecksPassed = uiChecks, Protection = controller.Protection,
                                AutomaticQuestionAnswerUiPassed = autoChecks, WindowAlpha = controller.WindowAlpha, NativeLayeredWindow = controller.NativeLayeredWindow,
                                WindowDragHitTestsPassed = dragChecks, IconPixelWidth = ((BitmapSource)controller.Window.Icon).PixelWidth,
                                DragDiagnostic = WindowMovement.LastDragDiagnostic,
                                ExecutableName = Path.GetFileName(Assembly.GetExecutingAssembly().Location),
                                ProductName = System.Diagnostics.FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductName,
                                ExplicitStartIdle = controller.RecognitionStartsOnlyOnClick,
                                StartCancelRestartStopUiPassed = captureChecks,
                                RecognitionBackend = controller.LastRecognitionBackend, OutputDevice = controller.LastOutputDevice,
                                CaptureSource = "WASAPI Render/Multimedia loopback only", ASR = "Whisper large-v3-turbo q5_0, multilingual",
                                Model = InterviewCore.Model, TencentMeetingReceiverVerified = false,
                                PreviewSaved = File.Exists(Path.GetFullPath(args[2]))
                            }), new UTF8Encoding(false));
                            controller.Window.Close();
                        };
                        timer.Start();
                    };
                }
                return app.Run(controller.Window);
            }
            catch (Exception ex)
            {
                string destination = test ? Path.GetFullPath(args[1]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt");
                try { File.WriteAllText(destination, "启动失败：" + ex.Message, Encoding.UTF8); } catch { }
                return 1;
            }
        }
    }
}
