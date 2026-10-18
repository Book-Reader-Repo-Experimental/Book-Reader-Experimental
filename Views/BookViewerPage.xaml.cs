using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
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
    private int _targetStep = 0;

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
            Dispatcher.Dispatch(() =>
            {
                if (StatusLabel != null)
                    StatusLabel.Text = message.Length < 80 ? message : message.Substring(0, 77) + "...";
            });
        }
        catch { }
    }

    public BookViewerPage(string bookFolder, int startFolio)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        _startFolio = startFolio;

        _bookService.OnPagesLoaded += (s, pages) => Dispatcher.Dispatch(UpdateUI);

        _bookService.OnPageChanged += async (s, content) =>
        {
            _currentContentHtml = content;

            if (_bookService.TwoPageSpread)
            {
                var combined = await _bookService.BuildTwoPageHtmlAsync();
                Dispatcher.Dispatch(() =>
                {
                    ContentWebView.Source = new HtmlWebViewSource { Html = combined };
                    ContentWebView.IsVisible = true;
                    UpdateUI();
                });
            }
            else
            {
                Dispatcher.Dispatch(() =>
                {
                    ContentWebView.Source = new HtmlWebViewSource { Html = content };
                    ContentWebView.IsVisible = true;
                    UpdateUI();
                });
            }
        };

        _bookService.OnNextPageChanged += async (s, html) =>
        {
            if (!_bookService.TwoPageSpread) return;
            var combined = await _bookService.BuildTwoPageHtmlAsync();
            Dispatcher.Dispatch(() =>
            {
                ContentWebView.Source = new HtmlWebViewSource { Html = combined };
                ContentWebView.IsVisible = true;
            });
        };

        _bookService.OnStatusChanged += (s, msg) =>
            Dispatcher.Dispatch(() => { if (StatusLabel != null) StatusLabel.Text = msg; });

        _bookService.OnBookLoaded += (s, title) =>
            Dispatcher.Dispatch(() => { if (BookTitleLabel != null) BookTitleLabel.Text = title; });

        _bookService.OnTwoPageSpreadToggled += (s, enabled) =>
        {
            Dispatcher.Dispatch(() =>
            {
                bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
                SideBySideButton.Text = enabled ? "▮▮ 2 Pages" : "▯ 1 Page";
                SideBySideButton.BackgroundColor = enabled
                    ? Color.FromArgb("#0A84FF")
                    : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
                SideBySideButton.TextColor = enabled ? Colors.White : (dark ? Colors.White : Colors.Black);
                _ = UpdateTwoPageLayoutAsync();
            });
        };

        _bookService.OnSideBySideToggled += (s, enabled) => Dispatcher.Dispatch(async () => await UpdateTwoPageLayoutAsync());

        _bookService.OnZoomChanged += (s, zoom) =>
            Dispatcher.Dispatch(() => { if (ZoomLabel != null) ZoomLabel.Text = $"{zoom:F1}x"; });

        ContentWebView.Navigating += OnWebViewNavigating;
        ContentWebView.Navigated += OnContentWebViewNavigated;

        Loaded += async (s, e) =>
        {
            if (_bookLoaded) return;
            _bookLoaded = true;

            var success = await _bookService.LoadBookAsync(bookFolder);
            if (!success)
            {
                await DisplayAlert("Error", "Failed to load book", "OK");
                return;
            }

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

            Log($"Loaded. targetSection='{_targetSectionId}' targetStep={_targetStep} startFolio={_startFolio} totalPages={_bookService.PageFiles.Count}");

            if (!string.IsNullOrEmpty(_targetSectionId) && _targetStep > 0)
            {
                int idx = _bookService.FindPageIndex(_targetSectionId, _targetStep);
                Log($"FindPageIndex('{_targetSectionId}', {_targetStep}) -> {idx}");
                if (idx >= 0 && idx < _bookService.PageFiles.Count)
                {
                    await _bookService.LoadPageAsync(idx);
                    return;
                }
                int first = _bookService.GetFirstIndexOfSection(_targetSectionId);
                Log($"GetFirstIndexOfSection('{_targetSectionId}') -> {first}");
                if (first >= 0 && first < _bookService.PageFiles.Count)
                {
                    await _bookService.LoadPageAsync(first);
                    return;
                }
            }

            if (!string.IsNullOrEmpty(_targetSectionId))
            {
                int idx = _bookService.GetFirstIndexOfSection(_targetSectionId);
                if (idx < 0 && _startFolio > 0)
                    idx = _bookService.GetIndexForSectionFolio(_targetSectionId, _startFolio);
                Log($"section-only lookup -> {idx}");
                if (idx >= 0 && idx < _bookService.PageFiles.Count)
                {
                    await _bookService.LoadPageAsync(idx);
                    return;
                }
            }

            if (_startFolio > 0)
            {
                int idx = _bookService.GetIndexForFolio(_startFolio);
                Log($"GetIndexForFolio({_startFolio}) -> {idx}");
                if (idx >= 0 && idx < _bookService.PageFiles.Count)
                {
                    await _bookService.LoadPageAsync(idx);
                    return;
                }
            }

            Log("No target matched — leaving on page 0");
        };
    }

    public BookViewerPage(string bookFolder, string sectionId, int startFolio)
        : this(bookFolder, startFolio)
    {
        _targetSectionId = sectionId;
    }

    public BookViewerPage(string bookFolder, string sectionId, int step, int startFolio)
        : this(bookFolder, startFolio)
    {
        _targetSectionId = sectionId;
        _targetStep = step;
    }

    // ============================================================
    // BRIDGE NAVIGATION
    // ============================================================
    private async void OnWebViewNavigating(object sender, WebNavigatingEventArgs e)
    {
        var url = e.Url;
        if (string.IsNullOrEmpty(url)) return;

        bool isBridge = url.StartsWith("bookviewer://", StringComparison.OrdinalIgnoreCase)
                     || url.Contains("about:blank#");
        if (!isBridge) return;

        e.Cancel = true;

        // Manual parse: never new Uri() on the raw bridge string
        string host, query;

        if (url.StartsWith("bookviewer://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url.Substring("bookviewer://".Length);
            var qIdx = rest.IndexOf('?');
            if (qIdx >= 0) { host = rest.Substring(0, qIdx); query = rest.Substring(qIdx); }
            else { host = rest; query = ""; }
        }
        else
        {
            var hash = url.IndexOf('#');
            var rest = hash >= 0 ? url.Substring(hash + 1) : url;
            var qIdx = rest.IndexOf('?');
            if (qIdx >= 0) { host = rest.Substring(0, qIdx); query = rest.Substring(qIdx); }
            else { host = rest; query = ""; }
        }

        host = host.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(host)) return;

        Log($"bridge nav: host='{host}' query='{query}'");

        try
        {
            var q = HttpUtility.ParseQueryString(query);   // decodes ONCE

            if (host == "splitpane-open")
            {
                var bookUid = q["bookUID"];
                var section = q["section"] ?? "";
                var unit = q["unit"] ?? "";
                var stepStr = q["step"] ?? "";

                int step = 0;
                int.TryParse(stepStr, out step);

                if (string.IsNullOrEmpty(section) && !string.IsNullOrEmpty(unit))
                {
                    var mapped = _bookService.GetFirstSectionFolderForUnit(unit);
                    if (!string.IsNullOrEmpty(mapped))
                    {
                        section = mapped;
                        Log($"[splitpane] mapped unit '{unit}' -> section '{section}'");
                    }
                    else
                    {
                        Log($"[splitpane] no unit→section mapping for unit '{unit}'");
                        section = unit;
                    }
                }

                Log($"splitpane-open bookUID='{bookUid}' section='{section}' unit='{unit}' step='{step}'");

                if (string.IsNullOrEmpty(bookUid)) return;

                var targetFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "BookViewer", "Books", "book_" + bookUid);

                if (!Directory.Exists(targetFolder))
                {
                    await DisplayAlert("Not found", $"Book folder not found:\nbook_{bookUid}", "OK");
                    return;
                }

                var viewer = new BookViewerPage(targetFolder, section, step, 0);
                await Navigation.PushAsync(viewer);
            }
            else if (host == "assessment-open")
            {
                var elId = q["id"] ?? "";
                var poolType = q["poolType"] ?? "ASSESSMENT";
                var groupFromUrl = q["group"] ?? "";
                await OpenAssessmentAsync(elId, poolType, groupFromUrl);
            }
            else if (host == "assessment-close" || host == "assessment-complete")
            {
                CloseAssessment();
            }
            else if (host == "splitpane-delete")
            {
                Log("splitpane delete " + q["bookUID"]);
            }
            else if (host == "open-resource")
            {
                var resourceUrl  = q["url"]  ?? ""; // Already decoded once by ParseQueryString
                var resourceDesc = q["desc"] ?? "";
                var openInApp    = q["openInApp"] == "1";
                var audio        = q["audio"] == "1";

                Log($"[resource] open url='{resourceUrl}' desc='{resourceDesc}' openInApp={openInApp} audio={audio}");

                if (string.IsNullOrEmpty(resourceUrl)) return;

                // 1. Check if it's a remote URL
                if (resourceUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (Uri.TryCreate(resourceUrl, UriKind.Absolute, out var remoteUri))
                            await Launcher.Default.OpenAsync(remoteUri);
                        else
                            await DisplayAlert("Cannot open", $"Not a valid URL:\n{resourceUrl}", "OK");
                    }
                    catch (Exception ex)
                    {
                        Log($"[resource] open remote err: {ex.Message}");
                        await DisplayAlert("Cannot open link", ex.Message, "OK");
                    }
                    return;
                }

                // 2. It's a local file path (e.g., "resources/K20107_pp.pptx").
                string absolutePath = resourceUrl;
                
                if (!Path.IsPathRooted(resourceUrl))
                {
                    // Resolve relative paths against the book's root folder
                    absolutePath = Path.Combine(_bookService.CurrentBookPath, resourceUrl.Replace('/', Path.DirectorySeparatorChar));
                }

                // 3. Verify it exists and open it
                if (File.Exists(absolutePath))
                {
                    await OpenLocalResourceAsync(absolutePath, openInApp);
                }
                else
                {
                    Log($"[resource] File NOT FOUND at resolved path: {absolutePath}");
                    await DisplayAlert("File Not Found", $"Could not find the file:\n{absolutePath}\n\nCheck if the book downloaded completely.", "OK");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"bridge nav error: {ex.Message}");
        }
    }

    private async Task OpenLocalResourceAsync(string path, bool forceApp)
    {
        var fileName = Path.GetFileName(path);
        try
        {
#if ANDROID
            var ok = Platforms.Android.FileOpener.OpenFile(path);
            if (!ok)
            {
                Log($"[resource] Android FileOpener returned false, falling back to share");
                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = fileName,
                    File = new ShareFile(path)
                });
            }
#elif IOS
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = fileName,
                File = new ShareFile(path)
            });
#elif MACCATALYST
            var ok = Platforms.MacCatalyst.FileOpener.OpenFile(path);
            if (!ok)
            {
                await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(path) });
            }
#elif WINDOWS
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.GetFullPath(path),
                UseShellExecute = true
            });
#else
            await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(path) });
#endif
            Log($"[resource] opened local: {path}");
        }
        catch (Exception ex)
        {
            Log($"[resource] open local failed: {ex.Message}");
            await DisplayAlert("Cannot open", ex.Message, "OK");
        }
    }

    private async void OnContentWebViewNavigated(object sender, WebNavigatedEventArgs e)
    {
        // Only call the override JS if the page actually defines it.
        if (string.IsNullOrEmpty(_currentContentHtml)) return;
        if (!_currentContentHtml.Contains("__setNotes") &&
            !_currentContentHtml.Contains("__setRedAnswers"))
        {
            Log("[override] skipped (bridge JS not present on page)");
            return;
        }
        await ApplyAnswerOverridesAsync();
    }

    private async Task ApplyAnswerOverridesAsync()
    {
        try
        {
            var notesJs = $"window.__setNotes({(_showTeacherNotes ? "true" : "false")})";
            await ContentWebView.EvaluateJavaScriptAsync(notesJs);

            var answersJs = $"window.__setRedAnswers({(_showStudentAnswers ? "true" : "false")})";
            await ContentWebView.EvaluateJavaScriptAsync(answersJs);

            Log($"[override] applied notes={_showTeacherNotes} answers={_showStudentAnswers}");
        }
        catch (Exception ex)
        {
            Log($"[override] apply err: {ex.Message}");
        }
    }

    private async Task OpenAssessmentAsync(string elId, string poolType, string groupFromUrl = null)
    {
        try
        {
            var groupId = groupFromUrl;
            if (string.IsNullOrEmpty(groupId))
            {
                var m = Regex.Match(_currentContentHtml ?? "",
                    @"id=""BY_assessmentQuestion_" + Regex.Escape(elId) + @"""[^>]*>([^<]+)<",
                    RegexOptions.IgnoreCase);
                if (m.Success) groupId = m.Groups[1].Value.Trim();
            }

            Log($"assessment open el={elId} group={groupId} pool={poolType}");

            if (string.IsNullOrEmpty(groupId))
            {
                await DisplayAlert("Missing group", "No question group id for " + elId, "OK");
                return;
            }

            var poolPath = _bookService.FindPoolFile(groupId);
            if (string.IsNullOrEmpty(poolPath) || !File.Exists(poolPath))
            {
                await DisplayAlert("Not found", $"Interactive activity not found for group {groupId}.", "OK");
                return;
            }

            var html = await _bookService.BuildPoolHtmlAsync(poolPath, groupId);

            AssessmentTitleLabel.Text = "Activity " + groupId;
            AssessmentWebView.Source = new HtmlWebViewSource { Html = html };
            AssessmentOverlay.IsVisible = true;
            ContentWebView.IsVisible = false;
        }
        catch (Exception ex)
        {
            Log($"OpenAssessmentAsync err: {ex.Message}");
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private void CloseAssessment()
    {
        AssessmentOverlay.IsVisible = false;
        AssessmentWebView.Source = new HtmlWebViewSource { Html = "<html><body></body></html>" };
        ContentWebView.IsVisible = true;
    }

    private void OnAssessmentCloseClicked(object sender, EventArgs e) => CloseAssessment();

    private async void OnAssessmentBackClicked(object sender, EventArgs e)
    {
        try { await AssessmentWebView.EvaluateJavaScriptAsync("history.back()"); }
        catch { CloseAssessment(); }
    }

    private async Task UpdateTwoPageLayoutAsync()
    {
        if (_bookService.TwoPageSpread)
        {
            var combined = await _bookService.BuildTwoPageHtmlAsync();
            ContentWebView.Source = new HtmlWebViewSource { Html = combined };
            ContentWebView.IsVisible = true;
        }
        else
        {
            ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
            ContentWebView.IsVisible = true;
        }

        NextPageWebView.IsVisible = false;
        RightColumn.Width = new GridLength(0);
        UpdateUI();
    }

    private async void OnTeacherNotesClicked(object sender, EventArgs e)
    {
        _showTeacherNotes = !_showTeacherNotes;
        UpdateTeacherButton();
        await ApplyAnswerOverridesAsync();
    }

    private async void OnStudentAnswersClicked(object sender, EventArgs e)
    {
        _showStudentAnswers = !_showStudentAnswers;
        UpdateStudentButton();
        await ApplyAnswerOverridesAsync();
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

    private async void OnLogClicked(object sender, EventArgs e)
    {
        var lines = _bookService.GetRecentLogs();
        var path = _bookService.GetLogFilePathOrEmpty();

        var logPage = new LogPage(lines, path);
        await Navigation.PushModalAsync(new NavigationPage(logPage));
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
            {
                int last = _bookService.PageFiles.Count - 1;
                int target = (last % 2 == 0) ? Math.Max(0, last - 1) : last;
                _ = _bookService.LoadPageAsync(target);
            }
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

        if (_bookService.TwoPageSpread && _bookService.CurrentPageIndex + 1 < total)
            PageInfoLabel.Text = $"{current}\u2013{current + 1} / {total}";
        else
            PageInfoLabel.Text = total > 0 ? $"{current} / {total}" : "0 / 0";

        ZoomLabel.Text = $"{_bookService.CurrentZoom:F1}x";
    }
}
