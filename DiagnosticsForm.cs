namespace SpotifyAdMuter
{
    internal sealed class DiagnosticsForm : Form
    {
        private readonly TextBox _entries = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9)
        };

        public DiagnosticsForm()
        {
            Text = "SpotifyMuter diagnostics";
            Width = 780;
            Height = 420;
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(_entries);
            FormClosing += (_, e) =>
            {
                e.Cancel = true;
                Hide();
            };
        }

        public void AddEntry(string entry)
        {
            if (IsDisposed) return;
            _entries.AppendText(entry + Environment.NewLine);

            const int maxCharacters = 40000;
            if (_entries.TextLength > maxCharacters)
            {
                _entries.Select(0, _entries.TextLength - maxCharacters);
                _entries.SelectedText = string.Empty;
            }
        }
    }
}
