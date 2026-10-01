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
        private string _currentBookPath;
        private string _currentBookUid;
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
        private string _tempFolder;

        private bool _showTeacherNotes = false;
        private bool _showStudentAnswers = false;

        // "s_117368/steps_1" → global index in _pageFiles
        private readonly Dictionary<string, int> _stepFileToIndex = new();

        // global folio → step file (first occurrence)
        private readonly Dictionary<int, string> _folioToStepFile = new();

        // sectionId → (folio → step file) from that section's pages.xml
        private readonly Dictionary<string, Dictionary<int, string>> _sectionFolioMap = new();

        // sectionId → global 0-based index of the section's first page (from book.xml seqindex)
        private readonly Dictionary<string, int> _sectionFirstIndex = new();

        private static readonly object _logLock = new object();
        private static string _logFilePath = null;

        private string GetLogFilePath()
        {
            if (_logFilePath == null)
            {
                try
                {
                    string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string logFolder = Path.Combine(documentsPath, "BookViewer");
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

        public void SetShowTeacherNotes(bool show)
        {
            _showTeacherNotes = show;
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
        }

        public void SetShowStudentAnswers(bool show)
        {
            _showStudentAnswers = show;
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
        }

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

        // ============================================================
        // Lookup helpers
        // ============================================================
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

                // ============================================================
                // Build maps
                // ============================================================
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

                // ---- Parse book.xml: section → first seqindex, plus folio map ----
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
                                {
                                    _sectionFirstIndex[sectionId] = seqIndex;
                                }
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

                // ---- For each section, read pages.xml as a fallback ----
                var sectionFolders = Directory.GetDirectories(folderPath, "s_*");
                foreach (var sectionDir in sectionFolders)
                {
                    var sectionId = Path.GetFileName(sectionDir);
                    var pagesXmlPath = Path.Combine(sectionDir, "pages.xml");
                    if (!File.Exists(pagesXmlPath)) continue;

                    try
                    {
                        var pagesContent = await File.ReadAllTextAsync(pagesXmlPath);

                        // Strip XML declaration if present
                        var trimmed = pagesContent.TrimStart();
                        if (trimmed.StartsWith("<?xml"))
                        {
                            var idx = pagesContent.IndexOf("?>");
                            if (idx >= 0)
                                pagesContent = pagesContent.Substring(idx + 2);
                            trimmed = pagesContent.TrimStart();
                        }

                        // Wrap bare <page>...</page> sequence in a root
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
                                {
                                    folioMap[folio] = file;
                                }
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
                Log($"Built global folio map: {_folioToStepFile.Count} entries");

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
                Log($"Book UID: {_currentBookUid}");

                var htmlFiles = Directory.GetFiles(folderPath, "*.html", SearchOption.AllDirectories).ToList();
                var xmlFiles = Directory.GetFiles(folderPath, "*.xml", SearchOption.AllDirectories).ToList();
                var htmFiles = Directory.GetFiles(folderPath, "*.htm", SearchOption.AllDirectories).ToList();

                Log($"Files found: {htmlFiles.Count} .html, {xmlFiles.Count} .xml, {htmFiles.Count} .htm");

                int bookKey = _decryptionService.CalculateKey(_currentBookUid);
                Log($"Book Key: {bookKey}");

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
                Log($"Loading page {index}: {fileName}");
        
                var content = await File.ReadAllTextAsync(filePath);
                var processedHtml = await ProcessHtmlContent(content, filePath);
                _currentPageHtml = processedHtml;
        
                if (_twoPageSpread && index + 1 < _pageFiles.Count)
                {
                    try
                    {
                        var nextFilePath = _pageFiles[index + 1];
                        var nextContent = await File.ReadAllTextAsync(nextFilePath);
                        _nextPageHtml = await ProcessHtmlContent(nextContent, nextFilePath);
                    }
                    catch (Exception ex)
                    {
                        Log($"Error loading next page for spread: {ex.Message}");
                        _nextPageHtml = "";
                    }
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
                Log($"ERROR loading page {index}: {ex.Message}");
                Log($"Stack: {ex.StackTrace}");
                OnStatusChanged?.Invoke(this, $"Error loading page: {ex.Message}");
            }
        }
        private async Task<string> ProcessHtmlContent(string htmlContent, string filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileName(filePath) ?? "";
        
                if (IsPureStepsFile(fileName) && fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    string bgImage = GetStepBackgroundImage(filePath);
                    string contentHtml = await ExtractContentFromHtmlFile(filePath, fileName, directory);
                    string fontCss = await GetFontCssWithEmbeddedFonts(directory);
        
                    return BuildOverlayHtml(bgImage, contentHtml, fileName, fontCss, "", "");
                }
        
                return htmlContent;
            }
            catch (Exception ex)
            {
                Log($"ProcessHtmlContent error on {Path.GetFileName(filePath)}: {ex.Message}");
                // Return a minimal fallback page instead of crashing
                return $@"<!DOCTYPE html>
        <html><head><meta charset='UTF-8'></head>
        <body style='background:#E8E8E8;color:#333;font-family:sans-serif;padding:40px;'>
        <div style='max-width:600px;margin:auto;background:#fff;padding:24px;border-radius:8px;'>
        <h2>Unable to render page</h2>
        <p>{Path.GetFileName(filePath)}</p>
        <pre style='background:#f5f5f5;padding:12px;border-radius:4px;font-size:12px;'>{System.Net.WebUtility.HtmlEncode(ex.Message)}</pre>
        </div>
        </body></html>";
            }
        }

        private async Task<string> ExtractContentFromHtmlFile(string filePath, string fileName, string directory)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string contentHtml = "";

            string oriHtmlPath = Path.Combine(directory, nameWithoutExt + "_ori.html");
            if (File.Exists(oriHtmlPath))
            {
                string oriContent = await File.ReadAllTextAsync(oriHtmlPath);
                return ExtractContentFromHtml(oriContent);
            }

            string paraXmlPath = Path.Combine(directory, nameWithoutExt + "_para.xml");
            if (File.Exists(paraXmlPath))
            {
                string paraContent = await File.ReadAllTextAsync(paraXmlPath);
                return ExtractContentFromParaXml(paraContent);
            }

            try
            {
                string fallbackContent = await File.ReadAllTextAsync(filePath);
                contentHtml = ExtractContentFromHtml(fallbackContent);
            }
            catch
            {
                contentHtml = "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";
            }

            return contentHtml;
        }

        private string ExtractContentFromHtml(string htmlContent)
        {
            var bodyMatch = Regex.Match(htmlContent, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
            if (!bodyMatch.Success) return htmlContent;
            return bodyMatch.Groups[1].Value;
        }

        private string ExtractContentFromParaXml(string paraXmlContent)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(paraXmlContent);
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
            }
            catch { }
            return "";
        }

        public async Task<string> GetRedAnswerContentAsync(string filePath, string answerType)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileNameWithoutExtension(filePath) ?? "";

                string redAnswerPath = Path.Combine(directory, fileName + "_redAnswer.html");

                if (!File.Exists(redAnswerPath))
                {
                    string[] altPatterns = answerType == "teacherNotes"
                        ? new[]
                        {
                            Path.Combine(directory, fileName + "_teacherNotes.html"),
                            Path.Combine(directory, fileName + "_teacher.html"),
                            Path.Combine(directory, "teacherNotes.html"),
                            Path.Combine(directory, "teacher.html")
                        }
                        : new[]
                        {
                            Path.Combine(directory, fileName + "_studentAnswers.html"),
                            Path.Combine(directory, fileName + "_studentAnswer.html"),
                            Path.Combine(directory, fileName + "_student.html"),
                            Path.Combine(directory, "studentAnswers.html"),
                            Path.Combine(directory, "studentAnswer.html"),
                            Path.Combine(directory, "redAnswer.html")
                        };

                    foreach (var alt in altPatterns)
                    {
                        if (File.Exists(alt))
                        {
                            redAnswerPath = alt;
                            break;
                        }
                    }
                }

                if (!File.Exists(redAnswerPath)) return "";

                string content = await File.ReadAllTextAsync(redAnswerPath);

                content = Regex.Replace(content, @"display\s*:\s*none\s*;?", "", RegexOptions.IgnoreCase);
                content = Regex.Replace(content, @"visibility\s*:\s*hidden\s*;?", "", RegexOptions.IgnoreCase);
                content = Regex.Replace(content, @"style\s*=\s*[""']\s*[""']", "", RegexOptions.IgnoreCase);

                var bodyMatch = Regex.Match(content, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                if (bodyMatch.Success) content = bodyMatch.Groups[1].Value;

                string extractedContent = "";
                string classPattern = answerType == "teacherNotes" ? "tbnote" : "sa";

                var matches = Regex.Matches(content,
                    @"<[^>]*class\s*=\s*[""'][^""']*" + classPattern + @"[^""']*[""'][^>]*>[\s\S]*?</[^>]*>",
                    RegexOptions.IgnoreCase);

                foreach (Match match in matches)
                    extractedContent += match.Value + "\n";

                if (string.IsNullOrEmpty(extractedContent) && answerType == "studentAnswers")
                    extractedContent = content;

                return extractedContent;
            }
            catch
            {
                return "";
            }
        }
        public string GetStepBackgroundImage(string filePath)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var stepNumber = ParseStepIndex(Path.GetFileName(filePath));
        
                var imagesPath = Path.Combine(directory, "images");
                if (Directory.Exists(imagesPath) && stepNumber >= 0)
                {
                    // Try each candidate; return as soon as one exists
                    var candidates = new[]
                    {
                        Path.Combine(imagesPath, $"steps_{stepNumber}.jpg"),
                        Path.Combine(imagesPath, $"steps_{stepNumber}.jpeg"),
                        Path.Combine(imagesPath, $"steps_{stepNumber}.png"),
                        Path.Combine(imagesPath, $"step_{stepNumber}.jpg"),
                        Path.Combine(imagesPath, $"step_{stepNumber}.jpeg"),
                        Path.Combine(imagesPath, $"step_{stepNumber}.png"),
                    };
        
                    foreach (var c in candidates)
                    {
                        if (File.Exists(c))
                        {
                            Log($"Background image found: {c} ({new FileInfo(c).Length} bytes)");
                            return ConvertImageToBase64HighQuality(c);
                        }
                    }
                }
        
                // Fallback: look for any image starting with the step name
                if (Directory.Exists(imagesPath) && stepNumber >= 0)
                {
                    var pattern = $"*{stepNumber}*";
                    var found = Directory.GetFiles(imagesPath, pattern)
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f.Length)
                        .FirstOrDefault();
        
                    if (found != null)
                    {
                        Log($"Fallback image: {found}");
                        return ConvertImageToBase64HighQuality(found);
                    }
                }
        
                Log($"No background image for step {stepNumber} in {imagesPath}");
                return GetPlaceholderImage();
            }
            catch (Exception ex)
            {
                Log($"GetStepBackgroundImage error: {ex.Message}");
                return GetPlaceholderImage();
            }
        }
        public async Task<string> GetFontCssWithEmbeddedFonts(string directory)
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
        
                    // ---- Build case-insensitive font file index ----
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
                        {
                            embeddedCount++;
        
                            // ---- Inject metric overrides + line-height to normalize baseline ----
                            var originalBlock = match.Value;
                            var bodyContent = match.Groups["body"].Value;
        
                            // Ensure a line-height is present after `font:` shorthand
                            var normalizedBody = Regex.Replace(
                                bodyContent,
                                @"(font\s*:[^;]+;)(?!\s*line-height)",
                                "$1\n    line-height: 1.5em;",
                                RegexOptions.IgnoreCase);
        
                            // Append metric overrides before the closing brace
                            normalizedBody = normalizedBody.TrimEnd();
                            if (!normalizedBody.EndsWith(";"))
                                normalizedBody += ";";
        
                            normalizedBody +=
                                "\n    ascent-override: 90%;" +
                                "\n    descent-override: 20%;" +
                                "\n    line-gap-override: 0%;";
        
                            var newBlock = "@font-face {\n" + normalizedBody + "\n}";
                            fontCss = fontCss.Replace(originalBlock, newBlock);
                        }
                    }
        
                    Log($"Font embedding summary: {embeddedCount} of {totalFaces} @font-face rules embedded");
        
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

        private string GetPlaceholderImage()
            => "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='1024' height='1344'%3E%3Crect width='1024' height='1344' fill='%23ffffff'/%3E%3C/svg%3E";
        private const long MAX_IMAGE_BYTES = 8 * 1024 * 1024; // 8 MB per image
        
        private string ConvertImageToBase64HighQuality(string imagePath)
        {
            try
            {
                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                    return GetPlaceholderImage();
        
                if (_imageCache.TryGetValue(imagePath, out var cached))
                    return cached;
        
                var fileInfo = new FileInfo(imagePath);
                if (fileInfo.Length > MAX_IMAGE_BYTES)
                {
                    Log($"Image too large ({fileInfo.Length} bytes), skipping: {imagePath}");
                    _imageCache[imagePath] = GetPlaceholderImage();
                    return GetPlaceholderImage();
                }
        
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
            catch (Exception ex)
            {
                Log($"ConvertImageToBase64 error: {ex.Message}");
                return GetPlaceholderImage();
            }
        }

        private string BuildOverlayHtml(string bgImage, string contentHtml, string fileName, string fontCss, string teacherAnswerHtml, string studentAnswerHtml)
        {
            if (string.IsNullOrEmpty(bgImage))
                bgImage = GetPlaceholderImage();
        
            if (string.IsNullOrEmpty(contentHtml))
                contentHtml = "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";
        
            double zoom = _currentZoom;
        
            return $@"
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset='UTF-8'>
            <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes'>
            <title>{fileName}</title>
            <style>
                {fontCss}
        
                * {{
                    margin: 0;
                    padding: 0;
                    box-sizing: border-box;
                }}
        
                html, body {{
                    width: 100%;
                    height: 100%;
                    overflow: auto;
                    background: #E8E8E8;
                    -webkit-font-smoothing: antialiased;
                    -moz-osx-font-smoothing: grayscale;
                }}
        
                body {{
                    display: flex;
                    justify-content: center;
                    align-items: flex-start;
                    min-height: 100vh;
                    padding: 0;
                    margin: 0;
                }}
        
                .page-wrapper {{
                    display: flex;
                    justify-content: center;
                    align-items: flex-start;
                    padding: 0;
                    margin: 0;
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
                    top: 0;
                    left: 0;
                    width: 100%;
                    height: 100%;
                    object-fit: contain;
                    pointer-events: none;
                    z-index: 1;
                }}
        
                .content-overlay {{
                    position: absolute;
                    top: 0;
                    left: 0;
                    width: 100%;
                    height: 100%;
                    z-index: 2;
                }}
        
                .content-overlay > * {{
                    position: absolute !important;
                }}
            </style>
        </head>
        <body>
            <div class='page-wrapper'>
                <div class='page-container'>
                    <img class='background-img' src='{bgImage}' alt='' />
                    <div class='content-overlay'>
                        {contentHtml}
                        {teacherAnswerHtml}
                        {studentAnswerHtml}
                    </div>
                </div>
            </div>
        </body>
        </html>";
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
    }
}
