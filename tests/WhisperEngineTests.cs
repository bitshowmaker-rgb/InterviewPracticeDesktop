using System;
using System.IO;
using System.Text;
using System.Threading;
using MockInterviewAI;

public static class WhisperEngineTests
{
    private static int passed;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    public static int Main()
    {
        try
        {
            Test("PCM WAV format and payload remain lossless", delegate {
                byte[] pcm = { 0, 0, 255, 127, 0, 128, 1, 0 };
                byte[] wav = WhisperEngine.EncodeWave(pcm);
                Check(wav.Length == pcm.Length + 44, "WAV length");
                Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wav, 36, 4) == "data", "Header");
                Check(BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt16(wav, 22) == 1 && BitConverter.ToInt16(wav, 34) == 16, "PCM format");
                for (int i = 0; i < pcm.Length; i++) Check(wav[i + 44] == pcm[i], "Audio corruption");
                bool rejected = false;
                try { WhisperEngine.EncodeWave(new byte[3]); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Incomplete sample accepted");
            });
            Test("Mixed language and ASCII word boundaries are preserved", delegate {
                string text = WhisperEngine.ParseTranscription("{\"segments\":[{\"text\":\"使用Spring\"},{\"text\":\" Boot 与 Redis。\"}]}");
                Check(text == "使用Spring Boot 与 Redis。", "Text corrupted: " + text);
                Check(WhisperEngine.ParseTranscription("{\"segments\":[{\"text\":\" inter\"},{\"text\":\"operability\"}]}") == "interoperability", "Split English word corrupted");
            });
            Test("Silence requires both weak probability and high no-speech score", delegate {
                string text = WhisperEngine.ParseTranscription("{\"text\":\"hallucination\",\"segments\":[{\"text\":\"drop\",\"no_speech_prob\":0.9,\"avg_logprob\":-2},{\"text\":\"保留\",\"no_speech_prob\":0.9,\"avg_logprob\":-0.3}]}");
                Check(text == "保留", "Incorrect silence filter");
                Check(WhisperEngine.ParseTranscription("{\"text\":\"must not return\",\"segments\":[]}") == "", "Filtered text revived");
                Check(WhisperEngine.ParseTranscription("{\"text\":\"fallback\"}") == "fallback", "Missing fallback");
            });
            Test("Invalid JSON and segment structure fail clearly", delegate {
                foreach (string value in new[] { "broken", "[]", "{\"segments\":42}" })
                {
                    bool rejected = false;
                    try { WhisperEngine.ParseTranscription(value); } catch (InvalidOperationException) { rejected = true; }
                    Check(rejected, "Invalid payload accepted");
                }
            });
            Test("Missing model reports the exact executable-relative path and shortcut recovery", delegate {
                string root = Path.Combine(Path.GetTempPath(), "Whisper-missing-model-" + Guid.NewGuid().ToString("N"));
                using (var engine = new WhisperEngine())
                {
                    bool rejected = false;
                    try { engine.StartAsync(root, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (InvalidOperationException error)
                    {
                        rejected = true;
                        Check(error.Message.Contains(Path.Combine(root, "models", "ggml-large-v3-turbo-q5_0.bin")), "Missing model path is ambiguous");
                        Check(error.Message.Contains("快捷方式") && error.Message.Contains("不能只复制 EXE"), "Missing recovery instructions");
                    }
                    Check(rejected, "Missing model accepted");
                }
            });
            Test("Cancelled startup creates no local process", delegate {
                using (var engine = new WhisperEngine())
                using (var abort = new CancellationTokenSource())
                {
                    abort.Cancel();
                    bool cancelled = false;
                    try { engine.StartAsync(Path.GetTempPath(), abort.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled, "Startup ignored cancellation");
                }
            });
            Console.WriteLine("RESULT " + passed + " Whisper engine tests passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
