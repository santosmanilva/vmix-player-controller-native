using System.Net;
using System.Windows;

namespace VMixPlayerController;

public partial class SettingsWindow : Window
{
    private readonly AppSettings settings;

    public SettingsWindow(AppSettings appSettings)
    {
        InitializeComponent();
        settings = appSettings;
        VmixAHostBox.Text = settings.VmixA.Host;
        VmixAPortBox.Text = settings.VmixA.Port.ToString();
        VmixBHostBox.Text = settings.VmixB.Host;
        VmixBPortBox.Text = settings.VmixB.Port.ToString();
        UseVmixBCheck.IsChecked = settings.UseVmixB;
        UpdateVmixBFields();
        AtemHostBox.Text = settings.AtemHost;
        AutoConnectCheck.IsChecked = settings.AutoConnect;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var vmixAHost = VmixAHostBox.Text.Trim();
        var vmixBHost = VmixBHostBox.Text.Trim();
        var atemHost = AtemHostBox.Text.Trim();
        var useVmixB = UseVmixBCheck.IsChecked == true;
        if (string.IsNullOrWhiteSpace(vmixAHost) || useVmixB && string.IsNullOrWhiteSpace(vmixBHost))
        {
            MessageBox.Show(this, useVmixB ? "Introduce la IP o el nombre de ambos equipos vMix." : "Introduce la IP o el nombre de vMix A.", "Configuración", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryPort(VmixAPortBox.Text, out var vmixAPort) || useVmixB && !TryPort(VmixBPortBox.Text, out _))
        {
            MessageBox.Show(this, "Los puertos de vMix deben estar entre 1 y 65535.", "Configuración", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IPAddress.TryParse(atemHost, out _))
        {
            MessageBox.Show(this, "Introduce una dirección IP válida para la ATEM.", "Configuración", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        settings.VmixA.Host = vmixAHost;
        settings.VmixA.Port = vmixAPort;
        settings.UseVmixB = useVmixB;
        if (useVmixB)
        {
            settings.VmixB.Host = vmixBHost;
            settings.VmixB.Port = int.Parse(VmixBPortBox.Text.Trim());
        }
        settings.AtemHost = atemHost;
        settings.AutoConnect = AutoConnectCheck.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UseVmixBCheck_OnChanged(object sender, RoutedEventArgs e) => UpdateVmixBFields();

    private void UpdateVmixBFields()
    {
        if (VmixBHostBox == null || VmixBPortBox == null) return;
        var enabled = UseVmixBCheck.IsChecked == true;
        VmixBHostBox.IsEnabled = enabled;
        VmixBPortBox.IsEnabled = enabled;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), out port) && port is > 0 and <= 65535;
}
