using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private string _currentContentHtml = "";
    private string _currentTeacherHtml = "";
    private string _currentStudentHtml = "";
    private string _currentViewMode = "content";
    private bool _bookLoaded = false;
    private int _startFolio = 1;
    private string _targetSectionId;

    private static readonly object _logLock = new object();
    private static string _logFilePath = null;

    private string GetLogFilePath()
    {
        if (_logFilePath == null)
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

        ContentWebView.BackgroundColor = Colors.Transparent;
        NextPageWebView.BackgroundColor = Colors.Transparent;
        TeacherWebView.BackgroundColor = Colors.Transparent;
        StudentWebView.BackgroundColor = Colors.Transparent;

        _bookService.OnPagesLoaded += (s, pages) =>
        {
            Dispatcher.Dispatch(() => UpdateUI());
        };

        _bookService.OnPageChanged += (s, content) =>
        {
            Dispatcher.Dispatch(() =>
            {
                _currentContentHtml = content;
                LoadTeacherView();
                LoadStudentView();
                UpdateDisplay();
                UpdateUI();
            });
        };

        _bookService.OnStatusChanged += (s, msg) =>
            Dispatcher.Dispatch(() => StatusLabel.Text = msg);

        _bookService.OnBookLoaded += (s, title) =>
            Dispatcher.Dispatch(() => BookTitleLabel.Text = title);
        // In constructor — remove these lines:
        // ContentWebView.BackgroundColor = Colors.Transparent;
        // NextPageWebView.BackgroundColor = Colors.Transparent;
        // TeacherWebView.BackgroundColor = Colors.Transparent;
        // StudentWebView.BackgroundColor = Colors.Transparent;
        // (they're not set in XAML anymore, remove entirely)
        
        // Replace the OnTwoPageSpreadToggled handler:
        _bookService.OnTwoPageSpreadToggled += (s, enabled) =>
        {
            Dispatcher.Dispatch(() =>
            {
                SideBySideButton.Text = enabled ? "2 Pages" : "1 Page";
                SideBySideButton.BackgroundColor = enabled
                    ? Color.FromArgb("#34C759")
                    : (Application.Current?.Resources.TryGetValue("ToggleButton", out var _) == true
                        ? (Color)(Application.Current.RequestedTheme == AppTheme.Dark
                            ? Color.FromArgb("#2C2C2E")
                            : Color.FromArgb("#EFEFF0"))
                        : Color.FromArgb("#EFEFF0"));
                SideBySideButton.TextColor = enabled
                    ? Colors.White
                    : (Application.Current.RequestedTheme == AppTheme.Dark
                        ? Colors.White
                        : Color.FromArgb("#000000"));
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
                _showTeacherNotes = false;
                _showStudentAnswers = false;
                UpdateViewModeButton();
                UpdateTeacherButton();
                UpdateStudentButton();
        
                if (!string.IsNullOrEmpty(_targetSectionId))
                {
                    // Primary: use book.xml seqindex
                    int idx = _bookService.GetFirstIndexOfSection(_targetSectionId);
                    Log($"Section {_targetSectionId} → first index {idx}");
        
                    // Fallback: pages.xml folio map
                    if (idx < 0 && _startFolio > 0)
                    {
                        idx = _bookService.GetIndexForSectionFolio(_targetSectionId, _startFolio);
                        Log($"Fallback folio {_startFolio} → index {idx}");
                    }
        
                    // Last fallback: folio minus 1
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

    /// <summary>
    /// Section-scoped constructor: jumps to the first page of the given section.
    /// </summary>
    public BookViewerPage(string bookFolder, string sectionId, int startFolio)
        : this(bookFolder, startFolio)
    {
        _targetSectionId = sectionId;
    }

    private async void LoadTeacherView()
    {
        try
        {
            if (_bookService.CurrentPageIndex >= 0 && _bookService.CurrentPageIndex < _bookService.PageFiles.Count)
            {
                var filePath = _bookService.PageFiles[_bookService.CurrentPageIndex];
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileName(filePath) ?? "";

                string baseContent = await GetBaseContentAsync(filePath);
                string redContent = await _bookService.GetRedAnswerContentAsync(filePath, "teacherNotes");
                string bgImage = _bookService.GetStepBackgroundImage(filePath);
                string fontCss = await _bookService.GetFontCssWithEmbeddedFonts(directory);

                _currentTeacherHtml = BuildFullViewHtml(bgImage, baseContent, redContent, fileName, fontCss, "teacherNotes");
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading teacher view: {ex.Message}");
        }
    }

    private async void LoadStudentView()
    {
        try
        {
            if (_bookService.CurrentPageIndex >= 0 && _bookService.CurrentPageIndex < _bookService.PageFiles.Count)
            {
                var filePath = _bookService.PageFiles[_bookService.CurrentPageIndex];
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileName(filePath) ?? "";

                string baseContent = await GetBaseContentAsync(filePath);
                string redContent = await _bookService.GetRedAnswerContentAsync(filePath, "studentAnswers");
                string bgImage = _bookService.GetStepBackgroundImage(filePath);
                string fontCss = await _bookService.GetFontCssWithEmbeddedFonts(directory);

                _currentStudentHtml = BuildFullViewHtml(bgImage, baseContent, redContent, fileName, fontCss, "studentAnswers");
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading student view: {ex.Message}");
        }
    }

    private async Task<string> GetBaseContentAsync(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath) ?? "";
            var fileName = Path.GetFileName(filePath) ?? "";
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            string oriHtmlPath = Path.Combine(directory, nameWithoutExt + "_ori.html");
            if (File.Exists(oriHtmlPath))
            {
                string oriContent = await File.ReadAllTextAsync(oriHtmlPath);
                var bodyMatch = Regex.Match(oriContent, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                if (bodyMatch.Success)
                    return bodyMatch.Groups[1].Value;
                return oriContent;
            }

            string paraXmlPath = Path.Combine(directory, nameWithoutExt + "_para.xml");
            if (File.Exists(paraXmlPath))
            {
                string paraContent = await File.ReadAllTextAsync(paraXmlPath);
                var doc = new XmlDocument();
                doc.LoadXml(paraContent);
                var parasNode = doc.SelectSingleNode("//paras");
                if (parasNode != null)
                {
                    var result = "";
                    foreach (XmlNode child in parasNode.ChildNodes)
                    {
                        if (child.Name == "para")
                        {
                            var text = child.InnerText;
                            var style = child.Attributes?["style"]?.Value ?? "";
                            result += $"<div class='para' style='{style}'>{text}</div>";
                        }
                    }
                    return result;
                }
                return "";
            }

            return "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";
        }
        catch (Exception ex)
        {
            Log($"Error getting base content: {ex.Message}");
            return "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";
        }
    }

    private string BuildFullViewHtml(string bgImage, string baseContent, string redContent, string fileName, string fontCss, string viewType)
    {
        double zoom = _bookService.CurrentZoom;

        if (string.IsNullOrEmpty(bgImage))
            bgImage = GetPlaceholderImage();

        string borderColor = viewType == "teacherNotes" ? "#3498db" : "#2ecc71";
        string highlightClass = viewType == "teacherNotes" ? "tbnote" : "sa";

        if (string.IsNullOrEmpty(redContent))
            return BuildBaseContentHtml(bgImage, baseContent, fileName, fontCss);

        return $@"
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset='UTF-8'>
            <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes'>
            <title>{fileName}</title>
            <style>
                {fontCss}
                * {{ margin: 0; padding: 0; box-sizing: border-box; }}
                html, body {{
                    width: 100%;
                    height: 100%;
                    overflow: auto;
                    background: #E8E8E8;
                }}
                body {{
                    display: flex;
                    justify-content: center;
                    align-items: flex-start;
                    min-height: 100vh;
                    padding: 10px;
                }}
                .page-container {{
                    position: relative;
                    width: 1024px;
                    height: 1344px;
                    flex-shrink: 0;
                    background: #ffffff;
                    box-shadow: 0 0 20px rgba(0,0,0,0.15);
                    overflow: hidden;
                    border-radius: 2px;
                    transform: scale({zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)});
                    transform-origin: top center;
                }}
                .background-img {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    object-fit: contain;
                    pointer-events: none;
                    z-index: 1;
                }}
                .content-overlay {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    z-index: 2;
                }}
                .content-overlay > * {{ position: absolute !important; }}
                .base-content {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    z-index: 5;
                    pointer-events: none;
                }}
                .base-content > * {{ position: absolute !important; }}
                .highlight-overlay {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    z-index: 10;
                    pointer-events: none;
                    overflow: visible;
                }}
                .highlight-overlay > *,
                .highlight-overlay > * > * {{
                    position: absolute !important;
                }}
                .{highlightClass} {{
                    background: rgba(255, 255, 0, 0.25);
                    border: 3px solid {borderColor};
                    border-radius: 4px;
                    padding: 3px;
                }}
            </style>
        </head>
        <body>
            <div class='page-container'>
                <img class='background-img' src='{bgImage}' alt='' />
                <div class='content-overlay'>
                    <div class='base-content'>{baseContent}</div>
                    <div class='highlight-overlay'>{redContent}</div>
                </div>
            </div>
        </body>
        </html>";
    }

    private string BuildBaseContentHtml(string bgImage, string baseContent, string fileName, string fontCss)
    {
        double zoom = _bookService.CurrentZoom;

        if (string.IsNullOrEmpty(bgImage))
            bgImage = GetPlaceholderImage();

        return $@"
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset='UTF-8'>
            <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes'>
            <title>{fileName}</title>
            <style>
                {fontCss}
                * {{ margin: 0; padding: 0; box-sizing: border-box; }}
                html, body {{
                    width: 100%;
                    height: 100%;
                    overflow: auto;
                    background: #E8E8E8;
                }}
                body {{
                    display: flex;
                    justify-content: center;
                    align-items: flex-start;
                    min-height: 100vh;
                    padding: 10px;
                }}
                .page-container {{
                    position: relative;
                    width: 1024px;
                    height: 1344px;
                    flex-shrink: 0;
                    background: #ffffff;
                    box-shadow: 0 0 20px rgba(0,0,0,0.15);
                    overflow: hidden;
                    border-radius: 2px;
                    transform: scale({zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)});
                    transform-origin: top center;
                }}
                .background-img {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    object-fit: contain;
                    pointer-events: none;
                    z-index: 1;
                }}
                .content-overlay {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    z-index: 2;
                }}
                .content-overlay > * {{ position: absolute !important; }}
                .base-content {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    z-index: 5;
                    pointer-events: none;
                }}
                .base-content > * {{ position: absolute !important; }}
            </style>
        </head>
        <body>
            <div class='page-container'>
                <img class='background-img' src='{bgImage}' alt='' />
                <div class='content-overlay'>
                    <div class='base-content'>{baseContent}</div>
                </div>
            </div>
        </body>
        </html>";
    }

    private string GetPlaceholderImage()
    {
        return "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='1024' height='1344'%3E%3Crect width='1024' height='1344' fill='%23ffffff'/%3E%3C/svg%3E";
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
                NextPageWebView.Source = new HtmlWebViewSource { Html = "<html><body style='background:#E8E8E8;'></body></html>" };
            }
            NextPageWebView.IsVisible = true;

            TeacherWebView.IsVisible = false;
            StudentWebView.IsVisible = false;
        }
        else
        {
            RightColumn.Width = new GridLength(0);
            NextPageWebView.IsVisible = false;

            string html = _currentViewMode switch
            {
                "content" => _currentContentHtml,
                "teacher" => _currentTeacherHtml,
                "student" => _currentStudentHtml,
                _ => _currentContentHtml
            };

            if (!string.IsNullOrEmpty(html))
            {
                ContentWebView.Source = new HtmlWebViewSource { Html = html };
            }
            else
            {
                ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
            }

            if (_showTeacherNotes && !string.IsNullOrEmpty(_currentTeacherHtml))
            {
                TeacherWebView.IsVisible = true;
                StudentWebView.IsVisible = false;
                TeacherWebView.Source = new HtmlWebViewSource { Html = _currentTeacherHtml };
            }
            else if (_showStudentAnswers && !string.IsNullOrEmpty(_currentStudentHtml))
            {
                TeacherWebView.IsVisible = false;
                StudentWebView.IsVisible = true;
                StudentWebView.Source = new HtmlWebViewSource { Html = _currentStudentHtml };
            }
            else
            {
                TeacherWebView.IsVisible = false;
                StudentWebView.IsVisible = false;
            }

            UpdateViewModeButton();
        }
    }

    private void UpdateViewModeButton()
    {
        string text = _currentViewMode switch
        {
            "content" => "Content",
            "teacher" => "Teacher",
            "student" => "Student",
            _ => "Content"
        };
        ViewModeButton.Text = text;
    }

    private void OnViewModeClicked(object sender, EventArgs e)
    {
        _currentViewMode = _currentViewMode switch
        {
            "content" when (!string.IsNullOrEmpty(_currentTeacherHtml)) => "teacher",
            "teacher" when (!string.IsNullOrEmpty(_currentStudentHtml)) => "student",
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
        if (!string.IsNullOrEmpty(_currentTeacherHtml))
        {
            _currentViewMode = _currentViewMode == "teacher" ? "content" : "teacher";
            UpdateDisplay();
            UpdateViewModeButton();
            UpdateTeacherButton();
            UpdateStudentButton();
        }
        else
        {
            StatusLabel.Text = "No teacher notes available for this page";
        }
    }

    private void OnStudentAnswersClicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentStudentHtml))
        {
            _currentViewMode = _currentViewMode == "student" ? "content" : "student";
            UpdateDisplay();
            UpdateViewModeButton();
            UpdateTeacherButton();
            UpdateStudentButton();
        }
        else
        {
            StatusLabel.Text = "No student answers available for this page";
        }
    }
    
    private void UpdateTeacherButton()
    {
        if (TeacherNotesButton == null) return;
    
        bool active = _currentViewMode == "teacher";
        TeacherNotesButton.Text = active ? "Hide Notes" : "Notes";
        TeacherNotesButton.BackgroundColor = active
            ? Color.FromArgb("#34C759")
            : (Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#2C2C2E")
                : Color.FromArgb("#EFEFF0"));
        TeacherNotesButton.TextColor = active
            ? Colors.White
            : (Application.Current?.RequestedTheme == AppTheme.Dark
                ? Colors.White
                : Color.FromArgb("#000000"));
    }
    
    private void UpdateStudentButton()
    {
        if (StudentAnswersButton == null) return;
    
        bool active = _currentViewMode == "student";
        StudentAnswersButton.Text = active ? "Hide Answers" : "Answers";
        StudentAnswersButton.BackgroundColor = active
            ? Color.FromArgb("#34C759")
            : (Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#2C2C2E")
                : Color.FromArgb("#EFEFF0"));
        StudentAnswersButton.TextColor = active
            ? Colors.White
            : (Application.Current?.RequestedTheme == AppTheme.Dark
                ? Colors.White
                : Color.FromArgb("#000000"));
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
