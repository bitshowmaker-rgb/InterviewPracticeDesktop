using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MockInterviewAI
{
    // Streaming PCM segmentation is independent of audio devices and the ASR engine.
    public sealed class PcmSpeechSegmenter
    {
        public event Action<byte[]> SegmentReady;
        public event Action<byte[]> PreviewReady;
        public bool SegmentHasOverlap { get; private set; }
        public bool IsSpeechInProgress { get { return inSpeech || overlapLeadFrames > 0; } }
        public const int SampleRate = 16000;
        private const int FrameBytes = 640; // 20 ms, mono PCM16.
        private readonly Queue<byte[]> lead = new Queue<byte[]>();
        private readonly MemoryStream active = new MemoryStream();
        private readonly List<byte> remainder = new List<byte>();
        private int speechFrames, silenceFrames;
        private bool inSpeech, currentOverlap;
        private int overlapLeadFrames;
        private long nextPreviewBytes = SampleRate * 2 * 4;
        private DateTime lastAudio = DateTime.UtcNow;

        public void Add(byte[] pcm)
        {
            lastAudio = DateTime.UtcNow;
            remainder.AddRange(pcm);
            int consumed = 0;
            while (remainder.Count - consumed >= FrameBytes)
            {
                byte[] frame = remainder.GetRange(consumed, FrameBytes).ToArray();
                ProcessFrame(frame);
                consumed += FrameBytes;
            }
            if (consumed > 0) remainder.RemoveRange(0, consumed);
        }

        private void ProcessFrame(byte[] frame)
        {
            double sum = 0;
            for (int i = 0; i < frame.Length; i += 2)
            {
                double value = (short)(frame[i] | (frame[i + 1] << 8)) / 32768.0;
                sum += value * value;
            }
            bool voiced = Math.Sqrt(sum / (frame.Length / 2)) >= 0.0035;
            if (!inSpeech)
            {
                lead.Enqueue(frame);
                while (lead.Count > 15)
                {
                    lead.Dequeue();
                    if (overlapLeadFrames > 0) overlapLeadFrames--;
                }
                if (!voiced) return;
                inSpeech = true;
                currentOverlap = overlapLeadFrames > 0;
                overlapLeadFrames = 0;
                foreach (byte[] prior in lead) active.Write(prior, 0, prior.Length);
                lead.Clear();
                speechFrames = 1;
                silenceFrames = 0;
                return;
            }
            active.Write(frame, 0, frame.Length);
            if (voiced) { speechFrames++; silenceFrames = 0; }
            else silenceFrames++;
            if (silenceFrames >= 25) Emit(false); // 500 ms pause closes a phrase.
            else if (active.Length >= SampleRate * 2 * 16) Emit(true); // Preserve context for confirmed text.
            else if (active.Length >= nextPreviewBytes)
            {
                nextPreviewBytes += SampleRate * 2 * 4;
                var preview = PreviewReady;
                if (preview != null) preview(active.ToArray()); // Snapshot only; do not consume speech or overlap.
            }
        }

        private void Emit(bool retainOverlap)
        {
            byte[] pcm = active.ToArray();
            bool enoughSpeech = speechFrames >= 12; // Reject clicks and very short transients.
            SegmentHasOverlap = currentOverlap;
            currentOverlap = false;
            active.SetLength(0);
            nextPreviewBytes = SampleRate * 2 * 4;
            inSpeech = false;
            speechFrames = silenceFrames = 0;
            lead.Clear();
            overlapLeadFrames = 0;
            if (retainOverlap && enoughSpeech)
            {
                int start = Math.Max(0, pcm.Length - 15 * FrameBytes);
                for (int position = start; position + FrameBytes <= pcm.Length; position += FrameBytes)
                {
                    byte[] frame = new byte[FrameBytes];
                    Buffer.BlockCopy(pcm, position, frame, 0, frame.Length);
                    lead.Enqueue(frame);
                    overlapLeadFrames++;
                }
            }
            if (enoughSpeech && SegmentReady != null) SegmentReady(pcm);
        }

        // WASAPI emits no frames when playback is silent; a wall-clock tick closes its tail.
        public void Tick(DateTime utcNow)
        {
            if (utcNow - lastAudio >= TimeSpan.FromMilliseconds(550))
            {
                if (inSpeech) Emit(false);
                else { lead.Clear(); overlapLeadFrames = 0; }
            }
        }

        public void Finish()
        {
            if (remainder.Count >= 2 && inSpeech)
            {
                byte[] tail = remainder.ToArray();
                active.Write(tail, 0, tail.Length - tail.Length % 2);
            }
            remainder.Clear();
            if (inSpeech) Emit(false);
            lead.Clear();
            overlapLeadFrames = 0;
        }
    }

    public static class TranscriptText
    {
        public static string RemoveOverlap(string previous, string next)
        {
            next = (next ?? "").Trim();
            previous = (previous ?? "").Trim();
            int max = Math.Min(160, Math.Min(previous.Length, next.Length));
            for (int length = max; length >= 4; length--)
                if (previous.EndsWith(next.Substring(0, length), StringComparison.OrdinalIgnoreCase))
                    return next.Substring(length).Trim();
            return next;
        }
    }

    public sealed class SpeakerTranscriptionSession : IDisposable
    {
        public event Action<string> StatusChanged;
        public event Action<string> TranscriptReady;
        public event Action<string> PreviewReady;
        public event Action<Exception> Failed;
        private readonly object gate = new object();
        private readonly string root, vocabulary;
        private readonly WhisperEngine engine = new WhisperEngine();
        private readonly CancellationTokenSource abort = new CancellationTokenSource();
        private sealed class AudioChunk { public byte[] Pcm; public bool HasOverlap, IsPreview; public int SegmentId; }
        private readonly BlockingCollection<AudioChunk> pending = new BlockingCollection<AudioChunk>();
        private readonly PcmSpeechSegmenter segmenter = new PcmSpeechSegmenter();
        private LoopbackAudio capture;
        private Timer idleTimer;
        private Task initialization, worker;
        private bool capturing, disposed, faulted;
        private string previousText = "";
        private int queued;
        private int segmentId;
        public string DeviceName { get { return capture == null ? "" : capture.DeviceName; } }
        public string Backend { get { return engine.Backend; } }
        public bool IsCapturing { get { lock (gate) return capturing; } }
        public bool HasUnfinishedAudio { get { lock (gate) return segmenter.IsSpeechInProgress || PendingCount > 0; } }
        public int PendingCount { get { return Math.Max(0, Volatile.Read(ref queued)); } }
        private string CaptureStatus { get { return "持续识别中 · " + engine.Backend + " · 输出：" + DeviceName; } }

        public SpeakerTranscriptionSession(string root, string vocabulary)
        {
            this.root = root;
            this.vocabulary = vocabulary ?? "";
            segmenter.SegmentReady += delegate(byte[] pcm) {
                if (pending.IsAddingCompleted || disposed) return;
                if (PendingCount >= 60)
                {
                    Fail(new InvalidOperationException("转写积压过多，已停止采集。请关闭其他占用 CPU 的程序后重新开始。"));
                    return;
                }
                Interlocked.Increment(ref queued);
                pending.Add(new AudioChunk { Pcm = pcm, HasOverlap = segmenter.SegmentHasOverlap, SegmentId = segmentId });
                Interlocked.Increment(ref segmentId); // Invalidate previews as soon as a confirmed chunk is queued.
            };
            segmenter.PreviewReady += delegate(byte[] pcm) {
                // Extra preview work is optional and must never delay confirmed speech.
                if (!engine.IsGpuAccelerated || pending.IsAddingCompleted || disposed || PendingCount > 0) return;
                Interlocked.Increment(ref queued);
                pending.Add(new AudioChunk { Pcm = pcm, IsPreview = true, SegmentId = segmentId });
            };
        }

        public Task StartAsync()
        {
            if (initialization != null) throw new InvalidOperationException("识别已启动。");
            initialization = StartCoreAsync();
            return initialization;
        }

        private async Task StartCoreAsync()
        {
            Notify("正在载入中英文识别模型…");
            await engine.StartAsync(root, abort.Token).ConfigureAwait(false);
            lock (gate)
            {
                abort.Token.ThrowIfCancellationRequested();
                if (disposed) throw new OperationCanceledException();
                capture = new LoopbackAudio();
                capture.PcmReceived += delegate(byte[] pcm) {
                    lock (gate) { if (capturing && !disposed) segmenter.Add(pcm); }
                };
                capture.Failed += Fail;
                worker = Task.Run((Func<Task>)ProcessQueueAsync);
                capturing = true;
                try { capture.Start(); }
                catch { capturing = false; throw; }
                idleTimer = new Timer(delegate {
                    lock (gate) { if (capturing && !disposed) segmenter.Tick(DateTime.UtcNow); }
                }, null, 100, 100);
            }
            Notify(CaptureStatus);
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                foreach (AudioChunk chunk in pending.GetConsumingEnumerable(abort.Token))
                {
                    try
                    {
                        if (chunk.IsPreview && !IsCurrentPreview(chunk.SegmentId, Volatile.Read(ref segmentId), PendingCount)) continue;
                        Notify("正在转写 · " + engine.Backend + " · 待处理 " + PendingCount + " 段");
                        string text = await engine.TranscribeAsync(chunk.Pcm, vocabulary, abort.Token).ConfigureAwait(false);
                        if (chunk.IsPreview)
                        {
                            var preview = PreviewReady;
                            if (!disposed && preview != null && IsCurrentPreview(chunk.SegmentId, Volatile.Read(ref segmentId), PendingCount)) preview(text);
                            continue;
                        }
                        var clearPreview = PreviewReady;
                        if (!disposed && clearPreview != null) clearPreview("");
                        if (chunk.HasOverlap) text = TranscriptText.RemoveOverlap(previousText, text);
                        if (!String.IsNullOrWhiteSpace(text))
                        {
                            previousText = (previousText + text).Trim();
                            if (previousText.Length > 200) previousText = previousText.Substring(previousText.Length - 200);
                            var handler = TranscriptReady;
                            if (!disposed && handler != null) handler(text);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref queued);
                        if (IsCapturing) Notify(CaptureStatus + (PendingCount > 0 ? " · 排队 " + PendingCount + " 段" : " · 等待播放声音"));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Fail(error); }
        }

        internal static bool IsCurrentPreview(int previewSegmentId, int currentSegmentId, int pendingCount)
        {
            return previewSegmentId == currentSegmentId && pendingCount <= 1;
        }

        public async Task StopAsync()
        {
            if (initialization != null && !initialization.IsCompleted) abort.Cancel();
            if (initialization != null)
            {
                try { await initialization.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { }
            }
            lock (gate) capturing = false;
            if (idleTimer != null) idleTimer.Dispose();
            if (capture != null) capture.Stop(); // Do not hold gate while joining the capture thread.
            lock (gate)
            {
                if (!pending.IsAddingCompleted)
                {
                    segmenter.Finish();
                    pending.CompleteAdding();
                }
            }
            if (PendingCount > 0) Notify("已停止采集，正在完成最后 " + PendingCount + " 段转写…");
            if (worker != null) await worker.ConfigureAwait(false);
            engine.Dispose();
        }

        private void Notify(string message)
        {
            var handler = StatusChanged;
            if (!disposed && handler != null) handler(message);
        }

        private void Fail(Exception error)
        {
            lock (gate) { if (disposed || faulted) return; faulted = true; capturing = false; }
            abort.Cancel();
            Task.Run(delegate { if (capture != null) capture.Stop(); engine.Dispose(); });
            var handler = Failed;
            if (handler != null) handler(error);
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; capturing = false; }
            abort.Cancel();
            if (idleTimer != null) idleTimer.Dispose();
            if (capture != null) capture.Dispose();
            if (!pending.IsAddingCompleted) pending.CompleteAdding();
            engine.Dispose();
        }
    }
}
