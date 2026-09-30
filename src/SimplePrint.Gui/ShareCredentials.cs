using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SimplePrint.Gui;

internal sealed record ShareCredentials(string UserName, string Password);

/// <summary>
/// Speichert Zugangsdaten für einen Server in der Windows-Anmeldeinformationsverwaltung
/// des aktuellen Benutzers (wie "cmdkey /add", aber ohne Passwort in einer Kommandozeile).
/// </summary>
internal static class ShareCredentialStore
{
    private const uint CRED_TYPE_DOMAIN_PASSWORD = 2;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    public static void Save(string server, string userName, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);

        try
        {
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_DOMAIN_PASSWORD,
                TargetName = server,
                Comment = "SimplePrint",
                CredentialBlobSize = (uint)(password.Length * 2),
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                TargetAlias = "",
                UserName = userName
            };

            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }
}

internal static class ShareCredentialPrompt
{
    public static void Notify(string text, string title, MessageBoxIcon icon)
    {
        RunOnUi(() =>
        {
            MessageBox.Show(text, title, MessageBoxButtons.OK, icon);
            return 0;
        });
    }

    /// <summary>
    /// Fragt Zugangsdaten für die Windows-Druckerfreigabe ab. previousError ist null beim
    /// ersten Versuch; bei einer Wiederholung enthält er die Windows-Meldung des
    /// abgelehnten Versuchs (leer, wenn keine vorhanden ist).
    /// </summary>
    public static ShareCredentials? Ask(
        string server,
        string printer,
        string serverDeviceName,
        string? previousError)
    {
        return RunOnUi(() =>
        {
            var owner = Application.OpenForms
                .Cast<Form>()
                .FirstOrDefault(x => x.Visible);

            using var dialog = new Form
            {
                Text = "Windows-Anmeldung für die Druckerfreigabe",
                Width = 660,
                Height = 600,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true
            };

            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(16),
                AutoScroll = true
            };

            Label MakeLabel(string text, bool bold = false, Color? color = null) => new()
            {
                AutoSize = true,
                MaximumSize = new Size(600, 0),
                Margin = new Padding(0, 0, 0, 10),
                Text = text,
                Font = bold ? new Font(dialog.Font, FontStyle.Bold) : dialog.Font,
                ForeColor = color ?? SystemColors.ControlText
            };

            if (previousError is not null)
            {
                panel.Controls.Add(MakeLabel(
                    "Die Anmeldung wurde abgelehnt. Bitte Benutzername und Passwort prüfen." +
                    (string.IsNullOrWhiteSpace(previousError)
                        ? ""
                        : "\r\n\r\nWindows meldet: " + previousError),
                    true,
                    Color.Firebrick));
            }

            panel.Controls.Add(MakeLabel(
                $"Der Server '{server}' verlangt für die Windows-Druckerfreigabe von '{printer}' " +
                "eine Anmeldung.\r\n\r\n" +
                "Gib ein Benutzerkonto des Servers ein. Die Zugangsdaten werden in der " +
                "Windows-Anmeldeinformationsverwaltung deines Benutzers gespeichert, nicht in SimplePrint."));

            panel.Controls.Add(MakeLabel("Benutzername (Format: SERVERNAME\\Benutzer)", true));

            var user = new TextBox
            {
                Width = 600,
                Margin = new Padding(0, 0, 0, 10),
                Text = string.IsNullOrWhiteSpace(serverDeviceName)
                    ? ""
                    : serverDeviceName.Trim() + "\\"
            };
            panel.Controls.Add(user);

            panel.Controls.Add(MakeLabel("Passwort", true));

            var password = new TextBox
            {
                Width = 600,
                UseSystemPasswordChar = true,
                Margin = new Padding(0, 0, 0, 14)
            };
            panel.Controls.Add(password);

            panel.Controls.Add(MakeLabel(
                "Alternative ohne Zugangsdaten: Auf dem Server \"Kennwortgeschütztes Freigeben\" " +
                "ausschalten (Netzwerk- und Freigabecenter → Erweiterte Freigabeeinstellungen) " +
                "und danach erneut versuchen. Das macht Freigaben im lokalen Netz offener. " +
                "Konten ohne Passwort können sich standardmäßig nicht über das Netzwerk anmelden.",
                false,
                SystemColors.GrayText));

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(12)
            };

            var cancel = new Button
            {
                Text = "Abbrechen",
                AutoSize = true,
                MinimumSize = new Size(110, 30),
                DialogResult = DialogResult.Cancel
            };

            var ok = new Button
            {
                Text = "Anmelden und erneut versuchen",
                AutoSize = true,
                MinimumSize = new Size(230, 30)
            };

            ok.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(user.Text) ||
                    string.IsNullOrEmpty(password.Text))
                {
                    MessageBox.Show(
                        dialog,
                        "Bitte Benutzername und Passwort eingeben.",
                        "SimplePrint",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                dialog.DialogResult = DialogResult.OK;
            };

            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            dialog.Controls.Add(panel);
            dialog.Controls.Add(buttons);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;

            return dialog.ShowDialog(owner) == DialogResult.OK
                ? new ShareCredentials(user.Text.Trim(), password.Text)
                : null;
        });
    }

    private static T RunOnUi<T>(Func<T> action)
    {
        var owner = Application.OpenForms
            .Cast<Form>()
            .FirstOrDefault(x => x.Visible);

        if (owner is { InvokeRequired: true })
            return (T)owner.Invoke(action)!;

        return action();
    }
}
