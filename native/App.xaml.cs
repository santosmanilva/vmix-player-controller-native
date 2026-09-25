using System.Windows;
using System.IO;

namespace VMixPlayerController;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var selfTest = e.Args.FirstOrDefault(a => a.StartsWith("--self-test=", StringComparison.OrdinalIgnoreCase));
        if (selfTest != null)
        {
            var path = selfTest["--self-test=".Length..].Trim('"');
            var result = Task.Run(NativeSelfTests.RunAsync).GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, result);
            Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
            return;
        }
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogService.Write("ERROR", $"Error no controlado: {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Write("ERROR", $"Error de interfaz: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "vMix Player Controller", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
