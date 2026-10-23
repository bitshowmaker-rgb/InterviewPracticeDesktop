using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MockInterviewAI;

public sealed class FakeHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handle;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) { return Handle(request, cancellation); }
}

public static class CoreTests
{
    private static int passed;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action action) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Assert(rejected, "Expected invalid response to be rejected"); }

    public static int Main(string[] args)
    {
        try
        {
            Test("API configuration normalizes base URLs and rejects unsafe or empty settings", delegate {
                Assert(new ApiConfiguration("https://api.example.com/v1/", " custom-model ", false, true).EndpointUrl == "https://api.example.com/v1/chat/completions", "Base URL not expanded");
                Assert(new ApiConfiguration("http://localhost:1234/v1", "local-model", false, false).EndpointUrl == "http://localhost:1234/v1/chat/completions", "Local API rejected");
                foreach (string address in new[] { "", "file:///C:/secret", "http://api.example.com/v1", "https://key@api.example.com/v1", "https://api.example.com/v1?api_key=secret", "https://api.example.com/v1#secret" })
                    Reject(delegate { new ApiConfiguration(address, "model", false, false); });
                Reject(delegate { new ApiConfiguration("https://api.example.com", " ", false, false); });
                ApiConfiguration migrated = ApiConfiguration.FromSettings(new SavedSettings());
                Assert(migrated.EndpointUrl == InterviewCore.Endpoint && migrated.ModelName == InterviewCore.Model && migrated.JsonMode && !migrated.CompletionTokenLimit, "Existing settings lost default provider");
            });
            Test("Custom providers omit vendor extensions and honor token and JSON capabilities", delegate {
                var api = new ApiConfiguration("https://api.example.com/v1", "custom-model", false, true);
                foreach (string body in new[] { InterviewCore.BuildRequest("问题", "", "", api), InterviewCore.BuildAutomaticRequest("问题", "", "", api) })
                {
                    var root = InterviewCore.Json().DeserializeObject(body) as Dictionary<string, object>;
                    Assert((string)root["model"] == "custom-model" && !root.ContainsKey("thinking") && !root.ContainsKey("response_format"), "Provider-specific fields leaked");
                    Assert(root.ContainsKey("max_completion_tokens") && !root.ContainsKey("max_tokens"), "Wrong token limit parameter");
                }
                var lookalike = new ApiConfiguration("https://api.deepseek.com.example.com", "model", true, false);
                Assert(!lookalike.IsDeepSeek, "Host matching leaked DeepSeek extensions");
            });
            Test("Manual and automatic HTTP requests use the configured model and endpoint", delegate {
                var api = new ApiConfiguration("https://api.example.com/v1", "custom-model", false, false);
                int requests = 0;
                using (var client = new HttpClient(new FakeHandler { Handle = async (request, cancellation) => {
                    requests++;
                    string body = await request.Content.ReadAsStringAsync();
                    Assert(request.RequestUri.AbsoluteUri == api.EndpointUrl && body.Contains("custom-model") && !body.Contains("private-key"), "Configured provider ignored");
                    Assert(request.Headers.Authorization.Parameter == "private-key", "Wrong authorization");
                    string content = requests == 1 ? "成功" : InterviewCore.Json().Serialize(new { is_question = true, question = "问题", answer = "成功" });
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(InterviewCore.Json().Serialize(new { choices = new[] { new { message = new { content = content } } } })) };
                } }))
                {
                    Assert(InterviewCore.SendAsync(client, "private-key", "问题", "", "", CancellationToken.None, api).GetAwaiter().GetResult() == "成功", "Custom manual failed");
                    Assert(InterviewCore.SendAutomaticAsync(client, "private-key", "问题", "", "", CancellationToken.None, api).GetAwaiter().GetResult().IsQuestion, "Custom automatic failed");
                }
                Assert(requests == 2, "Request path missing");
            });
            Test("Request model, Chinese context and instruction separation", delegate {
                string body = InterviewCore.BuildRequest("  如何设计控制系统？  ", "研发\n岗位", "A\\B 与 \"项目\"");
                var root = InterviewCore.Json().DeserializeObject(body) as Dictionary<string, object>;
                Assert((string)root["model"] == "deepseek-flash", "Model mismatch");
                var messages = (object[])root["messages"];
                Assert((string)((Dictionary<string, object>)messages[0])["role"] == "system", "Missing coach message");
                string content = (string)((Dictionary<string, object>)messages[1])["content"];
                Assert(content.Contains("模拟面试问题：如何设计控制系统？") && content.Contains("研发\n岗位") && content.Contains("A\\B 与 \"项目\""), "Context corrupted");
                Assert((string)((Dictionary<string, object>)root["thinking"])["type"] == "disabled", "Thinking mode mismatch");
            });
            Test("Parses visible Chinese content and ignores reasoning", delegate {
                Assert(InterviewCore.ParseAnswer("{\"choices\":[{\"message\":{\"content\":\" 中文答案。 \",\"reasoning_content\":\"hidden\"}}]}") == "中文答案。", "Answer mismatch");
            });
            Test("Answers remove bold markers while retaining the actual text", delegate {
                string original = "5. **面试加分**：可提三者共同思想——用概率统计刻画信息，给出可达性与 converse（逆定理）双向证明框架。";
                Assert(InterviewCore.PlainText(original) == original.Replace("**", ""), "Answer text changed beyond formatting");
                Assert(InterviewCore.PlainText("Python __init__") == "Python __init__", "Technical underscores were incorrectly removed");
            });
            Test("Automatic request separates transcript data and requests structured question detection", delegate {
                var root = InterviewCore.Json().DeserializeObject(InterviewCore.BuildAutomaticRequest("解释 Spring Boot 依赖注入", "JD", "CV")) as Dictionary<string, object>;
                Assert((string)((Dictionary<string, object>)root["response_format"])["type"] == "json_object", "Missing JSON mode");
                var messages = (object[])root["messages"];
                var data = InterviewCore.Json().DeserializeObject((string)((Dictionary<string, object>)messages[1])["content"]) as Dictionary<string, object>;
                Assert((string)data["transcript"] == "解释 Spring Boot 依赖注入" && (string)data["resume"] == "CV", "Transcript was not separated from instructions");
            });
            Test("User template skill routes technical, HR, behavioral and refinement questions", delegate {
                Assert(AnswerTemplatePolicy.SelectTemplate("请做一下自我介绍") == "T1", "Introduction route");
                Assert(AnswerTemplatePolicy.SelectTemplate("讲一个主导的项目") == "T2", "Project route");
                Assert(AnswerTemplatePolicy.SelectTemplate("讲一次跨部门冲突") == "T9", "Conflict route");
                Assert(AnswerTemplatePolicy.SelectTemplate("比较信源编码、信道编码与率失真定理") == "T14", "Technical route");
                Assert(AnswerTemplatePolicy.SelectTemplate("这段回答怎么样，帮我改一下") == "REFINE", "Refinement route");
            });
            Test("Both answer paths allow fuller spoken answers without extra versions", delegate {
                foreach (string body in new[] { InterviewCore.BuildRequest("解释 Redis 机制", "", ""), InterviewCore.BuildAutomaticRequest("解释 Redis 机制", "", "") })
                {
                    var root = InterviewCore.Json().DeserializeObject(body) as Dictionary<string, object>;
                    var messages = (object[])root["messages"];
                    string rules = (string)((Dictionary<string, object>)messages[0])["content"];
                    foreach (string heading in new[] { "【题型】", "【30 秒版】", "【填空清单】", "【追问预备】", "【扣分点】" }) Assert(!rules.Contains(heading), "Obsolete output requested: " + heading);
                    Assert(rules.Contains("2分钟答案正文") && rules.Contains("【实际指标】") && rules.Contains("绝不能当作候选人经历"), "Answer mode or fact boundary missing");
                    Assert(Convert.ToInt32(root["max_tokens"]) <= 1900 && Convert.ToInt32(root["max_tokens"]) >= 1600, "Output budget mismatch");
                    Assert(rules.Contains("硬上限700字") && rules.Contains("450–550字") && rules.Contains("不为凑篇幅重复"), "Answer length policy mismatch");
                    Assert(body.Length < 2600, "Full skill is still being sent with every request");
                }
            });
            Test("Refinement also produces only a two-minute answer", delegate {
                string rules = AnswerTemplatePolicy.BuildInstructions("我这样答行不行，帮我改");
                foreach (string heading in new[] { "【Strengths】", "【Areas for Improvement】", "【Refined Version】", "【Tips】" }) Assert(!rules.Contains(heading), "Refinement commentary still requested");
                Assert(rules.Contains("REFINE") && rules.Contains("2分钟答案正文") && rules.Contains("不使用星号加粗"), "Mode or plain text rule missing");
            });
            Test("Unexpected legacy sections are removed without losing answer placeholders", delegate {
                string formatted = "【题型】T14\n【30 秒版】短答案\n【2 分钟版】\n完整的两分钟正文，项目为【真实项目】。\n\n第二段。\n【填空清单】不要显示\n【追问预备】不要显示\n【扣分点】不要显示";
                Assert(InterviewCore.OnlyTwoMinuteAnswer(formatted) == "完整的两分钟正文，项目为【真实项目】。\n\n第二段。", "Legacy sections leaked into answer");
                Assert(InterviewCore.OnlyTwoMinuteAnswer("正常正文，只有两个自然段。") == "正常正文，只有两个自然段。", "Plain answer changed");
            });
            Test("Question detection rejects malformed flags and preserves no-question state", delegate {
                Func<object, string> response = value => InterviewCore.Json().Serialize(new { choices = new[] { new { message = new { content = InterviewCore.Json().Serialize(value) } } } });
                AutomaticAnswer result = InterviewCore.ParseAutomaticAnswer(response(new { is_question = true, question = "如何使用 Redis？", answer = "**结论**：使用缓存。" }));
                Assert(result.IsQuestion && result.Question == "如何使用 Redis？" && result.Answer == "结论：使用缓存。", "Automatic result mismatch");
                Assert(!InterviewCore.ParseAutomaticAnswer(response(new { is_question = false })).IsQuestion, "Chatter became an answer");
                Reject(delegate { InterviewCore.ParseAutomaticAnswer(response(new { is_question = "false" })); });
                Reject(delegate { InterviewCore.ParseAutomaticAnswer(response(new { is_question = true, question = "", answer = "" })); });
            });
            Test("Automatic HTTP pipeline uses credentials only in the header", delegate {
                var handler = new FakeHandler { Handle = async (request, cancellation) => {
                    string body = await request.Content.ReadAsStringAsync();
                    Assert(request.Headers.Authorization.Parameter == "automatic-test-key" && !body.Contains("automatic-test-key"), "Credential leak");
                    string payload = InterviewCore.Json().Serialize(new { is_question = true, question = "什么是 API？", answer = "**接口**。" });
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(InterviewCore.Json().Serialize(new { choices = new[] { new { message = new { content = payload } } } })) };
                } };
                using (var client = new HttpClient(handler))
                {
                    var result = InterviewCore.SendAutomaticAsync(client, "automatic-test-key", "什么是 API", "", "", CancellationToken.None).GetAwaiter().GetResult();
                    Assert(result.IsQuestion && result.Answer == "接口。", "Automatic pipeline failed");
                }
            });
            Test("Automatic batching waits for a complete utterance and preserves later fragments", delegate {
                var buffer = new AutomaticQuestionBuffer(); var now = DateTime.UtcNow;
                buffer.Add("请解释 Spring Boot", now);
                Assert(buffer.TakeReady(now.AddMilliseconds(400), false) == null, "Triggered during the debounce period");
                buffer.Add("中的 dependency injection。", now.AddMilliseconds(500));
                Assert(buffer.TakeReady(now.AddMilliseconds(1100), false) == null, "Later transcript did not restart the settling period");
                Assert(buffer.TakeReady(now.AddSeconds(3), true) == null, "Triggered while audio or ASR was still active");
                Assert(buffer.TakeReady(now.AddSeconds(70), false) == "请解释 Spring Boot 中的 dependency injection。", "Long utterance was lost or split");
                buffer.Add("下一个问题：比较 Java 与 Python。", now.AddSeconds(71));
                Assert(buffer.TakeReady(now.AddSeconds(73), false).Contains("下一个问题"), "Question arriving during a response was lost");
                buffer.Add("比较 Redis 与 MySQL。", now.AddSeconds(74));
                Assert(buffer.TakeReady(now.AddMilliseconds(74699), false) == null, "A fragment was submitted before settling");
                Assert(buffer.TakeReady(now.AddMilliseconds(74800), true) == null, "A partial question was submitted during ongoing audio or ASR");
                Assert(buffer.TakeReady(now.AddMilliseconds(74800), false) == "比较 Redis 与 MySQL。", "A settled question still incurs the old long delay");
            });
            Test("Automatic repeats are suppressed briefly, retry and C++ distinctions survive", delegate {
                var buffer = new AutomaticQuestionBuffer(); var now = DateTime.UtcNow;
                buffer.Add("如何使用Redis？", now);
                Assert(buffer.TakeReady(now.AddSeconds(2), false) != null, "First question lost");
                buffer.Add("如何使用 Redis", now.AddSeconds(3));
                Assert(buffer.TakeReady(now.AddSeconds(5), false) == null, "Repeated recognition resubmitted");
                buffer.AllowRetry("如何使用Redis"); buffer.Add("如何使用Redis", now.AddSeconds(6));
                Assert(buffer.TakeReady(now.AddSeconds(8), false) != null, "Failure cannot be retried");
                Assert(AutomaticQuestionBuffer.Normalize("解释 C++") != AutomaticQuestionBuffer.Normalize("解释 C#"), "Different languages collapsed");
                buffer.Add("谢谢", now.AddSeconds(9)); Assert(buffer.TakeReady(now.AddSeconds(11), false) == null, "Filler submitted");
                buffer.Add("不会发送的旧内容", now.AddSeconds(12)); buffer.Clear(); Assert(!buffer.HasPending, "Stop failed to clear pending content");
            });
            Test("Spoken answers never exceed 700 characters and preserve short text and complete sentences", delegate {
                string exact = new string('答', 700);
                Assert(InterviewCore.OnlyTwoMinuteAnswer(exact) == exact, "Valid length changed");
                string complete = new string('答', 499) + "。";
                Assert(InterviewCore.OnlyTwoMinuteAnswer(complete + new string('补', 300)) == complete, "Sentence ending was not preserved");
                Assert(InterviewCore.OnlyTwoMinuteAnswer(new string('答', 900)).Length <= 700, "Unpunctuated answer exceeded cap");
                Assert(InterviewCore.OnlyTwoMinuteAnswer("【2 分钟版】\n" + new string('答', 900)).Length <= 700, "Legacy answer bypassed cap");
                Assert(InterviewCore.OnlyTwoMinuteAnswer(new string('答', 698) + "😀" + new string('补', 20)).Length <= 700, "Surrogate boundary exceeded cap");
            });
            Test("Rejects malformed or missing answer payloads", delegate {
                foreach (string payload in new[] { "null", "{}", "[]", "<html>error</html>", "{\"choices\":[]}", "{\"choices\":[null]}", "{\"choices\":[{\"message\":{\"content\":[]}}]}", "{\"choices\":[{\"message\":{\"content\":\" \"}}]}" })
                    Reject(delegate { InterviewCore.ParseAnswer(payload); });
            });
            Test("HTTP pipeline sends key only in Authorization header", delegate {
                var handler = new FakeHandler();
                handler.Handle = async delegate(HttpRequestMessage request, CancellationToken cancellation) {
                    Assert(request.RequestUri.AbsoluteUri == InterviewCore.Endpoint, "Wrong endpoint");
                    Assert(request.Method == HttpMethod.Post, "Wrong method");
                    Assert(request.Headers.Authorization.Parameter == "test-only-key", "Missing authorization");
                    Assert(!(await request.Content.ReadAsStringAsync()).Contains("test-only-key"), "Key leaked to body");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"成功\"}}]}") };
                };
                using (var client = new HttpClient(handler))
                    Assert(InterviewCore.SendAsync(client, "test-only-key", "问题", "", "", CancellationToken.None).GetAwaiter().GetResult() == "成功", "Pipeline failure");
            });
            Test("HTTP failures do not expose server response bodies", delegate {
                foreach (int code in new[] {401, 402, 429, 500})
                {
                    var handler = new FakeHandler { Handle = (request, cancellation) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("private-server-detail") }) };
                    using (var client = new HttpClient(handler))
                    {
                        string error = "";
                        try { InterviewCore.SendAsync(client, "key", "q", "", "", CancellationToken.None).GetAwaiter().GetResult(); }
                        catch (InvalidOperationException ex) { error = ex.Message; }
                        Assert(error == InterviewCore.HttpError(code) && !error.Contains("private-server-detail"), "Unsafe server error");
                    }
                }
            });
            Test("Cancellation stops an active request", delegate {
                var handler = new FakeHandler { Handle = async (request, cancellation) => { await Task.Delay(30000, cancellation); return new HttpResponseMessage(HttpStatusCode.OK); } };
                using (var client = new HttpClient(handler))
                using (var cancel = new CancellationTokenSource())
                {
                    Task<string> active = InterviewCore.SendAsync(client, "key", "q", "", "", cancel.Token);
                    cancel.Cancel();
                    bool cancelled = false;
                    try { active.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                    Assert(cancelled, "Request ignored cancellation");
                }
            });
            Test("Settings without remembered key never persist a plaintext key", delegate {
                string folder = Path.Combine(Path.GetFullPath(args[0]), "desktop-core-settings-test");
                var store = new SettingsStore(folder);
                store.Save(new SavedSettings { RememberKey = false, JobDescription = "中文岗位", Resume = "真实经历", ApiEndpoint = "https://api.example.com/v1/chat/completions", ApiModel = "custom-model", ApiJsonMode = false, ApiCompletionTokenLimit = true, HideTrayIcon = true }, "do-not-store-this-key");
                string json = File.ReadAllText(Path.Combine(folder, "settings.json"));
                Assert(!json.Contains("do-not-store-this-key"), "Plaintext credential persisted");
                string key, notice;
                var saved = store.Load(out key, out notice);
                Assert(key == "" && saved.JobDescription == "中文岗位" && saved.Resume == "真实经历", "Settings roundtrip failed");
                Assert(saved.ApiModel == "custom-model" && !ApiConfiguration.FromSettings(saved).JsonMode && saved.ApiCompletionTokenLimit && saved.HideTrayIcon == true, "API/tray settings not persisted");
                store.Save(new SavedSettings { RememberKey = false, JobDescription = "更新岗位", Resume = "" }, "second-key");
                Assert(store.Load(out key, out notice).JobDescription == "更新岗位", "Atomic replacement failed");
            });
            Console.WriteLine("RESULT " + passed + " tests passed; no real API requests sent.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
