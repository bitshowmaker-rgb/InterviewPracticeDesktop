using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MockInterviewAI
{
    /// <summary>
    /// Captures only a Windows render endpoint through WASAPI loopback.
    /// It never opens a capture endpoint or falls back to a microphone.
    /// PCM events contain independent, frame-aligned 16 kHz / 16-bit / mono buffers.
    /// </summary>
    public sealed class LoopbackAudio : IDisposable
    {
        public const int SampleRate = 16000;
        public event Action<byte[]> PcmReceived;
        public event Action<Exception> Failed;

        private readonly object gate = new object();
        private WasapiLoopbackCapture capture;
        private MMDeviceEnumerator enumerator;
        private MMDevice device;
        private DefaultOutputWatcher watcher;
        private EventHandler<WaveInEventArgs> dataHandler;
        private EventHandler<StoppedEventArgs> stoppedHandler;
        private long generation;
        private bool running;
        private bool disposed;
        private int callbackThreadId;
        private string deviceName = "尚未开始";

        public bool IsRunning { get { lock (gate) return running; } }
        public string DeviceName { get { lock (gate) return deviceName; } }

        /// <summary>Reads endpoint metadata without activating an audio capture stream.</summary>
        public static string DescribeDefaultOutput()
        {
            using (var endpoints = new MMDeviceEnumerator())
            using (var output = endpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                if (output.DataFlow != DataFlow.Render)
                    throw new InvalidOperationException("默认设备不是播放输出设备。");
                return output.FriendlyName;
            }
        }

        public void Start()
        {
            Resources failedResources = null;
            Exception failure = null;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("LoopbackAudio");
                if (running || capture != null) throw new InvalidOperationException("系统输出识别已在运行。");
                long current = ++generation;
                var opening = new Resources();
                try
                {
                    opening.Enumerator = new MMDeviceEnumerator();
                    opening.Device = opening.Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (opening.Device.DataFlow != DataFlow.Render || opening.Device.State != DeviceState.Active)
                        throw new InvalidOperationException("当前没有可用的扬声器或耳机播放输出。");

                    opening.Capture = new WasapiLoopbackCapture(opening.Device);
                    // WASAPI shared-mode conversion in NAudio 2.2.1 handles source mix formats.
                    // A failure to initialize this exact format is reported; there is no mic fallback.
                    opening.Capture.WaveFormat = new WaveFormat(SampleRate, 16, 1);
                    WaveFormat format = opening.Capture.WaveFormat;
                    if (format.SampleRate != SampleRate || format.BitsPerSample != 16 ||
                        format.Channels != 1 || format.Encoding != WaveFormatEncoding.Pcm)
                        throw new InvalidOperationException("无法将系统播放声音转换为 16 kHz 单声道 PCM。");

                    opening.Watcher = new DefaultOutputWatcher(this, current, opening.Device.ID);
                    int result = opening.Enumerator.RegisterEndpointNotificationCallback(opening.Watcher);
                    Marshal.ThrowExceptionForHR(result);
                    WasapiLoopbackCapture source = opening.Capture;
                    opening.Data = delegate(object sender, WaveInEventArgs args) { Receive(current, source, args); };
                    opening.Stopped = delegate(object sender, StoppedEventArgs args) {
                        QueueFailure(current, args.Exception ?? new InvalidOperationException("系统播放声音采集意外结束，请重新开始。"));
                    };
                    source.DataAvailable += opening.Data;
                    source.RecordingStopped += opening.Stopped;

                    capture = opening.Capture;
                    device = opening.Device;
                    enumerator = opening.Enumerator;
                    watcher = opening.Watcher;
                    dataHandler = opening.Data;
                    stoppedHandler = opening.Stopped;
                    deviceName = opening.Device.FriendlyName;
                    running = true;
                    source.StartRecording();
                }
                catch (Exception ex)
                {
                    running = false;
                    generation++;
                    capture = null;
                    device = null;
                    enumerator = null;
                    watcher = null;
                    dataHandler = null;
                    stoppedHandler = null;
                    failedResources = opening;
                    failure = ex;
                }
            }
            if (failedResources != null) Release(failedResources);
            if (failure != null)
                throw new InvalidOperationException("无法开始采集系统播放声音，请检查扬声器或耳机输出设备。", failure);
        }

        private void Receive(long current, WasapiLoopbackCapture source, WaveInEventArgs args)
        {
            lock (gate)
            {
                if (!running || generation != current || capture != source) return;
                if (args.BytesRecorded == 0) return;
                if (args.BytesRecorded < 0 || args.BytesRecorded > args.Buffer.Length || (args.BytesRecorded & 1) != 0)
                {
                    QueueFailure(current, new InvalidOperationException("系统音频返回了无效的 PCM 数据。"));
                    return;
                }
                byte[] copy = new byte[args.BytesRecorded];
                Buffer.BlockCopy(args.Buffer, 0, copy, 0, copy.Length);
                Action<byte[]> callback = PcmReceived;
                if (callback == null) return;
                callbackThreadId = Thread.CurrentThread.ManagedThreadId;
                try { callback(copy); }
                catch (Exception ex) { QueueFailure(current, ex); }
                finally { callbackThreadId = 0; }
            }
        }

        private void QueueFailure(long current, Exception error)
        {
            // Endpoint notifications and DataAvailable can execute on native/capture threads.
            // Cleanup must not run there, because NAudio.Dispose joins its capture thread.
            ThreadPool.QueueUserWorkItem(delegate {
                Resources resources;
                lock (gate)
                {
                    if (!running || generation != current) return;
                    resources = Detach();
                    Action<Exception> callback = Failed;
                    if (callback != null)
                    {
                        try { callback(error); }
                        catch { /* A subscriber must not crash the native audio callback. */ }
                    }
                }
                Release(resources);
            });
        }

        public void Stop()
        {
            Resources resources;
            bool insideCaptureCallback;
            lock (gate)
            {
                insideCaptureCallback = callbackThreadId == Thread.CurrentThread.ManagedThreadId;
                resources = Detach();
            }
            if (resources == null) return;
            if (insideCaptureCallback) ThreadPool.QueueUserWorkItem(delegate { Release(resources); });
            else Release(resources);
        }

        // Requires gate. Removing event handlers and increasing the generation makes late
        // native callbacks harmless, including callbacks from a previous start/stop session.
        private Resources Detach()
        {
            running = false;
            generation++;
            if (capture == null && enumerator == null && device == null) return null;
            var resources = new Resources {
                Capture = capture, Enumerator = enumerator, Device = device,
                Watcher = watcher, Data = dataHandler, Stopped = stoppedHandler
            };
            if (capture != null)
            {
                if (dataHandler != null) capture.DataAvailable -= dataHandler;
                if (stoppedHandler != null) capture.RecordingStopped -= stoppedHandler;
            }
            capture = null;
            enumerator = null;
            device = null;
            watcher = null;
            dataHandler = null;
            stoppedHandler = null;
            return resources;
        }

        private static void Release(Resources resources)
        {
            if (resources == null) return;
            if (resources.Capture != null)
            {
                if (resources.Data != null) resources.Capture.DataAvailable -= resources.Data;
                if (resources.Stopped != null) resources.Capture.RecordingStopped -= resources.Stopped;
                try { resources.Capture.StopRecording(); } catch { }
                try { resources.Capture.Dispose(); } catch { }
            }
            if (resources.Enumerator != null && resources.Watcher != null)
            {
                try { resources.Enumerator.UnregisterEndpointNotificationCallback(resources.Watcher); } catch { }
            }
            if (resources.Device != null) { try { resources.Device.Dispose(); } catch { } }
            if (resources.Enumerator != null) { try { resources.Enumerator.Dispose(); } catch { } }
        }

        public void Dispose()
        {
            lock (gate) disposed = true;
            Stop();
        }

        private sealed class Resources
        {
            public WasapiLoopbackCapture Capture;
            public MMDeviceEnumerator Enumerator;
            public MMDevice Device;
            public DefaultOutputWatcher Watcher;
            public EventHandler<WaveInEventArgs> Data;
            public EventHandler<StoppedEventArgs> Stopped;
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        public sealed class DefaultOutputWatcher : IMMNotificationClient
        {
            private readonly LoopbackAudio owner;
            private readonly long generation;
            private readonly string id;
            internal DefaultOutputWatcher(LoopbackAudio owner, long generation, string id)
            { this.owner = owner; this.generation = generation; this.id = id; }
            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render && role == Role.Multimedia && !String.Equals(defaultDeviceId, id, StringComparison.Ordinal))
                    owner.QueueFailure(generation, new InvalidOperationException("默认播放设备已更改，请点击开始以使用新的扬声器或耳机。"));
            }
            public void OnDeviceStateChanged(string deviceId, DeviceState newState)
            {
                if (String.Equals(deviceId, id, StringComparison.Ordinal) && newState != DeviceState.Active)
                    owner.QueueFailure(generation, new InvalidOperationException("正在识别的播放设备已断开或停用，请检查输出设备后重新开始。"));
            }
            public void OnDeviceRemoved(string deviceId)
            {
                if (String.Equals(deviceId, id, StringComparison.Ordinal))
                    owner.QueueFailure(generation, new InvalidOperationException("正在识别的播放设备已移除，请重新选择输出设备并开始。"));
            }
            public void OnDeviceAdded(string deviceId) { }
            public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
        }
    }
}
