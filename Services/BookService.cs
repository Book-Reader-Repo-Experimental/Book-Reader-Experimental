using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace BookViewer
{
    public class BookService
    {
        private string _currentBookPath = "";
        private string _currentBookUid = "";
        private List<string> _pageFiles = new();
        private int _currentPageIndex = -1;
        private readonly DecryptionService _decryptionService = new();
        private string _bookTitle = "Unknown Book";
        private string _currentPageHtml = "";
        private readonly Dictionary<string, string> _imageCache = new();
        private readonly Dictionary<string, string> _fontCache = new();
        private bool _sideBySideMode = false;
        private bool _twoPageSpread = false;
        private string _nextPageHtml = "";
        private double _currentZoom = 1.0;
        private string _tempFolder = "";

        // Toggle state, mirrored into the rendered HTML as "checked" on the
        // matching checkbox. BookService does not render its own buttons;
        // the buttons live inside the page, generated from redAnswer.
        private bool _showTeacherNotes = false;
        private bool _showStudentAnswers = false;

        private readonly Dictionary<string, int> _stepFileToIndex = new();
        private readonly Dictionary<int, string> _folioToStepFile = new();
        private readonly Dictionary<string, Dictionary<int, string>> _sectionFolioMap = new();
        private readonly Dictionary<string, int> _sectionFirstIndex = new();

        private static readonly object _logLock = new object();
        private static string _logFilePath = "";

        public int CurrentPageWidth { get; private set; } = 1024;
        public int CurrentPageHeight { get; private set; } = 1344;

        private static string SurroundColor =>
            Application.Current?.RequestedTheme == AppTheme.Dark ? "#1C1C1E" : "#E8E8E8";

        private string GetLogFilePath()
        {
            if (string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    string logFolder = Path.Combine(FileSystem.AppDataDirectory, "Logs");
                    Directory.CreateDirectory(logFolder);
                    _logFilePath = Path.Combine(logFolder, $"BookService_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                }
                catch
                {
                    _logFilePath = Path.Combine(Path.GetTempPath(), $"BookService_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                }
            }
            return _logFilePath;
        }

        public string GetLogPath() => GetLogFilePath();

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
                    OnStatusChanged?.Invoke(this, message);
                else
                    OnStatusChanged?.Invoke(this, message.Substring(0, 77) + "...");
            }
            catch
            {
            }
        }

        public event EventHandler<List<string>> OnPagesLoaded;
        public event EventHandler<string> OnPageChanged;
        public event EventHandler<string> OnStatusChanged;
        public event EventHandler<string> OnBookLoaded;
        public event EventHandler<bool> OnToggleAnswersRequested;
        public event EventHandler<bool> OnSideBySideToggled;
        public event EventHandler<bool> OnTwoPageSpreadToggled;
        public event EventHandler<double> OnZoomChanged;

        public List<string> PageFiles => _pageFiles;
        public int CurrentPageIndex => _currentPageIndex;
        public string CurrentBookPath => _currentBookPath;
        public string BookTitle => _bookTitle;
        public string CurrentPageHtml => _currentPageHtml;
        public bool SideBySideMode => _sideBySideMode;
        public bool TwoPageSpread => _twoPageSpread;
        public string NextPageHtml => _nextPageHtml;
        public double CurrentZoom => _currentZoom;

        public bool ShowTeacherNotes => _showTeacherNotes;
        public bool ShowStudentAnswers => _showStudentAnswers;

        public Dictionary<int, string> FolioToStepFile => _folioToStepFile;
        public Dictionary<string, int> StepFileToIndex => _stepFileToIndex;

        // Flip the on-page toggle for a given redanswertype. The page's own
        // checkbox carries that state; we just rebuild the HTML with the
        // checkbox checked/unchecked. No JS bridge needed.
        public void SetToggleState(string redAnswerType, bool on)
        {
            switch (redAnswerType)
            {
                case "teacherNotes":   _showTeacherNotes   = on; break;
                case "studentAnswer":
                case "studentAnswers": _showStudentAnswers = on; break;
                default:
                    // Unknown type — remember it anyway so the button reflects
                    // state even for future answer kinds.
                    _extraToggles[redAnswerType] = on;
                    break;
            }

            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
        }

        private readonly Dictionary<string, bool> _extraToggles = new();

        public bool GetToggleState(string redAnswerType)
        {
            return redAnswerType switch
            {
                "teacherNotes"   => _showTeacherNotes,
                "studentAnswer"  => _showStudentAnswers,
                "studentAnswers" => _showStudentAnswers,
                _                => _extraToggles.TryGetValue(redAnswerType, out var v) && v
            };
        }

        // Kept for API compatibility with BookViewerPage.xaml.cs.
        public void SetShowTeacherNotes(bool show) => SetToggleState("teacherNotes", show);
        public void SetShowStudentAnswers(bool show) => SetToggleState("studentAnswer", show);

        public bool IsFileEncrypted(string content) => _decryptionService.IsEncrypted(content);
        public string DecryptFile(string content, string fileName) => _decryptionService.DecryptWithFileName(content, fileName);

        public void SetZoom(double zoom)
        {
            _currentZoom = Math.Max(0.5, Math.Min(3.0, zoom));
            OnZoomChanged?.Invoke(this, _currentZoom);
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
        }

        public void ZoomIn() => SetZoom(_currentZoom + 0.1);
        public void ZoomOut() => SetZoom(_currentZoom - 0.1);
        public void ResetZoom() => SetZoom(1.0);

        public void ToggleSideBySide()
        {
            _sideBySideMode = !_sideBySideMode;
            OnSideBySideToggled?.Invoke(this, _sideBySideMode);
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
        }

        public void ToggleTwoPageSpread()
        {
            _twoPageSpread = !_twoPageSpread;
            _sideBySideMode = _twoPageSpread;
            OnTwoPageSpreadToggled?.Invoke(this, _twoPageSpread);
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
        }

        public int GetIndexForStepFile(string stepFile)
        {
            if (string.IsNullOrEmpty(stepFile)) return -1;
            var name = Path.GetFileNameWithoutExtension(stepFile);
            if (_stepFileToIndex.TryGetValue(name, out int idx))
                return idx;
            return -1;
        }

        public int GetIndexForFolio(int folio)
        {
            if (_folioToStepFile.TryGetValue(folio, out var stepFile))
                return GetIndexForStepFile(stepFile);
            return -1;
        }

        public int GetIndexForSectionFolio(string sectionId, int folio)
        {
            if (string.IsNullOrEmpty(sectionId)) return -1;

            if (_sectionFolioMap.TryGetValue(sectionId, out var folioMap))
            {
                if (folioMap.TryGetValue(folio, out var stepFile))
                {
                    var composite = sectionId + "/" + stepFile;
                    if (_stepFileToIndex.TryGetValue(composite, out int idx))
                        return idx;

                    if (_stepFileToIndex.TryGetValue(stepFile, out int idx2))
                        return idx2;
                }
            }

            return -1;
        }

        public int GetFirstIndexOfSection(string sectionId)
        {
            if (string.IsNullOrEmpty(sectionId)) return -1;
            if (_sectionFirstIndex.TryGetValue(sectionId, out int idx))
                return idx;
            return -1;
        }

        public async Task<bool> LoadBookAsync(string folderPath)
        {
            try
            {
                Log($"=== LOADING BOOK from: {folderPath} ===");
                Log($"BookService log file: {GetLogFilePath()}");
                _currentBookPath = folderPath;

                _tempFolder = Path.Combine(FileSystem.CacheDirectory, "BookViewer", $"temp_{Guid.NewGuid().ToString().Substring(0, 8)}");
                Directory.CreateDirectory(_tempFolder);
                Log($"Temp folder: {_tempFolder}");

                var bookXmlPath = Path.Combine(folderPath, "book.xml");

                if (!File.Exists(bookXmlPath))
                {
                    Log($"ERROR: book.xml not found at: {bookXmlPath}");
                    OnStatusChanged?.Invoke(this, "book.xml not found");
                    return false;
                }

                var folderName = Path.GetFileName(folderPath);
                var bookIdMatch = Regex.Match(folderName, @"book_(\d+)");
                if (bookIdMatch.Success)
                {
                    _currentBookUid = bookIdMatch.Groups[1].Value;
                    Log($"Extracted book ID from folder: {_currentBookUid}");
                }

                var content = await File.ReadAllTextAsync(bookXmlPath);
                Log($"book.xml loaded, size: {content.Length} bytes");
                Log($"book.xml IsEncrypted check: {_decryptionService.IsEncrypted(content)}");

                if (string.IsNullOrEmpty(_currentBookUid))
                {
                    var uidMatch = Regex.Match(content, @"id=""([^""]+)""");
                    if (uidMatch.Success)
                    {
                        _currentBookUid = uidMatch.Groups[1].Value;
                        Log($"Extracted Book UID from book.xml: {_currentBookUid}");
                    }
                }

                bool bookXmlDecrypted = false;
                if (_decryptionService.IsEncrypted(content))
                {
                    Log("book.xml IS encrypted → decrypting book.xml");
                    if (string.IsNullOrEmpty(_currentBookUid))
                    {
                        Log("ERROR: Could not find book UID for decryption");
                        OnStatusChanged?.Invoke(this, "Could not decrypt book.xml - UID not found");
                        return false;
                    }

                    content = _decryptionService.DecryptBookFile(content, _currentBookUid);
                    Log($"book.xml decrypted, size: {content.Length} bytes");
                    await File.WriteAllTextAsync(bookXmlPath, content);
                    bookXmlDecrypted = true;
                }
                else
                {
                    Log("book.xml is NOT encrypted → skipping book.xml decryption");
                }

                _bookTitle = "Unknown Book";
                var titleMatch = Regex.Match(content, @"name=""([^""]+)""");
                if (titleMatch.Success) _bookTitle = titleMatch.Groups[1].Value;
                Log($"Book title: {_bookTitle}");
                Log($"Book UID: {_currentBookUid}");

                OnBookLoaded?.Invoke(this, _bookTitle);

                if (bookXmlDecrypted)
                {
                    Log("book.xml was decrypted → running DecryptBookFilesAsync");
                    await DecryptBookFilesAsync(folderPath);
                }

                _pageFiles.Clear();

                var stepsFiles = Directory.GetFiles(folderPath, "steps_*.html", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(folderPath, "step_*.html", SearchOption.AllDirectories))
                    .Where(f => IsPureStepsFile(Path.GetFileName(f)))
                    .Distinct()
                    .ToList();

                Log($"Found {stepsFiles.Count} step files");

                _pageFiles = stepsFiles
                    .GroupBy(f => Path.GetDirectoryName(f) ?? "")
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g =>
                        g.Select(f => new { Path = f, Index = ParseStepIndex(Path.GetFileName(f)) })
                         .OrderBy(x => x.Index >= 0 ? x.Index : int.MaxValue)
                         .ThenBy(x => x.Path)
                         .Select(x => x.Path)
                    )
                    .ToList();

                Log($"Sorted {_pageFiles.Count} page files");

                _stepFileToIndex.Clear();
                _folioToStepFile.Clear();
                _sectionFolioMap.Clear();
                _sectionFirstIndex.Clear();

                for (int i = 0; i < _pageFiles.Count; i++)
                {
                    var name = Path.GetFileNameWithoutExtension(_pageFiles[i]);
                    var parentDir = Path.GetFileName(Path.GetDirectoryName(_pageFiles[i]) ?? "");
                    var composite = parentDir + "/" + name;

                    if (!_stepFileToIndex.ContainsKey(composite))
                        _stepFileToIndex[composite] = i;

                    if (!_stepFileToIndex.ContainsKey(name))
                        _stepFileToIndex[name] = i;
                }

                try
                {
                    var bookDoc = new XmlDocument();
                    var xmlText = await File.ReadAllTextAsync(bookXmlPath);

                    var trimmedXml = xmlText.TrimStart();
                    if (!trimmedXml.StartsWith("<book") && !trimmedXml.StartsWith("<?xml") && !trimmedXml.StartsWith("<root"))
                        xmlText = $"<root>{xmlText}</root>";

                    bookDoc.LoadXml(xmlText);

                    var sectionNodes = bookDoc.SelectNodes("//sectiondetails/sectiondetail");
                    if (sectionNodes != null)
                    {
                        foreach (XmlNode sectionNode in sectionNodes)
                        {
                            var sectionId = sectionNode.Attributes?["id"]?.Value ?? "";
                            if (string.IsNullOrEmpty(sectionId)) continue;

                            var firstPageNode = sectionNode.SelectSingleNode(".//sectionorders/page");
                            if (firstPageNode != null)
                            {
                                var seqIndexStr = firstPageNode.Attributes?["seqindex"]?.Value ?? "";
                                if (int.TryParse(seqIndexStr, out int seqIndex))
                                    _sectionFirstIndex[sectionId] = seqIndex;
                            }

                            var pageNodes = sectionNode.SelectNodes(".//sectionorders/page");
                            if (pageNodes != null)
                            {
                                foreach (XmlNode p in pageNodes)
                                {
                                    var folioStr = p.Attributes?["folio"]?.Value ?? "";
                                    var file = p.Attributes?["file"]?.Value ?? "";
                                    if (int.TryParse(folioStr, out int folio) && !string.IsNullOrEmpty(file))
                                    {
                                        if (!_folioToStepFile.ContainsKey(folio))
                                            _folioToStepFile[folio] = file;
                                    }
                                }
                            }
                        }
                    }

                    Log($"book.xml: {_sectionFirstIndex.Count} sections mapped, {_folioToStepFile.Count} folios");
                }
                catch (Exception ex)
                {
                    Log($"Error parsing book.xml: {ex.Message}");
                }

                var sectionFolders = Directory.GetDirectories(folderPath, "s_*");
                foreach (var sectionDir in sectionFolders)
                {
                    var sectionId = Path.GetFileName(sectionDir);
                    var pagesXmlPath = Path.Combine(sectionDir, "pages.xml");
                    if (!File.Exists(pagesXmlPath)) continue;

                    try
                    {
                        var pagesContent = await File.ReadAllTextAsync(pagesXmlPath);

                        var trimmed = pagesContent.TrimStart();
                        if (trimmed.StartsWith("<?xml"))
                        {
                            var idx = pagesContent.IndexOf("?>");
                            if (idx >= 0)
                                pagesContent = pagesContent.Substring(idx + 2);
                            trimmed = pagesContent.TrimStart();
                        }

                        if (!trimmed.StartsWith("<pages"))
                            pagesContent = $"<pages>{pagesContent}</pages>";

                        var pagesDoc = new XmlDocument();
                        pagesDoc.LoadXml(pagesContent);

                        var folioMap = new Dictionary<int, string>();
                        var pageNodes = pagesDoc.SelectNodes("//page");
                        if (pageNodes != null)
                        {
                            foreach (XmlNode pageNode in pageNodes)
                            {
                                var folioStr = pageNode.Attributes?["folio"]?.Value ?? "";
                                var file = pageNode.Attributes?["file"]?.Value ?? "";
                                if (int.TryParse(folioStr, out int folio) && !string.IsNullOrEmpty(file))
                                    folioMap[folio] = file;
                            }
                        }

                        _sectionFolioMap[sectionId] = folioMap;
                    }
                    catch (Exception ex)
                    {
                        Log($"Error parsing {pagesXmlPath}: {ex.Message}");
                    }
                }

                Log($"Built section folio map: {_sectionFolioMap.Count} sections");

                OnPagesLoaded?.Invoke(this, _pageFiles);

                if (_pageFiles.Count > 0)
                {
                    _currentPageIndex = 0;
                    Log($"Loading first page: {Path.GetFileName(_pageFiles[0])}");
                    await LoadPageAsync(0);
                }
                else
                {
                    Log("No page files found!");
                    OnStatusChanged?.Invoke(this, "No pages found in this book");
                    return false;
                }

                OnStatusChanged?.Invoke(this, $"Loaded: {_bookTitle} ({_pageFiles.Count} pages)");
                Log($"=== BOOK LOADED SUCCESSFULLY ===");
                return true;
            }
            catch (Exception ex)
            {
                Log($"ERROR loading book: {ex.Message}");
                Log($"Stack trace: {ex.StackTrace}");
                OnStatusChanged?.Invoke(this, $"Error: {ex.Message}");
                return false;
            }
        }

        private async Task DecryptBookFilesAsync(string folderPath)
        {
            try
            {
                Log($"=== DECRYPT BOOK FILES START: {folderPath} ===");

                var htmlFiles = Directory.GetFiles(folderPath, "*.html", SearchOption.AllDirectories).ToList();
                var xmlFiles = Directory.GetFiles(folderPath, "*.xml", SearchOption.AllDirectories).ToList();
                var htmFiles = Directory.GetFiles(folderPath, "*.htm", SearchOption.AllDirectories).ToList();

                int bookKey = _decryptionService.CalculateKey(_currentBookUid);

                int decryptedCount = 0;
                int alreadyDecryptedCount = 0;
                int htmlDecryptedCount = 0;
                int htmlSkippedCount = 0;

                var allXmlAndHtmFiles = new List<string>();
                allXmlAndHtmFiles.AddRange(xmlFiles);
                allXmlAndHtmFiles.AddRange(htmFiles);

                foreach (var file in allXmlAndHtmFiles)
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var fileContent = await File.ReadAllTextAsync(file);
                        bool isEncrypted = _decryptionService.IsEncrypted(fileContent);

                        if (!isEncrypted)
                        {
                            alreadyDecryptedCount++;
                            continue;
                        }

                        string decryptedContent = _decryptionService.DecryptXmlOrHtm(fileContent, fileName);
                        await File.WriteAllTextAsync(file, decryptedContent);
                        decryptedCount++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Error decrypting {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                foreach (var file in htmlFiles)
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var fileContent = await File.ReadAllTextAsync(file);

                        bool isDecrypted = fileContent.Contains("<") && fileContent.Contains(">") &&
                                           (fileContent.Contains("</") || fileContent.Contains("/>")) &&
                                           (fileContent.Contains("class=") || fileContent.Contains("<div") || fileContent.Contains("id="));

                        if (isDecrypted)
                        {
                            htmlSkippedCount++;
                            continue;
                        }

                        string decryptedContent = _decryptionService.DecryptWithKey(fileContent, bookKey);
                        await File.WriteAllTextAsync(file, decryptedContent);
                        htmlDecryptedCount++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Error decrypting {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                Log($"Decrypt summary: XML/HTM {decryptedCount} decrypted, {alreadyDecryptedCount} plain; HTML {htmlDecryptedCount} decrypted, {htmlSkippedCount} plain");
            }
            catch (Exception ex)
            {
                Log($"ERROR in DecryptBookFilesAsync: {ex.Message}");
            }
        }

        public async Task DecryptBookAsync()
        {
            if (string.IsNullOrEmpty(_currentBookPath))
            {
                OnStatusChanged?.Invoke(this, "No book loaded");
                return;
            }

            OnStatusChanged?.Invoke(this, "Decrypting book files...");
            await DecryptBookFilesAsync(_currentBookPath);
            OnStatusChanged?.Invoke(this, "Decryption complete! Reloading book...");
            await LoadBookAsync(_currentBookPath);
        }

        private bool IsPureStepsFile(string fileName)
            => Regex.IsMatch(fileName ?? "", @"^steps?_\d+\.html$", RegexOptions.IgnoreCase);

        private int ParseStepIndex(string fileName)
        {
            try
            {
                var m = Regex.Match(fileName ?? "", @"^steps?_(\d+)\.html$", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int idx))
                    return idx;

                m = Regex.Match(fileName ?? "", @"(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int idx3))
                    return idx3;
            }
            catch { }
            return -1;
        }

        public async Task LoadPageAsync(int index)
        {
            if (index < 0 || index >= _pageFiles.Count) return;

            try
            {
                _currentPageIndex = index;
                var filePath = _pageFiles[index];
                var fileName = Path.GetFileName(filePath);

                var content = await File.ReadAllTextAsync(filePath);
                var processedHtml = await ProcessHtmlContent(content, filePath);
                _currentPageHtml = processedHtml;

                if (_twoPageSpread && index + 1 < _pageFiles.Count)
                {
                    var nextFilePath = _pageFiles[index + 1];
                    var nextContent = await File.ReadAllTextAsync(nextFilePath);
                    _nextPageHtml = await ProcessHtmlContent(nextContent, nextFilePath);
                }
                else
                {
                    _nextPageHtml = "";
                }

                OnPageChanged?.Invoke(this, processedHtml);
                OnStatusChanged?.Invoke(this, $"Viewing: {fileName} ({index + 1}/{_pageFiles.Count})");
                OnPagesLoaded?.Invoke(this, _pageFiles);
            }
            catch (Exception ex)
            {
                Log($"ERROR loading page: {ex.Message}");
                OnStatusChanged?.Invoke(this, $"Error loading page: {ex.Message}");
            }
        }

        // ================================================================
        // Page building
        // ================================================================

        private async Task<string> ProcessHtmlContent(string htmlContent, string filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileName(filePath) ?? "";
                string stem = Path.GetFileNameWithoutExtension(fileName);

                if (!IsPureStepsFile(fileName) || !fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    return htmlContent;

                // -- detect page size from the raw steps_N.html --
                int pageWidth = 1024;
                int pageHeight = 1344;

                var rezMatch = Regex.Match(htmlContent,
                    @"id\s*=\s*[""']rez[""'][^>]*style\s*=\s*[""'][^""']*width\s*:\s*(\d+)px[^""']*height\s*:\s*(\d+)px",
                    RegexOptions.IgnoreCase);
                if (rezMatch.Success)
                {
                    if (int.TryParse(rezMatch.Groups[1].Value, out int w)) pageWidth = w;
                    if (int.TryParse(rezMatch.Groups[2].Value, out int h)) pageHeight = h;
                    Log($"Detected page size from #rez: {pageWidth}x{pageHeight}");
                }
                else
                {
                    var bgMatch = Regex.Match(htmlContent,
                        @"class\s*=\s*[""']bgcls[""'][^>]*style\s*=\s*[""'][^""']*width\s*:\s*(\d+)px[^""']*height\s*:\s*(\d+)px",
                        RegexOptions.IgnoreCase);
                    if (bgMatch.Success)
                    {
                        if (int.TryParse(bgMatch.Groups[1].Value, out int w)) pageWidth = w;
                        if (int.TryParse(bgMatch.Groups[2].Value, out int h)) pageHeight = h;
                        Log($"Detected page size from .bgcls: {pageWidth}x{pageHeight}");
                    }
                }

                CurrentPageWidth = pageWidth;
                CurrentPageHeight = pageHeight;

                // -- background image --
                string bgImage = GetStepBackgroundImage(filePath);

                // -- base text from steps_N_base.html (fallback to _ori.html) --
                string baseHtml = await LoadBaseTextAsync(directory, stem, htmlContent);

                // -- answers: redAnswer is source of truth, fallback to _ori --
                var (answerLayer, answerTypes) = await LoadAnswerLayerAsync(directory, stem);

                // -- fonts --
                string fontCss = await GetFontCssWithEmbeddedFonts(directory);

                // -- assemble --
                return BuildOverlayHtml(
                    bgImage,
                    baseHtml,
                    answerLayer,
                    answerTypes,
                    fileName,
                    fontCss,
                    pageWidth,
                    pageHeight);
            }
            catch (Exception ex)
            {
                Log($"ERROR in ProcessHtmlContent: {ex.Message}");
                return htmlContent;
            }
        }

        private async Task<string> LoadBaseTextAsync(string directory, string stem, string rawStepsHtml)
        {
            // Preferred source: steps_N_base.html — already correctly positioned.
            var basePath = Path.Combine(directory, stem + "_base.html");
            if (File.Exists(basePath))
                return await File.ReadAllTextAsync(basePath);

            // Fallback: steps_N_ori.html with all tbred/tbnote stripped out.
            var oriPath = Path.Combine(directory, stem + "_ori.html");
            if (File.Exists(oriPath))
            {
                var ori = await File.ReadAllTextAsync(oriPath);
                return StripAnswerElements(ori);
            }

            // Last resort: use whatever was inside <pane class="base"> in the
            // raw steps_N.html, or the raw body.
            var pane = Regex.Match(rawStepsHtml,
                @"<pane[^>]*class\s*=\s*[""']base[""'][^>]*>(?<c>[\s\S]*?)</pane>",
                RegexOptions.IgnoreCase);
            if (pane.Success && !string.IsNullOrWhiteSpace(pane.Groups["c"].Value))
                return pane.Groups["c"].Value;

            return "";
        }

        private string StripAnswerElements(string html)
        {
            // Remove <svg class="tbred...">...</svg> and <svg class="tbnote...">...</svg>
            html = Regex.Replace(html,
                @"<svg\b[^>]*class\s*=\s*[""'][^""']*(?:tbred|tbnote)[^""']*[""'][^>]*>[\s\S]*?</svg>",
                "", RegexOptions.IgnoreCase);

            // Remove <div class="tbred...">...</div> and <div class="tbnote...">...</div>
            html = Regex.Replace(html,
                @"<div\b[^>]*class\s*=\s*[""'][^""']*(?:tbred|tbnote)[^""']*[""'][^>]*>[\s\S]*?</div>",
                "", RegexOptions.IgnoreCase);

            return html;
        }

        /// <summary>
        /// Reads steps_N_redAnswer.html (preferred) or falls back to the
        /// tbred/tbnote elements in steps_N_ori.html. Returns:
        ///   - layer HTML: each answer element wrapped in .answer-item
        ///     with a data-rat attribute equal to its redanswertype.
        ///   - set of distinct redanswertype values found.
        /// </summary>
        private async Task<(string Layer, HashSet<string> Types)> LoadAnswerLayerAsync(
            string directory, string stem)
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder();

            string redAnswerPath = Path.Combine(directory, stem + "_redAnswer.html");
            if (File.Exists(redAnswerPath))
            {
                var raw = await File.ReadAllTextAsync(redAnswerPath);
                AppendAnswerItems(sb, types, raw);
            }

            if (sb.Length == 0)
            {
                var oriPath = Path.Combine(directory, stem + "_ori.html");
                if (File.Exists(oriPath))
                {
                    var raw = await File.ReadAllTextAsync(oriPath);
                    AppendAnswerItems(sb, types, raw);
                }
            }

            return (sb.ToString(), types);
        }

        /// <summary>
        /// Walks an HTML fragment and emits one .answer-item per tbred/tbnote
        /// element. Preserves left/top/width/height from the original style,
        /// strips display:none and every other inline style, and records the
        /// redanswertype from the embedded has2/hsg redanswertype attribute.
        /// </summary>
        private void AppendAnswerItems(StringBuilder sb, HashSet<string> types, string raw)
        {
            // Divs first.
            var divMatches = Regex.Matches(raw,
                @"<div\b(?<attrs>[^>]*class\s*=\s*[""'][^""']*(?:tbred|tbnote)[^""']*[""'][^>]*)>(?<body>[\s\S]*?)</div>",
                RegexOptions.IgnoreCase);

            foreach (Match m in divMatches)
            {
                var attrs = m.Groups["attrs"].Value;
                var body = m.Groups["body"].Value;
                EmitAnswerItem(sb, types, "div", attrs, body);
            }

            // SVGs (teacher-note strokes in steps_N_ori.html).
            var svgMatches = Regex.Matches(raw,
                @"<svg\b(?<attrs>[^>]*class\s*=\s*[""'][^""']*(?:tbred|tbnote)[^""']*[""'][^>]*)>(?<body>[\s\S]*?)</svg>",
                RegexOptions.IgnoreCase);

            foreach (Match m in svgMatches)
            {
                var attrs = m.Groups["attrs"].Value;
                var body = m.Groups["body"].Value;
                EmitAnswerItem(sb, types, "svg", attrs, body);
            }
        }

        private void EmitAnswerItem(
            StringBuilder sb, HashSet<string> types, string tag, string attrs, string body)
        {
            // Skip empty shells (like <svg class="tbred" ...></svg> with nothing).
            bool hasBody = !string.IsNullOrWhiteSpace(body);
            bool hasHas2 = Regex.IsMatch(body, @"<has2\b", RegexOptions.IgnoreCase);
            bool hasPath = Regex.IsMatch(body, @"<path\b", RegexOptions.IgnoreCase);
            if (!hasBody && !hasHas2 && !hasPath) return;

            // Detect the redanswertype. Priority:
            //   1. redanswertype attribute on <has2> inside the body.
            //   2. redanswertype attribute on the outer element itself.
            //   3. class name: tbnote → teacherNotes, tbred → studentAnswer.
            string rat = null;

            var bodyRat = Regex.Match(body,
                @"redanswertype\s*=\s*[""'](?<t>[^""']+)[""']",
                RegexOptions.IgnoreCase);
            if (bodyRat.Success) rat = bodyRat.Groups["t"].Value;

            if (string.IsNullOrEmpty(rat))
            {
                var attrRat = Regex.Match(attrs,
                    @"redanswertype\s*=\s*[""'](?<t>[^""']+)[""']",
                    RegexOptions.IgnoreCase);
                if (attrRat.Success) rat = attrRat.Groups["t"].Value;
            }

            if (string.IsNullOrEmpty(rat))
            {
                if (Regex.IsMatch(attrs, @"class\s*=\s*[""'][^""']*\btbnote\b", RegexOptions.IgnoreCase))
                    rat = "teacherNotes";
                else
                    rat = "studentAnswer";
            }

            // Normalize to a consistent key. Book uses "studentAnswer" (singular).
            rat = rat == "studentAnswers" ? "studentAnswer" : rat;

            types.Add(rat);

            // Extract left/top/width/height from the style attribute.
            string style = "";
            var styleMatch = Regex.Match(attrs,
                @"style\s*=\s*[""'](?<s>[^""']*)[""']",
                RegexOptions.IgnoreCase);
            if (styleMatch.Success) style = styleMatch.Groups["s"].Value;

            var pos = new StringBuilder();
            foreach (var key in new[] { "left", "top", "width", "height" })
            {
                var mm = Regex.Match(style,
                    @"\b" + key + @"\s*:\s*(?<v>[^;]+)",
                    RegexOptions.IgnoreCase);
                if (mm.Success)
                    pos.Append(key).Append(':').Append(mm.Groups["v"].Value.Trim()).Append(';');
            }

            // Pull the class attribute so tbred/tbnote (and the x1..x9 kerning
            // modifiers) survive. Drop everything else.
            string cls = "";
            var classMatch = Regex.Match(attrs,
                @"class\s*=\s*[""'](?<c>[^""']*)[""']",
                RegexOptions.IgnoreCase);
            if (classMatch.Success) cls = classMatch.Groups["c"].Value;

            // Unwrap has2/hsg/hastext so the browser doesn't balk.
            string cleanBody = Regex.Replace(body,
                @"</?has2\b[^>]*>|</?hsg\b[^>]*>|</?hastext\b[^>]*>",
                "", RegexOptions.IgnoreCase);

            // Remove all inline display:none from the body.
            cleanBody = Regex.Replace(cleanBody,
                @"display\s*:\s*none\s*;?",
                "", RegexOptions.IgnoreCase);

            // SVGs need stroke/fill to survive. Re-emit them with the class
            // attribute (which carries the tbred/tbnote marker) and the body.
            if (tag == "svg")
            {
                // Extract width/height from the attrs — needed for SVG sizing.
                string svgW = Regex.Match(attrs, @"width\s*=\s*[""'](?<v>[^""']+)[""']",
                    RegexOptions.IgnoreCase).Groups["v"].Value;
                string svgH = Regex.Match(attrs, @"height\s*=\s*[""'](?<v>[^""']+)[""']",
                    RegexOptions.IgnoreCase).Groups["v"].Value;

                sb.Append("<div class=\"answer-item\" data-rat=\"")
                  .Append(rat)
                  .Append("\" style=\"")
                  .Append(pos.ToString())
                  .Append("\">");

                sb.Append("<svg class=\"").Append(cls).Append("\"");
                if (!string.IsNullOrEmpty(svgW)) sb.Append(" width=\"").Append(svgW).Append("\"");
                if (!string.IsNullOrEmpty(svgH)) sb.Append(" height=\"").Append(svgH).Append("\"");
                sb.Append(" xmlns=\"http://www.w3.org/2000/svg\">");
                sb.Append(cleanBody);
                sb.Append("</svg>");

                sb.Append("</div>\n");
                return;
            }

            // Div case: emit a nested div with the original class (so kerning
            // nudges x1..x9 still apply), wrapped in .answer-item.
            sb.Append("<div class=\"answer-item\" data-rat=\"")
              .Append(rat)
              .Append("\" style=\"")
              .Append(pos.ToString())
              .Append("\">");

            sb.Append("<div class=\"").Append(cls).Append("\">");
            sb.Append(cleanBody);
            sb.Append("</div>");

            sb.Append("</div>\n");
        }

        private async Task<string> GetFontCssWithEmbeddedFonts(string directory)
        {
            try
            {
                if (_fontCache.TryGetValue(directory, out var cached))
                    return cached;

                string fontCss = "";
                string fontCssPath = "";

                var fontCssFiles = Directory.GetFiles(directory, "*_font.css");
                if (fontCssFiles.Length > 0)
                    fontCssPath = fontCssFiles[0];
                else
                {
                    var rootFontCss = Path.Combine(_currentBookPath, "font.css");
                    if (File.Exists(rootFontCss))
                        fontCssPath = rootFontCss;
                }

                if (!string.IsNullOrEmpty(fontCssPath) && File.Exists(fontCssPath))
                {
                    fontCss = await File.ReadAllTextAsync(fontCssPath);

                    string fontsFolder = Path.Combine(_currentBookPath, "FONTS");
                    if (!Directory.Exists(fontsFolder))
                        fontsFolder = Path.Combine(_currentBookPath, "fonts");

                    Dictionary<string, string> fontFileIndex = null;
                    if (Directory.Exists(fontsFolder))
                    {
                        fontFileIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var f in Directory.GetFiles(fontsFolder, "*.*", SearchOption.AllDirectories))
                        {
                            var ext = Path.GetExtension(f).ToLowerInvariant();
                            if (ext != ".ttf" && ext != ".otf" && ext != ".woff" && ext != ".woff2")
                                continue;

                            var justName = Path.GetFileName(f);
                            var justStem = Path.GetFileNameWithoutExtension(f);
                            var noSpace = justStem.Replace(" ", "").Replace("-", "").Replace("_", "");

                            if (!fontFileIndex.ContainsKey(justName)) fontFileIndex[justName] = f;
                            if (!fontFileIndex.ContainsKey(justStem)) fontFileIndex[justStem] = f;
                            if (!fontFileIndex.ContainsKey(noSpace)) fontFileIndex[noSpace] = f;
                        }
                    }

                    var fontFaceMatches = Regex.Matches(
                        fontCss,
                        @"@font-face\s*\{(?<body>.*?)\}",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    int totalFaces = fontFaceMatches.Count;
                    int embeddedCount = 0;

                    foreach (Match match in fontFaceMatches)
                    {
                        var fontFaceContent = match.Groups[1].Value;
                        var urlMatches = Regex.Matches(fontFaceContent, @"url\(['""]?([^)'""]+)['""]?\)");
                        bool faceWasEmbedded = false;

                        foreach (Match urlMatch in urlMatches)
                        {
                            var rawPath = urlMatch.Groups[1].Value;
                            var cleanPath = rawPath
                                .Replace("../FONTS/", "")
                                .Replace("../fonts/", "")
                                .Replace("./", "")
                                .Trim();

                            string fullFontPath = null;

                            if (fontFileIndex != null)
                            {
                                var justName = Path.GetFileName(cleanPath);
                                var justStem = Path.GetFileNameWithoutExtension(cleanPath);
                                var noSpace = justStem.Replace(" ", "").Replace("-", "").Replace("_", "");

                                if (fontFileIndex.TryGetValue(justName, out var p1)) fullFontPath = p1;
                                else if (fontFileIndex.TryGetValue(justStem, out var p2)) fullFontPath = p2;
                                else if (fontFileIndex.TryGetValue(noSpace, out var p3)) fullFontPath = p3;
                            }

                            if (!string.IsNullOrEmpty(fullFontPath) && File.Exists(fullFontPath))
                            {
                                var fontBytes = File.ReadAllBytes(fullFontPath);
                                var fontBase64 = Convert.ToBase64String(fontBytes);
                                var ext = Path.GetExtension(fullFontPath).ToLower();
                                var mimeType = ext switch
                                {
                                    ".ttf" => "font/ttf",
                                    ".otf" => "font/otf",
                                    ".woff" => "font/woff",
                                    ".woff2" => "font/woff2",
                                    _ => "font/ttf"
                                };

                                var dataUri = $"data:{mimeType};base64,{fontBase64}";

                                fontCss = fontCss.Replace($"url('{rawPath}')", $"url('{dataUri}')");
                                fontCss = fontCss.Replace($"url(\"{rawPath}\")", $"url('{dataUri}')");
                                fontCss = fontCss.Replace($"url({rawPath})", $"url('{dataUri}')");

                                faceWasEmbedded = true;
                                Log($"Embedded font: {Path.GetFileName(fullFontPath)} ({fontBytes.Length} bytes)");
                            }
                            else
                            {
                                Log($"Font file NOT FOUND: '{rawPath}' in {fontsFolder}");
                            }
                        }
                        if (faceWasEmbedded)
                            embeddedCount++;
                    }

                    Log($"Font embedding summary: {embeddedCount} of {totalFaces} @font-face rules embedded");

                    fontCss += @"

/* ==== CJK metric lock ==== */
@font-face {
    font-family: 'CJKLocked';
    src: local('PingFang TC'), local('PingFang SC'), local('Heiti TC'),
         local('Microsoft JhengHei'), local('Microsoft YaHei'),
         local('Noto Sans CJK TC'), local('Noto Sans CJK SC'),
         local('Noto Serif CJK TC'), local('Noto Serif CJK SC');
    ascent-override: 100%;
    descent-override: 0%;
    line-gap-override: 0%;
}
body, .base-content, .base-content > div {
    font-family: 'CJKLocked', system-ui, -apple-system, 'Helvetica Neue', Arial, sans-serif;
}
";

                    _fontCache[directory] = fontCss;
                    return fontCss;
                }

                fontCss = await GenerateFontCssFromFiles();
                fontCss += @"

/* ==== CJK metric lock ==== */
@font-face {
    font-family: 'CJKLocked';
    src: local('PingFang TC'), local('PingFang SC'), local('Heiti TC'),
         local('Microsoft JhengHei'), local('Microsoft YaHei'),
         local('Noto Sans CJK TC'), local('Noto Sans CJK SC');
    ascent-override: 100%;
    descent-override: 0%;
    line-gap-override: 0%;
}
body, .base-content, .base-content > div {
    font-family: 'CJKLocked', system-ui, -apple-system, 'Helvetica Neue', Arial, sans-serif;
}
";
                _fontCache[directory] = fontCss;
                return fontCss;
            }
            catch (Exception ex)
            {
                Log($"Error loading fonts: {ex.Message}");
                return "";
            }
        }

        private async Task<string> GenerateFontCssFromFiles()
        {
            try
            {
                string fontCss = "";
                var fontsFolder = Path.Combine(_currentBookPath, "FONTS");
                if (!Directory.Exists(fontsFolder))
                    fontsFolder = Path.Combine(_currentBookPath, "fonts");
                if (!Directory.Exists(fontsFolder)) return "";

                var fontExtensions = new[] { ".ttf", ".otf", ".woff", ".woff2" };
                var fontFiles = new List<string>();
                foreach (var ext in fontExtensions)
                    fontFiles.AddRange(Directory.GetFiles(fontsFolder, "*" + ext));

                foreach (var fontFile in fontFiles)
                {
                    try
                    {
                        var fontName = Path.GetFileNameWithoutExtension(fontFile);
                        var fontBytes = File.ReadAllBytes(fontFile);
                        var fontBase64 = Convert.ToBase64String(fontBytes);
                        var ext = Path.GetExtension(fontFile).ToLower();
                        var format = ext switch
                        {
                            ".ttf" => "truetype",
                            ".otf" => "opentype",
                            ".woff" => "woff",
                            ".woff2" => "woff2",
                            _ => "truetype"
                        };
                        var mimeType = ext switch
                        {
                            ".ttf" => "font/ttf",
                            ".otf" => "font/otf",
                            ".woff" => "font/woff",
                            ".woff2" => "font/woff2",
                            _ => "font/ttf"
                        };
                        var dataUri = $"data:{mimeType};base64,{fontBase64}";

                        fontCss += $@"
@font-face {{
    font-family: '{fontName}';
    src: url('{dataUri}') format('{format}');
    font-weight: normal;
    font-style: normal;
    ascent-override: 100%;
    descent-override: 0%;
    line-gap-override: 0%;
}}";
                    }
                    catch { }
                }

                return fontCss;
            }
            catch { return ""; }
        }

        public string GetStepBackgroundImage(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath) ?? "";
            var stepNumber = ParseStepIndex(Path.GetFileName(filePath));

            var imagesPath = Path.Combine(directory, "images");
            if (Directory.Exists(imagesPath))
            {
                var imagePath = Path.Combine(imagesPath, $"steps_{stepNumber}.jpg");
                if (File.Exists(imagePath)) return ConvertImageToBase64HighQuality(imagePath);

                imagePath = Path.Combine(imagesPath, $"steps_{stepNumber}.png");
                if (File.Exists(imagePath)) return ConvertImageToBase64HighQuality(imagePath);

                imagePath = Path.Combine(imagesPath, $"step_{stepNumber}.jpg");
                if (File.Exists(imagePath)) return ConvertImageToBase64HighQuality(imagePath);

                imagePath = Path.Combine(imagesPath, $"step_{stepNumber}.png");
                if (File.Exists(imagePath)) return ConvertImageToBase64HighQuality(imagePath);
            }

            return GetPlaceholderImage();
        }

        private string GetPlaceholderImage()
            => "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='1024' height='1344'%3E%3Crect width='1024' height='1344' fill='%23ffffff'/%3E%3C/svg%3E";

        private string ConvertImageToBase64HighQuality(string imagePath)
        {
            try
            {
                if (_imageCache.TryGetValue(imagePath, out var cached)) return cached;

                var bytes = File.ReadAllBytes(imagePath);
                var extension = Path.GetExtension(imagePath).ToLower();
                var mimeType = extension switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    ".svg" => "image/svg+xml",
                    _ => "image/jpeg"
                };

                var base64 = Convert.ToBase64String(bytes);
                var result = $"data:{mimeType};base64,{base64}";
                _imageCache[imagePath] = result;
                return result;
            }
            catch
            {
                return GetPlaceholderImage();
            }
        }

        /// <summary>
        /// Builds the full HTML for a page. The toggle bar is generated from
        /// the redanswertype values actually present in the redAnswer file —
        /// one pill per type, no hardcoded buttons. No JS is emitted.
        /// </summary>
        private string BuildOverlayHtml(
            string bgImage,
            string contentHtml,
            string answerLayer,
            HashSet<string> answerTypes,
            string fileName,
            string fontCss,
            int pageWidth,
            int pageHeight)
        {
            if (string.IsNullOrEmpty(bgImage)) bgImage = GetPlaceholderImage();
            if (string.IsNullOrEmpty(contentHtml))
                contentHtml = "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";

            // Build one checkbox + one label per redanswertype.
            // teacherNotes on the left, studentAnswer on the right; anything
            // else in between.
            var ordered = answerTypes
                .OrderBy(t => t == "teacherNotes" ? 0 : (t == "studentAnswer" ? 2 : 1))
                .ToList();

            var checkboxHtml = new StringBuilder();
            var labelHtml = new StringBuilder();
            var cssRules = new StringBuilder();

            foreach (var t in ordered)
            {
                string id = "bv-toggle-" + SanitizeId(t);
                bool isOn = GetToggleState(t);
                string checkedAttr = isOn ? " checked" : "";
                string pillText = t switch
                {
                    "teacherNotes" => "✎ Notes",
                    "studentAnswer" => "✓ Answers",
                    _ => t
                };

                checkboxHtml.Append($"<input type='checkbox' id='{id}' class='bv-toggle'{checkedAttr}>\n");
                labelHtml.Append($"<label for='{id}' class='bv-toggle-label' data-type='{t}'>{pillText}</label>\n");

                cssRules.Append(
                    $"#bv-toggle-{SanitizeId(t)}:checked ~ .page-wrapper .page-container " +
                    $".answer-layer .answer-item[data-rat=\"{t}\"] {{ display:block; }}\n");
                cssRules.Append(
                    $"#bv-toggle-{SanitizeId(t)}:checked ~ .page-wrapper .bv-togglebar " +
                    $"label[for=\"bv-toggle-{SanitizeId(t)}\"] {{ background:#34C759; color:#fff; border-color:#34C759; }}\n");
            }

            string zoom = _currentZoom.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string surround = SurroundColor;

            return $@"<!DOCTYPE html>
<html>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width,initial-scale=1.0,maximum-scale=5.0,user-scalable=yes'>
<title>{fileName}</title>
<style>
{fontCss}

* {{ margin:0; padding:0; box-sizing:border-box; }}

html, body {{
    width:100%; height:100%; overflow:auto;
    background:{surround};
    -webkit-font-smoothing: antialiased;
}}
body {{ display:flex; justify-content:center; align-items:flex-start; }}

.page-wrapper {{ display:flex; justify-content:center; align-items:flex-start; }}
.page-container {{
    position:relative;
    width:{pageWidth}px;
    height:{pageHeight}px;
    flex-shrink:0;
    background:#fff;
    box-shadow:0 0 20px rgba(0,0,0,.15);
    overflow:hidden;
    border-radius:2px;
    transform: scale({zoom});
    transform-origin: top center;
}}

.bgcls-img {{
    position:absolute; top:0; left:0;
    width:100%; height:100%;
    pointer-events:none;
    z-index:1;
}}

.base-content {{
    position:absolute; top:0; left:0;
    width:100%; height:100%;
    z-index:5;
    pointer-events:none;
}}
.base-content > div {{
    position:absolute;
    line-height:1.5em;
    white-space:pre;
    font-kerning:none;
    font-feature-settings:'kern' 0,'liga' 0;
    text-rendering: geometricPrecision;
}}
.base-content .x1 {{ -webkit-transform: translateX(.1px); }}
.base-content .x2 {{ -webkit-transform: translateX(.2px); }}
.base-content .x3 {{ -webkit-transform: translateX(.3px); }}
.base-content .x4 {{ -webkit-transform: translateX(.4px); }}
.base-content .x5 {{ -webkit-transform: translateX(-.5px); }}
.base-content .x6 {{ -webkit-transform: translateX(-.4px); }}
.base-content .x7 {{ -webkit-transform: translateX(-.3px); }}
.base-content .x8 {{ -webkit-transform: translateX(-.2px); }}
.base-content .x9 {{ -webkit-transform: translateX(-.1px); }}

.answer-layer {{
    position:absolute; top:0; left:0;
    width:100%; height:100%;
    z-index:20;
    pointer-events:none;
}}
.answer-layer > .answer-item {{
    position:absolute;
    display:none;
}}
.answer-layer .answer-item > div,
.answer-layer .answer-item > svg {{
    position: static;
    display: block;
}}

.bv-toggle {{ position:absolute; opacity:0; pointer-events:none; }}

.bv-togglebar {{
    position:absolute; top:10px; right:10px; z-index:9999;
    display:flex; gap:6px; pointer-events:auto;
    font-family: system-ui, -apple-system, 'Helvetica Neue', Arial, sans-serif;
}}
.bv-toggle-label {{
    display:inline-block;
    padding:6px 14px;
    border-radius:18px;
    background:#F7F7F9;
    color:#111;
    font-size:13px;
    font-weight:600;
    cursor:pointer;
    user-select:none;
    -webkit-user-select:none;
    border:1px solid rgba(0,0,0,.08);
    box-shadow:0 1px 3px rgba(0,0,0,.08);
    transition: background .15s, color .15s;
}}

/* Per-page rules, generated from the redAnswer contents */
{cssRules}
</style>
</head>
<body>

{checkboxHtml}
<div class='page-wrapper'>
    <div class='page-container'>
        <div class='bv-togglebar'>
{labelHtml}
        </div>
        <img class='bgcls-img' src='{bgImage}' alt='' />
        <div class='base-content'>{contentHtml}</div>
        <div class='answer-layer'>{answerLayer}</div>
    </div>
</div>

</body>
</html>";
        }

        private static string SanitizeId(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "")
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        public void NavigatePrevious()
        {
            if (_currentPageIndex > 0) _ = LoadPageAsync(_currentPageIndex - 1);
        }

        public void NavigateNext()
        {
            if (_currentPageIndex < _pageFiles.Count - 1) _ = LoadPageAsync(_currentPageIndex + 1);
        }

        public void ToggleAnswers(bool show)
        {
            OnToggleAnswersRequested?.Invoke(this, show);
            OnStatusChanged?.Invoke(this, show ? "Answers shown" : "Answers hidden");
        }

        // ================================================================
        // PDF render path — kept from the original but now also supports
        // baking the current toggle state into the output.
        // ================================================================

        public async Task<string> BuildPageHtmlForRenderAsync(
            string filePath,
            bool includeAnswers,
            bool includeTeacherNotes,
            bool includeStudentAnswers)
        {
            var rawHtml = await File.ReadAllTextAsync(filePath);

            var savedTeacher = _showTeacherNotes;
            var savedStudent = _showStudentAnswers;

            try
            {
                _showTeacherNotes   = includeAnswers && includeTeacherNotes;
                _showStudentAnswers = includeAnswers && includeStudentAnswers;

                return await ProcessHtmlContent(rawHtml, filePath);
            }
            finally
            {
                _showTeacherNotes   = savedTeacher;
                _showStudentAnswers = savedStudent;
            }
        }
    }
}
