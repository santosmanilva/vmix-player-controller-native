using Microsoft.Win32;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.IO;

namespace VMixPlayerController;

public partial class MainWindow : Window
{
    private readonly SettingsService settingsService = new();
    private readonly VmixConnectionService vmixA = new("vMix A");
    private readonly VmixConnectionService vmixB = new("vMix B");
    private readonly AtemConnectionService atem = new();
    private readonly List<PlayerControl> players = [];
    private AppSettings settings;
    private bool connecting;
    private bool closing;
    private bool closedAfterCleanup;

    public MainWindow()
    {
        InitializeComponent();
        settings = settingsService.Load();
        players.AddRange([Player1, Player2, Player3, Player4]);
        LoadSettingsIntoUi();
        for (var i = 0; i < players.Count; i++) players[i].Configure(i + 1, settings.Players[i], [vmixA, vmixB], atem, SaveSettingsFromUi);
        vmixA.SnapshotChanged += (_, snapshot) => Dispatcher.BeginInvoke(() => UpdateVmixStatus(vmixA, snapshot));
        vmixB.SnapshotChanged += (_, snapshot) => Dispatcher.BeginInvoke(() => UpdateVmixStatus(vmixB, snapshot));
        vmixA.ConnectionChanged += service => Dispatcher.BeginInvoke(() => UpdateVmixStatus(service, service.Snapshot));
        vmixB.ConnectionChanged += service => Dispatcher.BeginInvoke(() => UpdateVmixStatus(service, service.Snapshot));
        atem.SnapshotChanged += snapshot => Dispatcher.BeginInvoke(() => UpdateAtemStatus(snapshot));
        LogService.LineAdded += line => Dispatcher.BeginInvoke(() => LastActionText.Text = line);
        Loaded += async (_, _) =>
        {
            var demoMode = Environment.GetCommandLineArgs().Contains("--demo", StringComparer.OrdinalIgnoreCase);
            if (demoMode)
            {
                var demoSelections = new[] { ("vMix A", "demo-A-1"), ("vMix A", "demo-A-2"), ("vMix B", "demo-B-1"), ("vMix A", "demo-A-3") };
                for (var i = 0; i < settings.Players.Count; i++)
                {
                    settings.Players[i].VmixName = demoSelections[i].Item1;
                    settings.Players[i].InputKey = demoSelections[i].Item2;
                }
                await vmixA.StartAsync("demo", 8088);
                await vmixB.StartAsync("demo", 8088);
            }
            else if (settings.AutoConnect) await ConnectAllAsync();
            var toolsScreenshotArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tools-screenshot=", StringComparison.OrdinalIgnoreCase));
            if (toolsScreenshotArg != null)
            {
                var path = toolsScreenshotArg["--tools-screenshot=".Length..].Trim('"');
                var tools = new ToolsWindow([vmixA], atem, settings, settingsService) { Owner = this };
                var tabArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tools-tab=", StringComparison.OrdinalIgnoreCase));
                if (tabArg != null && int.TryParse(tabArg["--tools-tab=".Length..], out var tabIndex))
                    tools.ToolsTabs.SelectedIndex = Math.Clamp(tabIndex, 0, tools.ToolsTabs.Items.Count - 1);
                tools.Show();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveVisualSnapshot(tools, path);
                tools.Close();
                Application.Current.Shutdown();
                return;
            }
            var toolsSmokeArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tools-smoke=", StringComparison.OrdinalIgnoreCase));
            if (toolsSmokeArg != null)
            {
                var path = toolsSmokeArg["--tools-smoke=".Length..].Trim('"');
                var exitCode = 0;
                try
                {
                    var tools = new ToolsWindow([vmixA], atem, settings, settingsService) { Owner = this };
                    tools.Show();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!tools.IsVisible) throw new InvalidOperationException("La ventana Herramientas no llegó a mostrarse.");
                    await NativeSelfTests.RunUiAsync();
                    tools.Close();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "PASS · Herramientas con un solo vMix · atajos · cambio de perfil · selección de clips");
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, $"FAIL · {ex}");
                }
                Application.Current.Shutdown(exitCode);
                return;
            }
            var screenshotArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase));
            if (screenshotArg != null)
            {
                var path = screenshotArg["--screenshot=".Length..].Trim('"');
                await Dispatcher.InvokeAsync(() => SaveVisualSnapshot(path), DispatcherPriority.ApplicationIdle);
                Application.Current.Shutdown();
            }
        };
    }

    private void LoadSettingsIntoUi()
    {
        StatusAText.ToolTip = $"{settings.VmixA.Host}:{settings.VmixA.Port}";
        StatusBText.Text = settings.UseVmixB ? "DESCONECTADO" : "DESACTIVADO";
        StatusBText.ToolTip = settings.UseVmixB ? $"{settings.VmixB.Host}:{settings.VmixB.Port}" : "Actívalo desde Configuración si necesitas un segundo vMix.";
        ProductionBText.Text = settings.UseVmixB ? "REC ○  STR ○  EXT ○" : "";
        AtemStatusText.ToolTip = settings.AtemHost;
    }

    private void SaveSettingsFromUi()
    {
        if (!settingsService.Save(settings))
        {
            LastActionText.Text = "CONFIGURACIÓN NO GUARDADA";
            LastActionText.ToolTip = settingsService.LastError;
        }
    }

    private async void Connect_OnClick(object sender, RoutedEventArgs e) => await ConnectAllAsync();

    private async Task ConnectAllAsync()
    {
        if (connecting || closing) return;
        connecting = true;
        try
        {
            SaveSettingsFromUi();
            LastActionText.Text = "Conectando dispositivos…";
            var vmixBTask = settings.UseVmixB
                ? vmixB.StartAsync(settings.VmixB.Host, settings.VmixB.Port)
                : vmixB.DisableAsync();
            await Task.WhenAll(vmixA.StartAsync(settings.VmixA.Host, settings.VmixA.Port), vmixBTask, atem.StartAsync(settings.AtemHost));
            LastActionText.Text = "Conexiones iniciadas";
        }
        catch (Exception ex)
        {
            LogService.Write("ERROR", $"Conexión: {ex.Message}");
            LastActionText.Text = $"No se pudo conectar: {ex.Message}";
        }
        finally { connecting = false; }
    }

    private void UpdateVmixStatus(VmixConnectionService service, VmixSnapshot snapshot)
    {
        var isA = service == vmixA;
        var dot = isA ? StatusADot : StatusBDot;
        var status = isA ? StatusAText : StatusBText;
        var production = isA ? ProductionAText : ProductionBText;
        if (!service.IsEnabled)
        {
            dot.Fill = (Brush)FindResource("MutedBrush");
            status.Text = "DESACTIVADO";
            status.ToolTip = "Actívalo desde Configuración si necesitas un segundo vMix.";
            production.Text = "";
            return;
        }
        dot.Fill = (Brush)FindResource(service.IsConnected ? "SuccessBrush" : "DangerBrush");
        status.Text = service.IsConnected ? $"ONLINE · {snapshot.LatencyMs} ms" : service.LastError.ToUpperInvariant();
        status.ToolTip = service.IsConnected ? $"vMix {snapshot.Version} · {snapshot.Edition}" : service.LastError;
        if (!service.IsConnected)
        {
            production.Text = "SIN DATOS";
            production.Foreground = (Brush)FindResource("MutedBrush");
            return;
        }
        production.Text = $"REC {(snapshot.Recording ? "●" : "○")}  STR {(snapshot.Streaming ? "●" : "○")}  EXT {(snapshot.External ? "●" : "○")}";
        production.Foreground = snapshot.Recording || snapshot.Streaming ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("MutedBrush");
    }

    private void UpdateAtemStatus(AtemSnapshot snapshot)
    {
        AtemStatusDot.Fill = (Brush)FindResource(snapshot.IsConnected ? "SuccessBrush" : "DangerBrush");
        AtemStatusText.Text = snapshot.Status;
        AtemStatusText.ToolTip = snapshot.IsConnected
            ? string.Join(" · ", snapshot.ProgramByMe.OrderBy(x => x.Key).Select(x => $"M/E {x.Key + 1} PGM {x.Value}"))
            : "La integración ATEM es de solo lectura: vigila PGM y no modifica la mesa.";
    }

    private void Tools_OnClick(object sender, RoutedEventArgs e)
    {
        var availableServices = settings.UseVmixB ? new[] { vmixA, vmixB } : new[] { vmixA };
        var tools = new ToolsWindow(availableServices, atem, settings, settingsService) { Owner = this };
        tools.ShowDialog();
    }

    private async void Settings_OnClick(object sender, RoutedEventArgs e)
    {
        if (connecting || closing) return;
        var dialog = new SettingsWindow(settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        SaveSettingsFromUi();
        LoadSettingsIntoUi();
        await ConnectAllAsync();
    }

    private void Profile_OnClick(object sender, RoutedEventArgs e)
    {
        if (connecting || closing) return;
        var menu = new ContextMenu();
        var export = new MenuItem { Header = "Exportar perfil…" };
        export.Click += (_, _) => ExportProfile();
        var import = new MenuItem { Header = "Importar perfil…" };
        import.Click += async (_, _) => await ImportProfileAsync();
        menu.Items.Add(export); menu.Items.Add(import);
        menu.IsOpen = true;
    }

    private void ExportProfile()
    {
        SaveSettingsFromUi();
        var dialog = new SaveFileDialog { Filter = "Perfil vMix Player (*.json)|*.json", FileName = "perfil-vmix-player.json" };
        if (dialog.ShowDialog(this) == true)
        {
            settingsService.Export(settings, dialog.FileName);
            LastActionText.Text = $"Perfil exportado: {dialog.FileName}";
        }
    }

    private async Task ImportProfileAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Perfil vMix Player (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = settingsService.Import(dialog.FileName);
            if (!settingsService.Save(imported)) throw new IOException(settingsService.LastError);
            connecting = true;
            try
            {
                await Task.WhenAll(vmixA.StopAsync(), vmixB.StopAsync(), atem.StopAsync());
                settings = imported;
                for (var i = 0; i < players.Count; i++)
                    players[i].Configure(i + 1, settings.Players[i], [vmixA, vmixB], atem, SaveSettingsFromUi);
                LoadSettingsIntoUi();
            }
            finally { connecting = false; }
            await ConnectAllAsync();
            LastActionText.Text = "Perfil importado y aplicado";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Perfil no válido", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || closing || IsEditingText(Keyboard.FocusedElement as DependencyObject)) return;
        var playerIndex = e.Key switch { Key.F1 => 0, Key.F2 => 1, Key.F3 => 2, Key.F4 => 3, _ => -1 };
        if (playerIndex >= 0)
        {
            e.Handled = true;
            await players[playerIndex].GoAsync();
            return;
        }
        if (e.Key == Key.Space && FindParent<PlayerControl>(Keyboard.FocusedElement as DependencyObject) is { } player)
        {
            e.Handled = true;
            await player.TogglePlayAsync();
        }
    }

    internal static bool IsEditingText(DependencyObject? element) =>
        FindParent<TextBoxBase>(element) != null || FindParent<PasswordBox>(element) != null ||
        FindParent<ComboBox>(element)?.IsEditable == true;

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T found) return found;
            child = child is Visual or Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }

    private void SaveVisualSnapshot(string path) => SaveVisualSnapshot(this, path);

    private static void SaveVisualSnapshot(FrameworkElement target, string path)
    {
        var width = Math.Max(1, (int)target.ActualWidth);
        var height = Math.Max(1, (int)target.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || closedAfterCleanup) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        IsEnabled = false;
        try
        {
            SaveSettingsFromUi();
            await vmixA.DisposeAsync();
            await vmixB.DisposeAsync();
            await atem.DisposeAsync();
        }
        catch (Exception ex) { LogService.Write("ERROR", $"Cierre: {ex.Message}"); }
        closedAfterCleanup = true;
        Close();
    }
}
