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

        private bool _showTeacherNotes = false;
        private bool _showStudentAnswers = false;

        private readonly Dictionary<string, int> _stepFileToIndex = new();
        private readonly Dictionary<int, string> _folioToStepFile = new();
        private readonly Dictionary<string, Dictionary<int, string>> _sectionFolioMap = new();
        private readonly Dictionary<string, int> _sectionFirstIndex = new();

        private static readonly object _logLock = new object();
        private static string _logFilePath = "";

        public int CurrentPageWidth { get; private set; } = 1024;
        public int CurrentPageHeight { get; private set; } = 1309;

        private static string SurroundColor =>
            Application.Current?.RequestedTheme == AppTheme.Dark ? "#1C1C1E" : "#E8E8E8";

        // ============================================================
        // Static CSS template — used by BuildOverlayHtml
        // ============================================================
        private const string OverlayCssTemplate = @"
    * { margin: 0; padding: 0; box-sizing: border-box; }
    html, body {
        width: 100%; height: 100%; overflow: auto;
        -webkit-font-smoothing: antialiased;
    }
    body { display: flex; justify-content: center; align-items: flex-start; min-height: 100vh; }
    .page-wrapper { display: flex; justify-content: center; align-items: flex-start; }
    .page-container {
        position: relative;
        flex-shrink: 0; background: #ffffff;
        box-shadow: 0 0 20px rgba(0,0,0,0.15);
        overflow: hidden; border-radius: 2px;
        transform-origin: top center;
    }
    .background-img { position: absolute; top: 0; left: 0; width: 100%; height: 100%; pointer-events: none; z-index: 1; }
    .content-overlay { position: absolute; top: 0; left: 0; width: 100%; height: 100%; z-index: 2; }
    .content-overlay > * { position: absolute !important; }

    .base-content { position: absolute; top: 0; left: 0; width: 100%; height: 100%; z-index: 5; pointer-events: none; }
    .base-content > * { position: absolute !important; }
    .base-content .para {
        position: absolute !important; line-height: 1 !important; white-space: pre;
        font-kerning: none; font-feature-settings: 'kern' 0, 'liga' 0;
        text-rendering: geometricPrecision; -webkit-font-smoothing: antialiased;
    }

    .answer-layer {
        position: absolute; top: 0; left: 0; width: 100%; height: 100%;
        z-index: 20; pointer-events: none; overflow: visible;
        display: none;
    }
    .answer-layer.active { display: block; }
    .answer-layer > * { position: absolute !important; }

    .answer-layer .tbnote,
    .answer-layer .tbred,
    .answer-layer .sa {
        display: none;
    }
    .answer-layer .tbnote.ans-on,
    .answer-layer .tbred.ans-on,
    .answer-layer .sa.ans-on {
        display: block !important;
    }

    .answer-layer .tbnote { background: rgba(255,235,150,0.55); border: 3px solid #3498db; border-radius: 4px; padding: 3px; }
    .answer-layer .tbred,
    .answer-layer .sa { background: rgba(255,235,150,0.55); border: 3px solid #2ecc71; border-radius: 4px; padding: 3px; }

    .item-btns {
        position: absolute; top: 0; left: 0; width: 100%; height: 100%;
        z-index: 30; pointer-events: none;
    }
    .item-btn {
        position: absolute;
        width: 22px; height: 22px;
        background: #FFEE99;
        border: 1px solid #C80;
        border-radius: 4px;
        font-size: 11px; font-weight: bold;
        color: #A00;
        display: flex; align-items: center; justify-content: center;
        cursor: pointer;
        pointer-events: auto;
        box-shadow: 0 1px 3px rgba(0,0,0,.3);
        user-select: none;
    }
    .item-btn:hover { background: #FFE44D; }
    .item-btn.on { background: #CC0000; color: #fff; border-color: #900; }
";

        // ============================================================
        // Static JS template — used by BuildOverlayHtml
        // ============================================================
        private const string OverlayJsTemplate = @"
(function() {
    function showItem(id, on) {
        var el = document.querySelector('[data-ans-id=""' + id + '""]');
        if (!el) return;
        if (on) el.classList.add('ans-on'); else el.classList.remove('ans-on');
    }

    window.__setNotes = function(on) {
        var layer = document.querySelector('.teacher-layer');
        if (!layer) return;
        if (on) layer.classList.add('active'); else layer.classList.remove('active');
        layer.querySelectorAll('.tbnote, .tbred').forEach(function(el) {
            if (on) el.classList.add('ans-on'); else el.classList.remove('ans-on');
        });
        document.querySelectorAll('.item-btn').forEach(function(b) {
            b.classList.toggle('on', !!on);
        });
    };

    window.__setAnswers = function(on) {
        var layer = document.querySelector('.student-layer');
        if (!layer) return;
        if (on) layer.classList.add('active'); else layer.classList.remove('active');
        layer.querySelectorAll('.tbred, .sa').forEach(function(el) {
            if (on) el.classList.add('ans-on'); else el.classList.remove('ans-on');
        });
    };

    window.__toggleItem = function(id) {
        var el = document.querySelector('[data-ans-id=""' + id + '""]');
        if (!el) return;
        var on = el.classList.toggle('ans-on');
        var btn = document.querySelector('.item-btn[data-target=""' + id + '""]');
        if (btn) btn.classList.toggle('on', on);
    };

    function buildButtons() {
        var container = document.getElementById('itemBtns');
        if (!container) return;
        container.innerHTML = '';

        var groups = document.querySelectorAll('hsg[id][name]');
        groups.forEach(function(g) {
            var icon = document.getElementById(g.id + '_img');
            if (!icon) return;
            var baseLeft = parseFloat(icon.style.left) || 0;
            var baseTop  = parseFloat(icon.style.top)  || 0;
            var children = g.querySelectorAll('hastext[linkname]');

            if (children.length <= 1) return;

            var i = 0;
            children.forEach(function(h) {
                var lid = h.getAttribute('linkname');
                if (!lid) return;

                var item = document.querySelector('[data-ans-id][data-lnk=""' + lid + '""]');
                if (!item) {
                    var all = document.querySelectorAll('[data-ans-id]');
                    for (var k = 0; k < all.length; k++) {
                        var inner = all[k].querySelector('[name=""' + lid + '""]');
                        if (inner) { all[k].setAttribute('data-lnk', lid); item = all[k]; break; }
                    }
                }
                if (!item) return;
                var id = item.getAttribute('data-ans-id');

                var labelEl = h.querySelector('a.s');
                var label = labelEl ? (labelEl.textContent || '').trim().substring(0, 2) : String(i + 1);
                if (!label) label = String(i + 1);

                var btn = document.createElement('div');
                btn.className = 'item-btn';
                btn.setAttribute('data-target', id);
                btn.title = labelEl ? labelEl.textContent : lid;
                btn.textContent = label;
                btn.style.left = (baseLeft - 26) + 'px';
                btn.style.top  = (baseTop + i * 24) + 'px';
                (function(capturedId) {
                    btn.addEventListener('click', function(ev) {
                        ev.stopPropagation();
                        window.__toggleItem(capturedId);
                    });
                })(id);
                container.appendChild(btn);
                i++;
            });
        });
    }

    document.querySelectorAll('hsg[id][name]').forEach(function(g) {
        var icon = document.getElementById(g.id + '_img');
        if (!icon) return;
        icon.style.cursor = 'pointer';
        icon.style.pointerEvents = 'auto';
        icon.style.display = '';
        icon.addEventListener('click', function(ev) {
            ev.stopPropagation();
            var isOn = g.getAttribute('on') === 'true';
            var turnOn = !isOn;
            g.setAttribute('on', turnOn ? 'true' : 'false');
            g.querySelectorAll('hastext[linkname]').forEach(function(h) {
                var lid = h.getAttribute('linkname');
                if (!lid) return;
                var item = document.querySelector('[data-lnk=""' + lid + '""]');
                if (item) {
                    if (turnOn) item.classList.add('ans-on');
                    else item.classList.remove('ans-on');
                }
                var targetId = item ? item.getAttribute('data-ans-id') : '';
                var btn = document.querySelector('.item-btn[data-target=""' + targetId + '""]');
                if (btn) btn.classList.toggle('on', turnOn);
            });
        });
    });

    buildButtons();

    if (document.querySelector('.teacher-layer.active')) {
        document.querySelectorAll('.teacher-layer .tbnote, .teacher-layer .tbred').forEach(function(el) {
            el.classList.add('ans-on');
        });
    }
    if (document.querySelector('.student-layer.active')) {
        document.querySelectorAll('.student-layer .tbred, .student-layer .sa').forEach(function(el) {
            el.classList.add('ans-on');
        });
    }
})();
";

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

        public void SetShowStudentAnswers(bool show)
        {
            _showStudentAnswers = show;
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
        }
        
        public void ShowAllTeacherNotes(bool show)
        {
            _showTeacherNotes = show;
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
        }
        
        public void ShowAllStudentAnswers(bool show)
        {
            _showStudentAnswers = show;
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                _ = LoadPageAsync(_currentPageIndex);
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

                    int pageWidth = 1024;
                    int pageHeight = 1309;

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
                        else
                        {
                            Log("Could not detect page size, using default 1024x1309");
                        }
                    }

                    CurrentPageWidth = pageWidth;
                    CurrentPageHeight = pageHeight;

                    return BuildOverlayHtml(bgImage, contentHtml, fileName, fontCss, pageWidth, pageHeight);
                }

                return htmlContent;
            }
            catch (Exception ex)
            {
                Log($"ERROR in ProcessHtmlContent: {ex.Message}");
                return htmlContent;
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
                if (parasNode == null) return "";

                var sb = new StringBuilder();

                foreach (XmlNode child in parasNode.ChildNodes)
                {
                    if (child.Name != "para") continue;

                    var text = child.InnerText;
                    if (string.IsNullOrEmpty(text)) continue;

                    text = System.Security.SecurityElement.Escape(text) ?? text;

                    var style = child.Attributes?["style"]?.Value ?? "";
                    var x     = child.Attributes?["x"]?.Value;
                    var y     = child.Attributes?["y"]?.Value;
                    var w     = child.Attributes?["width"]?.Value;
                    var h     = child.Attributes?["height"]?.Value;
                    var align = child.Attributes?["textalign"]?.Value;

                    bool hasCoords = !string.IsNullOrEmpty(x) && !string.IsNullOrEmpty(y);

                    var pos = new StringBuilder();
                    if (!string.IsNullOrEmpty(style)) pos.Append(style).Append(';');

                    if (hasCoords)
                    {
                        pos.Append("position:absolute;");
                        pos.Append("left:").Append(x).Append("px;");
                        pos.Append("top:").Append(y).Append("px;");
                    }

                    if (!string.IsNullOrEmpty(w)) pos.Append("width:").Append(w).Append("px;");
                    if (!string.IsNullOrEmpty(h)) pos.Append("height:").Append(h).Append("px;");
                    if (!string.IsNullOrEmpty(align)) pos.Append("text-align:").Append(align).Append(';');

                    if (string.IsNullOrEmpty(align)) pos.Append("text-align:left;");

                    var cls = hasCoords ? "para para-abs" : "para para-flow";

                    sb.Append("<div class='").Append(cls).Append("' style=\"")
                      .Append(pos.ToString())
                      .Append("\">")
                      .Append(text)
                      .Append("</div>");
                }

                return sb.ToString();
            }
            catch
            {
                return "";
            }
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
                        if (File.Exists(alt)) { redAnswerPath = alt; break; }
                    }
                }

                if (!File.Exists(redAnswerPath)) return "";

                string content = await File.ReadAllTextAsync(redAnswerPath);

                var bodyMatch = Regex.Match(content, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                if (bodyMatch.Success) content = bodyMatch.Groups[1].Value;

                string classPattern = answerType == "teacherNotes" ? "tbnote" : "tbred";
                var sb = new StringBuilder();

                var divMatches = Regex.Matches(content,
                    @"<div[^>]*class\s*=\s*[""'][^""']*" + classPattern + @"[^""']*[""'][^>]*>[\s\S]*?</div>",
                    RegexOptions.IgnoreCase);

                foreach (Match match in divMatches)
                    sb.AppendLine(match.Value);

                var svgMatches = Regex.Matches(content,
                    @"<svg[^>]*class\s*=\s*[""'][^""']*" + classPattern + @"[^""']*[""'][^>]*>[\s\S]*?</svg>",
                    RegexOptions.IgnoreCase);

                foreach (Match match in svgMatches)
                    sb.AppendLine(match.Value);

                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Assigns a unique data-ans-id to every tbnote/tbred wrapper so JS can toggle
        /// each item individually. Preserves the original `name` attribute for grouping.
        /// </summary>
        public static string AnnotateAnswerItems(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return fragment;

            var sb = new StringBuilder();
            var regex = new Regex(@"<div[^>]*class\s*=\s*[""']([^""']*?)[""'][^>]*>",
                                  RegexOptions.IgnoreCase);
            int idx = 0;
            int lastPos = 0;

            foreach (Match m in regex.Matches(fragment))
            {
                sb.Append(fragment.Substring(lastPos, m.Index - lastPos));
                var cls = m.Groups[1].Value;
                var tag = m.Value;

                if (cls.Contains("tbnote") || cls.Contains("tbred"))
                {
                    if (tag.Contains("data-ans-id="))
                    {
                        sb.Append(tag);
                    }
                    else
                    {
                        string id = "ans_" + idx++;
                        sb.Append(tag.Substring(0, tag.Length - 1) + " data-ans-id=\"" + id + "\">");
                    }
                }
                else
                {
                    sb.Append(tag);
                }

                lastPos = m.Index + m.Value.Length;
            }

            sb.Append(fragment.Substring(lastPos));
            return sb.ToString();
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
body, .para, .base-content, .content-overlay {
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
body, .para, .base-content, .content-overlay {
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

        private string GetPlaceholderImage()
            => "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='1024' height='1309'%3E%3Crect width='1024' height='1309' fill='%23ffffff'/%3E%3C/svg%3E";

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

        // ============================================================
        // BuildOverlayHtml — clean StringBuilder assembly (no $@"..." 
        // interpolated verbatim strings containing CSS/JS).
        // ============================================================
        private string BuildOverlayHtml(string bgImage, string contentHtml, string fileName, string fontCss,
            int pageWidth, int pageHeight)
        {
            if (string.IsNullOrEmpty(bgImage)) bgImage = GetPlaceholderImage();
            if (string.IsNullOrEmpty(contentHtml))
                contentHtml = "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";

            double zoom = _currentZoom;
            string surround = SurroundColor;
            string zoomStr = zoom.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Load + annotate answers
            string filePath = (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
                ? _pageFiles[_currentPageIndex]
                : "";

            var teacherRaw = _showTeacherNotes && !string.IsNullOrEmpty(filePath)
                ? GetRedAnswerContentAsync(filePath, "teacherNotes").GetAwaiter().GetResult()
                : "";
            var studentRaw = _showStudentAnswers && !string.IsNullOrEmpty(filePath)
                ? GetRedAnswerContentAsync(filePath, "studentAnswers").GetAwaiter().GetResult()
                : "";
            var teacherFrag = AnnotateAnswerItems(teacherRaw);
            var studentFrag = AnnotateAnswerItems(studentRaw);

            string teacherWrapper = string.IsNullOrEmpty(teacherFrag) ? "" :
                "<div class='answer-layer teacher-layer active'>" + teacherFrag + "</div>";
            string studentWrapper = string.IsNullOrEmpty(studentFrag) ? "" :
                "<div class='answer-layer student-layer active'>" + studentFrag + "</div>";

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html>\n<head>\n");
            sb.Append("<meta charset='UTF-8'>\n");
            sb.Append("<meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes'>\n");
            sb.Append("<title>").Append(fileName).Append("</title>\n");
            sb.Append("<style>\n");
            sb.Append(fontCss);
            sb.Append(OverlayCssTemplate);
            sb.Append("html, body { background: ").Append(surround).Append("; }\n");
            sb.Append(".page-container { width: ").Append(pageWidth).Append("px; height: ").Append(pageHeight).Append("px; }\n");
            sb.Append(".page-container { transform: scale(").Append(zoomStr).Append("); }\n");
            sb.Append("</style>\n</head>\n<body>\n");
            sb.Append("<div class='page-wrapper'><div class='page-container'>\n");
            sb.Append("<img class='background-img' src='").Append(bgImage).Append("' alt='' />\n");
            sb.Append("<div class='content-overlay'>\n");
            sb.Append("<div class='base-content'>").Append(contentHtml).Append("</div>\n");
            sb.Append(studentWrapper);
            sb.Append(teacherWrapper);
            sb.Append("<div class='item-btns' id='itemBtns'></div>\n");
            sb.Append("</div>\n</div>\n</div>\n");
            sb.Append("<script>\n");
            sb.Append(OverlayJsTemplate);
            sb.Append("</script>\n</body>\n</html>");

            return sb.ToString();
        }

        public async Task<string> BuildPageHtmlForRenderAsync(
            string filePath,
            bool includeAnswers,
            bool includeTeacherNotes,
            bool includeStudentAnswers)
        {
            // Temporarily flip the two flags so BuildOverlayHtml includes the layers
            bool prevTeacher = _showTeacherNotes;
            bool prevStudent = _showStudentAnswers;
            _showTeacherNotes = includeAnswers && includeTeacherNotes;
            _showStudentAnswers = includeAnswers && includeStudentAnswers;

            try
            {
                var rawHtml = await File.ReadAllTextAsync(filePath);
                return await ProcessHtmlContent(rawHtml, filePath);
            }
            finally
            {
                _showTeacherNotes = prevTeacher;
                _showStudentAnswers = prevStudent;
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
    }
}
