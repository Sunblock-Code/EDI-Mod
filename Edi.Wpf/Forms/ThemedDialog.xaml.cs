using System;
using System.Windows;

namespace Edi.Forms
{
    public partial class ThemedDialog : Window
    {
        private readonly string? _requiredText;

        public ThemedDialog(string title, string header, string body, bool yesNo, string? requiredText = null,
                            bool freeInput = false, string? initialText = null, string? iconGlyph = null)
        {
            DarkTitleBar.Apply(this);
            InitializeComponent();
            Title = title;
            lblHeader.Text = header;
            lblBody.Text = body;
            if (!string.IsNullOrEmpty(iconGlyph)) icon.Text = iconGlyph;
            if (!yesNo)
            {
                btnNo.Visibility = Visibility.Collapsed;
                btnYes.Content = "OK";
            }
            _requiredText = requiredText;
            if (freeInput)
            {
                txtInput.Visibility = Visibility.Visible;
                txtInput.Text = initialText ?? "";
                btnYes.Content = "OK";
                btnYes.IsEnabled = !string.IsNullOrWhiteSpace(txtInput.Text);
                txtInput.TextChanged += (_, _) =>
                    btnYes.IsEnabled = !string.IsNullOrWhiteSpace(txtInput.Text);
                Loaded += (_, _) => { txtInput.Focus(); txtInput.SelectAll(); };
            }
            else if (!string.IsNullOrEmpty(requiredText))
            {
                txtInput.Visibility = Visibility.Visible;
                btnYes.Content = "Confirm";
                btnYes.IsEnabled = false;
                txtInput.TextChanged += (_, _) =>
                    btnYes.IsEnabled = string.Equals(txtInput.Text.Trim(), _requiredText, StringComparison.OrdinalIgnoreCase);
                Loaded += (_, _) => txtInput.Focus();
            }
        }

        private void Yes_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
        private void No_Click(object sender, RoutedEventArgs e)  { DialogResult = false; Close(); }

        // Themed dialog requiring the user to type a specific word to confirm.
        public static bool RequireText(Window owner, string title, string header, string body, string requiredText)
        {
            var dlg = new ThemedDialog(title, header, body, yesNo: true, requiredText: requiredText) { Owner = owner };
            return dlg.ShowDialog() == true;
        }

        // Themed free-text prompt. Returns the trimmed input, or null if cancelled.
        // iconGlyph (optional) overrides the default check-circle with any Segoe MDL2 codepoint
        // (e.g. "" for the device-key icon to match the top-bar chip).
        public static string? Prompt(Window owner, string title, string header, string body, string? initialText = null, string? iconGlyph = null)
        {
            var dlg = new ThemedDialog(title, header, body, yesNo: true, freeInput: true, initialText: initialText, iconGlyph: iconGlyph) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg.txtInput.Text.Trim() : null;
        }

        // Themed Yes/No confirmation. Returns true for Yes.
        public static bool Confirm(Window owner, string title, string header, string body)
        {
            var dlg = new ThemedDialog(title, header, body, yesNo: true) { Owner = owner };
            return dlg.ShowDialog() == true;
        }

        // Themed OK notice.
        public static void Info(Window owner, string title, string header, string body)
        {
            var dlg = new ThemedDialog(title, header, body, yesNo: false) { Owner = owner };
            dlg.ShowDialog();
        }
    }
}
