using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VMixPlayerController;

public partial class ToolsWindow : Window
{
    private readonly IReadOnlyList<VmixConnectionService> services;
    private readonly AtemConnectionService atem;
    private readonly AppSettings settings;
    private readonly SettingsService settingsService;

    public ToolsWindow(IReadOnlyList<VmixConnectionService> vmixServices, AtemConnectionService atemService, AppSettings appSettings, SettingsService appSettingsService)
    {
        InitializeComponent();
        services = vmixServices;
        atem = atemService;
        settings = appSettings;
        settingsService = appSettingsService;
        foreach (var combo in new[] { OutputServiceSelector, DataServiceSelector, AudioServiceSelector, PresetServiceSelector })
        {
            combo.ItemsSource = services;
            combo.SelectedIndex = 0;
        }
        OutputNumberSelector.SelectedIndex = 0;
        OutputSourceSelector.SelectedIndex = 0;
        AudioBusSelector.SelectedIndex = 0;
        ProductionBPanel.Visibility = FindService(services, "B") == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateProductionStatus();
        UpdateAudioInputs();
        RefreshLog();
        foreach (var service in services) service.SnapshotChanged += ServiceOnSnapshotChanged;
        Closed += (_, _) => { foreach (var service in services) service.SnapshotChanged -= ServiceOnSnapshotChanged; };
    }

    private void ServiceOnSnapshotChanged(VmixConnectionService _, VmixSnapshot __) => Dispatcher.BeginInvoke(() =>
    {
        UpdateProductionStatus();
        UpdateAudioInputs();
    });

    private void UpdateProductionStatus()
    {
        var serviceA = FindService(services, "A");
        var serviceB = FindService(services, "B");
        ProductionAStatus.Text = serviceA == null ? "No disponible" : ProductionLine(serviceA);
        ProductionBStatus.Text = serviceB == null ? "Desactivado" : ProductionLine(serviceB);
    }

    private static string ProductionLine(VmixConnectionService service)
    {
        var s = service.Snapshot;
        return service.IsConnected
            ? $"REC {(s.Recording ? "●" : "○")}   STREAM {(s.Streaming ? "●" : "○")}   EXTERNAL {(s.External ? "●" : "○")}   MULTICORDER {(s.MultiCorder ? "●" : "○")}"
            : $"Desconectado · {service.LastError}";
    }

    private async void Production_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var parts = tag.Split('|');
        var service = FindService(services, parts[0]);
        if (service == null) return;
        var feature = parts[1];
        var active = feature switch
        {
            "Recording" => service.Snapshot.Recording,
            "Streaming" => service.Snapshot.Streaming,
            "External" => service.Snapshot.External,
            "MultiCorder" => service.Snapshot.MultiCorder,
            _ => false
        };
        var action = active ? "detener" : "iniciar";
        if (MessageBox.Show($"¿Quieres {action} {feature} en {service.Name}?", "Confirmar control de producción", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await SafeCommand(service, $"{(active ? "Stop" : "Start")}{feature}");
    }

    private async void ApplyOutput_OnClick(object sender, RoutedEventArgs e)
    {
        if (OutputServiceSelector.SelectedItem is not VmixConnectionService service ||
            OutputNumberSelector.SelectedItem is not ComboBoxItem numberItem ||
            OutputSourceSelector.SelectedItem is not ComboBoxItem sourceItem) return;
        var number = numberItem.Tag?.ToString() ?? "2";
        var source = sourceItem.Tag?.ToString() ?? "Output";
        var parameters = new Dictionary<string, string> { ["Value"] = source };
        string? input = null;
        if (source == "Input") input = OutputInputValue.Text.Trim();
        if (source == "Mix") parameters["Mix"] = OutputInputValue.Text.Trim();
        await SafeCommand(service, $"SetOutput{number}", input, parameters);
    }

    private async void GlobalData_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string function } || DataServiceSelector.SelectedItem is not VmixConnectionService service) return;
        var parts = new List<string> { GlobalDataSource.Text.Trim() };
        if (!string.IsNullOrWhiteSpace(GlobalDataTable.Text)) parts.Add(GlobalDataTable.Text.Trim());
        if (function == "DataSourceSelectRow") parts.Add(GlobalDataRow.Text.Trim());
        var parameters = new Dictionary<string, string> { ["Value"] = string.Join(",", parts) };
        await SafeCommand(service, function, null, parameters);
    }

    private async void DataAutoNext_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataServiceSelector.SelectedItem is not VmixConnectionService service) return;
        var function = DataAutoNextToggle.IsChecked == true ? "DataSourceAutoNextOn" : "DataSourceAutoNextOff";
        var value = string.IsNullOrWhiteSpace(GlobalDataTable.Text) ? GlobalDataSource.Text.Trim() : $"{GlobalDataSource.Text.Trim()},{GlobalDataTable.Text.Trim()}";
        await SafeCommand(service, function, null, new Dictionary<string, string> { ["Value"] = value });
    }

    private void AudioService_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAudioInputs();

    private void UpdateAudioInputs()
    {
        if (AudioServiceSelector.SelectedItem is not VmixConnectionService service) return;
        var key = (AudioInputSelector.SelectedItem as VmixInput)?.Key;
        AudioInputSelector.ItemsSource = service.Snapshot.Inputs;
        AudioInputSelector.SelectedItem = service.Snapshot.Inputs.FirstOrDefault(i => i.Key == key) ?? service.Snapshot.Inputs.FirstOrDefault();
    }

    private async void AudioCommand_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string function } || !TryAudioSelection(out var service, out var input)) return;
        await SafeCommand(service, function, input.Key);
    }

    private async void Volume_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryAudioSelection(out var service, out var input)) return;
        await SafeCommand(service, "SetVolume", input.Key, new Dictionary<string, string> { ["Value"] = ((int)VolumeSlider.Value).ToString() });
    }

    private async void AudioBus_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string state } || !TryAudioSelection(out var service, out var input) || AudioBusSelector.SelectedItem is not ComboBoxItem busItem) return;
        var bus = busItem.Tag?.ToString() ?? "M";
        await SafeCommand(service, $"AudioBus{state}", input.Key, new Dictionary<string, string> { ["Value"] = bus });
    }

    private bool TryAudioSelection(out VmixConnectionService service, out VmixInput input)
    {
        service = AudioServiceSelector.SelectedItem as VmixConnectionService ?? services.FirstOrDefault()!;
        input = AudioInputSelector.SelectedItem as VmixInput ?? null!;
        return service != null && input != null;
    }

    internal static VmixConnectionService? FindService(IReadOnlyList<VmixConnectionService> candidates, string code)
    {
        var wantB = code.Equals("B", StringComparison.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(service => service.Name.EndsWith(wantB ? "B" : "A", StringComparison.OrdinalIgnoreCase));
    }

    private async void Preset_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string function } || PresetServiceSelector.SelectedItem is not VmixConnectionService service) return;
        if (function == "OpenPreset" && MessageBox.Show($"Abrir este preset sustituirá la producción de {service.Name}. ¿Continuar?", "Abrir preset de vMix", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await SafeCommand(service, function, null, new Dictionary<string, string> { ["Value"] = PresetPath.Text.Trim() });
    }

    private void RefreshLog_OnClick(object sender, RoutedEventArgs e) => RefreshLog();
    private void RefreshLog()
    {
        LogBox.Text = string.Join(Environment.NewLine, LogService.Snapshot());
        LogBox.ScrollToEnd();
    }

    private void ExportDiagnostic_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Diagnóstico (*.txt)|*.txt", FileName = $"diagnostico-vmix-player-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        var generated = LogService.CreateSupportBundle(Path.GetDirectoryName(dialog.FileName)!, settings, services, atem);
        if (!generated.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase)) File.Copy(generated, dialog.FileName, true);
        MessageBox.Show($"Diagnóstico guardado en:\n{dialog.FileName}", "Diagnóstico", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SafeCommand(VmixConnectionService service, string function, string? input = null, Dictionary<string, string>? parameters = null)
    {
        try { await service.CommandAsync(function, input, parameters); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "No se pudo ejecutar la orden", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
