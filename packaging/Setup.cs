using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("WinShikiPlayer Setup")]
[assembly: AssemblyProduct("WinShikiPlayer")]
[assembly: AssemblyVersion("@@VERSION@@")]
[assembly: AssemblyFileVersion("@@VERSION@@")]

internal static class Setup
{
    private const string Version = "@@VERSION@@";
    private const string PayloadHash = "@@PAYLOAD_SHA256@@";
    private static readonly string PackageName = "WinShikiPlayer-" + Version + "-x64.msix";
    private static readonly string[] Files = { PackageName, "WinShikiPlayer-development.cer", "Install.cmd", "Install.ps1", "installer.json", "README.txt" };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--check-only") || args.Contains("--install"))
        {
            try { return Install(args.Contains("--check-only"), delegate { }); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupWindow());
        return 0;
    }

    private static int Install(bool checkOnly, Action<string> status)
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinShikiPlayer-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            status("Проверка установщика…");
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinShikiPlayer.Setup.Payload"))
            {
                using (SHA256 sha = SHA256.Create())
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "");
                    if (!string.Equals(actual, PayloadHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Установочный пакет повреждён.");
                }
                payload.Position = 0;
                using (ZipArchive zip = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    HashSet<string> expected = new HashSet<string>(Files, StringComparer.Ordinal);
                    if (zip.Entries.Count != expected.Count || zip.Entries.Any(e => !expected.Remove(e.FullName))) throw new InvalidDataException("Неожиданный состав установочного пакета.");
                    foreach (ZipArchiveEntry entry in zip.Entries)
                        using (Stream source = entry.Open())
                        using (FileStream target = new FileStream(Path.Combine(directory, entry.FullName), FileMode.CreateNew)) source.CopyTo(target);
                }
            }
            status(checkOnly ? "Проверка пакета…" : "Установка… При необходимости Windows запросит разрешение добавить сертификат разработчика.");
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            string command = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); & '" + Path.Combine(directory, "Install.ps1").Replace("'", "''") + "' " + (checkOnly ? "-CheckOnly" : "-TrustDevelopmentCertificate");
            start.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
            start.WorkingDirectory = directory;
            start.UseShellExecute = false; start.CreateNoWindow = true;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            start.StandardOutputEncoding = start.StandardErrorEncoding = System.Text.Encoding.UTF8;
            // A build launched from PowerShell 7 can inherit incompatible PSModulePath.
            // The installer only needs the Windows PowerShell built-in modules.
            start.EnvironmentVariables["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "Modules");
            using (Process process = new Process())
            {
                System.Text.StringBuilder errors = new System.Text.StringBuilder();
                process.StartInfo = start;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (!string.IsNullOrWhiteSpace(e.Data)) status(e.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) errors.AppendLine(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException(checkOnly ? errors.ToString() : "Windows не завершила установку. Убедитесь, что используется Windows 10 2004 или новее (x64), и разрешите установку сертификата при запросе Windows.");
                return process.ExitCode;
            }
        }
        finally
        {
            // Only remove our six known files. Never recurse through a user/temp directory.
            string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "WinShikiPlayer-Setup-";
            if (Path.GetFullPath(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (string file in Files) { string path = Path.Combine(directory, file); if (File.Exists(path)) File.Delete(path); }
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
        }
    }

    private sealed class SetupWindow : Form
    {
        private readonly Label message;
        private readonly Button button;
        private bool busy, complete;
        public SetupWindow()
        {
            Text = "Установка WinShikiPlayer " + Version;
            ClientSize = new Size(520, 245); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(23, 21, 27); ForeColor = Color.White;
            Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            Label title = new Label { Text = "WinShikiPlayer " + Version, Font = new Font("Segoe UI", 17, FontStyle.Bold), Location = new Point(26, 24), Size = new Size(470, 38) };
            message = new Label { Text = "Установка для Windows x64. Обновление сохранит настройки и авторизацию.\r\n\r\nПосле установки приложение будет доступно в меню «Пуск».", Location = new Point(28, 76), Size = new Size(464, 95) };
            button = new Button { Text = "Установить", Location = new Point(326, 187), Size = new Size(166, 36), BackColor = Color.FromArgb(255, 77, 94), ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
            button.FlatAppearance.BorderSize = 0;
            button.Click += async delegate
            {
                if (complete) { Close(); return; }
                busy = true; button.Enabled = false;
                try
                {
                    await Task.Run(() => Install(false, text => BeginInvoke((Action)(() => message.Text = text))));
                    message.Text = "Готово! Откройте WinShikiPlayer из меню «Пуск».";
                    complete = true; button.Text = "Закрыть";
                }
                catch (Exception error) { message.Text = error.Message; button.Text = "Повторить"; }
                finally { busy = false; button.Enabled = true; }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            Controls.Add(title); Controls.Add(message); Controls.Add(button);
            AcceptButton = button;
        }
    }
}
