using System;
using System.Collections.Generic;
using System.IO;
using MockInterviewAI;

// Pure in-memory PCM tests: no device, playback, model, network or real audio is used.
public static class TranscriptionTests
{
    private const int FrameBytes = 640;
    private static int passed;

    public static int Main()
    {
        try
        {
            Run("500 ms silence closes speech with leading context", SilenceClosesPhrase);
            Run("A short mid-sentence pause preserves one complete phrase", ShortPausePreservesPhrase);
            Run("Four second previews preserve the entire confirmed phrase and reset per phrase", PreviewSnapshotsPreserveContext);
            Run("Stale or backlogged previews are skipped without replacing confirmed speech", StalePreviewsAreSkipped);
            Run("wall-clock tick closes speech when loopback stops delivering data", IdleTickClosesPhrase);
            Run("RMS threshold rejects low level sound and short transients", RmsAndTransientThresholds);
            Run("arbitrary callback boundaries preserve all PCM frames", CallbackBoundariesPreservePcm);
            Run("16 second chunks remain bounded and retain overlap", MaximumChunkAndOverlap);
            Run("overlap flags distinguish continuous chunks from repeated ordinary phrases", OverlapFlagsDistinguishOrdinaryPhrases);
            Run("overlap context expires when actual or callback-free silence follows a full chunk", OverlapExpiresAfterPause);
            Run("continuous input continues beyond 60 seconds", ContinuousInputHasNoMinuteLimit);
            Run("Finish flushes a final aligned tail exactly once and resets", FinishFlushesAndResets);
            Run("Chinese and English transcript overlap is removed conservatively", TranscriptOverlap);
            Console.WriteLine("PASS " + passed + " transcription tests; synthetic PCM only; no audio played or saved");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            return 1;
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static PcmSpeechSegmenter Collect(List<byte[]> segments)
    {
        var result = new PcmSpeechSegmenter();
        result.SegmentReady += delegate(byte[] pcm) { segments.Add(pcm); };
        return result;
    }

    // Exactly 20 ms of 1 kHz sine PCM16 at 16 kHz. Sine amplitude / sqrt(2) is RMS.
    private static byte[] Tone(int amplitude)
    {
        var frame = new byte[FrameBytes];
        for (int sample = 0; sample < FrameBytes / 2; sample++)
        {
            short value = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * 1000 * sample / 16000));
            frame[sample * 2] = (byte)(value & 255);
            frame[sample * 2 + 1] = (byte)((value >> 8) & 255);
        }
        return frame;
    }

    private static void Frames(PcmSpeechSegmenter target, byte[] frame, int count)
    { for (int i = 0; i < count; i++) target.Add(frame); }

    private static void SilenceClosesPhrase()
    {
        var segments = new List<byte[]>();
        var segmenter = Collect(segments);
        Frames(segmenter, new byte[FrameBytes], 25);
        Frames(segmenter, Tone(1800), 12);
        Frames(segmenter, new byte[FrameBytes], 24);
        Assert(segments.Count == 0, "A pause shorter than 500 ms must not close a phrase.");
        segmenter.Add(new byte[FrameBytes]);
        Assert(segments.Count == 1, "500 ms of silence should close one phrase.");
        Assert(segments[0].Length == (14 + 12 + 25) * FrameBytes, "Phrase must contain bounded leading context, speech and the pause.");
        for (int i = 0; i < 14 * FrameBytes; i++) Assert(segments[0][i] == 0, "Leading context was corrupted.");
        Frames(segmenter, new byte[FrameBytes], 100);
        segmenter.Finish();
        Assert(segments.Count == 1, "Silence alone must not create a transcript segment.");
    }

    private static void ShortPausePreservesPhrase()
    {
        var segments = new List<byte[]>();
        var segmenter = Collect(segments);
        Frames(segmenter, Tone(1800), 30);
        Frames(segmenter, new byte[FrameBytes], 20);
        Assert(segments.Count == 0, "A 400 ms word-finding pause should not split the phrase.");
        Frames(segmenter, Tone(1800), 30);
        Frames(segmenter, new byte[FrameBytes], 25);
        Assert(segments.Count == 1 && segments[0].Length == 105 * FrameBytes, "A resumed phrase must preserve both sides of a short pause.");
    }

    private static void PreviewSnapshotsPreserveContext()
    {
        var segmenter = new PcmSpeechSegmenter();
        var previews = new List<byte[]>();
        var confirmed = new List<byte[]>();
        segmenter.PreviewReady += previews.Add;
        segmenter.SegmentReady += confirmed.Add;
        Frames(segmenter, Tone(1800), 199);
        Assert(previews.Count == 0 && confirmed.Count == 0, "Speech was cut before four seconds.");
        segmenter.Add(Tone(1800));
        Assert(previews.Count == 1 && previews[0].Length == 4 * 32000 && segmenter.IsSpeechInProgress, "Preview prematurely ended the utterance.");
        Frames(segmenter, Tone(1800), 200);
        Assert(previews.Count == 2 && previews[1].Length == 8 * 32000 && confirmed.Count == 0, "Preview lost earlier context.");
        Frames(segmenter, new byte[FrameBytes], 25);
        Assert(confirmed.Count == 1 && confirmed[0].Length == 425 * FrameBytes, "Preview consumed audio from the confirmed phrase.");
        for (int i = 0; i < previews[1].Length; i++) Assert(previews[1][i] == confirmed[0][i], "Preview changed confirmed PCM.");
        Frames(segmenter, Tone(1800), 199);
        Assert(previews.Count == 2, "Preview clock did not reset for the next phrase.");
        segmenter.Add(Tone(1800));
        Assert(previews.Count == 3, "The next phrase did not get its own preview.");
        segmenter.Finish();
    }

    private static void StalePreviewsAreSkipped()
    {
        Assert(SpeakerTranscriptionSession.IsCurrentPreview(1, 1, 1), "A current preview was rejected.");
        Assert(!SpeakerTranscriptionSession.IsCurrentPreview(1, 2, 1), "An old preview could replace a newly confirmed utterance.");
        Assert(!SpeakerTranscriptionSession.IsCurrentPreview(1, 1, 2), "Optional previews could delay queued speech.");
    }

    private static void IdleTickClosesPhrase()
    {
        var segments = new List<byte[]>();
        var segmenter = Collect(segments);
        Frames(segmenter, Tone(1800), 20);
        segmenter.Tick(DateTime.MinValue);
        Assert(segments.Count == 0, "An early tick must not close current speech.");
        // No Add call follows speech: a silent render endpoint may send no callbacks at all.
        segmenter.Tick(DateTime.UtcNow.AddSeconds(2));
        Assert(segments.Count == 1 && segments[0].Length == 20 * FrameBytes, "An idle tick must flush the pending phrase without synthetic silence.");
        segmenter.Tick(DateTime.UtcNow.AddSeconds(3));
        segmenter.Finish();
        Assert(segments.Count == 1, "Repeated idle ticks must not emit the same phrase again.");
    }

    private static void RmsAndTransientThresholds()
    {
        var segments = new List<byte[]>();
        var segmenter = Collect(segments);
        Frames(segmenter, Tone(160), 50); // RMS below 0.0035.
        Frames(segmenter, new byte[FrameBytes], 25);
        segmenter.Finish();
        Assert(segments.Count == 0, "Sub-threshold RMS audio must not count as speech.");
        Frames(segmenter, Tone(1800), 11);
        Frames(segmenter, new byte[FrameBytes], 25);
        Assert(segments.Count == 0, "A 220 ms transient must be discarded.");
        Frames(segmenter, Tone(164), 12); // RMS just above 0.0035, includes signed negative samples.
        Frames(segmenter, new byte[FrameBytes], 25);
        Assert(segments.Count == 1, "240 ms of above-threshold speech should survive filtering.");
    }

    private static void CallbackBoundariesPreservePcm()
    {
        byte[] input;
        using (var buffer = new MemoryStream())
        {
            for (int i = 0; i < 20; i++)
            {
                byte[] frame = Tone(1500 + i * 11);
                buffer.Write(frame, 0, frame.Length);
            }
            buffer.Write(new byte[25 * FrameBytes], 0, 25 * FrameBytes);
            input = buffer.ToArray();
        }
        var segments = new List<byte[]>();
        var segmenter = Collect(segments);
        int[] sizes = { 1, 639, 3, 1279, 777, 4, 2001 };
        for (int offset = 0, part = 0; offset < input.Length; part++)
        {
            int length = Math.Min(sizes[part % sizes.Length], input.Length - offset);
            byte[] chunk = new byte[length];
            Buffer.BlockCopy(input, offset, chunk, 0, length);
            segmenter.Add(chunk);
            offset += length;
        }
        segmenter.Finish();
        Assert(segments.Count == 1 && Equal(input, segments[0]), "Splitting callback buffers, including between PCM sample bytes, must not drop or duplicate audio.");
    }

    private static void MaximumChunkAndOverlap()
    {
        var segments = new List<byte[]>();
        var segmenter = Collect(segments);
        Frames(segmenter, Tone(1800), 799);
        Assert(segments.Count == 0, "A chunk shorter than 116 seconds should remain open during nonstop speech.");
        segmenter.Add(Tone(1800));
        Assert(segments.Count == 1 && segments[0].Length == 16000 * 2 * 16, "116 seconds of nonstop speech must emit a bounded chunk.");
        Frames(segmenter, Tone(1800), 20);
        segmenter.Finish();
        Assert(segments.Count == 2, "Speech following a full chunk must remain available for the final flush.");
        int overlapBytes = segments[1].Length - 20 * FrameBytes;
        Assert(overlapBytes >= 14 * FrameBytes && overlapBytes <= 15 * FrameBytes, "Adjacent chunks need a short overlap to preserve words across the boundary.");
        for (int i = 0; i < overlapBytes; i++)
            Assert(segments[0][segments[0].Length - overlapBytes + i] == segments[1][i], "Overlap must come from the prior chunk tail.");
    }

    private static void ContinuousInputHasNoMinuteLimit()
    {
        var segments = new List<byte[]>();
        var segmenter = Collect(segments);
        const int frameCount = 3500; // 70 seconds of synthetic uninterrupted audio.
        Frames(segmenter, Tone(1800), frameCount);
        Assert(segments.Count == 4, "Continuous audio should emit successive bounded chunks beyond one minute.");
        segmenter.Finish();
        Assert(segments.Count == 5, "The post-minute tail must be flushed after stopping.");
        int total = 0;
        foreach (byte[] segment in segments)
        {
            Assert(segment.Length > 0 && segment.Length <= 16 * 16000 * 2, "Continuous chunk size exceeded the 16 second bound.");
            total += segment.Length;
        }
        Assert(total >= frameCount * FrameBytes && total <= (frameCount + 4 * 15) * FrameBytes, "Continuous audio was lost or duplicated beyond the intentional overlap.");
    }

    private static void OverlapFlagsDistinguishOrdinaryPhrases()
    {
        var segmenter = new PcmSpeechSegmenter();
        var flags = new List<bool>();
        segmenter.SegmentReady += delegate(byte[] pcm) { flags.Add(segmenter.SegmentHasOverlap); };
        Frames(segmenter, Tone(1800), 800);
        Frames(segmenter, Tone(1800), 20);
        segmenter.Finish();
        Assert(flags.Count == 2 && !flags[0] && flags[1], "The first maximum chunk has no overlap; its continuous successor does.");

        var ordinary = new PcmSpeechSegmenter();
        var ordinaryFlags = new List<bool>();
        string previous = "", accumulated = "";
        const string repeated = "请再次确认这个结果。";
        ordinary.SegmentReady += delegate(byte[] pcm) {
            ordinaryFlags.Add(ordinary.SegmentHasOverlap);
            string text = ordinary.SegmentHasOverlap ? TranscriptText.RemoveOverlap(previous, repeated) : repeated;
            previous += text;
            accumulated += text;
        };
        for (int phrase = 0; phrase < 2; phrase++)
        {
            Frames(ordinary, Tone(1800), 12);
            Frames(ordinary, new byte[FrameBytes], 25);
        }
        Assert(ordinaryFlags.Count == 2 && !ordinaryFlags[0] && !ordinaryFlags[1], "Ordinary phrases separated by a pause must not be marked overlapping.");
        Assert(accumulated == repeated + repeated, "A genuine repeated sentence must not disappear through overlap removal.");
    }

    private static void OverlapExpiresAfterPause()
    {
        var segmenter = new PcmSpeechSegmenter();
        var flags = new List<bool>();
        segmenter.SegmentReady += delegate(byte[] pcm) { flags.Add(segmenter.SegmentHasOverlap); };
        Frames(segmenter, Tone(1800), 800);
        // This completely pushes the retained 300 ms of old speech out of leading context.
        Frames(segmenter, new byte[FrameBytes], 25);
        Frames(segmenter, Tone(1800), 12);
        segmenter.Finish();
        Assert(flags.Count == 2 && !flags[1], "A new phrase after retained overlap has been replaced by silence must not trigger transcript deduplication.");

        var idle = new PcmSpeechSegmenter();
        var idleFlags = new List<bool>();
        var lengths = new List<int>();
        idle.SegmentReady += delegate(byte[] pcm) { idleFlags.Add(idle.SegmentHasOverlap); lengths.Add(pcm.Length); };
        Frames(idle, Tone(1800), 800);
        idle.Tick(DateTime.UtcNow.AddSeconds(2));
        Frames(idle, Tone(1800), 12);
        idle.Finish();
        Assert(idleFlags.Count == 2 && !idleFlags[1], "A long callback-free pause should expire overlap after a full chunk.");
        Assert(lengths[1] == 12 * FrameBytes, "A new phrase after idle silence must not retain stale speech from the previous chunk.");
    }

    private static void FinishFlushesAndResets()
    {
        var segments = new List<byte[]>();
        var segmenter = Collect(segments);
        Frames(segmenter, Tone(1800), 12);
        byte[] partialFrame = { 1, 2, 3, 4, 5, 6, 7 };
        segmenter.Add(partialFrame);
        segmenter.Finish();
        Assert(segments.Count == 1 && segments[0].Length == 12 * FrameBytes + 6, "Finish must retain complete PCM samples from a partial frame and omit an incomplete sample.");
        for (int i = 0; i < 6; i++) Assert(segments[0][12 * FrameBytes + i] == partialFrame[i], "Final PCM tail changed.");
        segmenter.Finish();
        Assert(segments.Count == 1, "Finish should be idempotent.");
        Frames(segmenter, Tone(1800), 12);
        segmenter.Finish();
        Assert(segments.Count == 2 && segments[1].Length == 12 * FrameBytes, "A completed segmenter should not carry stale bytes into a new phrase.");
        var shortSegments = new List<byte[]>();
        var shortSegmenter = Collect(shortSegments);
        Frames(shortSegmenter, Tone(1800), 11);
        shortSegmenter.Finish();
        Assert(shortSegments.Count == 0, "Finish must apply the transient filter to a short final sound.");
    }

    private static void TranscriptOverlap()
    {
        Assert(TranscriptText.RemoveOverlap("接下来介绍异步消息处理", "异步消息处理，以及失败重试。") == "，以及失败重试。", "Chinese chunk overlap was repeated.");
        Assert(TranscriptText.RemoveOverlap("我们使用 WebSocket", "websocket and REST API 配合。") == "and REST API 配合。", "Mixed Chinese and English overlap should ignore ASCII case.");
        Assert(TranscriptText.RemoveOverlap("Alpha beta gamma", "beta gamma delta") == "delta", "The longest matching overlap should be removed.");
        Assert(TranscriptText.RemoveOverlap("完整重复句子", "完整重复句子") == "", "A duplicate chunk should not produce another transcript.");
        Assert(TranscriptText.RemoveOverlap("API", "API 调用") == "API 调用", "Short repeated words must be kept to avoid excessive deletion.");
        Assert(TranscriptText.RemoveOverlap("旧内容", "  全新内容  ") == "全新内容", "Nonoverlapping text should be trimmed and retained.");
        Assert(TranscriptText.RemoveOverlap(null, "  hello  ") == "hello", "Null history should be supported.");
        Assert(TranscriptText.RemoveOverlap("previous", null) == "", "A missing new transcript should be empty.");
    }

    private static bool Equal(byte[] first, byte[] second)
    {
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
        return true;
    }
}
