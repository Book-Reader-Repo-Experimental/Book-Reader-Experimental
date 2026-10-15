using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Color = Microsoft.Maui.Graphics.Color;

namespace BookViewer.Views;

public partial class BookViewerPage : ContentPage
{
    private readonly BookService _bookService = new();
    private bool _showTeacherNotes = false;
    private bool _showStudentAnswers = false;
    private bool _redAnswersOn = false;
    private string _currentContentHtml = "";
    private string _currentTeacherFragment = "";
    private string _currentStudentFragment = "";
    private string _currentViewMode = "content";
    private bool _bookLoaded = false;
    private int _startFolio = 1;
    private string _targetSectionId = "";

    // Prevents the hash-navigation bridge from re-firing during our own writes.
    private bool _suppressHashHandling = false;

    private static readonly object _logLock = new object();
    private static string _logFilePath = "";

    private static string SurroundColor =>
        Application.Current?.RequestedTheme == AppTheme.Dark ? "#1C1C1E" : "#E8E8E8";

    private string GetLogFilePath()
    {
        if (string.IsNullOrEmpty(_logFilePath))
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string logFolder = Path.Combine(documentsPath, "BookViewer");
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
            _logFilePath = Path.Combine(logFolder, $"BookViewerPage_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        }
        return _logFilePath;
    }

    private void Log(string message)
    {
        try
        {
            string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}";
            string logFile = GetLogFilePath();
            lock (_logLock) { File.AppendAllText(logFile, logMessage + Environment.NewLine); }
            System.Diagnostics.Debug.WriteLine(logMessage);
            if (message.Length < 80) Dispatcher.Dispatch(() => StatusLabel.Text = message);
            else Dispatcher.Dispatch(() => StatusLabel.Text = message.Substring(0, 77) + "...");
        }
        catch { }
    }

    public BookViewerPage(string bookFolder, int startFolio)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        _startFolio = startFolio;

        Log("=== BOOK VIEWER PAGE INITIALIZED ===");

        _bookService.OnPagesLoaded += (s, pages) => Dispatcher.Dispatch(() => UpdateUI());

        _bookService.OnPageChanged += (s, content) =>
        {
            Dispatcher.Dispatch(() =>
            {
                _currentContentHtml = content;
                LoadTeacherView();
                LoadStudentView();
                UpdateDisplay();
                UpdateUI();

                ContentWebView.Navigated -= OnContentWebViewNavigated;
                ContentWebView.Navigated += OnContentWebViewNavigated;
            });
        };

        _bookService.OnStatusChanged += (s, msg) => Dispatcher.Dispatch(() => StatusLabel.Text = msg);
        _bookService.OnBookLoaded += (s, title) => Dispatcher.Dispatch(() => BookTitleLabel.Text = title);

        _bookService.OnTwoPageSpreadToggled += (s, enabled) =>
        {
            Dispatcher.Dispatch(() =>
            {
                bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
                SideBySideButton.Text = enabled ? "▮▮ 2 Pages" : "▯ 1 Page";
                SideBySideButton.BackgroundColor = enabled
                    ? Color.FromArgb("#0A84FF")
                    : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
                SideBySideButton.TextColor = enabled
                    ? Colors.White
                    : (dark ? Colors.White : Colors.Black);
                StatusLabel.Text = enabled ? "Two-page spread" : "Single page";
                UpdateDisplay();
            });
        };

        _bookService.OnSideBySideToggled += (s, enabled) => Dispatcher.Dispatch(() => UpdateDisplay());
        _bookService.OnZoomChanged += (s, zoom) => Dispatcher.Dispatch(() => ZoomLabel.Text = $"{zoom:F1}x");

        // Wire the WebView hash bridge.
        ContentWebView.Navigating += OnContentWebViewNavigating;

        Loaded += async (s, e) =>
        {
            if (_bookLoaded) return;
            _bookLoaded = true;

            var success = await _bookService.LoadBookAsync(bookFolder);
            if (success)
            {
                RedAnswersButton.IsEnabled = true;
                TeacherNotesButton.IsEnabled = true;
                StudentAnswersButton.IsEnabled = true;
                DecryptButton.IsEnabled = true;
                SideBySideButton.IsEnabled = true;
                ViewModeButton.IsEnabled = true;
                ExportPdfButton.IsEnabled = true;
                GridButton.IsEnabled = true;

                _currentViewMode = "content";
                _showTeacherNotes = false;
                _showStudentAnswers = false;
                _redAnswersOn = false;
                UpdateViewModeButton();
                UpdateTeacherButton();
                UpdateStudentButton();
                UpdateRedAnswersButton();

                if (!string.IsNullOrEmpty(_targetSectionId))
                {
                    int idx = _bookService.GetFirstIndexOfSection(_targetSectionId);
                    if (idx < 0 && _startFolio > 0)
                        idx = _bookService.GetIndexForSectionFolio(_targetSectionId, _startFolio);
                    if (idx < 0 && _startFolio > 0) idx = _startFolio - 1;
                    if (idx >= 0 && idx < _bookService.PageFiles.Count)
                        await _bookService.LoadPageAsync(idx);
                }
                else if (_startFolio > 0)
                {
                    int idx = _bookService.GetIndexForFolio(_startFolio);
                    if (idx < 0) idx = _startFolio - 1;
                    if (idx >= 0 && idx < _bookService.PageFiles.Count)
                        await _bookService.LoadPageAsync(idx);
                }
            }
            else
            {
                await DisplayAlert("Error", "Failed to load book", "OK");
            }
        };
    }

    public BookViewerPage(string bookFolder, string sectionId, int startFolio)
        : this(bookFolder, startFolio)
    {
        _targetSectionId = sectionId;
    }

    // ============================================================
    // WebView hash bridge — catches ext-reposition / splitpane-open / etc.
    // ============================================================
    private void OnContentWebViewNavigating(object sender, WebNavigatingEventArgs e)
    {
        if (_suppressHashHandling) return;
        if (string.IsNullOrEmpty(e.Url)) return;

        var hashIdx = e.Url.IndexOf('#');
        if (hashIdx < 0) return;

        var hash = e.Url.Substring(hashIdx + 1);
        var colonIdx = hash.IndexOf(':');
        if (colonIdx < 0) return;

        var type = hash.Substring(0, colonIdx);
        var payloadEncoded = hash.Substring(colonIdx + 1);
        string payloadJson;
        try { payloadJson = Uri.UnescapeDataString(payloadEncoded); }
        catch { payloadJson = payloadEncoded; }

        switch (type)
        {
            case "ext-reposition":
            case "splitpane-reposition":
                // Cancel the navigation — it's just a signal channel.
                e.Cancel = true;
                _bookService.HandleExtMessage("ext:reposition", payloadJson);
                Log($"[bridge] reposition {payloadJson}");
                break;

            case "splitpane-delete":
                e.Cancel = true;
                _bookService.HandleExtMessage("splitpane:delete", payloadJson);
                Log($"[bridge] delete {payloadJson}");
                break;

            case "splitpane-open":
                e.Cancel = true;
                _bookService.HandleExtMessage("splitpane:open", payloadJson);
                Log($"[bridge] open {payloadJson}");
                // Opening the split-pane itself is handled by the page's <img onclick>
                // (which calls the real BY_SplitPaneController). We could push a new
                // BookViewerPage here, but the .ext overlay is a side-by-side view
                // inside the same page — leave it as a log for now.
                break;

            case "ext-open":
                e.Cancel = true;
                _bookService.HandleExtMessage("ext:open", payloadJson);
                Log($"[bridge] ext-open {payloadJson}");
                break;
        }
    }

    // ============================================================
    // Red Answers
    // ============================================================
    private void OnRedAnswersClicked(object sender, EventArgs e)
    {
        _redAnswersOn = !_redAnswersOn;
        _bookService.SetRedAnswers(_redAnswersOn);
        UpdateRedAnswersButton();
        ApplyRedAnswers(_redAnswersOn);
        StatusLabel.Text = _redAnswersOn ? "Red answers ON" : "Red answers OFF";
    }

    private void UpdateRedAnswersButton()
    {
        if (RedAnswersButton == null) return;
        bool active = _redAnswersOn;
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        RedAnswersButton.Text = active ? "✱ Hide Answers" : "✱ Red Answers";
        RedAnswersButton.BackgroundColor = active
            ? Color.FromArgb("#E40477")
            : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
        RedAnswersButton.TextColor = active
            ? Colors.White
            : (dark ? Colors.White : Colors.Black);
    }

    private void ApplyRedAnswers(bool show)
    {
        try
        {
            var js = $"window.__setRedAnswers && window.__setRedAnswers({(show ? "true" : "false")});";
            ContentWebView.EvaluateJavaScriptAsync(js);
        }
        catch (Exception ex) { Log($"ApplyRedAnswers: {ex.Message}"); }
    }

    private void OnContentWebViewNavigated(object sender, WebNavigatedEventArgs e)
    {
        ContentWebView.Navigated -= OnContentWebViewNavigated;

        // Restore persisted splitpane positions before anything else.
        try
        {
            ContentWebView.EvaluateJavaScriptAsync(
                "window.__applySplitPanePositions && window.__applySplitPanePositions();");
        }
        catch { }

        if (_redAnswersOn) ApplyRedAnswers(true);
    }

    // ============================================================
    // Teacher / student fragments (legacy)
    // ============================================================
    private async void LoadTeacherView()
    {
        try
        {
            _currentTeacherFragment = "";
            if (_bookService.CurrentPageIndex >= 0 && _bookService.CurrentPageIndex < _bookService.PageFiles.Count)
            {
                var filePath = _bookService.PageFiles[_bookService.CurrentPageIndex];
                _currentTeacherFragment = await _bookService.GetRedAnswerContentAsync(filePath, "teacherNotes");
            }
        }
        catch (Exception ex) { Log($"Error loading teacher view: {ex.Message}"); }
    }

    private async void LoadStudentView()
    {
        try
        {
            _currentStudentFragment = "";
            if (_bookService.CurrentPageIndex >= 0 && _bookService.CurrentPageIndex < _bookService.PageFiles.Count)
            {
                var filePath = _bookService.PageFiles[_bookService.CurrentPageIndex];
                _currentStudentFragment = await _bookService.GetRedAnswerContentAsync(filePath, "studentAnswers");
            }
        }
        catch (Exception ex) { Log($"Error loading student view: {ex.Message}"); }
    }

    private string InjectAnswerOverlay(string contentHtml, string fragment, string viewType)
    {
        if (string.IsNullOrEmpty(fragment)) return contentHtml;
        string borderColor = viewType == "teacherNotes" ? "#3498db" : "#2ecc71";
        string highlightClass = viewType == "teacherNotes" ? "tbnote" : "sa";

        var overlayHtml = $@"<div class='highlight-overlay'>
<style>.{highlightClass} {{ background: rgba(255,255,0,0.25); border: 3px solid {borderColor}; border-radius: 4px; padding: 3px; }}</style>
{fragment}
</div>";

        const string marker = "<!--ANSWERS-HERE-->";
        int idx = contentHtml.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return contentHtml.Substring(0, idx) + overlayHtml + contentHtml.Substring(idx + marker.Length);
        return contentHtml + overlayHtml;
    }

    private void UpdateDisplay()
    {
        bool isTwoPage = _bookService.TwoPageSpread;

        if (isTwoPage)
        {
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
            ContentWebView.IsVisible = true;

            if (!string.IsNullOrEmpty(_bookService.NextPageHtml))
                NextPageWebView.Source = new HtmlWebViewSource { Html = _bookService.NextPageHtml };
            else
                NextPageWebView.Source = new HtmlWebViewSource { Html = $"<html><body style='background:{SurroundColor};'></body></html>" };
            NextPageWebView.IsVisible = true;

            TeacherWebView.IsVisible = false;
            StudentWebView.IsVisible = false;
        }
        else
        {
            RightColumn.Width = new GridLength(0);
            NextPageWebView.IsVisible = false;

            string html = _currentContentHtml;
            if (_currentViewMode == "teacher" && !string.IsNullOrEmpty(_currentTeacherFragment))
                html = InjectAnswerOverlay(_currentContentHtml, _currentTeacherFragment, "teacherNotes");
            else if (_currentViewMode == "student" && !string.IsNullOrEmpty(_currentStudentFragment))
                html = InjectAnswerOverlay(_currentContentHtml, _currentStudentFragment, "studentAnswers");

            ContentWebView.Source = new HtmlWebViewSource { Html = html };
            ContentWebView.IsVisible = true;
            TeacherWebView.IsVisible = false;
            StudentWebView.IsVisible = false;

            UpdateViewModeButton();
        }
    }

    private void UpdateViewModeButton()
    {
        ViewModeButton.Text = _currentViewMode switch
        {
            "content" => "◉ Content",
            "teacher" => "✎ Teacher",
            "student" => "✓ Student",
            _ => "◉ Content"
        };
    }

    private void OnViewModeClicked(object sender, EventArgs e)
    {
        _currentViewMode = _currentViewMode switch
        {
            "content" when (!string.IsNullOrEmpty(_currentTeacherFragment)) => "teacher",
            "teacher" when (!string.IsNullOrEmpty(_currentStudentFragment)) => "student",
            "student" => "content",
            "content" => "content",
            _ => "content"
        };
        UpdateDisplay();
        UpdateViewModeButton();
        UpdateTeacherButton();
        UpdateStudentButton();
    }

    private void OnTeacherNotesClicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentTeacherFragment))
        {
            _currentViewMode = _currentViewMode == "teacher" ? "content" : "teacher";
            UpdateDisplay(); UpdateViewModeButton(); UpdateTeacherButton(); UpdateStudentButton();
        }
        else StatusLabel.Text = "No teacher notes available for this page";
    }

    private void OnStudentAnswersClicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentStudentFragment))
        {
            _currentViewMode = _currentViewMode == "student" ? "content" : "student";
            UpdateDisplay(); UpdateViewModeButton(); UpdateTeacherButton(); UpdateStudentButton();
        }
        else StatusLabel.Text = "No student answers available for this page";
    }

    private void UpdateTeacherButton()
    {
        if (TeacherNotesButton == null) return;
        bool active = _currentViewMode == "teacher";
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        TeacherNotesButton.Text = active ? "✎ Hide Notes" : "✎ Notes";
        TeacherNotesButton.BackgroundColor = active ? Color.FromArgb("#34C759") : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
        TeacherNotesButton.TextColor = active ? Colors.White : (dark ? Colors.White : Colors.Black);
    }

    private void UpdateStudentButton()
    {
        if (StudentAnswersButton == null) return;
        bool active = _currentViewMode == "student";
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        StudentAnswersButton.Text = active ? "✓ Hide Answers" : "✓ Answers";
        StudentAnswersButton.BackgroundColor = active ? Color.FromArgb("#34C759") : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
        StudentAnswersButton.TextColor = active ? Colors.White : (dark ? Colors.White : Colors.Black);
    }

    private async void OnBackClicked(object sender, EventArgs e) => await Navigation.PopAsync();

    private async void OnExportPdfClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_bookService.CurrentBookPath))
        {
            await DisplayAlert("Info", "Please load a book first.", "OK");
            return;
        }

        try
        {
            var includeAnswers = await DisplayAlert("Export Options", "Include answers in the PDF?", "Yes", "No");
            bool includeTeacherNotes = false;
            bool includeStudentAnswers = false;
            if (includeAnswers)
            {
                includeTeacherNotes = await DisplayAlert("Answer Type", "Include Teacher Notes?", "Yes", "No");
                includeStudentAnswers = await DisplayAlert("Answer Type", "Include Student Answers?", "Yes", "No");
                if (!includeTeacherNotes && !includeStudentAnswers) includeAnswers = false;
            }

            var safeFileName = string.Join("_", _bookService.BookTitle.Split(Path.GetInvalidFileNameChars()));
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var exportDir = Path.Combine(documentsPath, "BookViewer", "Exports");
            Directory.CreateDirectory(exportDir);
            var outputPath = Path.Combine(exportDir, $"{safeFileName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

            ExportPdfButton.IsEnabled = false;
            StatusLabel.Text = "Exporting PDF...";

            var pdfService = new Services.PdfExportService(_bookService);

            pdfService.RenderPageToImageAsync = async (html) =>
            {
            #if IOS
                return await BookViewer.Platforms.iOS.WebViewCapture.CaptureHtmlAsync(html, 1024, 1309);
            #elif MACCATALYST
                return await BookViewer.Platforms.MacCatalyst.WebViewCapture.CaptureHtmlAsync(html, 1024, 1309);
            #elif ANDROID
                return await BookViewer.Platforms.Android.WebViewCapture.CaptureHtmlAsync(html, 1024, 1309);
            #elif WINDOWS
                return await BookViewer.Platforms.Windows.WebViewCapture.CaptureHtmlAsync(html, 1024, 1309);
            #else
                await Task.CompletedTask;
                return null;
            #endif
            };

            pdfService.OnProgress += (s, progress) =>
                Dispatcher.Dispatch(() => StatusLabel.Text = $"Exporting PDF... {progress}%");

            var tcs = new TaskCompletionSource<string>();
            pdfService.OnComplete += (s, path) => tcs.TrySetResult(path);
            pdfService.OnError += (s, error) => tcs.TrySetException(new Exception(error));

            await pdfService.ExportBookAsPdfAsync(outputPath, includeAnswers, includeTeacherNotes, includeStudentAnswers);
            var resultPath = await tcs.Task;

            ExportPdfButton.IsEnabled = true;
            StatusLabel.Text = "PDF export complete!";

            var openNow = await DisplayAlert("Export Complete", $"PDF saved to:\n{resultPath}\n\nOpen it now?", "Open", "Later");
            if (openNow)
                await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(resultPath) });
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
        var gridPage = new PageGridView(
            _bookService.PageFiles,
            _bookService.CurrentPageIndex,
            (pageIndex) => Dispatcher.Dispatch(async () => await _bookService.LoadPageAsync(pageIndex)));
        await Navigation.PushAsync(gridPage);
    }

    private async void OnDecryptClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_bookService.CurrentBookPath))
        {
            await DisplayAlert("Info", "Please open a book first.", "OK");
            return;
        }

        var confirm = await DisplayAlert("Confirm Decrypt",
            $"This will decrypt all encrypted files in:\n{_bookService.CurrentBookPath}\n\nContinue?",
            "Yes", "No");

        if (confirm)
        {
            await _bookService.DecryptBookAsync();
            await _bookService.LoadBookAsync(_bookService.CurrentBookPath);
        }
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
            else if (_bookService.CurrentPageIndex < _bookService.PageFiles.Count - 1)
                _ = _bookService.LoadPageAsync(_bookService.PageFiles.Count - 1);
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
