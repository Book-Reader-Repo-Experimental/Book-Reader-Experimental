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
html, body { width: 100%; height: 100%; overflow: auto; background: #E8E8E8; -webkit-font-smoothing: antialiased; }
body { display: flex; justify-content: center; align-items: flex-start; min-height: 100vh; }
.page-wrapper { display: flex; justify-content: center; align-items: flex-start; }
.page-container { position: relative; flex-shrink: 0; background: #ffffff; box-shadow: 0 0 20px rgba(0,0,0,0.15); overflow: hidden; transform-origin: top center; }
#rez { position: relative; }
.bgcls { position: absolute; top: 0; left: 0; width: 100%; height: 100%; }
img.bgcls { width: 100%; height: 100%; }
pane { display: block; position: absolute; top: 0; left: 0; width: 100%; height: 100%; }
pane.base > *, pane.redAnswer > *, pane.ori > * { position: absolute; }
.redbtn { cursor: pointer; opacity: .85; }
.hideBtn { display: none; }
.tbred { color: #E40477 !important; }
.tbnote { color: #0070C0 !important; }
.hsgbtn { cursor: pointer; }
pane.redAnswer { pointer-events: none; }
pane.redAnswer .tbred { pointer-events: none; }
hsg { pointer-events: auto; }
img.hsgbtn[redanswertype='teacherNotes'], img.redbtn[redanswertype='teacherNotes'], img.has2btn[redanswertype='teacherNotes'] { pointer-events: auto !important; cursor: pointer !important; }
";

        private const string ReaderJsTemplate = @"
window.__readerRedAnswersVisible = false;
window.__readerNotesVisible = false;
window.__redAnswerOriginalDisplay = new WeakMap();

function rememberDisplay(el){
  if (!window.__redAnswerOriginalDisplay.has(el)) {
    window.__redAnswerOriginalDisplay.set(el, el.style.display || '');
  }
}

// ---- __toggleByName: toggle every .tbred/.tbnote wrapper containing [name=lid] ----
window.__toggleByName = function(lid, turnOn){
  if (!lid) return 0;
  var hits = document.querySelectorAll('[name=""' + lid + '""]');
  for (var i = 0; i < hits.length; i++) {
    var el = hits[i];
    var target = null;
    var p = el.parentElement;
    while (p && p !== document.body) {
      var cls = (p.className || '') + '';
      if (cls.indexOf('tbnote') !== -1 || cls.indexOf('tbred') !== -1) { target = p; break; }
      p = p.parentElement;
    }
    if (!target && el.parentElement) {
      var pcls = (el.parentElement.className || '') + '';
      if (pcls.indexOf('tbnote') !== -1 || pcls.indexOf('tbred') !== -1) target = el.parentElement;
    }
    if (!target) target = el;
    rememberDisplay(target);
    target.style.display = turnOn ? '' : 'none';
  }
  return hits.length;
};

// ---- __setRedAnswers: hide/show every answer marker + content ----
window.__setRedAnswers = function(show){
  window.__readerRedAnswersVisible = !!show;
  document.querySelectorAll('img.redbtn, img.has2btn, img.hsgbtn').forEach(function(b){
    if (b.getAttribute('redanswertype') === 'teacherNotes') return;
    rememberDisplay(b);
    if (show) { b.classList.remove('hideBtn'); if (b.style.display === 'none') b.style.display = ''; }
    else { b.classList.add('hideBtn'); b.style.display = 'none'; }
  });
  var rp = document.querySelector('pane.redAnswer');
  if (rp) {
    rememberDisplay(rp);
    if (show) { if (rp.style.display === 'none') rp.style.display = ''; }
    else rp.style.display = 'none';
  }
  if (!show) {
    document.querySelectorAll('svg.tbred, .tbred, has2').forEach(function(el){
      var tn = el.closest ? el.closest('hsg[redanswertype=""teacherNotes""]') : null;
      if (tn) return;
      var target = el;
      var p = el.parentElement;
      while (p && p !== document.body) {
        var cls = (p.className || '') + '';
        if (cls.indexOf('tbnote') !== -1 || cls.indexOf('tbred') !== -1) { target = p; break; }
        p = p.parentElement;
      }
      rememberDisplay(target); target.style.display = 'none';
    });
    document.querySelectorAll('hsg').forEach(function(h){
      if (h.getAttribute('redanswertype') === 'teacherNotes') return;
      h.setAttribute('on', 'false');
    });
  }
};

// ---- __setNotes: toggle all teacherNote groups ----
window.__setNotes = function(show){
  window.__readerNotesVisible = !!show;
  document.querySelectorAll('hsg[redanswertype=""teacherNotes""]').forEach(function(g){
    var turnOn = !!show;
    g.setAttribute('on', turnOn ? 'true' : 'false');
    g.querySelectorAll('hastext[linkname]').forEach(function(h){
      var lid = h.getAttribute('linkname');
      if (lid) window.__toggleByName(lid, turnOn);
    });
  });
  if (show) {
    var rp = document.querySelector('pane.redAnswer');
    if (rp) { rememberDisplay(rp); if (rp.style.display === 'none') rp.style.display = ''; }
  }
};

// ---- CH_toggleHSG: toggle one hsg group ----
window.CH_toggleHSG = function(id){
  var g = document.getElementById(id);
  if (!g) return;
  var isOn = g.getAttribute('on') === 'true';
  var turnOn = !isOn;
  g.setAttribute('on', turnOn ? 'true' : 'false');
  g.querySelectorAll('hastext[linkname]').forEach(function(h){
    var lid = h.getAttribute('linkname');
    if (lid) window.__toggleByName(lid, turnOn);
  });
  var img = document.getElementById(id + '_img');
  if (img) {
    var src = img.getAttribute('src') || '';
    img.setAttribute('src', turnOn
      ? src.replace('redlayerAll.svg','redlayerNone.svg').replace('teacherNotes.svg','teacherNotes_o.svg')
      : src.replace('redlayerNone.svg','redlayerAll.svg').replace('teacherNotes_o.svg','teacherNotes.svg'));
  }
};

// ---- Per-marker toggles ----
window.XN_toggleHands = function(id){
  var btn = document.getElementById(id);
  var isOn = btn && btn.getAttribute('data-on') === 'true';
  var turnOn = !isOn;
  if (btn) btn.setAttribute('data-on', turnOn ? 'true' : 'false');
  window.__toggleByName(id, turnOn);
};

window.XN_toggleRedLayers = function(id){
  var btn = document.getElementById(id);
  var isOn = btn && btn.getAttribute('data-on') === 'true';
  var turnOn = !isOn;
  if (btn) btn.setAttribute('data-on', turnOn ? 'true' : 'false');
  window.__toggleByName(id, turnOn);
};

// ---- Force teacher-note icons visible ----
(function(){
  function makeClickable(){
    document.querySelectorAll('img.hsgbtn[redanswertype=""teacherNotes""], img.redbtn[redanswertype=""teacherNotes""], img.has2btn[redanswertype=""teacherNotes""]').forEach(function(b){
      b.classList.remove('hideBtn');
      b.style.display = '';
      b.style.pointerEvents = 'auto';
      b.style.cursor = 'pointer';
      b.style.zIndex = '9999';
    });
  }
  makeClickable();
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', makeClickable);
  window.addEventListener('load', makeClickable);
  setTimeout(makeClickable, 200);
  setTimeout(makeClickable, 800);
})();

// ---- Stubs so the skeleton's inline onclick doesn't error ----
window.CH_RemoveHSGroup = function(){};
window.CH_changeButton = function(){};
window.BY_showAllSubElementIcons = function(){};
window.XN_MinPopH = function(id){ var e = document.getElementById(id); if (e) e.style.display = 'none'; };
window.XN_MaxPopH = function(id){ var e = document.getElementById(id); if (e) e.style.display = ''; };
window.CH_delHandS = function(){};
window.CH_showIcon = function(){};
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

        public void SetShowTeacherNotes(bool show) { _showTeacherNotes = show; if (_currentPageIndex >= 0) _ = LoadPageAsync(_currentPageIndex); }
        public void SetShowStudentAnswers(bool show) { _showStudentAnswers = show; if (_currentPageIndex >= 0) _ = LoadPageAsync(_currentPageIndex); }
        public void ShowAllTeacherNotes(bool show) { _showTeacherNotes = show; if (_currentPageIndex >= 0) _ = LoadPageAsync(_currentPageIndex); }
        public void ShowAllStudentAnswers(bool show) { _showStudentAnswers = show; if (_currentPageIndex >= 0) _ = LoadPageAsync(_currentPageIndex); }

        public bool IsFileEncrypted(string content) => _decryptionService.IsEncrypted(content);
        public string DecryptFile(string content, string fileName) => _decryptionService.DecryptWithFileName(content, fileName);

        public void SetZoom(double zoom)
        {
            _currentZoom = Math.Max(0.5, Math.Min(3.0, zoom));
            OnZoomChanged?.Invoke(this, _currentZoom);
            if (_currentPageIndex >= 0) _ = LoadPageAsync(_currentPageIndex);
        }
        public void ZoomIn() => SetZoom(_currentZoom + 0.1);
        public void ZoomOut() => SetZoom(_currentZoom - 0.1);
        public void ResetZoom() => SetZoom(1.0);

        public void ToggleSideBySide() { _sideBySideMode = !_sideBySideMode; OnSideBySideToggled?.Invoke(this, _sideBySideMode); if (_currentPageIndex >= 0) _ = LoadPageAsync(_currentPageIndex); }
        public void ToggleTwoPageSpread() { _twoPageSpread = !_twoPageSpread; _sideBySideMode = _twoPageSpread; OnTwoPageSpreadToggled?.Invoke(this, _twoPageSpread); if (_currentPageIndex >= 0) _ = LoadPageAsync(_currentPageIndex); }

        public int GetIndexForStepFile(string stepFile)
        {
            if (string.IsNullOrEmpty(stepFile)) return -1;
            var name = Path.GetFileNameWithoutExtension(stepFile);
            return _stepFileToIndex.TryGetValue(name, out int idx) ? idx : -1;
        }
        public int GetIndexForFolio(int folio) => _folioToStepFile.TryGetValue(folio, out var sf) ? GetIndexForStepFile(sf) : -1;
        public int GetIndexForSectionFolio(string sectionId, int folio)
        {
            if (string.IsNullOrEmpty(sectionId)) return -1;
            if (_sectionFolioMap.TryGetValue(sectionId, out var fm) && fm.TryGetValue(folio, out var sf))
            {
                var composite = sectionId + "/" + sf;
                if (_stepFileToIndex.TryGetValue(composite, out int i)) return i;
                if (_stepFileToIndex.TryGetValue(sf, out int j)) return j;
            }
            return -1;
        }
        public int GetFirstIndexOfSection(string sectionId) { if (string.IsNullOrEmpty(sectionId)) return -1; return _sectionFirstIndex.TryGetValue(sectionId, out int i) ? i : -1; }

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
                        foreach (XmlNode sn in sectionNodes)
                        {
                            var sectionId = sn.Attributes?["id"]?.Value ?? "";
                            if (string.IsNullOrEmpty(sectionId)) continue;

                            var firstPageNode = sn.SelectSingleNode(".//sectionorders/page");
                            if (firstPageNode != null)
                            {
                                var s = firstPageNode.Attributes?["seqindex"]?.Value ?? "";
                                if (int.TryParse(s, out int seq)) _sectionFirstIndex[sectionId] = seq;
                            }

                            var pageNodes = sn.SelectNodes(".//sectionorders/page");
                            if (pageNodes != null)
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
                        var fm = new Dictionary<int, string>();
                        var pageNodes = pagesDoc.SelectNodes("//page");
                        if (pageNodes != null)
                            foreach (XmlNode pn in pageNodes)
                            {
                                var folioStr = pn.Attributes?["folio"]?.Value ?? "";
                                var file = pn.Attributes?["file"]?.Value ?? "";
                                if (int.TryParse(folioStr, out int folio) && !string.IsNullOrEmpty(file))
                                    fm[folio] = file;
                            }
                        _sectionFolioMap[sectionId] = fm;
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
                var allXmlAndHtmFiles = xmlFiles.Concat(htmFiles).ToList();

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

        // ============================================================
        // ProcessHtmlContent — mirror of web reader's buildPageHtml
        //   1. read skeleton (steps_N.html)
        //   2. read _base.html, _ori.html, _redAnswer.html
        //   3. fill <pane class=base|ori|redAnswer> with the contents
        //   4. rewrite relative asset URLs to file:// paths
        //   5. wrap with CSS + the reader JS
        // ============================================================
        private async Task<string> ProcessHtmlContent(string htmlContent, string filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileName(filePath) ?? "";

                if (!IsPureStepsFile(fileName))
                    return htmlContent;

                string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                string baseHtml = await ReadIfExists(Path.Combine(directory, nameWithoutExt + "_base.html"));
                string oriHtml  = await ReadIfExists(Path.Combine(directory, nameWithoutExt + "_ori.html"));
                string redHtml  = await ReadIfExists(Path.Combine(directory, nameWithoutExt + "_redAnswer.html"));
                string fontCss  = await GetFontCssWithEmbeddedFonts(directory);

                // Page size from #rez
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

                // Fill the three panes
                string skeleton = htmlContent;

                skeleton = Regex.Replace(skeleton,
                    @"(<pane\s+class=""base""[^>]*>)([\s\S]*?)(</pane>)",
                    m => m.Groups[1].Value + baseHtml + m.Groups[3].Value,
                    RegexOptions.IgnoreCase);

                skeleton = Regex.Replace(skeleton,
                    @"(<pane\s+class=""ori""[^>]*>)([\s\S]*?)(</pane>)",
                    m => m.Groups[1].Value + oriHtml + m.Groups[3].Value,
                    RegexOptions.IgnoreCase);

                skeleton = Regex.Replace(skeleton,
                    @"(<pane\s+class=""redAnswer""[^>]*>)([\s\S]*?)(</pane>)",
                    m => m.Groups[1].Value + redHtml + m.Groups[3].Value,
                    RegexOptions.IgnoreCase);

                // Rewrite relative URLs to file:// absolute
                skeleton = RewriteUrlsToFile(skeleton, directory);

                // Initial visibility according to flags
                if (_showStudentAnswers)
                    skeleton = Regex.Replace(skeleton,
                        @"<pane\s+class=""redAnswer""([^>]*)>",
                        m => "<pane class=\"redAnswer\"" + (m.Groups[1].Value.Contains("style=") ? m.Groups[1].Value : m.Groups[1].Value + " style=\"\"") + ">",
                        RegexOptions.IgnoreCase);

                return BuildFinalHtml(skeleton, fontCss, pageWidth, pageHeight, fileName);
            }
            catch (Exception ex)
            {
                Log($"ERROR ProcessHtmlContent: {ex.Message}");
                return htmlContent;
            }
        }

        private async Task<string> ReadIfExists(string path)
        {
            try { return File.Exists(path) ? await File.ReadAllTextAsync(path) : ""; }
            catch { return ""; }
        }

        /// <summary>
        /// Rewrite every relative src/href/url() in the fragment to file:// absolute paths
        /// so the WebView can load images/svgs/fonts from the book folder.
        /// </summary>
        private string RewriteUrlsToFile(string html, string pageDir)
        {
            if (string.IsNullOrEmpty(html)) return html;

            string bookRoot = _currentBookPath.Replace('\\', '/');

            string Resolve(string raw)
            {
                if (string.IsNullOrEmpty(raw)) return raw;
                if (raw.StartsWith("data:") || raw.StartsWith("http") || raw.StartsWith("//")) return raw;

                // strip query/hash
                var clean = raw;
                var q = clean.IndexOfAny(new[] { '?', '#' });
                if (q >= 0) clean = clean.Substring(0, q);

                clean = clean.Replace('\\', '/').TrimStart('.', '/');

                // Resolve relative to page dir first
                var candidate = Path.GetFullPath(Path.Combine(pageDir, clean));
                if (File.Exists(candidate))
                    return "file://" + candidate.Replace('\\', '/');

                // Then book root
                candidate = Path.GetFullPath(Path.Combine(bookRoot, clean));
                if (File.Exists(candidate))
                    return "file://" + candidate.Replace('\\', '/');

                // Fallback: book root images/
                var fname = Path.GetFileName(clean);
                candidate = Path.GetFullPath(Path.Combine(bookRoot, "images", fname));
                if (File.Exists(candidate))
                    return "file://" + candidate.Replace('\\', '/');

                // Give up; return original
                return raw;
            }

            html = Regex.Replace(html,
                @"(\bsrc\s*=\s*[""'])([^""']+)([""'])",
                m => m.Groups[1].Value + Resolve(m.Groups[2].Value) + m.Groups[3].Value);
            html = Regex.Replace(html,
                @"(\bhref\s*=\s*[""'])([^""']+)([""'])",
                m => m.Groups[1].Value + Resolve(m.Groups[2].Value) + m.Groups[3].Value);
            html = Regex.Replace(html,
                @"url\(\s*['""]?([^'"")\s]+)['""]?\s*\)",
                m => "url('" + Resolve(m.Groups[1].Value) + "')");

            return html;
        }

        /// <summary>
        /// Wrap skeleton + CSS + JS into the final HTML document.
        /// </summary>
        private string BuildFinalHtml(string skeleton, string fontCss, int pageWidth, int pageHeight, string title)
        {
            string surround = SurroundColor;
            string zoom = _currentZoom.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html>\n<head>\n");
            sb.Append("<meta charset='UTF-8'>\n");
            sb.Append("<meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes'>\n");
            sb.Append("<title>").Append(title).Append("</title>\n");
            sb.Append("<style>\n");
            sb.Append(fontCss);
            sb.Append(OverlayCssTemplate);
            sb.Append("body { background: ").Append(surround).Append("; }\n");
            sb.Append(".page-container { width: ").Append(pageWidth).Append("px; height: ").Append(pageHeight).Append("px; transform: scale(").Append(zoom).Append("); }\n");
            sb.Append("</style>\n</head>\n<body>\n");
            sb.Append("<div class='page-wrapper'><div class='page-container'>\n");
            sb.Append(skeleton);
            sb.Append("\n</div></div>\n");
            sb.Append("<script>\n").Append(ReaderJsTemplate).Append("\n</script>\n");
            sb.Append("</body>\n</html>");
            return sb.ToString();
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

                    // Rewrite url('../FONTS/x.ttf') -> file://<bookRoot>/FONTS/x.ttf
                    var fontsFolder = Path.Combine(_currentBookPath, "FONTS");
                    if (!Directory.Exists(fontsFolder)) fontsFolder = Path.Combine(_currentBookPath, "fonts");
                    var fontsFolderUnix = fontsFolder.Replace('\\', '/');

                    fontCss = Regex.Replace(fontCss,
                        @"url\(\s*['""]?([^'"")\s]+)['""]?\s*\)",
                        m =>
                        {
                            var raw = m.Groups[1].Value;
                            if (raw.StartsWith("data:") || raw.StartsWith("http")) return m.Value;

                            var name = Path.GetFileName(raw.Replace('\\', '/'));
                            var full = Path.Combine(fontsFolder, name);
                            if (File.Exists(full))
                                return "url('file://" + full.Replace('\\', '/') + "')";
                            return m.Value;
                        });

                    _fontCache[directory] = fontCss;
                    return fontCss;
                }
            }
            catch (Exception ex) { Log($"Font error: {ex.Message}"); }
            return "";
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
