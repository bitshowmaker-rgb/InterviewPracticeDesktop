using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MockInterviewAI;

public static class VisionRequestTests
{
    public static int Main()
    {
        try
        {
            CheckImageRequest(32, false);
            CheckImageRequest(1024, true);
            Console.WriteLine("PASS: small and large PNG requests round-trip through a local HTTP server.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static byte[] MakePng(int size)
    {
        using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (var output = new MemoryStream())
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try
            {
                var pixels = new byte[data.Stride * size];
                new Random(1234).NextBytes(pixels);
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally { bitmap.UnlockBits(data); }
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
    }

    private static void CheckImageRequest(int size, bool large)
    {
        byte[] png = MakePng(size);
        string expectedUri = "data:image/png;base64," + Convert.ToBase64String(png);
        if (large && expectedUri.Length <= 2097152) throw new Exception("Fixture does not exceed the old JSON limit.");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(delegate
            {
                using (var connection = listener.AcceptTcpClient())
                using (var stream = connection.GetStream())
                {
                    stream.ReadTimeout = 10000;
                    var header = new StringBuilder();
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        int value = stream.ReadByte();
                        if (value < 0 || header.Length > 65536) throw new Exception("Invalid HTTP headers.");
                        header.Append((char)value);
                    }
                    int length = 0;
                    foreach (string line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = Int32.Parse(line.Substring("Content-Length:".Length).Trim());
                    if (length <= 0 || length > 16000000) throw new Exception("Unexpected request size.");
                    if (header.ToString().IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        byte[] interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        stream.Write(interim, 0, interim.Length);
                    }
                    byte[] body = new byte[length];
                    int offset = 0;
                    while (offset < length)
                    {
                        int read = stream.Read(body, offset, length - offset);
                        if (read == 0) throw new Exception("Incomplete request.");
                        offset += read;
                    }
                    var json = new JavaScriptSerializer { MaxJsonLength = 16000000 };
                    var request = (Dictionary<string, object>)json.DeserializeObject(Encoding.UTF8.GetString(body));
                    var messages = (object[])request["messages"];
                    var user = (Dictionary<string, object>)messages[1];
                    var content = (object[])user["content"];
                    var image = (Dictionary<string, object>)content[1];
                    var imageUrl = (Dictionary<string, object>)image["image_url"];
                    if ((string)imageUrl["url"] != expectedUri) throw new Exception("PNG data was truncated or changed.");
                    if ((string)request["model"] != "vision-test" || (int)request["max_tokens"] != 1234)
                        throw new Exception("Vision configuration was lost.");
                    byte[] answer = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"image received\"}}]}");
                    byte[] responseHeader = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + answer.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(responseHeader, 0, responseHeader.Length);
                    stream.Write(answer, 0, answer.Length);
                    Console.WriteLine("Verified PNG bytes: " + png.Length + "; request bytes: " + length);
                }
            });
            var api = new ApiConfiguration("http://127.0.0.1:" + port, "chat-test", true, false, 2000, 1234);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                string answer = InterviewCore.AskVisionAsync("local-test-key", "read this image", png, timeout.Token, api, "vision-test", "local test").GetAwaiter().GetResult();
                if (answer != "image received") throw new Exception("Response was not parsed correctly.");
            }
            server.GetAwaiter().GetResult();
        }
        finally { listener.Stop(); }
    }
}
