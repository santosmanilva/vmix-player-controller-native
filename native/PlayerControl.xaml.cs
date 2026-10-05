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
    private readonly AtemPlaybackAutomation automation = new();
    private bool goRunning;
    private bool titlesRunning;
    private int lastPlayingIndex = -1;
    private readonly HashSet<string> dirtyTitleFields = new();
    private bool updatingTitleValues;
    private string sourceSignature = "";

    public int PlayerNumber { get; private set; }

    public PlayerControl() => InitializeComponent();

    public void Configure(int number, PlayerConfig playerConfig, IEnumerable<VmixConnectionService> vmixServices, AtemConnectionService atemService, Action onSaveRequested)
    {
        foreach (var oldService in services)
        {
            oldService.SnapshotChanged -= ServiceOnSnapshotChanged;
            oldService.ConnectionChanged -= ServiceOnConnectionChanged;
        }
        if (atem != null) atem.SnapshotChanged -= AtemOnSnapshotChanged;
        services.Clear();
        automation.Reset();
        choice = null;
        sourceSignature = "";
        lastPlayingIndex = -1;
        dirtyTitleFields.Clear();
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
        suppressSelection = true;
        AtemMeSelector.ItemsSource = AtemMeChoices;
        AtemMeSelector.SelectedItem = AtemMeChoices.FirstOrDefault(choice => choice.Value == config.AtemMe) ?? AtemMeChoices[0];
        AtemAutoToggle.IsChecked = config.AtemAutoPlayPause;
        suppressSelection = false;
        UpdateSources();
        UpdateAtemInputs(atem.Snapshot);
    }

    public async Task GoAsync()
    {
        var target = choice;
        if (target == null || !target.Service.IsConnected || goRunning) return;
        goRunning = true;
        var commands = config.SelectedMixes.Order().Select(mix => new VmixCommand("ActiveInput", target.Input.Key,
            new Dictionary<string, string> { ["Mix"] = mix.ToString() })).ToList();
        if (config.GoAudioAuto) commands.Add(new("AudioAuto", target.Input.Key));
        if (config.GoRestart) commands.Add(new("Restart", target.Input.Key));
        commands.Add(new("Play", target.Input.Key));
        try
        {
            await target.Service.ExecuteBatchAsync(commands);
            SetTransientStatus("GO EJECUTADO", true);
        }
        catch (OperationCanceledException) { SetTransientStatus("GO CANCELADO", false); }
        catch (Exception ex) { ShowError(ex); }
        finally { goRunning = false; }
    }

    public async Task TogglePlayAsync()
    {
        if (choice == null) return;
        await SendAsync(choice.Input.IsPlaying ? "Pause" : "Play");
    }

    private void ServiceOnSnapshotChanged(VmixConnectionService service, VmixSnapshot snapshot) => Dispatcher.BeginInvoke(async () =>
    {
        var signature = BuildSourceSignature();
        if (signature != sourceSignature) UpdateSources();
        if (choice?.Service == service)
        {
            // Use the most recent snapshot if multiple notifications were queued.
            var updated = service.Snapshot.Inputs.FirstOrDefault(i => i.Key == choice.Input.Key);
            if (updated != null)
            {
                var listChanged = !updated.ListItems.SequenceEqual(choice.Input.ListItems);
                var fieldsChanged = !updated.TextFields.Select(i => i.Name).SequenceEqual(choice.Input.TextFields.Select(i => i.Name));
                choice = new InputChoice(service, updated);
                RenderState(updated, listChanged, fieldsChanged);
            }
        }
        UpdateAvailability();
        await EvaluateAtemAutomationAsync(atem.Snapshot);
    });

    private void ServiceOnConnectionChanged(VmixConnectionService _) => Dispatcher.BeginInvoke(async () =>
    {
        UpdateSources();
        await EvaluateAtemAutomationAsync(atem.Snapshot);
    });

    private string BuildSourceSignature() => string.Join("|", services.SelectMany(s =>
        s.Snapshot.Inputs.Select(i => $"{s.Name}:{s.IsEnabled}:{i.Key}:{i.Number}:{i.Title}:{i.Type}")));

    private void UpdateSources()
    {
        if (config == null) return;
        var choices = services.Where(s => s.IsEnabled).SelectMany(s => s.Snapshot.Inputs.Select(i => new InputChoice(s, i)))
            .OrderBy(c => c.Service.Name).ThenBy(c => c.Input.Number).ToList();
        sourceSignature = BuildSourceSignature();
        suppressSelection = true;
        InputSelector.ItemsSource = choices;
        var selected = choices.FirstOrDefault(c => c.Service.Name == config.VmixName && c.Input.Key == config.InputKey);
        InputSelector.SelectedItem = selected;
        suppressSelection = false;
        var assignmentChanged = choice?.Service != selected?.Service || choice?.Input.Key != selected?.Input.Key;
        choice = selected;
        if (selected != null)
        {
            RenderMode(selected.Input);
            RenderState(selected.Input, true, assignmentChanged);
        }
        else if (choices.Count > 0 && string.IsNullOrEmpty(config.InputKey)) InputSelector.SelectedIndex = 0;
        else
        {
            InputKind.Text = string.IsNullOrEmpty(config.InputKey) ? "SIN ASIGNAR" : "INPUT NO DISPONIBLE";
            NowText.Text = "Sin contenido disponible";
            NextText.Text = "SIGUIENTE —";
            ClipList.ItemsSource = null;
            TitleFieldsPanel.Children.Clear();
            RemainingText.Text = "—";
        }
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        var available = choice?.Service.IsConnected == true;
        foreach (var panel in new UIElement[] { TransportButtonsPanel, PositionPanel, ListOptionsPanel, AudioBusPanel, TitleScroll, ClipList })
            panel.IsEnabled = available;
        if (!available)
        {
            InputState.Text = "SIN DATOS";
            InputState.Foreground = (Brush)FindResource("DangerBrush");
            var received = choice?.Service.Snapshot.ReceivedAt ?? DateTime.MinValue;
            InputState.ToolTip = received == DateTime.MinValue ? "No hay un estado válido de vMix" : $"Último estado recibido: {received:HH:mm:ss}";
            RemainingText.Text = "—";
        }
        else InputState.ToolTip = null;
    }

    private void InputSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelection || InputSelector.SelectedItem is not InputChoice selected) return;
        choice = selected;
        config.VmixName = selected.Service.Name;
        config.InputKey = selected.Input.Key;
        automation.Reset();
        lastPlayingIndex = -1;
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
                if ((lastPlayingIndex != currentIndex || listChanged) && currentIndex < ClipList.Items.Count)
                {
                    ClipList.SelectedIndex = currentIndex;
                    ClipList.ScrollIntoView(ClipList.SelectedItem);
                }
                lastPlayingIndex = currentIndex;
            }
            else
            {
                NowText.Text = input.Title;
                NextText.Text = input.IsTitle ? $"{input.TextFields.Count} CAMPOS EDITABLES" : input.Type;
            }

            if (input.IsTitle && (fieldsChanged || TitleFieldsPanel.Children.Count != input.TextFields.Count)) BuildTitleFields(input);
            if (input.IsTitle)
            {
                RefreshTitleValues(input);
                RenderOverlayFeedback(input);
            }
            RenderMixFeedback(input);
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
        dirtyTitleFields.Clear();
        foreach (var field in input.TextFields)
        {
            var box = new TextBox { Text = field.Value, Tag = field.Name, MinWidth = 180 };
            box.TextChanged += (_, _) =>
            {
                if (!updatingTitleValues) dirtyTitleFields.Add(field.Name);
            };
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

    private void RefreshTitleValues(VmixInput input)
    {
        updatingTitleValues = true;
        try
        {
            foreach (var box in FindVisualChildren<TextBox>(TitleFieldsPanel))
            {
                var name = box.Tag?.ToString() ?? "";
                var value = input.TextFields.FirstOrDefault(f => f.Name == name)?.Value;
                if (value == null) continue;
                if (!box.IsKeyboardFocusWithin && !dirtyTitleFields.Contains(name)) box.Text = value;
            }
        }
        finally { updatingTitleValues = false; }
    }

    private void RenderMixFeedback(VmixInput input)
    {
        foreach (var toggle in FindVisualChildren<ToggleButton>(ListOptionsPanel))
        {
            if (!int.TryParse(toggle.Tag?.ToString(), out var mix)) continue;
            var onOutput = choice != null && choice.Service.Snapshot.MixActive.TryGetValue(mix, out var active) && active == input.Number;
            toggle.Content = $"{mix + 1}{(onOutput ? " ●" : "")}";
            toggle.ToolTip = $"Destino de GO {(config.SelectedMixes.Contains(mix) ? "seleccionado" : "no seleccionado")}. " +
                (onOutput ? "Este input está en la salida de este Mix." : "Este input no está en la salida de este Mix.");
        }
    }

    private async Task UpdateTitleFieldAsync(TextBox box)
    {
        var target = choice;
        var name = box.Tag?.ToString() ?? "";
        var value = box.Text;
        if (await SendAsync("SetText", new() { ["SelectedName"] = name, ["Value"] = value }) && choice?.Service == target?.Service && choice?.Input.Key == target?.Input.Key && box.Text == value)
            dirtyTitleFields.Remove(name);
    }

    private async void ApplyAllTitles_OnClick(object sender, RoutedEventArgs e)
    {
        var target = choice;
        if (target == null || titlesRunning) return;
        var fields = FindVisualChildren<TextBox>(TitleFieldsPanel)
            .Select(box => (Box: box, Name: box.Tag?.ToString() ?? "", Value: box.Text)).ToList();
        var commands = new List<VmixCommand> { new("PauseRender", target.Input.Key) };
        commands.AddRange(fields.Select(field => new VmixCommand("SetText", target.Input.Key,
            new Dictionary<string, string> { ["SelectedName"] = field.Name, ["Value"] = field.Value })));
        titlesRunning = true;
        try
        {
            await target.Service.ExecuteBatchAsync(commands, cleanup: new("ResumeRender", target.Input.Key));
            foreach (var field in fields)
                if (choice?.Service == target.Service && choice?.Input.Key == target.Input.Key && field.Box.Text == field.Value)
                    dirtyTitleFields.Remove(field.Name);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { titlesRunning = false; }
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
            if (choice != null) RenderOverlayFeedback(choice.Input);
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

    private void Mix_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || !int.TryParse(toggle.Tag?.ToString(), out var mix) || choice == null) return;
        if (toggle.IsChecked == true)
        {
            if (!config.SelectedMixes.Contains(mix)) config.SelectedMixes.Add(mix);
        }
        else config.SelectedMixes.Remove(mix);
        RenderMixFeedback(choice.Input);
        saveRequested();
    }

    private void AtemOnSnapshotChanged(AtemSnapshot snapshot) => Dispatcher.BeginInvoke(async () =>
    {
        UpdateAtemInputs(atem.Snapshot);
        await EvaluateAtemAutomationAsync(atem.Snapshot);
    });

    private void UpdateAtemInputs(AtemSnapshot snapshot)
    {
        var selectedId = config?.AtemInputId ?? 0;
        var placeholder = new AtemInput(0, snapshot.IsConnected ? "Selecciona entrada ATEM…" : "ATEM sin conexión", "");
        var inputs = new[] { placeholder }.Concat(snapshot.Inputs).ToList();
        if (AtemInputSelector.ItemsSource is List<AtemInput> oldInputs && oldInputs.SequenceEqual(inputs))
        {
            UpdateAtemPanelState();
            return;
        }
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
        var target = choice;
        if (config == null || !config.AtemAutoPlayPause || target?.Input.IsList != true || config.AtemInputId <= 0)
        {
            automation.Reset();
            AtemAutoToggle.Content = "AUTO";
            return;
        }
        bool? onAir = snapshot.IsConnected && target.Service.IsConnected
            ? snapshot.IsOnProgram(config.AtemInputId, config.AtemMe) : null;
        var identity = $"{target.Service.Name}:{target.Service.ConnectionGeneration}:{target.Input.Key}:{config.AtemInputId}:{config.AtemMe}";
        await automation.ReconcileAsync(identity, onAir, async (play, token) =>
        {
            await target.Service.CommandAsync(play ? "Play" : "Pause", target.Input.Key, token: token);
            LogService.Write("ATEM", $"Player {PlayerNumber}: {(play ? "Play" : "Pause")} confirmado por HTTP");
        });
        AtemAutoToggle.Content = onAir == null ? "AUTO ?" : automation.IsPending ? "AUTO …" : onAir == true ? "PGM ●" : "AUTO";
        AtemAutoToggle.ToolTip = string.IsNullOrEmpty(automation.LastError)
            ? "Play al entrar en PGM y Pause al salir; se recupera al reconectar"
            : $"Orden pendiente: {automation.LastError}";
    }

    private void AtemInputSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelection || config == null) return;
        config.AtemInputId = AtemInputSelector.SelectedItem is AtemInput input ? input.Id : 0;
        automation.Reset();
        saveRequested();
    }
    private void AtemMeSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelection || config == null || AtemMeSelector.SelectedItem is not AtemMeChoice selected) return;
        config.AtemMe = selected.Value;
        automation.Reset();
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
        automation.Reset();
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

    private async Task<bool> SendAsync(string function, Dictionary<string, string>? parameters = null)
    {
        var target = choice;
        if (target == null || !target.Service.IsConnected) return false;
        try
        {
            await target.Service.CommandAsync(function, target.Input.Key, parameters);
            return true;
        }
        catch (OperationCanceledException) { SetTransientStatus("ORDEN CANCELADA", false); }
        catch (Exception ex) { ShowError(ex); }
        return false;
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
