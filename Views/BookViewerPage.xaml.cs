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
    private string _currentContentHtml = "";
    private string _currentViewMode = "content";
    private bool _bookLoaded = false;
    private int _startFolio = 1;
    private string _targetSectionId = "";

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
            if (!Directory.Exists(logFolder))
            {
                Directory.CreateDirectory(logFolder);
            }
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

            lock (_logLock)
            {
                File.AppendAllText(logFile, logMessage + Environment.NewLine);
            }

            System.Diagnostics.Debug.WriteLine(logMessage);

            if (message.Length < 80)
            {
                Dispatcher.Dispatch(() => StatusLabel.Text = message);
            }
            else
            {
                Dispatcher.Dispatch(() => StatusLabel.Text = message.Substring(0, 77) + "...");
            }
        }
        catch
        {
        }
    }

    public BookViewerPage(string bookFolder, int startFolio)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        _startFolio = startFolio;

        Log("=== BOOK VIEWER PAGE INITIALIZED ===");
        Log($"Book folder: {bookFolder}");
        Log($"Start folio: {_startFolio}");
        Log($"Log file: {GetLogFilePath()}");

        _bookService.OnPagesLoaded += (s, pages) =>
        {
            Dispatcher.Dispatch(() => UpdateUI());
        };

        _bookService.OnPageChanged += (s, content) =>
        {
            Dispatcher.Dispatch(() =>
            {
                _currentContentHtml = content;
                UpdateDisplay();
                UpdateUI();
                // Keep the MAUI toolbar buttons in sync with the state that
                // BookService has (which is what the in-page checkboxes show).
                UpdateTeacherButton();
                UpdateStudentButton();
            });
        };

        _bookService.OnStatusChanged += (s, msg) =>
            Dispatcher.Dispatch(() => StatusLabel.Text = msg);

        _bookService.OnBookLoaded += (s, title) =>
            Dispatcher.Dispatch(() => BookTitleLabel.Text = title);

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

        _bookService.OnSideBySideToggled += (s, enabled) =>
        {
            Dispatcher.Dispatch(() => UpdateDisplay());
        };

        _bookService.OnZoomChanged += (s, zoom) =>
            Dispatcher.Dispatch(() => ZoomLabel.Text = $"{zoom:F1}x");

        Loaded += async (s, e) =>
        {
            if (_bookLoaded) return;
            _bookLoaded = true;

            Log("Loaded event fired → calling LoadBookAsync");
            var success = await _bookService.LoadBookAsync(bookFolder);
            if (success)
            {
                TeacherNotesButton.IsEnabled = true;
                StudentAnswersButton.IsEnabled = true;
                DecryptButton.IsEnabled = true;
                SideBySideButton.IsEnabled = true;
                ViewModeButton.IsEnabled = true;
                ExportPdfButton.IsEnabled = true;
                GridButton.IsEnabled = true;

                _currentViewMode = "content";
                UpdateViewModeButton();
                UpdateTeacherButton();
                UpdateStudentButton();

                if (!string.IsNullOrEmpty(_targetSectionId))
                {
                    int idx = _bookService.GetFirstIndexOfSection(_targetSectionId);
                    Log($"Section {_targetSectionId} → first index {idx}");

                    if (idx < 0 && _startFolio > 0)
                    {
                        idx = _bookService.GetIndexForSectionFolio(_targetSectionId, _startFolio);
                        Log($"Fallback folio {_startFolio} → index {idx}");
                    }

                    if (idx < 0 && _startFolio > 0)
                    {
                        idx = _startFolio - 1;
                        Log($"Last fallback: folio {_startFolio} → index {idx}");
                    }

                    if (idx >= 0 && idx < _bookService.PageFiles.Count)
                    {
                        await _bookService.LoadPageAsync(idx);
                    }
                }
                else if (_startFolio > 0)
                {
                    int idx = _bookService.GetIndexForFolio(_startFolio);
                    if (idx < 0) idx = _startFolio - 1;
                    if (idx >= 0 && idx < _bookService.PageFiles.Count)
                    {
                        await _bookService.LoadPageAsync(idx);
                    }
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

    private void UpdateDisplay()
    {
        bool isTwoPage = _bookService.TwoPageSpread;

        if (isTwoPage)
        {
            RightColumn.Width = new GridLength(1, GridUnitType.Star);

            ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
            ContentWebView.IsVisible = true;

            if (!string.IsNullOrEmpty(_bookService.NextPageHtml))
            {
                NextPageWebView.Source = new HtmlWebViewSource { Html = _bookService.NextPageHtml };
            }
            else
            {
                NextPageWebView.Source = new HtmlWebViewSource { Html = $"<html><body style='background:{SurroundColor};'></body></html>" };
            }
            NextPageWebView.IsVisible = true;

            TeacherWebView.IsVisible = false;
            StudentWebView.IsVisible = false;
        }
        else
        {
            RightColumn.Width = new GridLength(0);
            NextPageWebView.IsVisible = false;

            // The page itself contains both the base content and the answer
            // layer with in-page toggle buttons. We just hand it to the WebView.
            ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
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
        // Cycle the toolbar view mode, which now just flips the same
        // BookService toggle state that the in-page buttons flip.
        bool tn = _bookService.GetToggleState("teacherNotes");
        bool sa = _bookService.GetToggleState("studentAnswer");

        if (!tn && !sa)
        {
            _bookService.SetToggleState("teacherNotes", true);
            _currentViewMode = "teacher";
        }
        else if (tn)
        {
            _bookService.SetToggleState("teacherNotes", false);
            _bookService.SetToggleState("studentAnswer", true);
            _currentViewMode = "student";
        }
        else
        {
            _bookService.SetToggleState("studentAnswer", false);
            _currentViewMode = "content";
        }

        UpdateViewModeButton();
        UpdateTeacherButton();
        UpdateStudentButton();
    }

    private void OnTeacherNotesClicked(object sender, EventArgs e)
    {
        bool current = _bookService.GetToggleState("teacherNotes");
        _bookService.SetToggleState("teacherNotes", !current);
        _currentViewMode = !current ? "teacher" : "content";
        UpdateTeacherButton();
        UpdateStudentButton();
        UpdateViewModeButton();
    }

    private void OnStudentAnswersClicked(object sender, EventArgs e)
    {
        bool current = _bookService.GetToggleState("studentAnswer");
        _bookService.SetToggleState("studentAnswer", !current);
        _currentViewMode = !current ? "student" : "content";
        UpdateTeacherButton();
        UpdateStudentButton();
        UpdateViewModeButton();
    }

    private void UpdateTeacherButton()
    {
        if (TeacherNotesButton == null) return;

        bool active = _bookService.GetToggleState("teacherNotes");
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;

        TeacherNotesButton.Text = active ? "✎ Hide Notes" : "✎ Notes";
        TeacherNotesButton.BackgroundColor = active
            ? Color.FromArgb("#34C759")
            : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
        TeacherNotesButton.TextColor = active
            ? Colors.White
            : (dark ? Colors.White : Colors.Black);
    }

    private void UpdateStudentButton()
    {
        if (StudentAnswersButton == null) return;

        bool active = _bookService.GetToggleState("studentAnswer");
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;

        StudentAnswersButton.Text = active ? "✓ Hide Answers" : "✓ Answers";
        StudentAnswersButton.BackgroundColor = active
            ? Color.FromArgb("#34C759")
            : (dark ? Color.FromArgb("#2C2C2E") : Color.FromArgb("#F7F7F9"));
        StudentAnswersButton.TextColor = active
            ? Colors.White
            : (dark ? Colors.White : Colors.Black);
    }

    private async void OnBackClicked(object sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }

    private async void OnExportPdfClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_bookService.CurrentBookPath))
        {
            await DisplayAlert("Info", "Please load a book first.", "OK");
            return;
        }

        try
        {
            var includeAnswers = await DisplayAlert("Export Options",
                "Include answers in the PDF?",
                "Yes", "No");

            bool includeTeacherNotes = false;
            bool includeStudentAnswers = false;

            if (includeAnswers)
            {
                includeTeacherNotes = await DisplayAlert("Answer Type",
                    "Include Teacher Notes?", "Yes", "No");
                includeStudentAnswers = await DisplayAlert("Answer Type",
                    "Include Student Answers?", "Yes", "No");

                if (!includeTeacherNotes && !includeStudentAnswers)
                    includeAnswers = false;
            }

            var bookTitle = _bookService.BookTitle;
            var safeFileName = string.Join("_", bookTitle.Split(Path.GetInvalidFileNameChars()));
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
                return await BookViewer.Platforms.iOS.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
            #elif MACCATALYST
                return await BookViewer.Platforms.MacCatalyst.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
            #elif ANDROID
                return await BookViewer.Platforms.Android.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
            #elif WINDOWS
                return await BookViewer.Platforms.Windows.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
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

            var openNow = await DisplayAlert("Export Complete",
                $"PDF saved to:\n{resultPath}\n\nOpen it now?",
                "Open", "Later");

            if (openNow)
            {
                await Launcher.Default.OpenAsync(new OpenFileRequest
                {
                    File = new ReadOnlyFile(resultPath)
                });
            }
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
            (pageIndex) =>
            {
                Dispatcher.Dispatch(async () =>
                {
                    await _bookService.LoadPageAsync(pageIndex);
                });
            });

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
            if (newIndex >= 0)
                _ = _bookService.LoadPageAsync(newIndex);
            else if (_bookService.CurrentPageIndex > 0)
                _ = _bookService.LoadPageAsync(0);
        }
        else
        {
            _bookService.NavigatePrevious();
        }
    }

    private void OnNextClicked(object sender, EventArgs e)
    {
        if (_bookService.TwoPageSpread)
        {
            var newIndex = _bookService.CurrentPageIndex + 2;
            if (newIndex < _bookService.PageFiles.Count)
                _ = _bookService.LoadPageAsync(newIndex);
            else if (_bookService.CurrentPageIndex < _bookService.PageFiles.Count - 1)
                _ = _bookService.LoadPageAsync(_bookService.PageFiles.Count - 1);
        }
        else
        {
            _bookService.NavigateNext();
        }
    }

    private void OnSideBySideClicked(object sender, EventArgs e)
    {
        _bookService.ToggleTwoPageSpread();
    }

    private void OnZoomInClicked(object sender, EventArgs e)
    {
        _bookService.ZoomIn();
    }

    private void OnZoomOutClicked(object sender, EventArgs e)
    {
        _bookService.ZoomOut();
    }

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
