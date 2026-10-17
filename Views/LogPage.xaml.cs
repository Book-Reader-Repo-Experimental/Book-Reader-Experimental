using Microsoft.Maui.Controls;
using System;
using System.Threading.Tasks;

namespace BookViewer.Views
{
    public partial class LogPage : ContentPage
    {
        private readonly string _path;
        private string _fullText = "";

        public LogPage(string[] lines, string logFilePath)
        {
            InitializeComponent();
            _path = logFilePath ?? "";

            PathLabel.Text = string.IsNullOrEmpty(_path)
                ? "(no log file writable — showing in-memory buffer)"
                : "File: " + _path;

            if (lines == null || lines.Length == 0)
            {
                LogLabel.Text = "(no log entries yet)";
                _fullText = "";
            }
            else
            {
                _fullText = string.Join("\n", lines);
                LogLabel.Text = _fullText;
            }
        }

        private async void OnCloseClicked(object sender, EventArgs e)
        {
            await Navigation.PopModalAsync();
        }

        private async void OnCopyClicked(object sender, EventArgs e)
        {
            try
            {
                await Clipboard.Default.SetTextAsync(_fullText);
                await DisplayAlert("Copied", "Log copied to clipboard.", "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Copy failed", ex.Message, "OK");
            }
        }
    }
}
