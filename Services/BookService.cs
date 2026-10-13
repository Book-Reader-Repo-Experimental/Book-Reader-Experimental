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

        private readonly Dictionary<string, bool> _groupToggles = new(StringComparer.Ordinal);

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

        public Dictionary<int, string> FolioToStepFile => _folioToStepFile;
        public Dictionary<string, int> StepFileToIndex => _stepFileToIndex;

        public bool GetToggleState(string group)
            => _groupToggles.TryGetValue(group ?? "", out var v) && v;

        public void SetToggleState(string group, bool on)
        {
            if (string.IsNullOrEmpty(group)) return;
            _groupToggles[group] = on;

            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
        }

        public bool ShowTeacherNotes => GetToggleState("teacherNotes");
        public bool ShowStudentAnswers => GetToggleState("studentAnswer");
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
        // Rendering
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

                // Page size
                int pageWidth = 1024;
                int pageHeight = 1344;

                var rezMatch = Regex.Match(htmlContent,
                    @"id\s*=\s*[""']rez[""'][^>]*style\s*=\s*[""'][^""']*width\s*:\s*(\d+)px[^""']*height\s*:\s*(\d+)px",
                    RegexOptions.IgnoreCase);
                if (rezMatch.Success)
                {
                    if (int.TryParse(rezMatch.Groups[1].Value, out int w)) pageWidth = w;
                    if (int.TryParse(rezMatch.Groups[2].Value, out int h)) pageHeight = h;
                }

                CurrentPageWidth = pageWidth;
                CurrentPageHeight = pageHeight;

                // CSS
                string fontCss = await GetFontCssWithEmbeddedFonts(directory);
                string pageCss = await LoadBookCssAsync(directory, stem);

                // Base text and answer overlay come from the sibling files.
                string baseHtml = await ReadSiblingAsync(directory, stem + "_base.html");
                string oriHtml  = await ReadSiblingAsync(directory, stem + "_ori.html");
                string redHtml  = await ReadSiblingAsync(directory, stem + "_redAnswer.html");

                // Which answer elements do we have? Prefer _ori.html
                // (full layer) and fall back to _redAnswer.html.
                string answerSource = !string.IsNullOrEmpty(oriHtml) ? oriHtml : redHtml;

                // Transform the answer layer into tagged .answer-item elements.
                var groups = new HashSet<string>(StringComparer.Ordinal);
                string answerLayer = BuildAnswerLayer(answerSource, groups);

                // Now build the final body:
                //   - take steps_N.html
                //   - replace <pane class="base">…</pane> contents
                //   - replace <pane class="redAnswer">…</pane> contents
                //   - inline images
                //   - wrap icons in labels
                string bodyInner = BuildBodyFromSteps(
                    htmlContent,
                    baseHtml,
                    answerLayer,
                    directory);

                var checkedGroups = new HashSet<string>(StringComparer.Ordinal);
                foreach (var g in groups)
                    if (GetToggleState(g)) checkedGroups.Add(g);

                return AssemblePage(
                    fileName,
                    fontCss,
                    pageCss,
                    pageWidth,
                    pageHeight,
                    bodyInner,
                    groups,
                    checkedGroups);
            }
            catch (Exception ex)
            {
                Log($"ERROR in ProcessHtmlContent: {ex.Message}");
                return htmlContent;
            }
        }

        private async Task<string> ReadSiblingAsync(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) return "";
            return await File.ReadAllTextAsync(path);
        }

        /// <summary>
        /// Fills the panes inside steps_N.html:
        ///   <pane class="base">      ← steps_N_base.html
        ///   <pane class="redAnswer"> ← answer elements from _ori.html
        /// Also inlines all <img src> (including redlayer.svg from book root)
        /// and wraps the icon buttons in labels.
        /// </summary>
        private string BuildBodyFromSteps(
            string rawSteps,
            string baseHtml,
            string answerLayer,
            string pageDirectory)
        {
            string working = rawSteps;

            // 1. Fill <pane class="base">…</pane>. If the pane has no
            //    children yet, replace the empty content. If it already has
            //    content (unlikely), append.
            working = Regex.Replace(working,
                @"(<pane\b[^>]*\bclass\s*=\s*[""']base[""'][^>]*>)([\s\S]*?)(</pane>)",
                m =>
                {
                    string existing = m.Groups[2].Value ?? "";
                    string combined = string.IsNullOrWhiteSpace(existing)
                        ? baseHtml
                        : existing + baseHtml;
                    return m.Groups[1].Value + combined + m.Groups[3].Value;
                },
                RegexOptions.IgnoreCase);

            // 2. Fill <pane class="redAnswer">…</pane> with the tagged
            //    answer layer. We keep the pane hidden by the CSS rules
            //    that hide .answer-item by default.
            working = Regex.Replace(working,
                @"(<pane\b[^>]*\bclass\s*=\s*[""']redAnswer[""'][^>]*>)([\s\S]*?)(</pane>)",
                m =>
                {
                    string existing = m.Groups[2].Value ?? "";
                    string combined = string.IsNullOrWhiteSpace(existing)
                        ? answerLayer
                        : existing + answerLayer;
                    return m.Groups[1].Value + combined + m.Groups[3].Value;
                },
                RegexOptions.IgnoreCase);

            // 3. If steps_N.html has no <pane class="redAnswer"> at all,
            //    append the answer layer as a sibling so it still renders.
            if (!working.Contains("class=\"redAnswer\"") && !working.Contains("class='redAnswer'"))
            {
                working = working + "\n<div class=\"answer-layer\">" + answerLayer + "</div>";
            }
            else if (!working.Contains("answer-layer"))
            {
                // The pane exists but we want our CSS selectors to hit a
                // uniform container. Wrap the pane's children in .answer-layer.
                working = Regex.Replace(working,
                    @"(<pane\b[^>]*\bclass\s*=\s*[""']redAnswer[""'][^>]*>)([\s\S]*?)(</pane>)",
                    m =>
                    {
                        var paneAttrs = m.Groups[1].Value;
                        var inner = m.Groups[2].Value;
                        return paneAttrs + "<div class=\"answer-layer\">" + inner + "</div>" + m.Groups[3].Value;
                    },
                    RegexOptions.IgnoreCase);
            }

            // 4. Inline every <img src="..."> in the transformed markup.
            working = Regex.Replace(working,
                @"(<img\b[^>]*?\bsrc\s*=\s*[""'])([^""']+)([""'][^>]*>)",
                m =>
                {
                    string src = m.Groups[2].Value;
                    if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        return m.Value;

                    string cleanSrc = Regex.Replace(src, @"\?[^""']*$", "");
                    string resolved = ResolveBookImage(cleanSrc, pageDirectory);
                    if (string.IsNullOrEmpty(resolved))
                        return m.Value;

                    string dataUri = ConvertImageToBase64HighQuality(resolved);
                    return m.Groups[1].Value + dataUri + m.Groups[3].Value;
                },
                RegexOptions.IgnoreCase);

            // 5. Wrap each icon in a label + checkbox. The icon keeps its
            //    original style, so its left/top/z-index are preserved.
            int anon = 0;
            working = Regex.Replace(working,
                @"<img\b(?<attrs>[^>]*\bclass\s*=\s*[""'][^""']*\b(?:redbtn|has2btn|hsgbtn)\b[^""']*[""'][^>]*)>",
                m =>
                {
                    var attrs = m.Groups["attrs"].Value;

                    string targetName = Regex.Match(attrs,
                        @"\blinkname\s*=\s*[""'](?<v>[^""']+)[""']",
                        RegexOptions.IgnoreCase).Groups["v"].Value;
                    if (string.IsNullOrEmpty(targetName))
                        targetName = Regex.Match(attrs,
                            @"\bname\s*=\s*[""'](?<v>[^""']+)[""']",
                            RegexOptions.IgnoreCase).Groups["v"].Value;

                    string idAttr = Regex.Match(attrs,
                        @"\bid\s*=\s*[""'](?<v>[^""']+)[""']",
                        RegexOptions.IgnoreCase).Groups["v"].Value;
                    if (string.IsNullOrEmpty(idAttr))
                        idAttr = "bv-icon-" + (++anon);

                    string group = Regex.Match(attrs,
                        @"\bredanswertype\s*=\s*[""'](?<v>[^""']+)[""']",
                        RegexOptions.IgnoreCase).Groups["v"].Value;
                    if (string.IsNullOrEmpty(group))
                        group = "studentAnswer";
                    if (group == "studentAnswers") group = "studentAnswer";

                    string checkboxId = "bv-cb-" + idAttr;

                    var sb = new StringBuilder();
                    sb.Append("<input type='checkbox' id='").Append(checkboxId)
                      .Append("' class='bv-toggle' data-group='").Append(group)
                      .Append("' data-target='").Append(targetName).Append("'>");
                    sb.Append("<label for='").Append(checkboxId)
                      .Append("' class='bv-icon-label' data-group='").Append(group)
                      .Append("' data-target='").Append(targetName).Append("'>");
                    sb.Append("<img ").Append(attrs).Append(">");
                    sb.Append("</label>");
                    return sb.ToString();
                },
                RegexOptions.IgnoreCase);

            // 6. Strip inline onclick and any remaining cache-buster.
            working = Regex.Replace(working,
                @"\s+onclick\s*=\s*[""'][^""']*[""']", "", RegexOptions.IgnoreCase);
            working = Regex.Replace(working, @"\?16fszs", "", RegexOptions.IgnoreCase);

            return working;
        }

        /// <summary>
        /// Reads _ori.html or _redAnswer.html and extracts every .tbred /
        /// .tbnote element, wrapping each in an .answer-item div tagged
        /// with its redanswertype.
        /// </summary>
        private string BuildAnswerLayer(string source, HashSet<string> groups)
        {
            if (string.IsNullOrEmpty(source)) return "";

            var sb = new StringBuilder();

            foreach (Match m in Regex.Matches(source,
                @"<div\b(?<attrs>[^>]*\bclass\s*=\s*[""'][^""']*(?:tbred|tbnote)[^""']*[""'][^>]*)>(?<body>[\s\S]*?)</div>",
                RegexOptions.IgnoreCase))
            {
                EmitAnswerElement(sb, groups, "div", m.Groups["attrs"].Value, m.Groups["body"].Value);
            }

            foreach (Match m in Regex.Matches(source,
                @"<svg\b(?<attrs>[^>]*\bclass\s*=\s*[""'][^""']*(?:tbred|tbnote)[^""']*[""'][^>]*)>(?<body>[\s\S]*?)</svg>",
                RegexOptions.IgnoreCase))
            {
                EmitAnswerElement(sb, groups, "svg", m.Groups["attrs"].Value, m.Groups["body"].Value);
            }

            return sb.ToString();
        }

        private void EmitAnswerElement(
            StringBuilder sb, HashSet<string> groups,
            string tag, string attrs, string body)
        {
            string group = Regex.Match(attrs,
                @"\bredanswertype\s*=\s*[""'](?<v>[^""']+)[""']",
                RegexOptions.IgnoreCase).Groups["v"].Value;

            if (string.IsNullOrEmpty(group))
            {
                if (Regex.IsMatch(attrs, @"\bclass\s*=\s*[""'][^""']*\btbnote\b", RegexOptions.IgnoreCase))
                    group = "teacherNotes";
                else
                    group = "studentAnswer";
            }
            if (group == "studentAnswers") group = "studentAnswer";
            groups.Add(group);

            string targetName = Regex.Match(attrs,
                @"\bname\s*=\s*[""'](?<v>[^""']+)[""']",
                RegexOptions.IgnoreCase).Groups["v"].Value;
            if (string.IsNullOrEmpty(targetName))
                targetName = Regex.Match(attrs,
                    @"\bid\s*=\s*[""'](?<v>[^""']+)[""']",
                    RegexOptions.IgnoreCase).Groups["v"].Value;

            string style = Regex.Match(attrs,
                @"\bstyle\s*=\s*[""'](?<v>[^""']*)[""']",
                RegexOptions.IgnoreCase).Groups["v"].Value;

            var pos = new StringBuilder();
            foreach (var key in new[] { "left", "top", "width", "height" })
            {
                var mm = Regex.Match(style, @"\b" + key + @"\s*:\s*(?<v>[^;]+)", RegexOptions.IgnoreCase);
                if (mm.Success)
                    pos.Append(key).Append(':').Append(mm.Groups["v"].Value.Trim()).Append(';');
            }

            string cls = Regex.Match(attrs,
                @"\bclass\s*=\s*[""'](?<v>[^""']*)[""']",
                RegexOptions.IgnoreCase).Groups["v"].Value;

            string cleanBody = Regex.Replace(body,
                @"</?has2\b[^>]*>|</?hsg\b[^>]*>|</?hastext\b[^>]*>",
                "", RegexOptions.IgnoreCase);
            cleanBody = Regex.Replace(cleanBody, @"display\s*:\s*none\s*;?", "", RegexOptions.IgnoreCase);

            sb.Append("<div class=\"answer-item\" data-group=\"").Append(group).Append('"');
            if (!string.IsNullOrEmpty(targetName))
                sb.Append(" data-target=\"").Append(targetName).Append('"');
            sb.Append(" style=\"").Append(pos.ToString()).Append("\">");

            if (tag == "svg")
            {
                string w = Regex.Match(attrs, @"\bwidth\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.IgnoreCase).Groups["v"].Value;
                string h = Regex.Match(attrs, @"\bheight\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.IgnoreCase).Groups["v"].Value;
                sb.Append("<svg class=\"").Append(cls).Append('"');
                if (!string.IsNullOrEmpty(w)) sb.Append(" width=\"").Append(w).Append('"');
                if (!string.IsNullOrEmpty(h)) sb.Append(" height=\"").Append(h).Append('"');
                sb.Append(" xmlns=\"http://www.w3.org/2000/svg\">");
                sb.Append(cleanBody);
                sb.Append("</svg>");
            }
            else
            {
                sb.Append("<div class=\"").Append(cls).Append("\">");
                sb.Append(cleanBody);
                sb.Append("</div>");
            }

            sb.Append("</div>\n");
        }

        private string ResolveBookImage(string relativePath, string pageDirectory)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;

            string rel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart('\\', '/');

            var rootCandidate = Path.Combine(_currentBookPath, rel);
            if (File.Exists(rootCandidate)) return rootCandidate;

            var pageDirName = Path.GetFileName(pageDirectory ?? "");
            if (!string.IsNullOrEmpty(pageDirName))
            {
                var secCandidate = Path.Combine(_currentBookPath, pageDirName, rel);
                if (File.Exists(secCandidate)) return secCandidate;
            }

            var pageCandidate = Path.Combine(pageDirectory ?? "", rel);
            if (File.Exists(pageCandidate)) return pageCandidate;

            try
            {
                var name = Path.GetFileName(rel);
                if (!string.IsNullOrEmpty(name))
                {
                    var hits = Directory.GetFiles(_currentBookPath, name, SearchOption.AllDirectories);
                    if (hits.Length > 0) return hits[0];
                }
            }
            catch { }

            return null;
        }

        private async Task<string> LoadBookCssAsync(string directory, string stem)
        {
            var sb = new StringBuilder();

            var stepsCss = Path.Combine(directory, "steps.css");
            if (File.Exists(stepsCss))
                sb.AppendLine(await File.ReadAllTextAsync(stepsCss));

            var folioCss = Path.Combine(directory, stem + "_folio.css");
            if (File.Exists(folioCss))
                sb.AppendLine(await File.ReadAllTextAsync(folioCss));

            return sb.ToString();
        }

        private string AssemblePage(
            string fileName,
            string fontCss,
            string pageCss,
            int pageWidth,
            int pageHeight,
            string bodyInner,
            HashSet<string> distinctGroups,
            HashSet<string> checkedGroups)
        {
            var cssByGroup = new StringBuilder();
            foreach (var g in distinctGroups)
            {
                cssByGroup.Append(
                    $".bv-toggle[data-group=\"{g}\"]:checked ~ .page-wrapper .answer-item[data-group=\"{g}\"] {{ display:block; }}\n");
                cssByGroup.Append(
                    $".bv-toggle[data-group=\"{g}\"]:checked ~ .page-wrapper .bv-icon-label[data-group=\"{g}\"] img {{ opacity:1; }}\n");
            }

            string zoom = _currentZoom.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string surround = SurroundColor;

            string html = $@"<!DOCTYPE html>
<html>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width,initial-scale=1.0,maximum-scale=5.0,user-scalable=yes'>
<title>{fileName}</title>
<style>
/* ---- book CSS ---- */
{pageCss}

/* ---- embedded fonts ---- */
{fontCss}

/* ---- app CSS ---- */
* {{ margin:0; padding:0; box-sizing:border-box; }}
html, body {{
    width:100%; height:100%; overflow:auto;
    background:{surround};
    -webkit-font-smoothing: antialiased;
}}
body {{ display:flex; justify-content:center; align-items:flex-start; }}

.page-wrapper {{
    display:flex;
    justify-content:center;
    align-items:flex-start;
}}

#rez {{
    position:relative;
    transform: scale({zoom});
    transform-origin: top center;
    background:#fff;
    box-shadow: 0 0 20px rgba(0,0,0,.15);
    overflow:hidden;
}}

/* answer items hidden by default */
.answer-item {{ display:none; }}

/* per-group reveal rules */
{cssByGroup}

/* the checkbox itself is invisible; the label carries the icon */
.bv-toggle {{ position:absolute; opacity:0; pointer-events:none; }}
.bv-icon-label {{
    position:absolute;
    cursor:pointer;
    user-select:none;
    -webkit-user-select:none;
    display:inline-block;
}}
.bv-icon-label img {{
    display:block;
    opacity:.55;
    transition: opacity .15s;
}}
.bv-icon-label:active img {{ opacity:1; }}

/* hide legacy pane elements that would otherwise show because we stripped
   the inline display:none. The panes that matter are base and redAnswer;
   ori and BY_maskImage stay hidden. */
pane.ori, .BY_maskImage {{ display:none; }}
</style>
</head>
<body>

<div class='page-wrapper'>
    <div id='rez' style='width:{pageWidth}px;height:{pageHeight}px;'>
        {bodyInner}
    </div>
</div>

</body>
</html>";

            foreach (var g in checkedGroups)
            {
                html = Regex.Replace(
                    html,
                    @"(<input\b[^>]*class\s*=\s*[""'][^""']*\bbv-toggle\b[^""']*[""'][^>]*data-group\s*=\s*[""']" + Regex.Escape(g) + @"[""'][^>]*?)(\s*/?>)",
                    "$1 checked$2",
                    RegexOptions.IgnoreCase);
            }

            return html;
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

                    foreach (Match match in fontFaceMatches)
                    {
                        var fontFaceContent = match.Groups[1].Value;
                        var urlMatches = Regex.Matches(fontFaceContent, @"url\(['""]?([^)'""]+)['""]?\)");

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
                            }
                        }
                    }

                    _fontCache[directory] = fontCss;
                    return fontCss;
                }

                fontCss = await GenerateFontCssFromFiles();
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

        public async Task<string> BuildPageHtmlForRenderAsync(
            string filePath,
            bool includeAnswers,
            bool includeTeacherNotes,
            bool includeStudentAnswers)
        {
            var rawHtml = await File.ReadAllTextAsync(filePath);

            var saved = new Dictionary<string, bool>(_groupToggles);

            try
            {
                _groupToggles.Clear();

                if (includeAnswers)
                {
                    if (includeTeacherNotes) _groupToggles["teacherNotes"] = true;
                    if (includeStudentAnswers) _groupToggles["studentAnswer"] = true;
                }

                return await ProcessHtmlContent(rawHtml, filePath);
            }
            finally
            {
                _groupToggles.Clear();
                foreach (var kv in saved) _groupToggles[kv.Key] = kv.Value;
            }
        }
    }
}
