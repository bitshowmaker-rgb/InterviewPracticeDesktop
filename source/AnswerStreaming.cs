using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MockInterviewAI
{
    public static partial class InterviewCore
    {
        public static async Task<string> AskStreamingAsync(string key, string question, string jd, string resume, Action<string> progress, CancellationToken cancellation)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
                return await SendStreamingAnswerAsync(client, key, question, jd, resume, progress, cancellation).ConfigureAwait(false);
        }

        public static async Task<AutomaticAnswer> DetectAndAnswerStreamingAsync(string key, string transcript, string jd, string resume, Action<string> progress, CancellationToken cancellation)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
                return await SendAutomaticStreamingAsync(client, key, transcript, jd, resume, progress, cancellation).ConfigureAwait(false);
        }

        internal static async Task<string> SendStreamingAnswerAsync(HttpClient client, string key, string question, string jd, string resume, Action<string> progress, CancellationToken cancellation)
        {
            string raw = await ReceiveAnswerStream(client, key, BuildRequest(question, jd, resume), delegate(string content) {
                if (progress != null) progress(OnlyTwoMinuteAnswer(content));
            }, cancellation).ConfigureAwait(false);
            string answer = OnlyTwoMinuteAnswer(raw);
            if (String.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("服务没有返回有效的参考答案，请稍后重试。");
            return answer;
        }

        internal static async Task<AutomaticAnswer> SendAutomaticStreamingAsync(HttpClient client, string key, string transcript, string jd, string resume, Action<string> progress, CancellationToken cancellation)
        {
            string raw = await ReceiveAnswerStream(client, key, BuildAutomaticRequest(transcript, jd, resume), delegate(string content) {
                string preview;
                if (progress != null && AutomaticAnswerPreview.TryRead(content, out preview)) progress(OnlyTwoMinuteAnswer(preview));
            }, cancellation).ConfigureAwait(false);
            // The final object still goes through the same strict question/answer validation.
            return ParseAutomaticAnswer(Json().Serialize(new { choices = new[] { new { message = new { content = raw } } } }));
        }

        // DeepSeek Chat Completions SSE: delta.content only, terminated by [DONE].
        // https://api-docs.deepseek.com/api/create-chat-completion/
        private static async Task<string> ReceiveAnswerStream(HttpClient client, string key, string requestBody, Action<string> progress, CancellationToken cancellation)
        {
            var body = Json().DeserializeObject(requestBody) as Dictionary<string, object>;
            body["stream"] = true;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(75));
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Trim());
                request.Content = new StringContent(Json().Serialize(body), Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(HttpError((int)response.StatusCode));
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (deadline.Token.Register(delegate { stream.Dispose(); }))
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024))
                        {
                            var decoder = new AnswerStreamDecoder(progress);
                            var data = new StringBuilder();
                            while (!decoder.Done)
                            {
                                deadline.Token.ThrowIfCancellationRequested();
                                string line = await reader.ReadLineAsync().ConfigureAwait(false);
                                deadline.Token.ThrowIfCancellationRequested();
                                if (line == null || line.Length == 0)
                                {
                                    if (data.Length > 0) { decoder.AddEvent(data.ToString()); data.Clear(); }
                                    if (line == null) break;
                                }
                                else if (line.StartsWith("data:", StringComparison.Ordinal))
                                {
                                    string part = line.Substring(5);
                                    if (part.StartsWith(" ", StringComparison.Ordinal)) part = part.Substring(1);
                                    if (data.Length > 0) data.Append('\n');
                                    data.Append(part);
                                    if (data.Length > 2097152) throw new InvalidOperationException("流式回答内容异常，请重新生成。");
                                }
                            }
                            return decoder.Complete();
                        }
                    }
                }
                catch (Exception error)
                {
                    if (!(error is OperationCanceledException) && !(error is ObjectDisposedException) && !(error is IOException)) throw;
                    cancellation.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) throw new InvalidOperationException("回答生成超时，当前显示的内容尚未完成，请重新生成。");
                    throw;
                }
            }
        }
    }

    internal sealed class AnswerStreamDecoder
    {
        private readonly Action<string> progress;
        private readonly StringBuilder content = new StringBuilder();
        private bool stopped;
        public bool Done { get; private set; }
        public AnswerStreamDecoder(Action<string> progress) { this.progress = progress; }
        public void AddEvent(string data)
        {
            if (data.Trim() == "[DONE]") { Done = true; return; }
            try
            {
                var root = InterviewCore.Json().DeserializeObject(data) as Dictionary<string, object>;
                object value;
                if (root == null || root.ContainsKey("error") || !root.TryGetValue("choices", out value)) throw new FormatException();
                var choices = value as object[];
                if (choices == null) throw new FormatException();
                if (choices.Length == 0) return; // Usage-only event.
                var choice = choices[0] as Dictionary<string, object>;
                if (choice == null) throw new FormatException();
                if (choice.TryGetValue("finish_reason", out value) && value != null)
                {
                    if ((value as string) != "stop") throw new InvalidOperationException("回答生成被中断，当前显示的内容尚未完成，请重新生成。");
                    stopped = true;
                }
                if (choice.TryGetValue("delta", out value))
                {
                    var delta = value as Dictionary<string, object>;
                    if (delta == null) throw new FormatException();
                    if (delta.TryGetValue("content", out value) && value != null)
                    {
                        if (!(value is string)) throw new FormatException();
                        content.Append((string)value);
                        if (content.Length > 2097152) throw new FormatException();
                        if (progress != null && ((string)value).Length > 0) progress(content.ToString());
                    }
                }
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException) && !(error is FormatException)) throw;
                throw new InvalidOperationException("流式回答格式异常，当前内容尚未完成，请重新生成。");
            }
        }
        public string Complete()
        {
            if (!Done || !stopped || content.Length == 0) throw new InvalidOperationException("回答连接中断，当前显示的内容尚未完成，请重新生成。");
            return content.ToString();
        }
    }

    // Reads only top-level answer text after an explicit true question flag.
    // Incomplete escape sequences are held until the next content delta arrives.
    internal static class AutomaticAnswerPreview
    {
        public static bool TryRead(string json, out string answer)
        {
            answer = null;
            bool question = false;
            int i = 0;
            SkipSpace(json, ref i);
            if (i >= json.Length || json[i++] != '{') return false;
            while (i < json.Length)
            {
                SkipSpace(json, ref i);
                if (i >= json.Length || json[i] == '}') break;
                string key, value; bool complete;
                if (!ReadString(json, ref i, out key, out complete) || !complete) return false;
                SkipSpace(json, ref i);
                if (i >= json.Length || json[i++] != ':') return false;
                SkipSpace(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == '"')
                {
                    if (!ReadString(json, ref i, out value, out complete)) return false;
                    if (key == "answer") answer = value;
                    if (!complete) break;
                }
                else
                {
                    int start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != '}')
                    {
                        if (json[i] == '{' || json[i] == '[') return false;
                        i++;
                    }
                    if (i == json.Length) break;
                    string token = json.Substring(start, i - start).Trim();
                    if (key == "is_question") question = token == "true";
                }
                SkipSpace(json, ref i);
                if (i < json.Length && json[i] == ',') i++;
                else break;
            }
            return question && !String.IsNullOrEmpty(answer);
        }
        private static void SkipSpace(string json, ref int i) { while (i < json.Length && Char.IsWhiteSpace(json[i])) i++; }
        private static bool ReadString(string json, ref int i, out string value, out bool complete)
        {
            value = ""; complete = false;
            if (i >= json.Length || json[i++] != '"') return false;
            var text = new StringBuilder();
            while (i < json.Length)
            {
                char ch = json[i++];
                if (ch == '"') { complete = true; break; }
                if (ch < 32) return false;
                if (ch == '\\')
                {
                    if (i >= json.Length) break;
                    ch = json[i++];
                    switch (ch)
                    {
                        case '"': case '\\': case '/': break;
                        case 'n': ch = '\n'; break;
                        case 'r': ch = '\r'; break;
                        case 't': ch = '\t'; break;
                        case 'b': ch = '\b'; break;
                        case 'f': ch = '\f'; break;
                        case 'u':
                            if (json.Length - i < 4) { i = json.Length; continue; }
                            int code;
                            if (!Int32.TryParse(json.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) return false;
                            ch = (char)code; i += 4; break;
                        default: return false;
                    }
                }
                text.Append(ch);
            }
            if (text.Length > 0 && Char.IsHighSurrogate(text[text.Length - 1])) text.Length--;
            value = text.ToString();
            return true;
        }
    }
}
