using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.IO;
using System.Threading.Tasks;
using Color = Microsoft.Maui.Graphics.Color;

namespace BookViewer.Views;

public partial class BookViewerPage : ContentPage
{
    private readonly BookService _bookService = new();
    private bool _showTeacherNotes = false;
    private bool _showStudentAnswers = false;
    private string _currentContentHtml = "";
    private bool _bookLoaded = false;
    private int _startFolio = 1;
    private string _targetSectionId = "";

    private static readonly object _logLock = new object();
    private static string _logFilePath = "";

    private string GetLogFilePath()
    {
        if (string.IsNullOrEmpty(_logFilePath))
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BookViewer");
                Directory.CreateDirectory(dir);
                _logFilePath = Path.Combine(dir, $"BookViewerPage_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            }
            catch { _logFilePath = Path.Combine(Path.GetTempPath(), $"BookViewerPage_{DateTime.Now:yyyyMMdd_HHmmss}.log"); }
        }
        return _logFilePath;
    }

    private void Log(string message)
    {
        try
        {
            lock (_logLock) File.AppendAllText(GetLogFilePath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}\n");
            System.Diagnostics.Debug.WriteLine(message);
            Dispatcher.Dispatch(() => { if (StatusLabel != null) StatusLabel.Text = message.Length < 80 ? message : message.Substring(0, 77) + "..."; });
        }
        catch { }
    }

    public BookViewerPage(string bookFolder, int startFolio)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        _startFolio = startFolio;

        _bookService.OnPagesLoaded += (s, pages) => Dispatcher.Dispatch(UpdateUI);

        _bookService.OnPageChanged += (s, content) =>
        {
            Dispatcher.Dispatch(() =>
            {
                _currentContentHtml = content;
                ContentWebView.Source = new HtmlWebViewSource { Html = content };
                ContentWebView.IsVisible = true;
                UpdateUI();
            });
        };

        _bookService.OnStatusChanged += (s, msg) => Dispatcher.Dispatch(() => { if (StatusLabel != null) StatusLabel.Text = msg; });
        _bookService.OnBookLoaded += (s, title) => Dispatcher.Dispatch(() => { if (BookTitleLabel != null) BookTitleLabel.Text = title; });

        _bookService.OnTwoPageSpreadToggled += (s, enabled) =>
        {
            Dispatcher.Dispatch(() =>
            {
                bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
                SideBySideButton.Text = enabled ? "▮▮ 2 Pages" : "▯ 1 Page";
                SideBySideButton.BackgroundColor = enabled ? Color.FromArgb("#0A84FF") : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
                SideBySideButton.TextColor = enabled ? Colors.White : (dark ? Colors.White : Colors.Black);
                UpdateTwoPageLayout();
            });
        };

        _bookService.OnSideBySideToggled += (s, enabled) => Dispatcher.Dispatch(UpdateTwoPageLayout);
        _bookService.OnZoomChanged += (s, zoom) => Dispatcher.Dispatch(() => { if (ZoomLabel != null) ZoomLabel.Text = $"{zoom:F1}x"; });

        Loaded += async (s, e) =>
        {
            if (_bookLoaded) return;
            _bookLoaded = true;

            var success = await _bookService.LoadBookAsync(bookFolder);
            if (success)
            {
                TeacherNotesButton.IsEnabled = true;
                StudentAnswersButton.IsEnabled = true;
                DecryptButton.IsEnabled = true;
                SideBySideButton.IsEnabled = true;
                ExportPdfButton.IsEnabled = true;
                GridButton.IsEnabled = true;

                _showTeacherNotes = false;
                _showStudentAnswers = false;
                UpdateTeacherButton();
                UpdateStudentButton();

                if (!string.IsNullOrEmpty(_targetSectionId))
                {
                    int idx = _bookService.GetFirstIndexOfSection(_targetSectionId);
                    if (idx < 0 && _startFolio > 0) idx = _bookService.GetIndexForSectionFolio(_targetSectionId, _startFolio);
                    if (idx < 0 && _startFolio > 0) idx = _startFolio - 1;
                    if (idx >= 0 && idx < _bookService.PageFiles.Count) await _bookService.LoadPageAsync(idx);
                }
                else if (_startFolio > 0)
                {
                    int idx = _bookService.GetIndexForFolio(_startFolio);
                    if (idx < 0) idx = _startFolio - 1;
                    if (idx >= 0 && idx < _bookService.PageFiles.Count) await _bookService.LoadPageAsync(idx);
                }
            }
            else await DisplayAlert("Error", "Failed to load book", "OK");
        };
    }

    public BookViewerPage(string bookFolder, string sectionId, int startFolio) : this(bookFolder, startFolio)
    {
        _targetSectionId = sectionId;
    }

    private void UpdateTwoPageLayout()
    {
        if (_bookService.TwoPageSpread)
        {
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
            ContentWebView.IsVisible = true;
            NextPageWebView.Source = new HtmlWebViewSource { Html = _bookService.NextPageHtml ?? "" };
            NextPageWebView.IsVisible = true;
        }
        else
        {
            RightColumn.Width = new GridLength(0);
            NextPageWebView.IsVisible = false;
            ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
            ContentWebView.IsVisible = true;
        }
    }

    // Toolbar: Notes / Answers — bulk toggle via EvaluateJavaScriptAsync
    private async void OnTeacherNotesClicked(object sender, EventArgs e)
    {
        _showTeacherNotes = !_showTeacherNotes;
        UpdateTeacherButton();
        try { await ContentWebView.EvaluateJavaScriptAsync($"window.__setNotes({(_showTeacherNotes ? "true" : "false")})"); }
        catch (Exception ex) { Log($"setNotes err: {ex.Message}"); }
    }

    private async void OnStudentAnswersClicked(object sender, EventArgs e)
    {
        _showStudentAnswers = !_showStudentAnswers;
        UpdateStudentButton();
        try { await ContentWebView.EvaluateJavaScriptAsync($"window.__setAnswers({(_showStudentAnswers ? "true" : "false")})"); }
        catch (Exception ex) { Log($"setAnswers err: {ex.Message}"); }
    }

    private void UpdateTeacherButton()
    {
        if (TeacherNotesButton == null) return;
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        TeacherNotesButton.Text = _showTeacherNotes ? "✎ Hide Notes" : "✎ Notes";
        TeacherNotesButton.BackgroundColor = _showTeacherNotes ? Color.FromArgb("#34C759") : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
        TeacherNotesButton.TextColor = _showTeacherNotes ? Colors.White : (dark ? Colors.White : Colors.Black);
    }

    private void UpdateStudentButton()
    {
        if (StudentAnswersButton == null) return;
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        StudentAnswersButton.Text = _showStudentAnswers ? "✓ Hide Answers" : "✓ Answers";
        StudentAnswersButton.BackgroundColor = _showStudentAnswers ? Color.FromArgb("#34C759") : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
        StudentAnswersButton.TextColor = _showStudentAnswers ? Colors.White : (dark ? Colors.White : Colors.Black);
    }

    private async void OnBackClicked(object sender, EventArgs e) => await Navigation.PopAsync();

    private async void OnExportPdfClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_bookService.CurrentBookPath)) { await DisplayAlert("Info", "Please load a book first.", "OK"); return; }
        try
        {
            var includeAnswers = await DisplayAlert("Export Options", "Include answers in the PDF?", "Yes", "No");
            bool includeTeacherNotes = false, includeStudentAnswers = false;
            if (includeAnswers)
            {
                includeTeacherNotes = await DisplayAlert("Answer Type", "Include Teacher Notes?", "Yes", "No");
                includeStudentAnswers = await DisplayAlert("Answer Type", "Include Student Answers?", "Yes", "No");
                if (!includeTeacherNotes && !includeStudentAnswers) includeAnswers = false;
            }

            var safe = string.Join("_", _bookService.BookTitle.Split(Path.GetInvalidFileNameChars()));
            var exportDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BookViewer", "Exports");
            Directory.CreateDirectory(exportDir);
            var outputPath = Path.Combine(exportDir, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

            ExportPdfButton.IsEnabled = false;
            StatusLabel.Text = "Exporting PDF...";

            var pdfService = new Services.PdfExportService(_bookService);
            pdfService.RenderPageToImageAsync = async (html) =>
            {
#if IOS
                return await BookViewer.Platforms.iOS.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
#elif MACCATALYST
                return await BookViewer.Platforms.MacCatalyst.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
#elif ANDROID
                return await BookViewer.Platforms.Android.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
#elif WINDOWS
                return await BookViewer.Platforms.Windows.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
#else
                await Task.CompletedTask; return null;
#endif
            };

            var tcs = new TaskCompletionSource<string>();
            pdfService.OnComplete += (s, path) => tcs.TrySetResult(path);
            pdfService.OnError += (s, error) => tcs.TrySetException(new Exception(error));

            await pdfService.ExportBookAsPdfAsync(outputPath, includeAnswers, includeTeacherNotes, includeStudentAnswers);
            var resultPath = await tcs.Task;

            ExportPdfButton.IsEnabled = true;
            StatusLabel.Text = "PDF export complete!";

            var openNow = await DisplayAlert("Export Complete", $"PDF saved to:\n{resultPath}\n\nOpen it now?", "Open", "Later");
            if (openNow) await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(resultPath) });
        }
        catch (Exception ex)
        {
            ExportPdfButton.IsEnabled = true;
            StatusLabel.Text = $"Export failed: {ex.Message}";
            await DisplayAlert("Error", $"Export failed: {ex.Message}", "OK");
        }
    }

    private async void OnGridClicked(object sender, EventArgs e)
    {
        if (_bookService.PageFiles.Count == 0) return;
        var gridPage = new PageGridView(_bookService.PageFiles, _bookService.CurrentPageIndex,
            (pageIndex) => Dispatcher.Dispatch(async () => await _bookService.LoadPageAsync(pageIndex)));
        await Navigation.PushAsync(gridPage);
    }

    private async void OnDecryptClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_bookService.CurrentBookPath)) { await DisplayAlert("Info", "Please open a book first.", "OK"); return; }
        var confirm = await DisplayAlert("Confirm Decrypt", $"This will decrypt all encrypted files in:\n{_bookService.CurrentBookPath}\n\nContinue?", "Yes", "No");
        if (confirm) { await _bookService.DecryptBookAsync(); await _bookService.LoadBookAsync(_bookService.CurrentBookPath); }
    }

    private void OnPrevClicked(object sender, EventArgs e)
    {
        if (_bookService.TwoPageSpread)
        {
            var newIndex = _bookService.CurrentPageIndex - 2;
            if (newIndex >= 0) _ = _bookService.LoadPageAsync(newIndex);
            else if (_bookService.CurrentPageIndex > 0) _ = _bookService.LoadPageAsync(0);
        }
        else _bookService.NavigatePrevious();
    }

    private void OnNextClicked(object sender, EventArgs e)
    {
        if (_bookService.TwoPageSpread)
        {
            var newIndex = _bookService.CurrentPageIndex + 2;
            if (newIndex < _bookService.PageFiles.Count) _ = _bookService.LoadPageAsync(newIndex);
            else if (_bookService.CurrentPageIndex < _bookService.PageFiles.Count - 1) _ = _bookService.LoadPageAsync(_bookService.PageFiles.Count - 1);
        }
        else _bookService.NavigateNext();
    }

    private void OnSideBySideClicked(object sender, EventArgs e) => _bookService.ToggleTwoPageSpread();
    private void OnZoomInClicked(object sender, EventArgs e) => _bookService.ZoomIn();
    private void OnZoomOutClicked(object sender, EventArgs e) => _bookService.ZoomOut();

    private void UpdateUI()
    {
        var total = _bookService.PageFiles.Count;
        var current = _bookService.CurrentPageIndex + 1;
        PrevButton.IsEnabled = _bookService.CurrentPageIndex > 0 && total > 0;
        NextButton.IsEnabled = _bookService.CurrentPageIndex < total - 1 && total > 0;
        PageInfoLabel.Text = total > 0 ? $"{current} / {total}" : "0 / 0";
        ZoomLabel.Text = $"{_bookService.CurrentZoom:F1}x";
    }
}
