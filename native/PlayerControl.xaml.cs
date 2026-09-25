using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;

namespace VMixPlayerController;

public partial class PlayerControl : UserControl
{
    private sealed record ClipRow(int Index, string Name, string FullPath);
    private sealed record AtemMeChoice(int Value, string Label)
    {
        public override string ToString() => Label;
    }
    private static readonly IReadOnlyList<AtemMeChoice> AtemMeChoices =
    [new(0, "M/E 1"), new(1, "M/E 2"), new(-1, "CUALQ.")];
    private readonly List<VmixConnectionService> services = [];
    private AtemConnectionService atem = null!;
    private PlayerConfig config = null!;
    private Action saveRequested = null!;
    private InputChoice? choice;
    private bool suppressSelection;
    private bool seeking;
    private bool rendering;
    private bool? lastAtemOnAir;

    public int PlayerNumber { get; private set; }

    public PlayerControl() => InitializeComponent();

    public void Configure(int number, PlayerConfig playerConfig, IEnumerable<VmixConnectionService> vmixServices, AtemConnectionService atemService, Action onSaveRequested)
    {
        PlayerNumber = number;
        PlayerName.Text = $"PLAYER {number}";
        config = playerConfig;
        saveRequested = onSaveRequested;
        atem = atemService;
        services.AddRange(vmixServices);
        foreach (var service in services)
        {
            service.SnapshotChanged += ServiceOnSnapshotChanged;
            service.ConnectionChanged += ServiceOnConnectionChanged;
        }
        atem.SnapshotChanged += AtemOnSnapshotChanged;
        AtemMeSelector.ItemsSource = AtemMeChoices;
        AtemMeSelector.SelectedItem = AtemMeChoices.FirstOrDefault(choice => choice.Value == config.AtemMe) ?? AtemMeChoices[0];
        AtemAutoToggle.IsChecked = config.AtemAutoPlayPause;
        UpdateSources();
        UpdateAtemInputs(atem.Snapshot);
    }

    public async Task GoAsync()
    {
        if (choice == null) return;
        try
        {
            foreach (var mix in config.SelectedMixes.Order())
                await choice.Service.CommandAsync("ActiveInput", choice.Input.Key, new Dictionary<string, string> { ["Mix"] = mix.ToString() });
            if (config.GoAudioAuto) await choice.Service.CommandAsync("AudioAuto", choice.Input.Key);
            if (config.GoRestart) await choice.Service.CommandAsync("Restart", choice.Input.Key);
            else await choice.Service.CommandAsync("Play", choice.Input.Key);
            SetTransientStatus("GO EJECUTADO", true);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    public async Task TogglePlayAsync()
    {
        if (choice == null) return;
        await SendAsync(choice.Input.IsPlaying ? "Pause" : "Play");
    }

    private void ServiceOnSnapshotChanged(VmixConnectionService service, VmixSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (choice?.Service == service)
            {
                var updated = snapshot.Inputs.FirstOrDefault(i => i.Key == choice.Input.Key);
                if (updated != null)
                {
                    var listChanged = !updated.ListItems.Select(i => i.Value).SequenceEqual(choice.Input.ListItems.Select(i => i.Value));
                    var fieldsChanged = !updated.TextFields.Select(i => i.Name).SequenceEqual(choice.Input.TextFields.Select(i => i.Name));
                    choice = new InputChoice(service, updated);
                    RenderState(updated, listChanged, fieldsChanged);
                }
            }
            if (InputSelector.Items.Count == 0) UpdateSources();
        });
    }

    private void ServiceOnConnectionChanged(VmixConnectionService _) => Dispatcher.BeginInvoke(UpdateSources);

    private void UpdateSources()
    {
        if (config == null) return;
        var selectedService = choice?.Service.Name ?? config.VmixName;
        var selectedKey = choice?.Input.Key ?? config.InputKey;
        var choices = services.Where(s => s.IsEnabled).SelectMany(s => s.Snapshot.Inputs.Select(i => new InputChoice(s, i))).OrderBy(c => c.Service.Name).ThenBy(c => c.Input.Number).ToList();
        suppressSelection = true;
        InputSelector.ItemsSource = choices;
        var selected = choices.FirstOrDefault(c => c.Service.Name == selectedService && c.Input.Key == selectedKey);
        InputSelector.SelectedItem = selected;
        suppressSelection = false;
        if (selected != null)
        {
            choice = selected;
            RenderMode(selected.Input);
            RenderState(selected.Input, true, true);
        }
        else if (choices.Count > 0 && string.IsNullOrEmpty(config.InputKey)) InputSelector.SelectedIndex = 0;
        else if (choices.Count == 0)
        {
            choice = null;
            InputKind.Text = "SIN CONEXIÓN";
            InputState.Text = "Conecta vMix A o B";
        }
    }

    private void InputSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelection || InputSelector.SelectedItem is not InputChoice selected) return;
        choice = selected;
        config.VmixName = selected.Service.Name;
        config.InputKey = selected.Input.Key;
        lastAtemOnAir = null;
        RenderMode(selected.Input);
        RenderState(selected.Input, true, true);
        saveRequested();
    }

    private void RenderMode(VmixInput input)
    {
        PlaybackSummaryPanel.Visibility = input.IsTitle ? Visibility.Collapsed : Visibility.Visible;
        TransportButtonsPanel.Visibility = input.IsTitle ? Visibility.Collapsed : Visibility.Visible;
        PositionPanel.Visibility = input.IsTitle ? Visibility.Collapsed : Visibility.Visible;
        ListOptionsPanel.Visibility = input.IsList ? Visibility.Visible : Visibility.Collapsed;
        AudioBusPanel.Visibility = input.IsList ? Visibility.Visible : Visibility.Collapsed;
        ClipList.Visibility = input.IsList ? Visibility.Visible : Visibility.Collapsed;
        AtemPanel.Visibility = input.IsList ? Visibility.Visible : Visibility.Collapsed;
        TitleScroll.Visibility = input.IsTitle ? Visibility.Visible : Visibility.Collapsed;
        GenericPanel.Visibility = !input.IsList && !input.IsTitle ? Visibility.Visible : Visibility.Collapsed;
        UpdateAtemPanelState();
        foreach (var toggle in FindVisualChildren<ToggleButton>(ListOptionsPanel).Where(t => t.Tag is string or int))
            if (int.TryParse(toggle.Tag?.ToString(), out var mix)) toggle.IsChecked = config.SelectedMixes.Contains(mix);
    }

    private void RenderState(VmixInput input, bool listChanged, bool fieldsChanged)
    {
        if (rendering) return;
        rendering = true;
        try
        {
            InputKind.Text = $"{choice?.Service.Name} · {input.Kind}";
            InputState.Text = input.State.ToUpperInvariant();
            InputState.Foreground = input.IsPlaying ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("MutedBrush");
            PlayPauseToggle.IsChecked = input.IsPlaying;
            PlayPauseToggle.Content = input.IsPlaying ? "Ⅱ" : "▶";
            PlayPauseToggle.ToolTip = input.IsPlaying ? "Pausar" : "Reproducir";
            LoopToggle.IsChecked = input.Loop;
            LoopToggle.ToolTip = input.Loop ? "Loop activado en vMix" : "Loop desactivado en vMix";
            AutoNextToggle.IsChecked = input.AutoNext == true;
            AutoNextToggle.Content = input.AutoNext.HasValue ? "AUTO NEXT" : "AUTO NEXT ?";
            AutoNextToggle.ToolTip = input.AutoNext.HasValue
                ? $"Auto Next {(input.AutoNext.Value ? "activado" : "desactivado")} desde este controlador"
                : "vMix no publica Auto Next en su API. Pulsa para establecer un estado conocido.";
            if (input.IsList) RenderAudioBusFeedback(input);
            if (!seeking)
            {
                PositionSlider.Maximum = Math.Max(1, input.Duration);
                PositionSlider.Value = Math.Clamp(input.Position, 0, Math.Max(1, input.Duration));
                CurrentTimeText.Text = Time(input.Position);
            }
            DurationText.Text = Time(input.Duration);
            var remaining = Math.Max(0, input.Duration - input.Position);
            RemainingText.Text = Time(remaining);
            RemainingText.Foreground = remaining <= 10_000 && input.Duration > 0
                ? (Brush)FindResource("DangerBrush")
                : remaining <= 30_000 && input.Duration > 0
                    ? (Brush)FindResource("WarningBrush")
                    : (Brush)FindResource("TextBrush");

            if (input.IsList)
            {
                var currentIndex = Math.Clamp(input.SelectedIndex, 0, Math.Max(0, input.ListItems.Count - 1));
                NowText.Text = input.ListItems.Count > 0 ? DisplayClipName(input.ListItems[currentIndex].Value) : "Lista vacía";
                NextText.Text = currentIndex + 1 < input.ListItems.Count ? $"SIGUIENTE  {DisplayClipName(input.ListItems[currentIndex + 1].Value)}" : "SIGUIENTE  — FIN DE LISTA";
                if (listChanged || ClipList.Items.Count != input.ListItems.Count)
                    ClipList.ItemsSource = input.ListItems.Select((item, index) => new ClipRow(index + 1, DisplayClipName(item.Value), item.Value)).ToList();
                if (ClipList.SelectedIndex != currentIndex && currentIndex < ClipList.Items.Count)
                {
                    ClipList.SelectedIndex = currentIndex;
                    ClipList.ScrollIntoView(ClipList.SelectedItem);
                }
            }
            else
            {
                NowText.Text = input.Title;
                NextText.Text = input.IsTitle ? $"{input.TextFields.Count} CAMPOS EDITABLES" : input.Type;
            }

            if (input.IsTitle && (fieldsChanged || TitleFieldsPanel.Children.Count != input.TextFields.Count)) BuildTitleFields(input);
            if (input.IsTitle) RenderOverlayFeedback(input);
        }
        finally { rendering = false; }
    }

    private void RenderOverlayFeedback(VmixInput input)
    {
        foreach (var toggle in new[] { Overlay1Toggle, Overlay2Toggle, Overlay3Toggle, Overlay4Toggle })
        {
            if (!int.TryParse(toggle.Tag?.ToString(), out var overlayNumber)) continue;
            var active = choice?.Service.Snapshot.IsInputOnOverlay(overlayNumber, input.Number) == true;
            toggle.IsChecked = active;
            toggle.Content = $"OVL {overlayNumber}";
            toggle.ToolTip = active
                ? $"Overlay {overlayNumber} activo con este título · pulsa para retirar"
                : $"Enviar este título al Overlay {overlayNumber}";
        }
    }

    private void RenderAudioBusFeedback(VmixInput input)
    {
        foreach (var toggle in new[] { AudioBusMToggle, AudioBusAToggle, AudioBusBToggle, AudioBusCToggle, AudioBusDToggle })
        {
            var bus = toggle.Tag?.ToString() ?? "";
            var active = input.IsAudioBusEnabled(bus);
            toggle.IsChecked = active;
            toggle.ToolTip = active
                ? $"Bus {bus} asignado a esta lista · pulsa para retirarlo"
                : $"Enviar el audio de esta lista al bus {bus}";
        }

        AudioInputState.Text = input.Muted ? "MUTE" : "ON";
        AudioInputState.Foreground = (Brush)FindResource(input.Muted ? "WarningBrush" : "SuccessBrush");
        AudioInputState.ToolTip = input.Muted
            ? "La entrada está silenciada en vMix; sus rutas de audio permanecen asignadas."
            : "El audio de la entrada está activado en vMix.";
    }

    private void BuildTitleFields(VmixInput input)
    {
        TitleFieldsPanel.Children.Clear();
        foreach (var field in input.TextFields)
        {
            var box = new TextBox { Text = field.Value, Tag = field.Name, MinWidth = 180 };
            var update = new Button { Content = "ACTUALIZAR", Tag = box, MinWidth = 85 };
            update.Click += async (_, _) => await UpdateTitleFieldAsync(box);
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = field.Name, Foreground = (Brush)FindResource("MutedBrush"), FontSize = 10, Margin = new Thickness(4, 2, 0, 0) };
            Grid.SetColumnSpan(label, 2);
            Grid.SetRow(box, 1);
            Grid.SetRow(update, 1);
            Grid.SetColumn(update, 1);
            grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(update);
            TitleFieldsPanel.Children.Add(grid);
        }
    }

    internal static string DisplayClipName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Sin nombre";
        var cleaned = value.Trim().Trim('"');
        try
        {
            if (Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) && uri.IsFile)
                cleaned = uri.LocalPath;
            var fileName = Path.GetFileName(cleaned.Replace('/', Path.DirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(fileName) ? cleaned : fileName;
        }
        catch { return cleaned; }
    }

    private async Task UpdateTitleFieldAsync(TextBox box)
    {
        await SendAsync("SetText", new() { ["SelectedName"] = box.Tag?.ToString() ?? "", ["Value"] = box.Text });
    }

    private async void ApplyAllTitles_OnClick(object sender, RoutedEventArgs e)
    {
        if (choice == null) return;
        try
        {
            await choice.Service.CommandAsync("PauseRender", choice.Input.Key);
            foreach (var box in FindVisualChildren<TextBox>(TitleFieldsPanel))
                await choice.Service.CommandAsync("SetText", choice.Input.Key, new Dictionary<string, string> { ["SelectedName"] = box.Tag?.ToString() ?? "", ["Value"] = box.Text });
            await choice.Service.CommandAsync("ResumeRender", choice.Input.Key);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void PlayPause_OnClick(object sender, RoutedEventArgs e) =>
        await SendAsync(PlayPauseToggle.IsChecked == true ? "Play" : "Pause");
    private async void Restart_OnClick(object sender, RoutedEventArgs e) => await SendAsync("Restart");
    private async void First_OnClick(object sender, RoutedEventArgs e) => await SendAsync("SelectIndex", new() { ["Value"] = "1" });
    private async void Previous_OnClick(object sender, RoutedEventArgs e) => await SendAsync("PreviousItem");
    private async void Next_OnClick(object sender, RoutedEventArgs e) => await SendAsync("NextItem");
    private async void Go_OnClick(object sender, RoutedEventArgs e) => await GoAsync();
    private async void PreviousPreset_OnClick(object sender, RoutedEventArgs e) => await SendAsync("PreviousTitlePreset");
    private async void NextPreset_OnClick(object sender, RoutedEventArgs e) => await SendAsync("NextTitlePreset");

    private async void OverlayToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (choice == null || sender is not ToggleButton { Tag: var tag } toggle) return;
        try
        {
            if (toggle.IsChecked == true)
                await choice.Service.CommandAsync($"OverlayInput{tag}In", choice.Input.Key);
            else
                await choice.Service.CommandAsync($"OverlayInput{tag}Out");
        }
        catch (Exception ex)
        {
            RenderOverlayFeedback(choice.Input);
            ShowError(ex);
        }
    }

    private async void Loop_OnClick(object sender, RoutedEventArgs e) =>
        await SendAsync(LoopToggle.IsChecked == true ? "LoopOn" : "LoopOff");
    private async void AutoNext_OnClick(object sender, RoutedEventArgs e) =>
        await SendAsync(AutoNextToggle.IsChecked == true ? "AutoPlayNextOn" : "AutoPlayNextOff");

    private async void AudioBusToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: var tag } toggle) return;
        await SendAsync(toggle.IsChecked == true ? "AudioBusOn" : "AudioBusOff",
            new() { ["Value"] = tag?.ToString() ?? "" });
    }

    private void PositionSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => seeking = true;
    private async void PositionSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        await SendAsync("SetPosition", new() { ["Value"] = ((long)PositionSlider.Value).ToString() });
        seeking = false;
    }
    private void PositionSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (seeking) CurrentTimeText.Text = Time((long)e.NewValue);
    }

    private async void ClipList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClipList.SelectedIndex >= 0) await SendAsync("SelectIndex", new() { ["Value"] = (ClipList.SelectedIndex + 1).ToString() });
    }
    private void ClipList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private async void Mix_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || !int.TryParse(toggle.Tag?.ToString(), out var mix) || choice == null) return;
        if (toggle.IsChecked == true)
        {
            if (!config.SelectedMixes.Contains(mix)) config.SelectedMixes.Add(mix);
            await SendAsync("ActiveInput", new() { ["Mix"] = mix.ToString() });
        }
        else config.SelectedMixes.Remove(mix);
        saveRequested();
    }

    private void AtemOnSnapshotChanged(AtemSnapshot snapshot) => Dispatcher.BeginInvoke(async () =>
    {
        UpdateAtemInputs(snapshot);
        await EvaluateAtemAutomationAsync(snapshot);
    });

    private void UpdateAtemInputs(AtemSnapshot snapshot)
    {
        var selectedId = config?.AtemInputId ?? 0;
        var placeholder = new AtemInput(0, snapshot.IsConnected ? "Selecciona entrada ATEM…" : "ATEM sin conexión", "");
        var inputs = new[] { placeholder }.Concat(snapshot.Inputs).ToList();
        suppressSelection = true;
        AtemInputSelector.ItemsSource = inputs;
        AtemInputSelector.SelectedItem = inputs.FirstOrDefault(i => i.Id == selectedId) ?? placeholder;
        suppressSelection = false;
        UpdateAtemPanelState();
        AtemAutoToggle.ToolTip = snapshot.IsConnected
            ? "Play al entrar en PGM y Pause al salir"
            : $"ATEM {snapshot.Status}";
    }

    private void UpdateAtemPanelState()
    {
        var isList = choice?.Input.IsList == true;
        AtemInputSelector.IsEnabled = isList;
        AtemMeSelector.IsEnabled = isList;
        AtemAutoToggle.IsEnabled = isList;
        AtemPanelHint.Text = !isList
            ? "SOLO VIDEOLIST"
            : atem?.Snapshot.IsConnected == true
                ? $"{atem.Snapshot.Inputs.Count} ENTRADAS"
                : "ATEM SIN CONEXIÓN";
    }

    private async Task EvaluateAtemAutomationAsync(AtemSnapshot snapshot)
    {
        if (config == null || !config.AtemAutoPlayPause || choice?.Input.IsList != true || config.AtemInputId <= 0)
        {
            lastAtemOnAir = null;
            return;
        }
        if (!snapshot.IsConnected) return;
        var onAir = snapshot.IsOnProgram(config.AtemInputId, config.AtemMe);
        AtemAutoToggle.Content = onAir ? "PGM ●" : "AUTO";
        if (lastAtemOnAir == onAir) return;
        var previous = lastAtemOnAir;
        lastAtemOnAir = onAir;
        if (onAir)
        {
            await SendAsync("Play");
            LogService.Write("ATEM", $"Player {PlayerNumber}: Play por entrada {config.AtemInputId} en PGM");
        }
        else if (previous == true)
        {
            await SendAsync("Pause");
            LogService.Write("ATEM", $"Player {PlayerNumber}: Pause al retirar entrada {config.AtemInputId} de PGM");
        }
    }

    private void AtemInputSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelection || config == null) return;
        config.AtemInputId = AtemInputSelector.SelectedItem is AtemInput input ? input.Id : 0;
        lastAtemOnAir = false;
        saveRequested();
    }
    private void AtemMeSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (config == null || AtemMeSelector.SelectedItem is not AtemMeChoice selected) return;
        config.AtemMe = selected.Value;
        lastAtemOnAir = false;
        saveRequested();
    }
    private async void AtemAutoToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (config == null) return;
        if (AtemAutoToggle.IsChecked == true && (config.AtemInputId <= 0 || choice?.Input.IsList != true))
        {
            AtemAutoToggle.IsChecked = false;
            MessageBox.Show("Selecciona una lista de vídeo y una entrada ATEM antes de activar AUTO.", "Automatización ATEM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        config.AtemAutoPlayPause = AtemAutoToggle.IsChecked == true;
        lastAtemOnAir = config.AtemAutoPlayPause ? false : null;
        saveRequested();
        if (config.AtemAutoPlayPause) await EvaluateAtemAutomationAsync(atem.Snapshot);
    }

    private async void DataPrevious_OnClick(object sender, RoutedEventArgs e) => await SendDataSourceAsync("DataSourcePreviousRow");
    private async void DataNext_OnClick(object sender, RoutedEventArgs e) => await SendDataSourceAsync("DataSourceNextRow");
    private async void DataSelect_OnClick(object sender, RoutedEventArgs e) => await SendDataSourceAsync("DataSourceSelectRow", DataRowNumber.Text);
    private async Task SendDataSourceAsync(string function, string? row = null)
    {
        if (choice == null) return;
        var parts = new List<string> { DataSourceName.Text.Trim() };
        if (!string.IsNullOrWhiteSpace(DataTableName.Text)) parts.Add(DataTableName.Text.Trim());
        if (!string.IsNullOrWhiteSpace(row)) parts.Add(row.Trim());
        var args = new Dictionary<string, string> { ["Value"] = string.Join(",", parts) };
        try { await choice.Service.CommandAsync(function, null, args); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task SendAsync(string function, Dictionary<string, string>? parameters = null)
    {
        if (choice == null) return;
        try { await choice.Service.CommandAsync(function, choice.Input.Key, parameters); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SetTransientStatus(string text, bool success)
    {
        InputState.Text = text;
        InputState.Foreground = (Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
    }

    private void ShowError(Exception ex)
    {
        SetTransientStatus("ERROR", false);
        MessageBox.Show(ex.Message, "vMix Player Controller", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string Time(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
