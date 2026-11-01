using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using WinShikiPlayer.Services;

namespace WinShikiPlayer;

public partial class App : Application
{
    // Keep application-owned resources alive independently of secondary XamlRoots.
    // The palette mutates these brush instances, so all windows still share one theme.
    private static readonly Dictionary<string, object> SharedResources = new();
    internal static T Resource<T>(string key)
    {
        if (!SharedResources.TryGetValue(key, out var resource))
        {
            resource = Current.Resources[key];
            SharedResources.Add(key, resource);
        }
        return (T)resource;
    }
    public static MainWindow MainWindow { get; private set; } = null!;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, error) =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShikiPlayer"); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "startup-error.log"), error.Exception.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        AppInstance.GetCurrent().Activated += (_, activationArgs) =>
        {
            if (activationArgs.Kind == ExtendedActivationKind.Protocol && activationArgs.Data is ProtocolActivatedEventArgs activatedProtocol)
                MainWindow?.DispatcherQueue.TryEnqueue(() => MainWindow.AcceptProtocolCallback(activatedProtocol.Uri));
        };
        if (arguments.Contains("--playback-smoke") || arguments.Contains("--register-protocol") || arguments.Contains("--protocol-smoke") || arguments.Contains("--direct-protocol-smoke") || arguments.Contains("--session-smoke") || arguments.Contains("--subtitle-smoke") || arguments.Contains("--auth-config-smoke"))
        {
            await ApplicationDiagnostics.RunAsync(arguments); Exit(); return;
        }
        var callback = arguments.Skip(1).FirstOrDefault(x => x.StartsWith("winshikiplayer://", StringComparison.OrdinalIgnoreCase));
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.Protocol && activation.Data is ProtocolActivatedEventArgs protocol) callback = protocol.Uri.AbsoluteUri;
        }
        catch (System.Runtime.InteropServices.COMException) { /* Raw command line remains available in unpackaged deployments. */ }
        if (Uri.TryCreate(callback, UriKind.Absolute, out var uri))
        {
            try
            {
                await ProtocolActivation.SendAsync(uri);
                if (uri.Host == "diagnostic") await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "protocol-child.json"), "{\"sent\":true}");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
            {
                if (uri.Host == "diagnostic") await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "protocol-child.json"), System.Text.Json.JsonSerializer.Serialize(new { sent = false, error = ex.GetType().Name, message = ex.Message }));
            }
            Exit(); return;
        }
        MainWindow = new MainWindow(); MainWindow.Activate();
    }
}
