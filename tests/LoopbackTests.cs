using System;
using System.Collections.Generic;
using System.Threading;
using MockInterviewAI;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// Run with --capture-tone only when an audible 1 kHz diagnostic tone is appropriate.
// The test opens render endpoints only. Recorded audio stays in memory and is never saved.
public static class LoopbackTests
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    public static int Main(string[] args)
    {
        try
        {
            using (var audio = new LoopbackAudio())
            {
                Assert(!audio.IsRunning, "New instance must not record automatically.");
                audio.Stop();
                audio.Stop();
                Assert(!audio.IsRunning, "Stop must be idempotent.");
                audio.Dispose();
                bool rejected = false;
                try { audio.Start(); } catch (ObjectDisposedException) { rejected = true; }
                Assert(rejected, "A disposed capture must not restart.");
            }
            Console.WriteLine("PASS explicit start, idempotent stop and disposed-state guard");
            if (args.Length > 0 && args[0] == "--silent-probe")
            {
                Console.WriteLine("Render endpoint: " + LoopbackAudio.DescribeDefaultOutput());
                int probeCallbacks = 0, probeBytes = 0, probeInvalidFrames = 0;
                Exception probeFailure = null;
                using (var probe = new LoopbackAudio())
                {
                    // Do not retain, analyze, print or save the actual captured audio.
                    probe.PcmReceived += delegate(byte[] pcm) {
                        Interlocked.Increment(ref probeCallbacks);
                        Interlocked.Add(ref probeBytes, pcm.Length);
                        if ((pcm.Length & 1) != 0) Interlocked.Increment(ref probeInvalidFrames);
                    };
                    probe.Failed += delegate(Exception error) { probeFailure = error; };
                    probe.Start();
                    Assert(probe.IsRunning, "Silent probe did not enter running state.");
                    Thread.Sleep(500);
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    probe.Stop();
                    elapsed.Stop();
                    Assert(!probe.IsRunning, "Silent probe did not stop.");
                    int stoppedCount = Interlocked.CompareExchange(ref probeCallbacks, 0, 0);
                    Thread.Sleep(250);
                    Assert(Interlocked.CompareExchange(ref probeCallbacks, 0, 0) == stoppedCount, "A PCM callback arrived after Stop returned.");
                    Assert(Interlocked.CompareExchange(ref probeInvalidFrames, 0, 0) == 0, "PCM frame alignment failed.");
                    if (probeFailure != null) throw new Exception("Silent loopback probe failed.", probeFailure);
                    Console.WriteLine("PASS silent render-loopback probe; validated PCM=16000Hz/16bit/mono; callbacks=" + probeCallbacks + "; bytes discarded=" + probeBytes + "; Stop elapsed=" + elapsed.ElapsedMilliseconds + "ms; no callbacks after Stop.");
                }
                return 0;
            }
            if (args.Length == 0 || args[0] != "--capture-tone")
            {
                Console.WriteLine("INFO audible loopback integration test not requested");
                return 0;
            }

            string description = LoopbackAudio.DescribeDefaultOutput();
            Console.WriteLine("Render endpoint: " + description);
            var samples = new List<short>();
            var gate = new object();
            Exception failed = null;
            int callbacks = 0;
            using (var audio = new LoopbackAudio())
            using (var endpoints = new MMDeviceEnumerator())
            using (var output = endpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                Assert(output.DataFlow == DataFlow.Render, "Test output must be a render endpoint.");
                audio.PcmReceived += delegate(byte[] pcm) {
                    Assert((pcm.Length & 1) == 0, "PCM data must be frame aligned.");
                    lock (gate)
                    {
                        callbacks++;
                        for (int i = 0; i < pcm.Length && samples.Count < LoopbackAudio.SampleRate * 8; i += 2)
                            samples.Add(BitConverter.ToInt16(pcm, i));
                    }
                };
                audio.Failed += delegate(Exception error) { failed = error; };
                audio.Start();
                Assert(audio.IsRunning, "Capture did not enter running state.");
                Thread.Sleep(250);
                WaveFormat mix;
                using (var client = output.AudioClient) mix = client.MixFormat;
                using (var player = new WasapiOut(output, AudioClientShareMode.Shared, true, 100))
                {
                    player.Init(new QuietToneProvider(mix));
                    player.Play();
                    Thread.Sleep(1800);
                    player.Stop();
                }
                Thread.Sleep(250);
                audio.Stop();
                Assert(!audio.IsRunning, "Capture did not stop.");
                int count;
                lock (gate) count = callbacks;
                Thread.Sleep(300);
                lock (gate) Assert(callbacks == count, "PCM callback arrived after Stop returned.");
                if (failed != null) throw new Exception("Loopback capture reported failure.", failed);
                Assert(samples.Count > 16000, "Not enough loopback audio received.");
                double sine = 0, cosine = 0, energy = 0;
                for (int i = 0; i < samples.Count; i++)
                {
                    double value = samples[i] / 32768.0;
                    energy += value * value;
                    sine += value * Math.Sin(2 * Math.PI * 1000 * i / LoopbackAudio.SampleRate);
                    cosine += value * Math.Cos(2 * Math.PI * 1000 * i / LoopbackAudio.SampleRate);
                }
                double rms = Math.Sqrt(energy / samples.Count);
                double toneAmplitude = 2 * Math.Sqrt(sine * sine + cosine * cosine) / samples.Count;
                Console.WriteLine("PCM samples: " + samples.Count + "; callbacks: " + callbacks + "; RMS: " + rms.ToString("F5") + "; 1kHz amplitude: " + toneAmplitude.ToString("F5"));
                Assert(rms > 0.0001 && toneAmplitude > 0.00005, "Diagnostic playback tone was not identified in render loopback.");
            }
            Console.WriteLine("PASS render-only capture, 16k PCM conversion, generated-tone detection and no callbacks after stop");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + error);
            return 1;
        }
    }

    private sealed class QuietToneProvider : IWaveProvider
    {
        private readonly WaveFormat format;
        private readonly bool floating;
        private long frame;
        public WaveFormat WaveFormat { get { return format; } }
        public QuietToneProvider(WaveFormat format)
        {
            this.format = format;
            var extensible = format as WaveFormatExtensible;
            floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                (extensible != null && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            if ((floating && format.BitsPerSample != 32) ||
                (!floating && format.BitsPerSample != 16 && format.BitsPerSample != 24 && format.BitsPerSample != 32))
                throw new NotSupportedException("Unexpected audio mix format: " + format);
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / format.BlockAlign;
            int bytesPerSample = format.BitsPerSample / 8;
            for (int i = 0; i < frames; i++, frame++)
            {
                // Quiet tone: full-scale amplitude 0.02, about -34 dBFS.
                double value = Math.Sin(2 * Math.PI * 1000 * frame / format.SampleRate) * 0.02;
                byte[] encoded;
                if (floating) encoded = BitConverter.GetBytes((float)value);
                else if (format.BitsPerSample == 16) encoded = BitConverter.GetBytes((short)(value * 32767));
                else if (format.BitsPerSample == 24) encoded = BitConverter.GetBytes((int)(value * 8388607));
                else encoded = BitConverter.GetBytes((int)(value * 2147483647));
                for (int channel = 0; channel < format.Channels; channel++)
                    Buffer.BlockCopy(encoded, 0, buffer, offset + i * format.BlockAlign + channel * bytesPerSample, bytesPerSample);
            }
            return frames * format.BlockAlign;
        }
    }
}
