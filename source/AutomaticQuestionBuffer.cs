using System;
using System.Collections.Generic;
using System.Text;

namespace MockInterviewAI
{
    // Called only on the UI thread. Wait for both audio and ASR to settle.
    public sealed class AutomaticQuestionBuffer
    {
        private readonly StringBuilder pending = new StringBuilder();
        private readonly Dictionary<string, DateTime> recent = new Dictionary<string, DateTime>();
        private DateTime lastPart;
        public bool HasPending { get { return pending.Length > 0; } }
        public void Add(string text, DateTime now)
        {
            if (String.IsNullOrWhiteSpace(text)) return;
            if (pending.Length > 0) pending.Append(' ');
            pending.Append(text.Trim());
            lastPart = now;
        }
        public string TakeReady(DateTime now, bool audioOrAsrBusy)
        {
            if (!HasPending || audioOrAsrBusy || now - lastPart < TimeSpan.FromMilliseconds(700)) return null;
            string text = pending.ToString().Trim();
            pending.Clear();
            string normalized = Normalize(text);
            if (normalized.Length < 2 || normalized == "好的" || normalized == "谢谢" || normalized == "嗯嗯" || normalized == "ok" || normalized == "thanks") return null;
            if (text.Length > 12000) throw new InvalidOperationException("连续内容过长，未自动提交。请缩短语音或选中问题后重新生成。");
            var expired = new List<string>();
            foreach (var item in recent) if (now - item.Value > TimeSpan.FromMinutes(2)) expired.Add(item.Key);
            foreach (string item in expired) recent.Remove(item);
            if (recent.ContainsKey(normalized)) return null;
            recent[normalized] = now;
            return text;
        }
        public void Clear() { pending.Clear(); }
        public void AllowRetry(string text) { recent.Remove(Normalize(text)); }
        public static string Normalize(string text)
        {
            var result = new StringBuilder();
            foreach (char value in text ?? "")
                if (Char.IsLetterOrDigit(value) || value == '+' || value == '#') result.Append(Char.ToLowerInvariant(value));
            return result.ToString();
        }
    }
}
