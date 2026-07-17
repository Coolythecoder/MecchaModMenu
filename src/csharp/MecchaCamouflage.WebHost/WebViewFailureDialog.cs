using System.Diagnostics;

namespace MecchaCamouflage.WebHost;

internal enum WebViewRecoveryAction
{
    Close,
    Retry
}

internal static class WebViewFailureDialog
{
    public static WebViewRecoveryAction Show(
        IWin32Window owner,
        string message,
        string diagnostics,
        string manualInstallUrl,
        bool retryAvailable)
    {
        using var dialog = new Form
        {
            Text = "Meccha Mod Menu",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(620, 280),
            AutoScaleMode = AutoScaleMode.Dpi
        };

        var heading = new Label
        {
            AutoSize = false,
            Left = 18,
            Top = 18,
            Width = 584,
            Height = 52,
            Text = message,
            Font = new Font(dialog.Font, FontStyle.Bold)
        };
        var description = new Label
        {
            AutoSize = false,
            Left = 18,
            Top = 76,
            Width = 584,
            Height = 34,
            Text = "Diagnostic details are included below. If the runtime is missing or installation failed, install Microsoft Edge WebView2 Runtime from the official download page."
        };
        var manualInstallLink = new LinkLabel
        {
            AutoSize = true,
            Left = 18,
            Top = 114,
            Text = "Open official WebView2 Runtime download page"
        };
        manualInstallLink.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(manualInstallUrl) { UseShellExecute = true });
            }
            catch
            {
                // The diagnostic text remains available if the default browser cannot be opened.
            }
        };
        var details = new TextBox
        {
            Left = 18,
            Top = 142,
            Width = 584,
            Height = 88,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = diagnostics
        };
        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Left = 527,
            Top = 244,
            Width = 75
        };
        dialog.CancelButton = close;
        dialog.Controls.AddRange([heading, description, manualInstallLink, details, close]);

        if (retryAvailable)
        {
            var retry = new Button
            {
                Text = "Retry once",
                DialogResult = DialogResult.Retry,
                Left = 435,
                Top = 244,
                Width = 84
            };
            dialog.AcceptButton = retry;
            dialog.Controls.Add(retry);
        }

        return dialog.ShowDialog(owner) == DialogResult.Retry
            ? WebViewRecoveryAction.Retry
            : WebViewRecoveryAction.Close;
    }
}
