using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MockInterviewAI;

public static class StreamingTests
{
    private static int passed;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static int Main()
    {
        try
        {
            Run("Manual answer appears before the stream finishes; UTF8 and plain text survive", Manual);
            Run("Automatic JSON streams only the answer, including split Unicode and escaped quotes", Automatic);
            Run("Non-question JSON and quoted field names never become visible answers", NoQuestion);
            Run("Partial JSON holds incomplete escapes and surrogate pairs", JsonBoundaries);
            Run("Cancellation interrupts a silent response body without waiting for another token", Cancel);
            Run("Truncated, malformed and error streams never report a completed answer", InvalidStreams);
            Run("HTTP errors do not expose response bodies", HttpError);
            Run("Custom provider streams use configured endpoint and model in both answer paths", CustomProvider);
            Console.WriteLine("RESULT " + passed + " streaming tests passed; no real API requests sent.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run(string name, Func<Task> test) { test().GetAwaiter().GetResult(); passed++; Console.WriteLine("PASS " + name); }
    private static string Delta(string content, string finish, string reasoning)
    {
        return "data: " + InterviewCore.Json().Serialize(new { choices = new[] { new { delta = new { content = content, reasoning_content = reasoning }, finish_reason = finish } } }) + "\n\n";
    }
    private static void End(FeedStream stream) { stream.Feed(Delta(null, "stop", null) + "data: {\"choices\":[],\"usage\":{}}\n\ndata: [DONE]\n\n"); stream.End(); }
    private static async Task Wait(Task task)
    {
        Check(await Task.WhenAny(task, Task.Delay(3000)) == task, "Operation did not complete promptly");
        await task;
    }
    private static async Task Manual()
    {
        var stream = new FeedStream();
        var first = new TaskCompletionSource<bool>();
        var previews = new List<string>();
        using (var client = new HttpClient(new Handler(stream)))
        {
            stream.Feed(": keepalive\n\n" + Delta(null, null, "never display this") + Delta("**第一", null, null));
            var pending = InterviewCore.SendStreamingAnswerAsync(client, "fake-key", "解释 API", "", "", text => { previews.Add(text); first.TrySetResult(true); }, CancellationToken.None);
            await Wait(first.Task);
            Check(!pending.IsCompleted && previews[0] == "第一", "Answer was buffered until completion or exposed reasoning");
            stream.Feed(Delta("段**。\n\n第二段。", null, null));
            End(stream);
            string answer = await pending;
            Check(answer == "第一段。\n\n第二段。" && previews.Count >= 2, "Chinese text or paragraph boundaries corrupted");
        }
    }
    private static async Task CustomProvider()
    {
        var api = new ApiConfiguration("https://api.example.com/v1", "custom-model", false, true);
        foreach (bool automatic in new[] { false, true })
        {
            var stream = new FeedStream();
            stream.Feed(Delta(automatic ? "{\"is_question\":true,\"question\":\"问题\",\"answer\":\"自定义服务答案\"}" : "自定义服务答案", null, null));
            End(stream);
            string preview = "";
            using (var client = new HttpClient(new Handler(stream, api)))
            {
                string answer = automatic
                    ? (await InterviewCore.SendAutomaticStreamingAsync(client, "fake-key", "问题", "", "", text => preview = text, CancellationToken.None, api)).Answer
                    : await InterviewCore.SendStreamingAnswerAsync(client, "fake-key", "问题", "", "", text => preview = text, CancellationToken.None, api);
                Check(answer == "自定义服务答案" && preview == answer, "Custom provider lost streaming output");
            }
        }
    }
    private static async Task Automatic()
    {
        var stream = new FeedStream();
        var first = new TaskCompletionSource<bool>();
        string latest = "";
        using (var client = new HttpClient(new Handler(stream)))
        {
            stream.Feed(Delta("{\"is_question\":true,\"question\":\"解释转义\",\"answer\":\"第一\\u6", null, null));
            var pending = InterviewCore.SendAutomaticStreamingAsync(client, "fake-key", "解释转义", "", "", text => { latest = text; first.TrySetResult(true); }, CancellationToken.None);
            await Wait(first.Task);
            Check(!pending.IsCompleted && latest == "第一", "Partial Unicode or raw JSON leaked");
            stream.Feed(Delta("bb5\\n引用\\\"Spring Boot\\\"，路径 C:\\\\Temp。\"}", null, null));
            End(stream);
            var answer = await pending;
            Check(answer.IsQuestion && answer.Question == "解释转义", "Question parsing failed");
            Check(answer.Answer == "第一段\n引用\"Spring Boot\"，路径 C:\\Temp。" && latest == answer.Answer, "Escapes were corrupted or metadata was displayed");
        }
    }
    private static async Task NoQuestion()
    {
        var stream = new FeedStream();
        int calls = 0;
        using (var client = new HttpClient(new Handler(stream)))
        {
            stream.Feed(Delta("{\"is_question\":false,\"question\":\"\",\"answer\":\"must not show\"}", null, null)); End(stream);
            var result = await InterviewCore.SendAutomaticStreamingAsync(client, "fake-key", "你好", "", "", text => calls++, CancellationToken.None);
            Check(!result.IsQuestion && calls == 0, "A greeting overwrote the previous answer");
        }
        string preview;
        Check(!AutomaticAnswerPreview.TryRead("{\"question\":\"\\\"is_question\\\":true\",\"answer\":\"wrong\"}", out preview), "A flag inside question text was treated as metadata");
    }
    private static Task JsonBoundaries()
    {
        string preview;
        string prefix = "{\"is_question\":true,\"answer\":\"中文";
        Check(AutomaticAnswerPreview.TryRead(prefix + "\\", out preview) && preview == "中文", "Dangling escape shown");
        Check(AutomaticAnswerPreview.TryRead(prefix + "\\uD83D", out preview) && preview == "中文", "Half a surrogate pair shown");
        Check(AutomaticAnswerPreview.TryRead(prefix + "\\uD83D\\uDE00", out preview) && preview == "中文😀", "Surrogate pair lost");
        Check(AutomaticAnswerPreview.TryRead("{\"answer\":\"完整答案\",\"is_question\":true}", out preview) && preview == "完整答案", "JSON property order assumed");
        return Task.FromResult(0);
    }
    private static async Task Cancel()
    {
        var stream = new FeedStream();
        var first = new TaskCompletionSource<bool>();
        using (var abort = new CancellationTokenSource())
        using (var client = new HttpClient(new Handler(stream)))
        {
            stream.Feed(Delta("已经显示", null, null));
            var pending = InterviewCore.SendStreamingAnswerAsync(client, "fake-key", "解释 API", "", "", text => first.TrySetResult(true), abort.Token);
            await Wait(first.Task);
            abort.Cancel();
            Check(await Task.WhenAny(pending, Task.Delay(3000)) == pending, "Cancellation waited for another server event");
            bool cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && stream.WasDisposed, "Stream cancellation did not release the body");
        }
    }
    private static async Task InvalidStreams()
    {
        foreach (string payload in new[] {
            Delta("未完成", "length", null) + "data: [DONE]\n\n",
            Delta("未完成", null, null),
            Delta("未完成", "stop", null),
            "data: {broken}\n\n",
            "data: {\"error\":{\"message\":\"private-server-body\"}}\n\n"
        })
        {
            var stream = new FeedStream(); stream.Feed(payload); stream.End();
            using (var client = new HttpClient(new Handler(stream)))
            {
                bool failed = false;
                try { await InterviewCore.SendStreamingAnswerAsync(client, "fake-key", "问题", "", "", null, CancellationToken.None); }
                catch (InvalidOperationException error) { failed = true; Check(!error.Message.Contains("private-server-body"), "Error body leaked"); }
                Check(failed, "An interrupted stream was marked complete");
            }
        }
    }
    private static async Task HttpError()
    {
        using (var client = new HttpClient(new Handler(null)))
        {
            bool failed = false;
            try { await InterviewCore.SendStreamingAnswerAsync(client, "fake-key", "问题", "", "", null, CancellationToken.None); }
            catch (InvalidOperationException error) { failed = true; Check(!error.Message.Contains("private-server-body") && error.Message.Contains("Key"), "Unsafe HTTP error"); }
            Check(failed, "HTTP error accepted");
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly FeedStream stream;
        private readonly ApiConfiguration api;
        public Handler(FeedStream stream, ApiConfiguration api = null) { this.stream = stream; this.api = api ?? ApiConfiguration.Default; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            string body = await request.Content.ReadAsStringAsync();
            var root = InterviewCore.Json().DeserializeObject(body) as Dictionary<string, object>;
            Check((bool)root["stream"] && (string)root["model"] == api.ModelName && request.RequestUri.AbsoluteUri == api.EndpointUrl, "Streaming request not configured");
            if (!api.IsDeepSeek) Check(!root.ContainsKey("thinking") && !root.ContainsKey("response_format") && root.ContainsKey("max_completion_tokens"), "Vendor parameters leaked to custom provider");
            Check(request.Headers.Authorization.Parameter == "fake-key" && !body.Contains("fake-key"), "Key leaked into request body");
            return stream == null
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private-server-body") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }
    }
    // Every read returns at most three bytes, including across Chinese UTF8 boundaries.
    private sealed class FeedStream : Stream
    {
        private readonly ConcurrentQueue<byte[]> queue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource abort = new CancellationTokenSource();
        private byte[] active;
        private int offset, disposed;
        private volatile bool ended;
        public bool WasDisposed { get { return disposed != 0; } }
        public void Feed(string value) { queue.Enqueue(Encoding.UTF8.GetBytes(value)); signal.Release(); }
        public void End() { ended = true; signal.Release(); }
        public override async Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken cancellation)
        {
            while (active == null || offset == active.Length)
            {
                if (queue.TryDequeue(out active)) { offset = 0; break; }
                if (ended) return 0;
                await signal.WaitAsync(abort.Token);
            }
            int size = Math.Min(3, Math.Min(count, active.Length - offset));
            Buffer.BlockCopy(active, offset, buffer, start, size); offset += size;
            return size;
        }
        public override int Read(byte[] buffer, int start, int count) { return ReadAsync(buffer, start, count, CancellationToken.None).GetAwaiter().GetResult(); }
        protected override void Dispose(bool disposing) { if (Interlocked.Exchange(ref disposed, 1) == 0) abort.Cancel(); base.Dispose(disposing); }
        public override bool CanRead { get { return !WasDisposed; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long length) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int start, int count) { throw new NotSupportedException(); }
        public override void Flush() { }
    }
}
