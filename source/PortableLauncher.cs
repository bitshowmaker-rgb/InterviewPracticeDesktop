using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Wallpaper Engine Launcher")]
[assembly: AssemblyVersion("5.5.0.0")]
internal static class PortableLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        string destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewPracticeDesktop");
        if (args.Length == 2 && args[0] == "--extract-only")
        {
            try { Extract(Path.GetFullPath(args[1]), null); return 0; }
            catch { return 1; }
        }
        Application.EnableVisualStyles();
        var form = new Form { Text = "Wallpaper Engine · 首次启动准备", Width = 480, Height = 170, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
        var label = new Label { Left = 20, Top = 18, Width = 420, Height = 40, Text = "正在解压语音模型和运行库，请稍候…" };
        var bar = new ProgressBar { Left = 20, Top = 70, Width = 420, Height = 22 };
        form.Controls.Add(label); form.Controls.Add(bar);
        bool finished = false;
        form.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!finished) e.Cancel = true; };
        form.Shown += async delegate {
            try
            {
                await Task.Run(delegate { Extract(destination, percent => form.BeginInvoke(new Action(delegate { bar.Value = percent; }))); });
                Process.Start(new ProcessStartInfo(Path.Combine(destination, "Wallpaper Engine.exe")) { WorkingDirectory = destination, UseShellExecute = true });
            }
            catch (Exception error) { MessageBox.Show(form, "启动准备失败：" + error.Message + "\n请检查磁盘空间，并关闭已打开的应用后重试。", "Wallpaper Engine"); }
            finally { finished = true; form.Close(); }
        };
        Application.Run(form);
        return 0;
    }

    private static void Extract(string destination, Action<int> progress)
    {
        string executable = Assembly.GetExecutingAssembly().Location;
        string stamp = new FileInfo(executable).Length + ":" + File.GetLastWriteTimeUtc(executable).Ticks;
        string marker = Path.Combine(destination, ".package-version");
        if (File.Exists(marker) && File.ReadAllText(marker) == stamp &&
            File.Exists(Path.Combine(destination, "Wallpaper Engine.exe")) &&
            File.Exists(Path.Combine(destination, "NAudio.Core.dll")) &&
            File.Exists(Path.Combine(destination, "models", "ggml-large-v3-turbo.bin"))) return;
        using (var file = File.OpenRead(Assembly.GetExecutingAssembly().Location))
        {
            file.Seek(-16, SeekOrigin.End);
            var footer = new BinaryReader(file, Encoding.UTF8, true);
            string signature = Encoding.ASCII.GetString(footer.ReadBytes(8));
            if (signature != "IPDZIP01" && signature != "IPDZIP02") throw new InvalidDataException("运行包不完整，请重新下载。");
            long offset = footer.ReadInt64();
            if (offset < 0 || offset >= file.Length - 16) throw new InvalidDataException("运行包数据无效。");
            string dataFile = executable + ".data";
            if (signature == "IPDZIP02" && !File.Exists(dataFile))
                throw new FileNotFoundException("请把 Release 中的 EXE 和同名 .exe.data 文件都下载到同一个文件夹，再双击 EXE。", dataFile);
            using (var continuation = signature == "IPDZIP02" ? File.OpenRead(dataFile) : null)
            using (var payload = new PayloadStream(file, continuation, offset, file.Length - offset - 16))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(root);
                long total = 0, done = 0;
                foreach (var entry in archive.Entries) total += entry.Length;
                byte[] buffer = new byte[1048576];
                foreach (var entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("运行包路径无效。");
                    if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var output = File.Create(target))
                    {
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, count); done += count;
                            if (progress != null) progress((int)(done * 100 / Math.Max(1, total)));
                        }
                    }
                }
                if (!File.Exists(Path.Combine(root, "Wallpaper Engine.exe")) || !File.Exists(Path.Combine(root, "models", "ggml-large-v3-turbo.bin")))
                    throw new InvalidDataException("运行包缺少程序或语音模型。");
                File.WriteAllText(marker, stamp);
            }
        }
    }

    private sealed class PayloadStream : Stream
    {
        private readonly Stream source, continuation;
        private readonly long offset, primaryLength, length;
        private long position;
        public PayloadStream(Stream source, Stream continuation, long offset, long primaryLength)
        {
            this.source = source; this.continuation = continuation; this.offset = offset;
            this.primaryLength = primaryLength; length = primaryLength + (continuation == null ? 0 : continuation.Length);
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }
        public override long Position { get { return position; } set { Seek(value, SeekOrigin.Begin); } }
        public override long Seek(long value, SeekOrigin origin)
        {
            long next = origin == SeekOrigin.Begin ? value : origin == SeekOrigin.Current ? Position + value : length + value;
            if (next < 0 || next > length) throw new IOException("Invalid archive position");
            position = next; return next;
        }
        public override int Read(byte[] buffer, int start, int count)
        {
            if (position >= length) return 0;
            Stream current = position < primaryLength ? source : continuation;
            long local = position < primaryLength ? offset + position : position - primaryLength;
            long available = position < primaryLength ? primaryLength - position : length - position;
            current.Position = local;
            int read = current.Read(buffer, start, (int)Math.Min(count, available));
            position += read; return read;
        }
        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int start, int count) { throw new NotSupportedException(); }
    }
}
