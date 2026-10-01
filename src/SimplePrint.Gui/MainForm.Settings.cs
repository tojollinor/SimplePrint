using SimplePrint.Common;

namespace SimplePrint.Gui;

public sealed partial class MainForm
{
    private StatusRow _rowService = null!;
    private StatusRow _rowNetwork = null!;
    private StatusRow _rowFirewall = null!;
    private StatusRow _rowStartup = null!;

    private UiHealth _healthService;
    private UiHealth _healthNetwork;
    private UiHealth _healthFirewall;
    private UiHealth _healthStartup;

    private readonly CheckBox _startupTray = new()
    {
        AutoSize = true,
        Text = "GUI beim Windows-Start im Infobereich starten",
        Checked = true
    };

    private readonly Label _settingsDeviceName = SettingsValueLabel();
    private readonly Label _settingsDiscovery = SettingsValueLabel();
    private readonly Label _settingsGateway = SettingsValueLabel();
    private readonly Label _settingsDiagnostics = SettingsValueLabel();

    private static Label SettingsValueLabel() => new()
    {
        AutoSize = true,
        Text = "…",
        Margin = new Padding(3, 3, 3, 8)
    };

    private static Label SectionHeading(string text) => new()
    {
        AutoSize = true,
        Text = text,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold),
        Margin = new Padding(0, 10, 0, 6)
    };

    private TabPage CreateSettingsTab()
    {
        var tab = new TabPage("Einstellungen");

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(20)
        };

        panel.Controls.Add(SectionHeading("Status"));

        _rowService = new StatusRow(
            "Dienst",
            MakeActionButton("Neu starten", RestartServiceAsync));

        _rowNetwork = new StatusRow("Netzwerkprofil");

        _rowFirewall = new StatusRow(
            "Firewall",
            MakeActionButton("Regeln anwenden", ApplyFirewallAsync),
            MakeActionButton("Zurücksetzen", RemoveFirewallAsync),
            MakeActionButton("Neu prüfen", RefreshSystemManagementAsync));

        _rowStartup = new StatusRow(
            "Autostart",
            MakeActionButton("Aktivieren", () => SetStartupAsync(true)),
            MakeActionButton("Deaktivieren", () => SetStartupAsync(false)));

        foreach (var row in new[] { _rowService, _rowNetwork, _rowFirewall, _rowStartup })
            row.SetState(UiHealth.Unknown, "wird geprüft …");

        panel.Controls.Add(_rowService);
        panel.Controls.Add(_rowNetwork);
        panel.Controls.Add(_rowFirewall);
        panel.Controls.Add(_rowStartup);

        _startupTray.Margin = new Padding(8, 0, 0, 8);
        panel.Controls.Add(_startupTray);

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = UiColors.Muted,
            Margin = new Padding(0, 0, 0, 6),
            Text =
                "Firewall: SimplePrint benötigt eingehend UDP für die Geräteerkennung sowie TCP für " +
                "Druckdaten und Diagnose, ausschließlich für Privat/Domäne und das lokale Subnetz."
        });

        panel.Controls.Add(SectionHeading("Gerät"));

        var deviceTable = new TableLayoutPanel
        {
            AutoSize = true,
            Width = 820,
            ColumnCount = 2,
            RowCount = 4,
            Margin = new Padding(0, 0, 0, 10)
        };

        deviceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        deviceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 560));

        AddSettingsRow(deviceTable, 0, "Gerätename", _settingsDeviceName);
        AddSettingsRow(deviceTable, 1, "Discovery-Port (UDP)", _settingsDiscovery);
        AddSettingsRow(deviceTable, 2, "Print-Gateway (TCP)", _settingsGateway);
        AddSettingsRow(deviceTable, 3, "Diagnose-Port (TCP)", _settingsDiagnostics);

        panel.Controls.Add(deviceTable);

        panel.Controls.Add(SectionHeading("Updates"));

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = UiColors.Muted,
            Margin = new Padding(0, 0, 0, 6),
            Text =
                "Die Updateprüfung verwendet das gemeinsame SimplePrint-Release. " +
                "Die eigentliche Aktualisierung läuft nach Administratorfreigabe automatisch."
        });

        var updateButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 0, 0, 10)
        };

        updateButtons.Controls.Add(MakeButton(
            "Nach Updates suchen",
            async (_, _) => await CheckForUpdatesAsync(true)));

        panel.Controls.Add(updateButtons);

        panel.Controls.Add(SectionHeading("Konfiguration"));

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = UiColors.Muted,
            Text = AppPaths.DeviceConfig
        });

        tab.Controls.Add(panel);
        return tab;
    }

    private static void AddSettingsRow(
        TableLayoutPanel panel,
        int row,
        string title,
        Label value)
    {
        panel.Controls.Add(BoldLabel(title), 0, row);
        panel.Controls.Add(value, 1, row);
    }

    private void RefreshSettings()
    {
        _settingsDeviceName.Text = _config.DeviceName;
        _settingsDiscovery.Text = _config.DiscoveryPort.ToString();
        _settingsGateway.Text = _config.GatewayPort.ToString();
        _settingsDiagnostics.Text = _config.DiagnosticsPort.ToString();
    }

    // ---------- Status lesen ----------

    // Dienststatus direkt aus der Windows-Dienststeuerung (kein PowerShell-Prozess).
    private void ApplyServiceStatus(string text)
    {
        _serviceStatus.Text = text;

        var running = text.Equals(
            ServiceStatusReader.Running,
            StringComparison.OrdinalIgnoreCase);

        var health = running
            ? UiHealth.Ok
            : text is "Startet" or "Wird beendet" or "Wird fortgesetzt" or "Wird angehalten" or "Unbekannt"
                ? UiHealth.Warning
                : UiHealth.Error;

        _healthService = health;
        _serviceStatus.ForeColor = UiColors.For(health);
        _rowService.SetState(
            health,
            running
                ? "SimplePrint-Dienst läuft"
                : $"SimplePrint-Dienst: {text}");
    }

    private static async Task<NetworkProfileState?> ReadNetworkStateAsync()
    {
        try
        {
            return await NetworkProfileHelper.GetStateAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task<(UiHealth Health, string Text)> ReadFirewallStateAsync()
    {
        var config = _config;

        try
        {
            var firewall = await UnifiedSystemManager.GetFirewallStateAsync(config);

            if (firewall.Correct)
                return (UiHealth.Ok, "Discovery, Gateway und Diagnose sind freigegeben");

            var missing = new[]
                {
                    firewall.Discovery.Correct ? null : "Discovery",
                    firewall.Gateway.Correct ? null : "Gateway",
                    firewall.Diagnostics.Correct ? null : "Diagnose"
                }
                .Where(x => x is not null);

            return (
                UiHealth.Error,
                $"Unvollständig ({string.Join(", ", missing)}) – „Regeln anwenden“ klicken");
        }
        catch (Exception ex)
        {
            return (UiHealth.Warning, "Status konnte nicht gelesen werden – " + ex.Message);
        }
    }

    private async Task RefreshSystemManagementAsync()
    {
        ApplyServiceStatus(ServiceStatusReader.GetStatusText("SimplePrint"));

        // Netzwerkprofil und Firewall werden parallel abgefragt.
        var networkTask = ReadNetworkStateAsync();
        var firewallTask = ReadFirewallStateAsync();

        await Task.WhenAll(networkTask, firewallTask);

        var network = networkTask.Result;

        if (network is null)
        {
            _healthNetwork = UiHealth.Warning;
            _rowNetwork.SetState(UiHealth.Warning, "Status konnte nicht gelesen werden");
        }
        else if (network.HasPublicProfile)
        {
            _healthNetwork = UiHealth.Error;
            _rowNetwork.SetState(
                UiHealth.Error,
                "Öffentlich – der Netzwerkzugriff von SimplePrint ist blockiert. " +
                "Stelle das Netzwerk in Windows auf „Privat“.");
        }
        else
        {
            _healthNetwork = UiHealth.Ok;
            _rowNetwork.SetState(UiHealth.Ok, "Privat/Domäne – bereit");
        }

        var (firewallHealth, firewallText) = firewallTask.Result;
        _healthFirewall = firewallHealth;
        _rowFirewall.SetState(firewallHealth, firewallText);

        RefreshStartupState();
        RefreshOverview();

        if (network is { HasPublicProfile: true })
            SetStatus("⚠ Öffentliches Netzwerk: SimplePrint ist im Netzwerk eingeschränkt.");
    }

    private void RefreshStartupState()
    {
        var enabled = StartupManager.IsSystemWideEnabled("SimplePrintGui");

        // Ein deaktivierter Autostart ist kein Problem, sondern eine Wahl.
        _healthStartup = UiHealth.Ok;
        _rowStartup.SetState(
            UiHealth.Ok,
            enabled
                ? "GUI-Autostart aktiviert"
                : "GUI-Autostart deaktiviert (optional)");

        _startupTray.Checked = enabled
            ? StartupManager.IsTrayModeEnabled("SimplePrintGui", true)
            : true;
    }

    // ---------- Aktionen ----------

    private async Task SetStartupAsync(bool enabled)
    {
        try
        {
            SetBusy(
                enabled
                    ? "Autostart wird aktiviert …"
                    : "Autostart wird deaktiviert …");

            await StartupManager.SetSystemWideAsync(
                "SimplePrintGui",
                Application.ExecutablePath,
                enabled,
                _startupTray.Checked);

            RefreshStartupState();
            RefreshOverview();

            SetStatus(
                enabled
                    ? "✓ Autostart aktiviert"
                    : "✓ Autostart deaktiviert");
        }
        catch (OperationCanceledException ex)
        {
            SetStatus("Autostart-Änderung abgebrochen");
            MessageBox.Show(
                ex.Message,
                "Autostart",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Autostart konnte nicht geändert werden");
            MessageBox.Show(
                ex.Message,
                "Autostart",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task RestartServiceAsync()
    {
        try
        {
            SetBusy("SimplePrint-Dienst wird neu gestartet …");
            await UnifiedSystemManager.RestartServiceAsync();
            await Task.Delay(700);
            await RefreshSystemManagementAsync();
            SetStatus("✓ SimplePrint-Dienst läuft");
        }
        catch (OperationCanceledException ex)
        {
            SetStatus("Dienstneustart abgebrochen");
            MessageBox.Show(
                ex.Message,
                "SimplePrint-Dienst",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("✗ SimplePrint-Dienst konnte nicht neu gestartet werden");
            MessageBox.Show(
                ex.Message,
                "SimplePrint-Dienst",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task ApplyFirewallAsync()
    {
        try
        {
            SetBusy("Firewall-Regeln werden angewendet und geprüft …");

            await UnifiedSystemManager.ApplyFirewallAsync(_config);

            var state = await UnifiedSystemManager.GetFirewallStateAsync(_config);
            if (!state.Correct)
            {
                throw new InvalidOperationException(
                    "Windows hat nicht alle SimplePrint-Firewallregeln korrekt übernommen." +
                    Environment.NewLine +
                    $"Discovery: {state.Discovery.Detail}" +
                    Environment.NewLine +
                    $"Gateway: {state.Gateway.Detail}" +
                    Environment.NewLine +
                    $"Diagnose: {state.Diagnostics.Detail}");
            }

            await RefreshSystemManagementAsync();
            SetStatus("✓ Firewall-Regeln angewendet und verifiziert");
        }
        catch (OperationCanceledException ex)
        {
            SetStatus("Firewall-Änderung abgebrochen");
            MessageBox.Show(
                ex.Message,
                "Firewall",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Firewall-Regeln konnten nicht angewendet werden");
            MessageBox.Show(
                ex.Message,
                "Firewall",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task RemoveFirewallAsync()
    {
        if (MessageBox.Show(
                "Die drei SimplePrint-Kernregeln für Discovery, Druck-Gateway und Diagnose entfernen?",
                "Firewall zurücksetzen",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy("Firewall-Regeln werden entfernt …");
            await UnifiedSystemManager.RemoveFirewallAsync();
            await RefreshSystemManagementAsync();
            SetStatus("✓ Firewall-Kernregeln entfernt");
        }
        catch (OperationCanceledException ex)
        {
            SetStatus("Firewall-Änderung abgebrochen");
            MessageBox.Show(
                ex.Message,
                "Firewall",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Firewall-Regeln konnten nicht entfernt werden");
            MessageBox.Show(
                ex.Message,
                "Firewall",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckRunning)
        {
            if (manual)
                SetStatus("Updateprüfung läuft bereits …");

            return;
        }

        _updateCheckRunning = true;

        try
        {
            if (manual)
                SetBusy("GitHub Releases werden geprüft …");

            var current =
                typeof(MainForm).Assembly.GetName().Version
                ?? new Version(0, 0, 0, 0);

            var update = await GitHubUpdateService.CheckAsync(
                current,
                SimplePrintComponent.Unified);

            if (update is null)
            {
                if (manual)
                {
                    SetStatus("✓ SimplePrint ist aktuell");
                    MessageBox.Show(
                        "Es ist kein neueres SimplePrint-Release verfügbar.",
                        "SimplePrint Update",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return;
            }

            if (!manual &&
                string.Equals(
                    _lastOfferedUpdate,
                    update.TagName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastOfferedUpdate = update.TagName;
            SetStatus($"Update verfügbar: {update.TagName}");

            using var dialog = new UpdateForm(update);
            dialog.ShowDialog(Visible ? this : null);

            if (dialog.InstallerStarted)
                ExitApplication();
        }
        catch (Exception ex)
        {
            if (manual)
            {
                SetStatus("✗ Updateprüfung fehlgeschlagen");
                MessageBox.Show(
                    ex.Message,
                    "SimplePrint Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _updateCheckRunning = false;

            if (manual && _operationProgress.Visible)
                SetIdle();
        }
    }
}
