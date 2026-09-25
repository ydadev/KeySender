using KeySender.Services;

namespace KeySender;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (_, args) => AppLogger.Write($"Unhandled exception: {args.Exception.GetType().Name}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLogger.Write($"Unhandled exception: {args.ExceptionObject?.GetType().Name ?? "Unknown"}");
    }
}
