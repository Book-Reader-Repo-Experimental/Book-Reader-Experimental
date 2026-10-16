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
        public int CurrentPageHeight { get; private set; } = 1344;

        private static string SurroundColor =>
            Application.Current?.RequestedTheme == AppTheme.Dark ? "#1C1C1E" : "#E8E8E8";

        private const string OverlayCssTemplate = @"
    * { margin: 0; padding: 0; box-sizing: border-box; }
    html, body { width: 100%; height: 100%; overflow: auto; -webkit-font-smoothing: antialiased; }
    body { display: flex; justify-content: center; align-items: flex-start; min-height: 100vh; }
    .page-wrapper { display: flex; justify-content: center; align-items: flex-start; }
    .page-container {
        position: relative; flex-shrink: 0; background: #ffffff;
        box-shadow: 0 0 20px rgba(0,0,0,0.15);
        overflow: hidden; border-radius: 2px; transform-origin: top center;
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

    .tbnote, .tbred, .sa {
        display: none;
        position: absolute !important;
        pointer-events: none;
    }
    .tbnote.ans-on, .tbred.ans-on, .sa.ans-on { display: block !important; }

    .tbnote { background: rgba(255,235,150,0.55); border: 3px solid #3498db; border-radius: 4px; padding: 3px; }
    .tbred, .sa { background: rgba(255,235,150,0.55); border: 3px solid #2ecc71; border-radius: 4px; padding: 3px; }

    hsg { display: none; }
    img.hsgbtn, img.redbtn, img.has2btn {
        pointer-events: auto !important;
        cursor: pointer !important;
        z-index: 500 !important;
    }

    .item-btns { position: absolute; top: 0; left: 0; width: 100%; height: 100%; z-index: 600; pointer-events: none; }
    .item-btn {
        position: absolute; width: 22px; height: 22px;
        background: #FFEE99; border: 1px solid #C80; border-radius: 4px;
        font-size: 11px; font-weight: bold; color: #A00;
        display: flex; align-items: center; justify-content: center;
        cursor: pointer; pointer-events: auto;
        box-shadow: 0 1px 3px rgba(0,0,0,.3); user-select: none;
    }
    .item-btn:hover { background: #FFE44D; }
    .item-btn.on { background: #CC0000; color: #fff; border-color: #900; }
";

        private const string OverlayJsTemplate = @"
(function() {
    function log(m) { try { console.log('[reader] ' + m); } catch(e) {} }

    function findItemsByLinkname(lid) {
        var out = [];
        var all = document.querySelectorAll('.tbnote, .tbred, .sa');
        for (var i = 0; i < all.length; i++) {
            if (all[i].querySelector('[name=""' + lid + '""]')) out.push(all[i]);
        }
        return out;
    }

    window.__toggleByName = function(lid, on) {
        var items = findItemsByLinkname(lid);
        log('toggleByName ' + lid + ' -> ' + items.length + ' items, on=' + on);
        for (var i = 0; i < items.length; i++) {
            if (on) items[i].classList.add('ans-on');
            else items[i].classList.remove('ans-on');
        }
        return items.length;
    };

    window.__setNotes = function(on) {
        var items = document.querySelectorAll('.tbnote');
        for (var i = 0; i < items.length; i++) {
            if (on) items[i].classList.add('ans-on'); else items[i].classList.remove('ans-on');
        }
    };

    window.__setAnswers = function(on) {
        var items = document.querySelectorAll('.tbred, .sa');
        for (var i = 0; i < items.length; i++) {
            if (on) items[i].classList.add('ans-on'); else items[i].classList.remove('ans-on');
        }
    };

    window.__toggleItem = function(itemId) {
        var el = document.querySelector('[data-ans-id=""' + itemId + '""]');
        if (!el) return;
        var on = el.classList.toggle('ans-on');
        var btn = document.querySelector('.item-btn[data-target=""' + itemId + '""]');
        if (btn) btn.classList.toggle('on', on);
    };

    function wireGroupIcons() {
        var groups = document.querySelectorAll('hsg[id][name]');
        log('wireGroupIcons: ' + groups.length + ' groups');
        groups.forEach(function(g) {
            var icon = document.getElementById(g.id + '_img');
            if (!icon) return;
            icon.style.display = '';
            icon.style.visibility = 'visible';
            icon.style.pointerEvents = 'auto';
            icon.style.cursor = 'pointer';

            if (icon.__wired) return;
            icon.__wired = true;

            var onclickStr = icon.getAttribute('onclick') || '';
            var fn = null;
            if (onclickStr) {
                try { fn = new Function('event', onclickStr + '; return false;'); } catch(e) {}
            }
            icon.removeAttribute('onclick');

            icon.addEventListener('click', function(ev) {
                ev.stopPropagation();
                if (fn) { try { fn.call(icon, ev); } catch(e) { log('icon fn err ' + e.message); } }
                else {
                    var turnOn = g.getAttribute('on') !== 'true';
                    g.setAttribute('on', turnOn ? 'true' : 'false');
                    g.querySelectorAll('hastext[linkname]').forEach(function(h) {
                        var lid = h.getAttribute('linkname');
                        if (lid) window.__toggleByName(lid, turnOn);
                    });
                }
                g.querySelectorAll('hastext[linkname]').forEach(function(h) {
                    var lid = h.getAttribute('linkname');
                    if (!lid) return;
                    var items = findItemsByLinkname(lid);
                    if (!items.length) return;
                    var itemId = items[0].getAttribute('data-ans-id');
                    if (!itemId) return;
                    var on = items[0].classList.contains('ans-on');
                    var btn = document.querySelector('.item-btn[data-target=""' + itemId + '""]');
                    if (btn) btn.classList.toggle('on', on);
                });
            }, true);
        });
    }

    function buildButtons() {
        var container = document.getElementById('itemBtns');
        if (!container) return;
        container.innerHTML = '';

        var groups = document.querySelectorAll('hsg[id][name]');
        log('buildButtons: ' + groups.length + ' groups');

        groups.forEach(function(g) {
            var icon = document.getElementById(g.id + '_img');
            if (!icon) return;
            var baseLeft = parseFloat(icon.style.left) || 0;
            var baseTop  = parseFloat(icon.style.top)  || 0;
            var children = g.querySelectorAll('hastext[linkname]');

            var i = 0;
            children.forEach(function(h) {
                var lid = h.getAttribute('linkname');
                if (!lid) return;
                var items = findItemsByLinkname(lid);
                if (!items.length) { log('  no items for ' + lid); return; }

                if (!items[0].getAttribute('data-ans-id')) {
                    items[0].setAttribute('data-ans-id', 'ans_' + g.id + '_' + i);
                }
                var itemId = items[0].getAttribute('data-ans-id');

                var labelEl = h.querySelector('a.s');
                var label = labelEl ? (labelEl.textContent || '').trim().substring(0, 3) : String(i + 1);
                if (!label) label = String(i + 1);

                var btn = document.createElement('div');
                btn.className = 'item-btn';
                btn.setAttribute('data-target', itemId);
                btn.title = labelEl ? labelEl.textContent : lid;
                btn.textContent = label;
                btn.style.left = (baseLeft - 30) + 'px';
                btn.style.top  = (baseTop + i * 24) + 'px';
                btn.addEventListener('click', function(ev) {
                    ev.stopPropagation();
                    window.__toggleItem(itemId);
                });
                container.appendChild(btn);
                i++;
            });
        });
    }

    function init() {
        log('init');
        wireGroupIcons();
        buildButtons();
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
    setTimeout(init, 200);
    setTimeout(init, 800);
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
                catch { _logFilePath = Path.Combine(Path.GetTempPath(), $"BookService_{DateTime.Now:yyyyMMdd_HHmmss}.log"); }
            }
            return _logFilePath;
        }

        public string GetLogPath() => GetLogFilePath();

        private void Log(string message)
        {
            try
            {
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}";
                lock (_logLock) File.AppendAllText(GetLogFilePath(), logMessage + Environment.NewLine);
                System.Diagnostics.Debug.WriteLine(logMessage);
                OnStatusChanged?.Invoke(this, message.Length < 80 ? message : message.Substring(0, 77) + "...");
            }
            catch { }
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

        public void ShowAllTeacherNotes(bool show) { _showTeacherNotes = show; if (_currentPageIndex >= 0) _ = LoadPageAsync(_currentPageIndex); }
        public void ShowAllStudentAnswers(bool show) { _showStudentAnswers = show; if (_currentPageIndex >= 0) _ = LoadPageAsync(_currentPageIndex); }

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
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count) _ = LoadPageAsync(_currentPageIndex);
        }

        public void ToggleTwoPageSpread()
        {
            _twoPageSpread = !_twoPageSpread;
            _sideBySideMode = _twoPageSpread;
            OnTwoPageSpreadToggled?.Invoke(this, _twoPageSpread);
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count) _ = LoadPageAsync(_currentPageIndex);
        }

        public int GetIndexForStepFile(string stepFile)
        {
            if (string.IsNullOrEmpty(stepFile)) return -1;
            var name = Path.GetFileNameWithoutExtension(stepFile);
            return _stepFileToIndex.TryGetValue(name, out int idx) ? idx : -1;
        }

        public int GetIndexForFolio(int folio)
        {
            return _folioToStepFile.TryGetValue(folio, out var stepFile) ? GetIndexForStepFile(stepFile) : -1;
        }

        public int GetIndexForSectionFolio(string sectionId, int folio)
        {
            if (string.IsNullOrEmpty(sectionId)) return -1;
            if (_sectionFolioMap.TryGetValue(sectionId, out var folioMap))
            {
                if (folioMap.TryGetValue(folio, out var stepFile))
                {
                    var composite = sectionId + "/" + stepFile;
                    if (_stepFileToIndex.TryGetValue(composite, out int idx)) return idx;
                    if (_stepFileToIndex.TryGetValue(stepFile, out int idx2)) return idx2;
                }
            }
            return -1;
        }

        public int GetFirstIndexOfSection(string sectionId)
        {
            if (string.IsNullOrEmpty(sectionId)) return -1;
            return _sectionFirstIndex.TryGetValue(sectionId, out int idx) ? idx : -1;
        }

        public async Task<bool> LoadBookAsync(string folderPath)
        {
            try
            {
                Log($"=== LOADING BOOK from: {folderPath} ===");
                _currentBookPath = folderPath;

                _tempFolder = Path.Combine(FileSystem.CacheDirectory, "BookViewer", $"temp_{Guid.NewGuid().ToString().Substring(0, 8)}");
                Directory.CreateDirectory(_tempFolder);

                var bookXmlPath = Path.Combine(folderPath, "book.xml");
                if (!File.Exists(bookXmlPath)) { OnStatusChanged?.Invoke(this, "book.xml not found"); return false; }

                var folderName = Path.GetFileName(folderPath);
                var bookIdMatch = Regex.Match(folderName, @"book_(\d+)");
                if (bookIdMatch.Success) _currentBookUid = bookIdMatch.Groups[1].Value;

                var content = await File.ReadAllTextAsync(bookXmlPath);

                if (string.IsNullOrEmpty(_currentBookUid))
                {
                    var uidMatch = Regex.Match(content, @"id=""([^""]+)""");
                    if (uidMatch.Success) _currentBookUid = uidMatch.Groups[1].Value;
                }

                bool bookXmlDecrypted = false;
                if (_decryptionService.IsEncrypted(content))
                {
                    if (string.IsNullOrEmpty(_currentBookUid)) { OnStatusChanged?.Invoke(this, "No UID"); return false; }
                    content = _decryptionService.DecryptBookFile(content, _currentBookUid);
                    await File.WriteAllTextAsync(bookXmlPath, content);
                    bookXmlDecrypted = true;
                }

                _bookTitle = "Unknown Book";
                var titleMatch = Regex.Match(content, @"name=""([^""]+)""");
                if (titleMatch.Success) _bookTitle = titleMatch.Groups[1].Value;
                OnBookLoaded?.Invoke(this, _bookTitle);

                if (bookXmlDecrypted) await DecryptBookFilesAsync(folderPath);

                _pageFiles.Clear();

                var stepsFiles = Directory.GetFiles(folderPath, "steps_*.html", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(folderPath, "step_*.html", SearchOption.AllDirectories))
                    .Where(f => IsPureStepsFile(Path.GetFileName(f)))
                    .Distinct()
                    .ToList();

                _pageFiles = stepsFiles
                    .GroupBy(f => Path.GetDirectoryName(f) ?? "")
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g => g.Select(f => new { Path = f, Index = ParseStepIndex(Path.GetFileName(f)) })
                        .OrderBy(x => x.Index >= 0 ? x.Index : int.MaxValue)
                        .ThenBy(x => x.Path)
                        .Select(x => x.Path))
                    .ToList();

                _stepFileToIndex.Clear();
                _folioToStepFile.Clear();
                _sectionFolioMap.Clear();
                _sectionFirstIndex.Clear();

                for (int i = 0; i < _pageFiles.Count; i++)
                {
                    var name = Path.GetFileNameWithoutExtension(_pageFiles[i]);
                    var parentDir = Path.GetFileName(Path.GetDirectoryName(_pageFiles[i]) ?? "");
                    var composite = parentDir + "/" + name;
                    if (!_stepFileToIndex.ContainsKey(composite)) _stepFileToIndex[composite] = i;
                    if (!_stepFileToIndex.ContainsKey(name)) _stepFileToIndex[name] = i;
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
                                        if (!_folioToStepFile.ContainsKey(folio))
                                            _folioToStepFile[folio] = file;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Log($"Error parsing book.xml: {ex.Message}"); }

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
                            if (idx >= 0) pagesContent = pagesContent.Substring(idx + 2);
                            trimmed = pagesContent.TrimStart();
                        }
                        if (!trimmed.StartsWith("<pages")) pagesContent = $"<pages>{pagesContent}</pages>";
                        var pagesDoc = new XmlDocument();
                        pagesDoc.LoadXml(pagesContent);
                        var folioMap = new Dictionary<int, string>();
                        var pageNodes = pagesDoc.SelectNodes("//page");
                        if (pageNodes != null)
                            foreach (XmlNode pageNode in pageNodes)
                            {
                                var folioStr = pageNode.Attributes?["folio"]?.Value ?? "";
                                var file = pageNode.Attributes?["file"]?.Value ?? "";
                                if (int.TryParse(folioStr, out int folio) && !string.IsNullOrEmpty(file))
                                    folioMap[folio] = file;
                            }
                        _sectionFolioMap[sectionId] = folioMap;
                    }
                    catch (Exception ex) { Log($"Error parsing {pagesXmlPath}: {ex.Message}"); }
                }

                OnPagesLoaded?.Invoke(this, _pageFiles);

                if (_pageFiles.Count > 0)
                {
                    _currentPageIndex = 0;
                    await LoadPageAsync(0);
                }
                else { OnStatusChanged?.Invoke(this, "No pages found"); return false; }

                OnStatusChanged?.Invoke(this, $"Loaded: {_bookTitle} ({_pageFiles.Count} pages)");
                return true;
            }
            catch (Exception ex)
            {
                Log($"ERROR loading book: {ex.Message}");
                OnStatusChanged?.Invoke(this, $"Error: {ex.Message}");
                return false;
            }
        }

        private async Task DecryptBookFilesAsync(string folderPath)
        {
            try
            {
                var htmlFiles = Directory.GetFiles(folderPath, "*.html", SearchOption.AllDirectories).ToList();
                var xmlFiles = Directory.GetFiles(folderPath, "*.xml", SearchOption.AllDirectories).ToList();
                var htmFiles = Directory.GetFiles(folderPath, "*.htm", SearchOption.AllDirectories).ToList();

                int bookKey = _decryptionService.CalculateKey(_currentBookUid);

                var allXmlAndHtmFiles = new List<string>();
                allXmlAndHtmFiles.AddRange(xmlFiles);
                allXmlAndHtmFiles.AddRange(htmFiles);

                foreach (var file in allXmlAndHtmFiles)
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var fileContent = await File.ReadAllTextAsync(file);
                        if (!_decryptionService.IsEncrypted(fileContent)) continue;
                        await File.WriteAllTextAsync(file, _decryptionService.DecryptXmlOrHtm(fileContent, fileName));
                    }
                    catch (Exception ex) { Log($"Error decrypting {Path.GetFileName(file)}: {ex.Message}"); }
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
                        if (isDecrypted) continue;
                        await File.WriteAllTextAsync(file, _decryptionService.DecryptWithKey(fileContent, bookKey));
                    }
                    catch (Exception ex) { Log($"Error decrypting {Path.GetFileName(file)}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log($"ERROR DecryptBookFilesAsync: {ex.Message}"); }
        }

        public async Task DecryptBookAsync()
        {
            if (string.IsNullOrEmpty(_currentBookPath)) { OnStatusChanged?.Invoke(this, "No book loaded"); return; }
            OnStatusChanged?.Invoke(this, "Decrypting book files...");
            await DecryptBookFilesAsync(_currentBookPath);
            await LoadBookAsync(_currentBookPath);
        }

        private bool IsPureStepsFile(string fileName)
            => Regex.IsMatch(fileName ?? "", @"^steps?_\d+\.html$", RegexOptions.IgnoreCase);

        private int ParseStepIndex(string fileName)
        {
            try
            {
                var m = Regex.Match(fileName ?? "", @"^steps?_(\d+)\.html$", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int idx)) return idx;
                m = Regex.Match(fileName ?? "", @"(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int idx3)) return idx3;
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
                else _nextPageHtml = "";

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
                    string hsgMarkup = ExtractHsgMarkup(htmlContent, directory);

                    int pageWidth = 1024, pageHeight = 1344;
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

                    return await BuildOverlayHtmlAsync(bgImage, contentHtml, hsgMarkup, fileName, fontCss, pageWidth, pageHeight);
                }

                return htmlContent;
            }
            catch (Exception ex)
            {
                Log($"ERROR in ProcessHtmlContent: {ex.Message}");
                return htmlContent;
            }
        }

        /// <summary>
        /// Pull every hsg group, every toggle marker, and every splitpane/assessment
        /// block out of the raw skeleton. Rewrites every src / onimagepath /
        /// offimagepath to an inline data URI so the WebView can actually render
        /// the SVG. All SVGs live at <bookRoot>/images/.
        /// </summary>
        private string ExtractHsgMarkup(string skeletonHtml, string pageDirectory)
        {
            if (string.IsNullOrEmpty(skeletonHtml)) return "";

            var sb = new StringBuilder();

            var hsgMatches = Regex.Matches(skeletonHtml,
                @"<hsg\b[^>]*>[\s\S]*?</hsg>",
                RegexOptions.IgnoreCase);
            foreach (Match m in hsgMatches)
                sb.AppendLine(RewriteImgSrcs(m.Value, pageDirectory));

            var imgMatches = Regex.Matches(skeletonHtml,
                @"<img\b[^>]*\bclass\s*=\s*[""'][^""']*\b(hsgbtn|redbtn|has2btn)\b[^""']*[""'][^>]*>",
                RegexOptions.IgnoreCase);
            foreach (Match m in imgMatches)
                sb.AppendLine(RewriteImgSrcs(m.Value, pageDirectory));

            return sb.ToString();
        }

        /// <summary>
        /// Rewrite every src / onimagepath / offimagepath inside the fragment to an
        /// inline data URI. All SVGs live at <bookRoot>/images/, so we only need the
        /// filename part of whatever path the skeleton used.
        /// </summary>
        private string RewriteImgSrcs(string fragment, string pageDirectory)
        {
            if (string.IsNullOrEmpty(fragment) || string.IsNullOrEmpty(_currentBookPath))
                return fragment;

            var imagesDir = Path.Combine(_currentBookPath, "images");

            string ResolveToDataUri(string rawPath)
            {
                if (string.IsNullOrEmpty(rawPath)) return rawPath;

                if (rawPath.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    rawPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    rawPath.StartsWith("//"))
                    return rawPath;

                var clean = rawPath;
                var q = clean.IndexOfAny(new[] { '?', '#' });
                if (q >= 0) clean = clean.Substring(0, q);

                var fileName = Path.GetFileName(clean.Replace('\\', '/'));
                if (string.IsNullOrEmpty(fileName)) return rawPath;

                var full = Path.Combine(imagesDir, fileName);
                if (!File.Exists(full))
                {
                    Log($"[svg-miss] '{fileName}' not found in {imagesDir}");
                    return rawPath;
                }

                try
                {
                    var bytes = File.ReadAllBytes(full);
                    var ext = Path.GetExtension(full).ToLowerInvariant();
                    var mime = ext switch
                    {
                        ".svg" => "image/svg+xml",
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        ".bmp" => "image/bmp",
                        _ => "application/octet-stream"
                    };
                    return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                }
                catch { return rawPath; }
            }

            fragment = Regex.Replace(fragment,
                @"(\bsrc\s*=\s*[""'])([^""']+)([""'])",
                m => m.Groups[1].Value + ResolveToDataUri(m.Groups[2].Value) + m.Groups[3].Value);

            fragment = Regex.Replace(fragment,
                @"(\b(on|off)imagepath\s*=\s*[""'])([^""']+)([""'])",
                m => m.Groups[1].Value + ResolveToDataUri(m.Groups[3].Value) + m.Groups[4].Value);

            return fragment;
        }

        private async Task<string> ExtractContentFromHtmlFile(string filePath, string fileName, string directory)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            string oriHtmlPath = Path.Combine(directory, nameWithoutExt + "_ori.html");
            if (File.Exists(oriHtmlPath)) return ExtractContentFromHtml(await File.ReadAllTextAsync(oriHtmlPath));

            string paraXmlPath = Path.Combine(directory, nameWithoutExt + "_para.xml");
            if (File.Exists(paraXmlPath)) return ExtractContentFromParaXml(await File.ReadAllTextAsync(paraXmlPath));

            try { return ExtractContentFromHtml(await File.ReadAllTextAsync(filePath)); }
            catch { return "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>"; }
        }

        private string ExtractContentFromHtml(string htmlContent)
        {
            var bodyMatch = Regex.Match(htmlContent, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
            return bodyMatch.Success ? bodyMatch.Groups[1].Value : htmlContent;
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
                    var x = child.Attributes?["x"]?.Value;
                    var y = child.Attributes?["y"]?.Value;
                    var w = child.Attributes?["width"]?.Value;
                    var h = child.Attributes?["height"]?.Value;
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

                    sb.Append("<div class='para' style=\"").Append(pos.ToString()).Append("\">").Append(text).Append("</div>");
                }
                return sb.ToString();
            }
            catch { return ""; }
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
                    string[] alts = answerType == "teacherNotes"
                        ? new[] { Path.Combine(directory, fileName + "_teacherNotes.html"),
                                  Path.Combine(directory, fileName + "_teacher.html"),
                                  Path.Combine(directory, "teacherNotes.html") }
                        : new[] { Path.Combine(directory, fileName + "_studentAnswers.html"),
                                  Path.Combine(directory, fileName + "_studentAnswer.html"),
                                  Path.Combine(directory, "studentAnswers.html"),
                                  Path.Combine(directory, "redAnswer.html") };
                    foreach (var a in alts) if (File.Exists(a)) { redAnswerPath = a; break; }
                }

                if (!File.Exists(redAnswerPath)) return "";

                string content = await File.ReadAllTextAsync(redAnswerPath);
                var bodyMatch = Regex.Match(content, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                if (bodyMatch.Success) content = bodyMatch.Groups[1].Value;

                string classPattern = answerType == "teacherNotes" ? "tbnote" : "tbred";
                var sb = new StringBuilder();

                var divMatches = Regex.Matches(content,
                    @"<div\b[^>]*\bclass\s*=\s*[""'][^""']*\b" + classPattern + @"\b[^""']*[""'][^>]*>[\s\S]*?</div>",
                    RegexOptions.IgnoreCase);
                foreach (Match m in divMatches) sb.AppendLine(m.Value);

                var svgMatches = Regex.Matches(content,
                    @"<svg\b[^>]*\bclass\s*=\s*[""'][^""']*\b" + classPattern + @"\b[^""']*[""'][^>]*>[\s\S]*?</svg>",
                    RegexOptions.IgnoreCase);
                foreach (Match m in svgMatches) sb.AppendLine(m.Value);

                return sb.ToString();
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
                foreach (var name in new[] { $"steps_{stepNumber}.jpg", $"steps_{stepNumber}.png", $"step_{stepNumber}.jpg", $"step_{stepNumber}.png" })
                {
                    var p = Path.Combine(imagesPath, name);
                    if (File.Exists(p)) return ConvertImageToBase64HighQuality(p);
                }
            }
            return GetPlaceholderImage();
        }

        public async Task<string> GetFontCssWithEmbeddedFonts(string directory)
        {
            try
            {
                if (_fontCache.TryGetValue(directory, out var cached)) return cached;

                string fontCss = "";
                string fontCssPath = "";

                var fontCssFiles = Directory.GetFiles(directory, "*_font.css");
                if (fontCssFiles.Length > 0) fontCssPath = fontCssFiles[0];
                else
                {
                    var root = Path.Combine(_currentBookPath, "font.css");
                    if (File.Exists(root)) fontCssPath = root;
                }

                if (!string.IsNullOrEmpty(fontCssPath) && File.Exists(fontCssPath))
                {
                    fontCss = await File.ReadAllTextAsync(fontCssPath);

                    string fontsFolder = Path.Combine(_currentBookPath, "FONTS");
                    if (!Directory.Exists(fontsFolder)) fontsFolder = Path.Combine(_currentBookPath, "fonts");

                    Dictionary<string, string> fontIndex = null;
                    if (Directory.Exists(fontsFolder))
                    {
                        fontIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var f in Directory.GetFiles(fontsFolder, "*.*", SearchOption.AllDirectories))
                        {
                            var ext = Path.GetExtension(f).ToLowerInvariant();
                            if (ext != ".ttf" && ext != ".otf" && ext != ".woff" && ext != ".woff2") continue;
                            var justName = Path.GetFileName(f);
                            var stem = Path.GetFileNameWithoutExtension(f);
                            var noSpace = stem.Replace(" ", "").Replace("-", "").Replace("_", "");
                            if (!fontIndex.ContainsKey(justName)) fontIndex[justName] = f;
                            if (!fontIndex.ContainsKey(stem)) fontIndex[stem] = f;
                            if (!fontIndex.ContainsKey(noSpace)) fontIndex[noSpace] = f;
                        }
                    }

                    foreach (Match match in Regex.Matches(fontCss, @"@font-face\s*\{(?<body>.*?)\}", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                    {
                        var body = match.Groups[1].Value;
                        foreach (Match urlMatch in Regex.Matches(body, @"url\(['""]?([^)'""]+)['""]?\)"))
                        {
                            var rawPath = urlMatch.Groups[1].Value;
                            var cleanPath = rawPath.Replace("../FONTS/", "").Replace("../fonts/", "").Replace("./", "").Trim();
                            string fullFontPath = null;

                            if (fontIndex != null)
                            {
                                var justName = Path.GetFileName(cleanPath);
                                var stem = Path.GetFileNameWithoutExtension(cleanPath);
                                var noSpace = stem.Replace(" ", "").Replace("-", "").Replace("_", "");
                                if (fontIndex.TryGetValue(justName, out var p1)) fullFontPath = p1;
                                else if (fontIndex.TryGetValue(stem, out var p2)) fullFontPath = p2;
                                else if (fontIndex.TryGetValue(noSpace, out var p3)) fullFontPath = p3;
                            }

                            if (!string.IsNullOrEmpty(fullFontPath) && File.Exists(fullFontPath))
                            {
                                var bytes = File.ReadAllBytes(fullFontPath);
                                var b64 = Convert.ToBase64String(bytes);
                                var ext = Path.GetExtension(fullFontPath).ToLower();
                                var mime = ext switch { ".ttf" => "font/ttf", ".otf" => "font/otf", ".woff" => "font/woff", ".woff2" => "font/woff2", _ => "font/ttf" };
                                var dataUri = $"data:{mime};base64,{b64}";
                                fontCss = fontCss.Replace($"url('{rawPath}')", $"url('{dataUri}')")
                                                 .Replace($"url(\"{rawPath}\")", $"url('{dataUri}')")
                                                 .Replace($"url({rawPath})", $"url('{dataUri}')");
                            }
                        }
                    }

                    fontCss += "\n@font-face { font-family: 'CJKLocked'; "
                             + "src: local('PingFang TC'), local('Microsoft JhengHei'), local('Noto Sans CJK TC'); "
                             + "ascent-override: 100%; descent-override: 0%; line-gap-override: 0%; }\n"
                             + "body, .para, .base-content, .content-overlay { "
                             + "font-family: 'CJKLocked', system-ui, -apple-system, 'Helvetica Neue', Arial, sans-serif; }\n";

                    _fontCache[directory] = fontCss;
                    return fontCss;
                }
            }
            catch (Exception ex) { Log($"Font error: {ex.Message}"); }
            return "";
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
                var result = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
                _imageCache[imagePath] = result;
                return result;
            }
            catch { return GetPlaceholderImage(); }
        }

        private async Task<string> BuildOverlayHtmlAsync(string bgImage, string contentHtml, string hsgMarkup,
            string fileName, string fontCss, int pageWidth, int pageHeight)
        {
            if (string.IsNullOrEmpty(bgImage)) bgImage = GetPlaceholderImage();
            if (string.IsNullOrEmpty(contentHtml))
                contentHtml = "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";

            double zoom = _currentZoom;
            string surround = SurroundColor;
            string zoomStr = zoom.ToString(System.Globalization.CultureInfo.InvariantCulture);

            string filePath = (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count) ? _pageFiles[_currentPageIndex] : "";

            string teacherRaw = "";
            string studentRaw = "";
            if (!string.IsNullOrEmpty(filePath))
            {
                teacherRaw = await GetRedAnswerContentAsync(filePath, "teacherNotes");
                studentRaw = await GetRedAnswerContentAsync(filePath, "studentAnswers");
            }

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
            sb.Append(studentRaw);
            sb.Append(teacherRaw);
            sb.Append(hsgMarkup);
            sb.Append("<div class='item-btns' id='itemBtns'></div>\n");
            sb.Append("</div>\n</div>\n</div>\n");
            sb.Append("<script>\n").Append(OverlayJsTemplate).Append("\n</script>\n</body>\n</html>");
            return sb.ToString();
        }

        public async Task<string> BuildPageHtmlForRenderAsync(string filePath, bool includeAnswers, bool includeTeacherNotes, bool includeStudentAnswers)
        {
            bool prevT = _showTeacherNotes, prevS = _showStudentAnswers;
            _showTeacherNotes = includeAnswers && includeTeacherNotes;
            _showStudentAnswers = includeAnswers && includeStudentAnswers;
            try
            {
                var rawHtml = await File.ReadAllTextAsync(filePath);
                return await ProcessHtmlContent(rawHtml, filePath);
            }
            finally { _showTeacherNotes = prevT; _showStudentAnswers = prevS; }
        }

        public void NavigatePrevious() { if (_currentPageIndex > 0) _ = LoadPageAsync(_currentPageIndex - 1); }
        public void NavigateNext() { if (_currentPageIndex < _pageFiles.Count - 1) _ = LoadPageAsync(_currentPageIndex + 1); }
        public void ToggleAnswers(bool show) { OnToggleAnswersRequested?.Invoke(this, show); OnStatusChanged?.Invoke(this, show ? "Answers shown" : "Answers hidden"); }
    }
}
