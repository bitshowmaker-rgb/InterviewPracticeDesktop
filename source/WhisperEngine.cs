using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MockInterviewAI
{
    /// <summary>Owns one local whisper.cpp server. Audio never leaves loopback.</summary>
    public sealed class WhisperEngine : IDisposable
    {
        private readonly object gate = new object();
        private readonly SemaphoreSlim startGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim inferenceGate = new SemaphoreSlim(1, 1);
        private Process server;
        private HttpClient client;
        private CancellationToken lifetimeCancellation;
        private CancellationTokenRegistration lifetimeRegistration;
        private volatile bool ready, gpuBackendActive;
        private bool disposed;
        public string Backend { get; private set; }
        public bool IsGpuAccelerated { get { return gpuBackendActive; } }

        public async Task StartAsync(string appRoot, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(appRoot)) throw new ArgumentException("应用程序目录无效。", "appRoot");
            string accelerated = Path.Combine(appRoot, "runtime", "whisper-gpu", "whisper-server.exe");
            if (File.Exists(accelerated))
            {
                try
                {
                    await StartServerAsync(appRoot, "whisper-gpu", true, cancellation).ConfigureAwait(false);
                    Backend = gpuBackendActive ? "GPU · CUDA" : "CPU · 显卡加速未确认，可能积压";
                    return;
                }
                catch (InvalidOperationException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    // A failed native GPU startup has already disposed its own process.
                }
            }
            await StartServerAsync(appRoot, "whisper", false, cancellation).ConfigureAwait(false);
            Backend = "CPU · 较慢，可能积压";
        }

        private async Task StartServerAsync(string appRoot, string runtimeFolder, bool useGpu, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            await startGate.WaitAsync(cancellation).ConfigureAwait(false);
            Process owned = null;
            HttpClient localClient = null;
            bool started = false;
            try
            {
                lock (gate)
                {
                    ThrowIfDisposed();
                    if (ready && server != null && !HasExited(server)) return;
                    if (server != null)
                        throw new InvalidOperationException("语音识别服务已停止，请停止识别后重新点击开始。");
                }

                if (String.IsNullOrWhiteSpace(appRoot))
                    throw new ArgumentException("应用程序目录无效。", "appRoot");
                string root = Path.GetFullPath(appRoot);
                string model = Path.Combine(root, "models", "ggml-large-v3-turbo-q5_0.bin");
                string executable = Path.Combine(root, "runtime", runtimeFolder, "whisper-server.exe");
                if (!File.Exists(model))
                    throw new InvalidOperationException("当前程序目录中找不到语音识别模型：" + model
                        + "\n请使用桌面快捷方式，或从完整的应用文件夹启动。移动程序时需要保留 models 和 runtime 文件夹，不能只复制 EXE。");
                if (new FileInfo(model).Length < 1000000)
                    throw new InvalidOperationException("语音识别模型未下载完整，请重新下载模型。");
                if (!File.Exists(executable))
                    throw new InvalidOperationException("缺少语音识别程序，请检查 runtime\\whisper 文件夹中的 whisper-server.exe。");

                int port = FindAvailablePort();
                int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
                var info = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    Arguments = "-m \"" + model + "\" -t " + threads.ToString(CultureInfo.InvariantCulture)
                        + (useGpu ? "" : " -ng") + " -l auto -sns --host 127.0.0.1 --port " + port.ToString(CultureInfo.InvariantCulture),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                gpuBackendActive = false;
                owned = new Process { StartInfo = info };
                try
                {
                    if (!owned.Start()) throw new InvalidOperationException("无法启动语音识别程序。");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new InvalidOperationException("语音识别程序无法运行，请检查 runtime\\whisper 内的运行文件是否完整。");
                }

                // Read fixed-size buffers and discard them: no transcript or audio is logged.
                DrainOutput(owned.StandardError);
                DrainOutput(owned.StandardOutput);
                localClient = new HttpClient(new HttpClientHandler { UseProxy = false })
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/"),
                    Timeout = Timeout.InfiniteTimeSpan
                };
                Process capturedProcess = owned;
                lock (gate)
                {
                    ThrowIfDisposed();
                    server = owned;
                    client = localClient;
                    lifetimeCancellation = cancellation;
                    lifetimeRegistration = cancellation.Register(delegate { KillOwnedProcess(capturedProcess); });
                }

                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (deadline.Token.Register(delegate { KillOwnedProcess(capturedProcess); }))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(120));
                    try
                    {
                        while (true)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            if (HasExited(owned))
                                throw new InvalidOperationException("语音识别服务启动失败，请检查运行文件和模型是否完整。");
                            using (var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                            {
                                probe.CancelAfter(TimeSpan.FromSeconds(2));
                                try
                                {
                                    using (HttpResponseMessage response = await localClient.GetAsync("health", probe.Token).ConfigureAwait(false))
                                    {
                                        if (response.IsSuccessStatusCode)
                                        {
                                            lock (gate)
                                            {
                                                ThrowIfDisposed();
                                                cancellation.ThrowIfCancellationRequested();
                                                ready = true;
                                            }
                                            started = true;
                                            return;
                                        }
                                    }
                                }
                                catch (HttpRequestException) { }
                                catch (OperationCanceledException)
                                {
                                    deadline.Token.ThrowIfCancellationRequested();
                                }
                            }
                            await Task.Delay(250, deadline.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("语音模型启动超过两分钟，请关闭占用较高的程序后重试。");
                    }
                }
            }
            finally
            {
                if (!started && owned != null)
                {
                    CancellationTokenRegistration registration = default(CancellationTokenRegistration);
                    lock (gate)
                    {
                        if (Object.ReferenceEquals(server, owned))
                        {
                            registration = lifetimeRegistration;
                            lifetimeRegistration = default(CancellationTokenRegistration);
                            server = null;
                            client = null;
                            ready = false;
                        }
                    }
                    registration.Dispose();
                    KillOwnedProcess(owned);
                    if (localClient != null) localClient.Dispose();
                    owned.Dispose();
                }
                startGate.Release();
            }
        }

        public async Task<string> TranscribeAsync(byte[] pcm16Mono16k, string vocabulary, CancellationToken cancellation)
        {
            if (pcm16Mono16k == null) throw new ArgumentNullException("pcm16Mono16k");
            cancellation.ThrowIfCancellationRequested();
            if (pcm16Mono16k.Length == 0) return String.Empty;
            if ((pcm16Mono16k.Length & 1) != 0)
                throw new ArgumentException("语音数据必须是完整的 16 位采样。", "pcm16Mono16k");

            await inferenceGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                Process owned;
                HttpClient localClient;
                CancellationToken lifetime;
                lock (gate)
                {
                    ThrowIfDisposed();
                    if (!ready || server == null || HasExited(server))
                        throw new InvalidOperationException("语音识别服务未运行，请重新点击开始。");
                    owned = server;
                    localClient = client;
                    lifetime = lifetimeCancellation;
                }

                using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime))
                using (requestCancellation.Token.Register(delegate { KillOwnedProcess(owned); }))
                using (var form = new MultipartFormDataContent())
                {
                    requestCancellation.CancelAfter(TimeSpan.FromSeconds(90));
                    var audio = new ByteArrayContent(EncodeWave(pcm16Mono16k));
                    audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                    form.Add(audio, "file", "speech.wav");
                    form.Add(new StringContent("verbose_json"), "response_format");
                    form.Add(new StringContent("auto"), "language");
                    form.Add(new StringContent("0"), "temperature");
                    form.Add(new StringContent("0.2"), "temperature_inc");
                    form.Add(new StringContent("true"), "suppress_nst");
                    form.Add(new StringContent("true"), "no_language_probabilities");
                    form.Add(new StringContent("false"), "token_timestamps");
                    if (!String.IsNullOrWhiteSpace(vocabulary))
                    {
                        string prompt = vocabulary.Trim();
                        if (prompt.Length > 2000) prompt = prompt.Substring(0, 2000);
                        form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
                    }
                    try
                    {
                        using (HttpResponseMessage response = await localClient.PostAsync("inference", form, requestCancellation.Token).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                                throw new InvalidOperationException("本地语音识别失败（HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + "），请停止后重新开始。");
                            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            requestCancellation.Token.ThrowIfCancellationRequested();
                            return ParseTranscription(json);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        lifetime.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("本地语音识别处理超过 90 秒，请停止后重新开始。");
                    }
                    catch (HttpRequestException)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        lifetime.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("无法连接本地语音识别服务，请停止后重新开始。");
                    }
                }
            }
            finally { inferenceGate.Release(); }
        }

        internal static byte[] EncodeWave(byte[] pcm16Mono16k)
        {
            if (pcm16Mono16k == null) throw new ArgumentNullException("pcm16Mono16k");
            if ((pcm16Mono16k.Length & 1) != 0)
                throw new ArgumentException("语音数据必须是完整的 16 位采样。", "pcm16Mono16k");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(checked(36 + pcm16Mono16k.Length));
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(16000);
                writer.Write(32000);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(pcm16Mono16k.Length);
                writer.Write(pcm16Mono16k);
                writer.Flush();
                return stream.ToArray();
            }
        }

        internal static string ParseTranscription(string json)
        {
            Dictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer { MaxJsonLength = 4194304 }.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("本地语音识别返回了无法读取的结果，请停止后重新开始。");
            }
            if (root == null) throw new InvalidOperationException("本地语音识别返回了无效结果。");
            object value;
            if (!root.TryGetValue("segments", out value) || value == null)
                return ReadText(root).Trim();
            var segments = value as object[];
            if (segments == null) throw new InvalidOperationException("本地语音识别返回了无效的语音片段。");
            var result = new StringBuilder();
            foreach (object item in segments)
            {
                var segment = item as Dictionary<string, object>;
                if (segment == null) continue;
                // These are Whisper's silence heuristics, not a calibrated accuracy score.
                double silence = ReadNumber(segment, "no_speech_prob");
                double logProbability = ReadNumber(segment, "avg_logprob");
                if (silence > 0.6 && logProbability < -1.0) continue;
                string text = ReadText(segment);
                if (String.IsNullOrWhiteSpace(text)) continue;
                result.Append(text);
            }
            return result.ToString().Trim();
        }

        private static string ReadText(Dictionary<string, object> data)
        {
            object value;
            return data.TryGetValue("text", out value) ? value as string ?? String.Empty : String.Empty;
        }

        private static double ReadNumber(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null || value is bool || value is string) return Double.NaN;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (InvalidCastException) { return Double.NaN; }
            catch (FormatException) { return Double.NaN; }
            catch (OverflowException) { return Double.NaN; }
        }

        private static int FindAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally { listener.Stop(); }
        }

        private async void DrainOutput(StreamReader reader)
        {
            try
            {
                var buffer = new char[2048];
                string startupTail = "";
                int count;
                while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                {
                    if (!ready)
                    {
                        // Inspect only bounded startup metadata, never keep inference output.
                        string metadata = startupTail + new string(buffer, 0, count);
                        if (metadata.Contains("CUDA0 total size")) gpuBackendActive = true;
                        if (metadata.Contains("failed to initialize CUDA")) gpuBackendActive = false;
                        startupTail = metadata.Substring(Math.Max(0, metadata.Length - 256));
                    }
                    else startupTail = "";
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static bool HasExited(Process owned)
        {
            try { return owned.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        private static void KillOwnedProcess(Process owned)
        {
            if (owned == null) return;
            try { if (!owned.HasExited) owned.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("WhisperEngine");
        }

        public void Dispose()
        {
            Process owned;
            HttpClient localClient;
            CancellationTokenRegistration registration;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                ready = false;
                owned = server;
                localClient = client;
                registration = lifetimeRegistration;
                server = null;
                client = null;
                lifetimeRegistration = default(CancellationTokenRegistration);
            }
            registration.Dispose();
            KillOwnedProcess(owned);
            if (localClient != null) localClient.Dispose();
            if (owned != null) owned.Dispose();
        }
    }
}
