using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace MockInterviewAI
{
    public static class AnswerTemplatePolicy
    {
        public const string SourceRoot = @"C:\Users\56526\Documents\deepseek-harness\default-workspace\skills\interview-answer-templates";
        private static readonly string Templates = ReadCore(Path.Combine("references", "templates.md"), "AnswerTemplates.md");

        private static string ReadLocal(string relative)
        {
            try { return File.ReadAllText(Path.Combine(SourceRoot, relative), Encoding.UTF8); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string ReadCore(string relative, string resource)
        {
            string local = ReadLocal(relative);
            if (!String.IsNullOrWhiteSpace(local)) return local;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null) throw new InvalidOperationException("回答模板缺失，请检查程序文件是否完整。");
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        public static string SelectTemplate(string question)
        {
            string q = question ?? "";
            if (Regex.IsMatch(q, "我这样答行不行|帮我改|这段回答怎么样|精修|改进我的回答|refine my answer", RegexOptions.IgnoreCase)) return "REFINE";
            string[,] routes = {
                {"自我介绍|介绍一下你自己|简单聊聊|自我评价|tell me about yourself", "T1"},
                {"主导的项目|最有成就感|简历上.*项目|项目深挖|介绍.*项目", "T2"},
                {"缺点|短板|weakness", "T3b"}, {"优点|优势|strengths", "T3a"},
                {"离职|离开上一家|看机会|为什么想换", "T4"},
                {"为什么选我们|为什么这个岗位|了解我们吗|why.*work.*here", "T5"},
                {"职业规划|未来规划|五年后|想成为什么", "T6"},
                {"薪资|薪酬|期望.*package|给不到怎么办|salary", "T7"},
                {"冲突|难搞的同事|跨部门.*推不动|conflict", "T9"},
                {"失败|犯过的错|后悔的决定|failure", "T10"},
                {"加班|多任务|压力最大|抗压|pressure", "T11"},
                {"还有什么想问|反问|questions for us", "T12"},
                {"学历|空窗期|跳槽频繁|转行|婚育|年龄", "T13"},
                {"不会的题|没有接触过|没接触过|不知道怎么回答", "T15"},
                {"估算|案例估算|定价|估计.*数量|market sizing", "T16"},
                {"还有什么.*补充", "T17"},
                {"讲一次|举一次|行为题|STAR|tell me about a time", "T8"},
                {"原理|机制|区别|定理|算法|信源|信道|率失真|如何.*实现|解释|技术|设计.*系统|explain|how does|difference between", "T14"}
            };
            for (int i = 0; i < routes.GetLength(0); i++)
                if (Regex.IsMatch(q, routes[i, 0], RegexOptions.IgnoreCase)) return routes[i, 1];
            return "FALLBACK";
        }

        public static string BuildInstructions(string question)
        {
            string route = SelectTemplate(question);
            var result = new StringBuilder();
            result.AppendLine("参考 interview-answer-templates 的内部骨架（不显示题型编号）：" + route);
            if (route != "REFINE" && route != "FALLBACK")
            {
                Match section = Regex.Match(Templates, @"(?ms)^## " + Regex.Escape(route) + @"\s[^\n]*\n(?<body>.*?)(?=^## |\z)");
                if (section.Success)
                {
                    foreach (string line in section.Groups["body"].Value.Split('\n'))
                    {
                        if (Regex.IsMatch(line, @"^\*\*(示例|示范|扣分点|追问弹药)")) break;
                        if (line.Contains("禁用") || line.Contains("禁忌") || line.Trim() == "---") continue;
                        result.AppendLine(line.Replace("**", "").Replace("`", ""));
                    }
                }
            }
            else result.AppendLine(route == "REFINE" ? "根据提供的草稿直接给出改好的完整口述答案，保留真实事实。" : "按主问选择骨架：技术题用定义→原理→取舍→应用→边界；项目或行为题用背景→个人任务→关键动作→结果→复盘；其余用结论→证据→取舍→岗位关联。");
            result.AppendLine("口语表达优先：像候选人在现场自然回答，用短句和日常说法，第一句直接回应主问。不说‘下面我从几个方面展开’‘综上所述’，不用赋能、抓手、闭环等空话，不刻意排比，不机械地首先其次最后。段落之间用自然的因果或转折衔接，不能为了口语化堆‘嗯、啊、然后’。");
            result.AppendLine("行为或项目题：参考STAR但不念框架名，背景一两句带过，把篇幅留给我具体做了什么、为什么这样选、结果和学到什么。技术题不要硬套STAR：先用一句话讲清核心，再用一个具体例子说明机制和适用边界；保留必要术语，中英文混用时给自然的中文解释。追问只回答新增重点，精修保留原话的个人语气。没有相关经历时讲假设方案，不能把假设写成我做过。");
            result.AppendLine("只输出可直接口述的2分钟答案正文，全文硬上限700字（含标点），通常控制在450–550字。简单问题更短，复杂问题只保留核心结论、关键机制和一个必要例子或取舍，不展开旁支，也不为凑篇幅重复，按意思自然分段。先在内部检查是否答中问题、能否一口气读顺、是否有具体证据，再输出正文。不输出标题、题型、分析、清单、追问、点评或其他版本。不使用星号加粗或代码围栏。JD和简历仅作事实背景；模板示例绝不能当作候选人经历，缺事实在正文使用【真实项目】【实际指标】等必要占位，不编造。动作突出我个人的贡献，岗位关联只在确实相关时自然带出，不强行以‘所以我适合贵司’结尾。未知公司信息不杜撰。");
            return result.ToString();
        }
    }
}
