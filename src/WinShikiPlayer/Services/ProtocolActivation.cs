using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Win32;

namespace WinShikiPlayer.Services;

internal static class ProtocolActivation
{
    // CurrentUserOnly prevents another Windows account from injecting callbacks.
    private static string PipeName => "WinShikiPlayer.OAuth." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|" + Environment.ProcessPath?.ToLowerInvariant())))[..16];
    public static void Register()
    {
        if (HasPackageIdentity()) return; // MSIX registers the manifest protocol during installation.
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь приложения.");
        ActivationRegistrationManager.RegisterForProtocolActivation("winshikiplayer", executable + ",0", "WinShikiPlayer", executable);
        // Repair the legacy direct registration as well. A stale shell\open\command takes
        // precedence over OpenWithProgids entries added by the rich activation API.
        using (var scheme = Registry.CurrentUser.CreateSubKey(@"Software\Classes\winshikiplayer"))
        {
            scheme.SetValue("", "URL:WinShikiPlayer", RegistryValueKind.String);
            scheme.SetValue("URL Protocol", "", RegistryValueKind.String);
            using var icon = scheme.CreateSubKey("DefaultIcon"); icon.SetValue("", executable + ",0");
            using var shell = scheme.CreateSubKey("shell"); shell.SetValue("", "open");
            using var command = scheme.CreateSubKey(@"shell\open\command"); command.SetValue("", $"\"{executable}\" \"%1\"");
        }
        const string progId = "WinShikiPlayer.Protocol";
        using (var type = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId))
        {
            type.SetValue("", "WinShikiPlayer"); type.SetValue("URL Protocol", "");
            using var command = type.CreateSubKey(@"shell\open\command"); command.SetValue("", $"\"{executable}\" \"%1\"");
            using var application = type.CreateSubKey("Application");
            application.SetValue("ApplicationName", "WinShikiPlayer"); application.SetValue("ApplicationIcon", executable + ",0");
        }
        using (var capabilities = Registry.CurrentUser.CreateSubKey(@"Software\WinShikiPlayer\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", "WinShikiPlayer"); capabilities.SetValue("ApplicationDescription", "WinShikiPlayer для Windows"); capabilities.SetValue("ApplicationIcon", executable + ",0");
            using var associations = capabilities.CreateSubKey("URLAssociations"); associations.SetValue("winshikiplayer", progId);
        }
        using (var applications = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) applications.SetValue("WinShikiPlayer", @"Software\WinShikiPlayer\Capabilities");
        using (var executableRegistration = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\WinShikiPlayer.exe"))
        {
            executableRegistration.SetValue("FriendlyAppName", "WinShikiPlayer");
            using var protocols = executableRegistration.CreateSubKey("SupportedProtocols"); protocols.SetValue("winshikiplayer", "");
            using var command = executableRegistration.CreateSubKey(@"shell\open\command"); command.SetValue("", $"\"{executable}\" \"%1\"");
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    public static bool IsDefaultHandler()
    {
        if (HasPackageIdentity()) return true;
        var command = new StringBuilder(2048); uint length = (uint)command.Capacity;
        return AssocQueryString(0x1000, 1, "winshikiplayer", "open", command, ref length) == 0 &&
            command.ToString().Contains("WinShikiPlayer.exe", StringComparison.OrdinalIgnoreCase);
    }
    public static void OpenDefaultApps() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
        "ms-settings:defaultapps?registeredAppUser=WinShikiPlayer") { UseShellExecute = true });
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint AssocQueryString(uint flags, uint type, string association, string extra, StringBuilder output, ref uint length);
    public static bool HasPackageIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != 15700;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? packageName);
    public static async Task SendAsync(Uri callback)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, Encoding.UTF8);
        await writer.WriteLineAsync(callback.AbsoluteUri.AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);
    }
    public static async Task ListenAsync(Action<Uri> receive, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var text = new StringBuilder();
                var character = new char[1];
                while (text.Length < 4096 && await reader.ReadAsync(character.AsMemory(), readTimeout.Token) > 0)
                {
                    if (character[0] == '\n') break;
                    text.Append(character[0]);
                }
                if (text.Length < 4096 && Uri.TryCreate(text.ToString().Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "winshikiplayer" && uri.Host is "oauth" or "diagnostic") receive(uri);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
}
