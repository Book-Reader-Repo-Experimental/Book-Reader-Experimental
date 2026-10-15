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
        private bool _redAnswersOn = false;

        private readonly Dictionary<string, int> _stepFileToIndex = new();
        private readonly Dictionary<int, string> _folioToStepFile = new();
        private readonly Dictionary<string, Dictionary<int, string>> _sectionFolioMap = new();
        private readonly Dictionary<string, int> _sectionFirstIndex = new();

        private static readonly object _logLock = new object();
        private static string _logFilePath = "";

        // Splitpane overlay geometry, keyed by splitpane id. Persisted so a
        // dragged panel stays where the user put it across page reloads.
        private readonly Dictionary<string, (double left, double top)> _splitPanePositions = new();
        public IReadOnlyDictionary<string, (double left, double top)> SplitPanePositions => _splitPanePositions;

        public int CurrentPageWidth { get; private set; } = 1024;
        public int CurrentPageHeight { get; private set; } = 1309;

        private static string SurroundColor =>
            Application.Current?.RequestedTheme == AppTheme.Dark ? "#1C1C1E" : "#E8E8E8";

        // ============================================================
        // Red Answers runtime (ported from the HTML reader's index.html)
        // ============================================================
        private const string RedAnswersRuntimeJs = @"
window.__readerRedAnswersVisible = false;
window.__redAnswerOriginalDisplay = new WeakMap();
function rememberDisplay(el){
  if (!window.__redAnswerOriginalDisplay.has(el)) {
    window.__redAnswerOriginalDisplay.set(el, el.style.display || '');
  }
}
window.__setRedAnswers = function(show){
  window.__readerRedAnswersVisible = !!show;
  document.querySelectorAll('img.redbtn, img.has2btn, img.hsgbtn').forEach(function(b){
    rememberDisplay(b);
    if (show) {
      b.classList.remove('hideBtn');
      if (b.style.display === 'none') b.style.display = '';
    } else {
      b.classList.add('hideBtn');
      b.style.display = 'none';
    }
  });
  var rp = document.querySelector('pane.redAnswer');
  if (rp) {
    rememberDisplay(rp);
    if (show) { if (rp.style.display === 'none') rp.style.display = ''; }
    else { rp.style.display = 'none'; }
  }
  if (!show) {
    document.querySelectorAll('svg.tbred').forEach(function(s){
      rememberDisplay(s); s.style.display = 'none'; });
    document.querySelectorAll('pane.redAnswer .tbred').forEach(function(t){
      rememberDisplay(t); t.style.display = 'none'; });
    document.querySelectorAll('has2').forEach(function(el){
      var wrap = el.closest('.tbred') || el;
      rememberDisplay(wrap); wrap.style.display = 'none'; });
    document.querySelectorAll('hsg').forEach(function(h){
      h.setAttribute('on','false');
      var gi = document.getElementById(h.id + '_img');
      if (gi) { var gs = gi.getAttribute('src') || '';
        gi.setAttribute('src', gs.replace('redlayerNone.svg','redlayerAll.svg').replace('None','All')); }
    });
    document.querySelectorAll('[data-on]').forEach(function(el){ el.setAttribute('data-on','false'); });
  }
};
window.XN_toggleRedLayers = function(id){
  var btn = document.getElementById(id);
  var isOn = btn && btn.getAttribute('data-on') === 'true';
  var turnOn = !isOn;
  if (btn) btn.setAttribute('data-on', turnOn ? 'true' : 'false');
  document.querySelectorAll('svg.tbred[name=""' + id + '""]').forEach(function(s){
    rememberDisplay(s); s.style.display = turnOn ? '' : 'none'; });
  document.querySelectorAll('pane.redAnswer .tbred').forEach(function(div){
    if (div.querySelector('has2[name=""' + id + '""]')) {
      rememberDisplay(div); div.style.display = turnOn ? '' : 'none'; } });
  document.querySelectorAll('.tbred[name=""' + id + '""]').forEach(function(el){
    rememberDisplay(el); el.style.display = turnOn ? '' : 'none'; });
};
window.XN_toggleHands = function(id){
  var btn = document.getElementById(id);
  var isOn = btn && btn.getAttribute('data-on') === 'true';
  var turnOn = !isOn;
  if (btn) btn.setAttribute('data-on', turnOn ? 'true' : 'false');
  document.querySelectorAll('has2[name=""' + id + '""]').forEach(function(el){
    var wrap = el.closest('.tbred') || el;
    rememberDisplay(wrap); wrap.style.display = turnOn ? '' : 'none'; });
  document.querySelectorAll('svg.tbred[name=""' + id + '""]').forEach(function(s){
    rememberDisplay(s); s.style.display = turnOn ? '' : 'none'; });
};
window.CH_toggleHSG = function(id){
  var g = document.getElementById(id);
  var img = document.getElementById(id + '_img');
  if (!g) return;
  var isOn = g.getAttribute('on') === 'true';
  var turnOn = !isOn;
  g.setAttribute('on', turnOn ? 'true' : 'false');
  var linknames = [];
  g.querySelectorAll('hastext[linkname]').forEach(function(h){
    var ln = h.getAttribute('linkname');
    if (linknames.indexOf(ln) === -1) linknames.push(ln); });
  linknames.forEach(function(lid){
    var isHas2 = lid.indexOf('has2_') === 0;
    if (isHas2) {
      document.querySelectorAll('has2[name=""' + lid + '""]').forEach(function(el){
        var wrap = el.closest('.tbred') || el;
        rememberDisplay(wrap); wrap.style.display = turnOn ? '' : 'none'; });
    } else {
      document.querySelectorAll('svg.tbred[name=""' + lid + '""]').forEach(function(s){
        rememberDisplay(s); s.style.display = turnOn ? '' : 'none'; });
      document.querySelectorAll('pane.redAnswer .tbred').forEach(function(div){
        if (div.querySelector('has2[name=""' + lid + '""]')) {
          rememberDisplay(div); div.style.display = turnOn ? '' : 'none'; } });
    }
    var b = document.getElementById(lid);
    if (b) b.setAttribute('data-on', turnOn ? 'true' : 'false');
  });
  if (img) {
    var src = img.getAttribute('src') || '';
    if (turnOn) img.setAttribute('src', src.replace('redlayerAll.svg','redlayerNone.svg').replace('All','None'));
    else img.setAttribute('src', src.replace('redlayerNone.svg','redlayerAll.svg').replace('None','All'));
  }
};
window.CH_RemoveHSGroup = function(){};
window.CH_changeButton = function(){};
window.BY_showAllSubElementIcons = function(){};
window.XN_MinPopH = function(id){ var e = document.getElementById(id); if (e) e.style.display = 'none'; };
window.XN_MaxPopH = function(id){ var e = document.getElementById(id); if (e) e.style.display = ''; };
window.CH_delHandS = function(){};
window.CH_showIcon = function(){};
";

        // ============================================================
        // Splitpane dragger runtime (the \"move\" button)
        // Ported from the HTML reader's startDrag/moveDrag/endDrag block.
        // A splitpane.ext/assessment.ext/exercise.ext is a movable panel:
        //   - click its <a class=\"btn_h_m dragger\">move</a>  => open panel
        //   - drag  that same handle                           => reposition
        // On reposition, we post to MAUI via a hash sentinel:
        //   #ext-reposition:<json>
        // MAUI's WebView.Navigating catches it and persists left/top.
        // ============================================================
        private const string DraggerRuntimeJs = @"
(function(){
  var EX_SEL = 'splitpane.ext, assessment.ext, exercise.ext, assessment, exercise';
  var dragState = null;

  function minimizePanel(sp){
    var toHide = sp.querySelector('tohide');
    if (!toHide) return;
    if (toHide.getAttribute('data-minimized') === 'true') return;
    toHide.setAttribute('data-minimized', 'true');
    if (!toHide.__origDisplays) toHide.__origDisplays = new Map();
    toHide.querySelectorAll(':scope > div').forEach(function(d){
      if (!toHide.__origDisplays.has(d)) {
        toHide.__origDisplays.set(d, d.style.display || '');
      }
      d.style.display = 'none';
    });
    var mv = sp.querySelector('.dragger');
    if (mv) {
      mv.style.display = '';
      mv.style.position = 'absolute';
      mv.style.left = '0';
      mv.style.top = '0';
      mv.style.zIndex = '101';
      mv.style.background = '#EEEEEE';
      mv.style.border = '1px solid #888';
      mv.style.padding = '2px 6px';
      mv.style.fontSize = '11px';
    }
  }

  function togglePanel(sp){
    var toHide = sp.querySelector('tohide');
    if (!toHide) return;
    var isMin = toHide.getAttribute('data-minimized') === 'true';
    if (isMin) {
      toHide.setAttribute('data-minimized','false');
      toHide.querySelectorAll(':scope > div').forEach(function(d){
        var orig = toHide.__origDisplays ? toHide.__origDisplays.get(d) : '';
        d.style.display = orig == null ? '' : orig;
      });
    } else {
      minimizePanel(sp);
    }
  }

  function openExt(pane){
    var tag = (pane.tagName || '').toLowerCase();
    var img = pane.querySelector('img');
    if (img && img.onclick) {
      try { img.click(); return; } catch(e){}
    }
    var info = (tag === 'splitpane')
      ? (window.getSplitPaneInfo ? window.getSplitPaneInfo(pane) : { id: pane.id })
      : (window.getAssessmentInfo ? window.getAssessmentInfo(pane) : { id: pane.id });
    try {
      if (window.parent && window.parent !== window) {
        window.parent.postMessage({ type: 'ext:open', payload: info, tag: tag }, '*');
      }
    } catch(e){}
    // Also emit a hash sentinel so MAUI can catch it even without a parent frame.
    try {
      window.location.hash = 'ext-open:' + encodeURIComponent(JSON.stringify({ info: info, tag: tag }));
    } catch(e){}
  }

  function startDrag(e, handle, pane){
    var pt = e.touches ? e.touches[0] : e;
    dragState = {
      pane: pane, handle: handle,
      startX: pt.clientX, startY: pt.clientY,
      originLeft: pane.offsetLeft, originTop: pane.offsetTop,
      moved: false
    };
    pane.style.zIndex = '9999';
  }

  function moveDrag(e){
    if (!dragState) return;
    var pt = e.touches ? e.touches[0] : e;
    var dx = pt.clientX - dragState.startX;
    var dy = pt.clientY - dragState.startY;
    if (Math.abs(dx) > 4 || Math.abs(dy) > 4) dragState.moved = true;
    dragState.pane.style.left = (dragState.originLeft + dx) + 'px';
    dragState.pane.style.top  = (dragState.originTop  + dy) + 'px';
    e.preventDefault();
  }

  function endDrag(e){
    if (!dragState) return;
    var pane = dragState.pane;
    var moved = dragState.moved;
    if (moved) {
      var payload = {
        id: pane.id,
        left: pane.style.left,
        top: pane.style.top,
        tag: (pane.tagName || '').toLowerCase()
      };
      try {
        if (window.parent && window.parent !== window) {
          window.parent.postMessage({ type: 'ext:reposition', payload: payload }, '*');
        }
      } catch(err){}
      // Hash sentinel for MAUI bridging.
      try {
        window.location.hash = 'ext-reposition:' + encodeURIComponent(JSON.stringify(payload));
      } catch(err){}
    } else {
      openExt(pane);
    }
    dragState = null;
  }

  function attach(){
    document.querySelectorAll(EX_SEL).forEach(function(sp){
      if (sp.__bound) return;
      sp.__bound = true;
      minimizePanel(sp);
      var mv = sp.querySelector('.dragger');
      if (mv) {
        mv.addEventListener('mousedown', function(e){
          e.preventDefault(); e.stopPropagation();
          startDrag(e, mv, sp);
        });
        mv.addEventListener('touchstart', function(e){
          e.stopPropagation(); startDrag(e, mv, sp);
        }, { passive: false });
        mv.addEventListener('contextmenu', function(e){
          e.preventDefault(); togglePanel(sp);
        });
      }
    });
  }

  window.addEventListener('mousemove', moveDrag);
  window.addEventListener('touchmove', moveDrag, { passive: false });
  window.addEventListener('mouseup', endDrag);
  window.addEventListener('touchend', endDrag);

  if (document.readyState === 'loading')
    document.addEventListener('DOMContentLoaded', attach);
  else attach();

  var mo = new MutationObserver(attach);
  mo.observe(document.documentElement, { childList: true, subtree: true });

  // Re-apply persisted positions supplied by MAUI via window.__splitPanePositions
  function applyPersistedPositions(){
    var map = window.__splitPanePositions;
    if (!map) return;
    Object.keys(map).forEach(function(id){
      var el = document.getElementById(id);
      if (!el) return;
      var p = map[id];
      if (p && (p.left || p.top)) {
        if (p.left) el.style.left = p.left;
        if (p.top)  el.style.top  = p.top;
      }
    });
  }
  if (document.readyState === 'loading')
    document.addEventListener('DOMContentLoaded', applyPersistedPositions);
  else applyPersistedPositions();
  window.__applySplitPanePositions = applyPersistedPositions;
})();
";

        // ============================================================
        // Splitpane controller stubs (called by the page's inline onclick)
        // ============================================================
        private const string SplitPaneControllerJs = @"
window.BY_SplitPaneATController = {
  removeSplitPaneFrame: function(id){
    var el = document.getElementById(id);
    if (!el) return;
    var info = window.getSplitPaneInfo ? window.getSplitPaneInfo(el) : { id: id };
    try { console.log('[reader] removeSplitPaneFrame', info); } catch(e){}
    try { if (window.parent && window.parent !== window) {
      window.parent.postMessage({ type: 'splitpane:delete', payload: info }, '*'); } } catch(e){}
    try { window.location.hash = 'splitpane-delete:' + encodeURIComponent(JSON.stringify(info)); } catch(e){}
    if (el.parentNode) el.parentNode.removeChild(el);
  },
  validateSplitRatio: function(id){
    var el = document.getElementById('BY_SplitPaneRatio_'+id);
    if (!el) return;
    var txt = (el.innerText || '').replace('%','').trim();
    var n = parseInt(txt, 10); if (isNaN(n)) n = 50;
    if (n < 1) n = 1; if (n > 99) n = 99;
    el.innerText = n + '%';
  },
  setTooltips: function(id){
    var el = document.getElementById('BY_SplitPaneTooltips_'+id);
    var sp = document.getElementById(id);
    if (!el || !sp) return;
    var txt = (el.innerText || '').trim();
    sp.setAttribute('data-tooltip', txt);
    var img = sp.querySelector('img');
    if (img && txt) img.setAttribute('title', txt);
  }
};
window.BY_SplitPaneController = {
  showSplitPanePage: function(id){
    var sp = document.getElementById(id);
    if (!sp) return;
    var info = window.getSplitPaneInfo ? window.getSplitPaneInfo(sp) : { id: id };
    try { console.log('[reader] showSplitPanePage', info); } catch(e){}
    try { if (window.parent && window.parent !== window) {
      window.parent.postMessage({ type: 'splitpane:open', payload: info }, '*'); } } catch(e){}
    try { window.location.hash = 'splitpane-open:' + encodeURIComponent(JSON.stringify(info)); } catch(e){}
  }
};
(function(){
  window.getSplitPaneInfo = function(pane){
    var id = pane.id;
    function t(x){ return (document.getElementById(x)||{}).innerText || ''; }
    return {
      id: id,
      bookUID: t('BY_SplitPaneBookUID_'+id) || pane.getAttribute('data-book-uid') || '',
      section: t('BY_SplitPaneSectionNum_'+id) || pane.getAttribute('data-section') || '',
      unit:    t('BY_SplitPaneUnitNum_'+id)    || pane.getAttribute('data-unit')    || '',
      step:    t('BY_SplitPaneStepNum_'+id)    || pane.getAttribute('data-step')    || '',
      split:   t('BY_SplitPaneRatio_'+id)      || pane.getAttribute('data-split')   || '50%',
      tooltip: t('BY_SplitPaneTooltips_'+id)   || pane.getAttribute('data-tooltip') || ''
    };
  };
})();
";

        // ============================================================
        // Logging
        // ============================================================
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
                lock (_logLock) { File.AppendAllText(logFile, logMessage + Environment.NewLine); }
                System.Diagnostics.Debug.WriteLine(logMessage);
                if (message.Length < 80) OnStatusChanged?.Invoke(this, message);
                else OnStatusChanged?.Invoke(this, message.Substring(0, 77) + "...");
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
        public event EventHandler<bool> OnRedAnswersToggled;

        // Fired when the WebView reports an ext:* event (drag, open, delete).
        // Args: (type, jsonPayload). Consumer = BookViewerPage.
        public event EventHandler<(string type, string payloadJson)> OnExtMessage;

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
        public bool RedAnswersOn => _redAnswersOn;

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

        public void SetRedAnswers(bool show)
        {
            _redAnswersOn = show;
            OnRedAnswersToggled?.Invoke(this, show);
        }

        // Called by BookViewerPage when it receives an ext:* hash from the WebView.
        public void HandleExtMessage(string type, string payloadJson)
        {
            try
            {
                if (type == "ext:reposition" || type == "splitpane:reposition")
                {
                    var id = ExtractJsonString(payloadJson, "id");
                    var left = ExtractJsonString(payloadJson, "left");
                    var top = ExtractJsonString(payloadJson, "top");
                    if (!string.IsNullOrEmpty(id))
                    {
                        _splitPanePositions[id] = (ParsePx(left), ParsePx(top));
                        Log($"Reposition {id} -> left={left} top={top}");
                    }
                }
                OnExtMessage?.Invoke(this, (type, payloadJson));
            }
            catch (Exception ex)
            {
                Log($"HandleExtMessage error: {ex.Message}");
            }
        }

        private static double ParsePx(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim();
            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 2);
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
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
            if (_stepFileToIndex.TryGetValue(name, out int idx)) return idx;
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
                    if (_stepFileToIndex.TryGetValue(composite, out int idx)) return idx;
                    if (_stepFileToIndex.TryGetValue(stepFile, out int idx2)) return idx2;
                }
            }
            return -1;
        }

        public int GetFirstIndexOfSection(string sectionId)
        {
            if (string.IsNullOrEmpty(sectionId)) return -1;
            if (_sectionFirstIndex.TryGetValue(sectionId, out int idx)) return idx;
            return -1;
        }

        // ============================================================
        // Load book
        // ============================================================
        public async Task<bool> LoadBookAsync(string folderPath)
        {
            try
            {
                Log($"=== LOADING BOOK from: {folderPath} ===");
                Log($"BookService log file: {GetLogFilePath()}");
                _currentBookPath = folderPath;

                _tempFolder = Path.Combine(FileSystem.CacheDirectory, "BookViewer", $"temp_{Guid.NewGuid().ToString().Substring(0, 8)}");
                Directory.CreateDirectory(_tempFolder);

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
                    _currentBookUid = bookIdMatch.Groups[1].Value;

                var content = await File.ReadAllTextAsync(bookXmlPath);
                Log($"book.xml loaded, size: {content.Length} bytes");

                if (string.IsNullOrEmpty(_currentBookUid))
                {
                    var uidMatch = Regex.Match(content, @"id=""([^""]+)""");
                    if (uidMatch.Success) _currentBookUid = uidMatch.Groups[1].Value;
                }

                bool bookXmlDecrypted = false;
                if (_decryptionService.IsEncrypted(content))
                {
                    if (string.IsNullOrEmpty(_currentBookUid))
                    {
                        OnStatusChanged?.Invoke(this, "Could not decrypt book.xml - UID not found");
                        return false;
                    }
                    content = _decryptionService.DecryptBookFile(content, _currentBookUid);
                    await File.WriteAllTextAsync(bookXmlPath, content);
                    bookXmlDecrypted = true;
                }

                _bookTitle = "Unknown Book";
                var titleMatch = Regex.Match(content, @"name=""([^""]+)""");
                if (titleMatch.Success) _bookTitle = titleMatch.Groups[1].Value;

                OnBookLoaded?.Invoke(this, _bookTitle);

                if (bookXmlDecrypted)
                    await DecryptBookFilesAsync(folderPath);

                _pageFiles.Clear();

                var stepsFiles = Directory.GetFiles(folderPath, "steps_*.html", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(folderPath, "step_*.html", SearchOption.AllDirectories))
                    .Where(f => IsPureStepsFile(Path.GetFileName(f)))
                    .Distinct()
                    .ToList();

                _pageFiles = stepsFiles
                    .GroupBy(f => Path.GetDirectoryName(f) ?? "")
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g =>
                        g.Select(f => new { Path = f, Index = ParseStepIndex(Path.GetFileName(f)) })
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
                            if (idx >= 0) pagesContent = pagesContent.Substring(idx + 2);
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

                OnPagesLoaded?.Invoke(this, _pageFiles);

                if (_pageFiles.Count > 0)
                {
                    _currentPageIndex = 0;
                    await LoadPageAsync(0);
                }
                else
                {
                    OnStatusChanged?.Invoke(this, "No pages found in this book");
                    return false;
                }

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

        // ============================================================
        // Decryption
        // ============================================================
        private async Task DecryptBookFilesAsync(string folderPath)
        {
            try
            {
                var htmlFiles = Directory.GetFiles(folderPath, "*.html", SearchOption.AllDirectories).ToList();
                var xmlFiles = Directory.GetFiles(folderPath, "*.xml", SearchOption.AllDirectories).ToList();
                var htmFiles = Directory.GetFiles(folderPath, "*.htm", SearchOption.AllDirectories).ToList();

                int bookKey = _decryptionService.CalculateKey(_currentBookUid);

                int decryptedCount = 0, alreadyDecryptedCount = 0;
                int htmlDecryptedCount = 0, htmlSkippedCount = 0;

                var allXmlAndHtmFiles = new List<string>();
                allXmlAndHtmFiles.AddRange(xmlFiles);
                allXmlAndHtmFiles.AddRange(htmFiles);

                foreach (var file in allXmlAndHtmFiles)
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var fileContent = await File.ReadAllTextAsync(file);
                        if (!_decryptionService.IsEncrypted(fileContent)) { alreadyDecryptedCount++; continue; }
                        var decryptedContent = _decryptionService.DecryptXmlOrHtm(fileContent, fileName);
                        await File.WriteAllTextAsync(file, decryptedContent);
                        decryptedCount++;
                    }
                    catch (Exception ex) { Log($"Error decrypting {Path.GetFileName(file)}: {ex.Message}"); }
                }

                foreach (var file in htmlFiles)
                {
                    try
                    {
                        var fileContent = await File.ReadAllTextAsync(file);
                        // Use IsEncrypted, NOT the class/id heuristic that corrupted skeleton pages.
                        if (!_decryptionService.IsEncrypted(fileContent)) { htmlSkippedCount++; continue; }
                        var decryptedContent = _decryptionService.DecryptWithKey(fileContent, bookKey);
                        await File.WriteAllTextAsync(file, decryptedContent);
                        htmlDecryptedCount++;
                    }
                    catch (Exception ex) { Log($"Error decrypting {Path.GetFileName(file)}: {ex.Message}"); }
                }

                Log($"Decrypt summary: XML/HTM {decryptedCount} decrypted, {alreadyDecryptedCount} plain; HTML {htmlDecryptedCount} decrypted, {htmlSkippedCount} plain");
            }
            catch (Exception ex) { Log($"ERROR in DecryptBookFilesAsync: {ex.Message}"); }
        }

        public async Task DecryptBookAsync()
        {
            if (string.IsNullOrEmpty(_currentBookPath)) { OnStatusChanged?.Invoke(this, "No book loaded"); return; }
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
        // ProcessHtmlContent — skeleton + pane composition
        // ============================================================
        private async Task<string> ProcessHtmlContent(string htmlContent, string filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileName(filePath) ?? "";

                if (IsPureStepsFile(fileName) && fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                    string skeletonHtml = htmlContent;
                    string baseHtml = await ReadSiblingAsync(directory, nameWithoutExt + "_base.html");
                    string oriHtml  = await ReadSiblingAsync(directory, nameWithoutExt + "_ori.html");
                    string redHtml  = await ReadSiblingAsync(directory, nameWithoutExt + "_redAnswer.html");
                    string pageXml  = await ReadSiblingAsync(directory, nameWithoutExt + ".xml");

                    int pageWidth = 1024, pageHeight = 1344;
                    var rezMatch = Regex.Match(skeletonHtml,
                        @"id\s*=\s*[""']rez[""'][^>]*style\s*=\s*[""'][^""']*width\s*:\s*(\d+)px[^""']*height\s*:\s*(\d+)px",
                        RegexOptions.IgnoreCase);
                    if (rezMatch.Success)
                    {
                        if (int.TryParse(rezMatch.Groups[1].Value, out int w)) pageWidth = w;
                        if (int.TryParse(rezMatch.Groups[2].Value, out int h)) pageHeight = h;
                    }
                    else
                    {
                        // Fall back to the skeleton's own style="" width/height
                        var wh = Regex.Match(skeletonHtml,
                            @"id\s*=\s*[""']rez[""'][^>]*style\s*=\s*[""'][^""']*width\s*:\s*(\d+)px[^""']*height\s*:\s*(\d+)px",
                            RegexOptions.IgnoreCase);
                        if (wh.Success)
                        {
                            if (int.TryParse(wh.Groups[1].Value, out int w2)) pageWidth = w2;
                            if (int.TryParse(wh.Groups[2].Value, out int h2)) pageHeight = h2;
                        }
                    }
                    CurrentPageWidth = pageWidth;
                    CurrentPageHeight = pageHeight;

                    var cssBuilder = new StringBuilder();
                    if (!string.IsNullOrEmpty(pageXml))
                    {
                        var cssTags = Regex.Matches(pageXml, @"<css>([^<]+)</css>", RegexOptions.IgnoreCase);
                        var seenCss = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (Match m in cssTags)
                        {
                            var cssName = m.Groups[1].Value.Trim();
                            var cssRel = Path.Combine(directory, cssName);
                            if (!File.Exists(cssRel)) continue;
                            if (!seenCss.Add(cssRel)) continue;
                            var cssText = await File.ReadAllTextAsync(cssRel);
                            cssBuilder.AppendLine(await InlineFontCssAsync(cssText, directory));
                        }
                    }

                    string fontCss = cssBuilder.Length > 0
                        ? cssBuilder.ToString()
                        : await GetFontCssWithEmbeddedFonts(directory);

                    skeletonHtml = await RewriteRelativeUrls(skeletonHtml, directory);
                    baseHtml     = await RewriteRelativeUrls(baseHtml,     directory);
                    oriHtml      = await RewriteRelativeUrls(oriHtml,      directory);
                    redHtml      = await RewriteRelativeUrls(redHtml,      directory);

                    skeletonHtml = FillPane(skeletonHtml, "ori",       oriHtml);
                    skeletonHtml = FillPane(skeletonHtml, "base",      baseHtml);
                    skeletonHtml = FillPane(skeletonHtml, "redAnswer", redHtml);

                    return BuildSkeletonOverlayHtml(skeletonHtml, fontCss, pageWidth, pageHeight);
                }

                return htmlContent;
            }
            catch (Exception ex)
            {
                Log($"ERROR in ProcessHtmlContent: {ex.Message}");
                return htmlContent;
            }
        }

        private async Task<string> ReadSiblingAsync(string directory, string name)
        {
            try
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) return "";
                return await File.ReadAllTextAsync(path);
            }
            catch { return ""; }
        }

        private static string FillPane(string skeleton, string paneClass, string content)
        {
            if (string.IsNullOrEmpty(skeleton) || content == null) return skeleton;
            var pattern = @"<pane\s+class=""" + Regex.Escape(paneClass) + @"""([^>]*)></pane>";
            return Regex.Replace(skeleton, pattern, m =>
                "<pane class=\"" + paneClass + "\"" + m.Groups[1].Value + ">" + content + "</pane>",
                RegexOptions.IgnoreCase);
        }

        // ============================================================
        // BuildSkeletonOverlayHtml — wraps skeleton, adds runtime JS + CSS
        // ============================================================
        private string BuildSkeletonOverlayHtml(string skeletonHtml, string fontCss, int pageWidth, int pageHeight)
        {
            double zoom = _currentZoom;
            string surround = SurroundColor;

            // Serialize persisted splitpane positions so the dragger JS can
            // reapply them on load.
            var posJson = SerializeSplitPanePositions();

            return $@"<!DOCTYPE html>
<html>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes'>
<title>Page</title>
<style>
{fontCss}
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
html, body {{
    width: 100%; height: 100%; overflow: auto;
    background: {surround};
    -webkit-font-smoothing: antialiased;
}}
body {{ display: flex; justify-content: center; align-items: flex-start; min-height: 100vh; }}
.page-wrapper {{ display: flex; justify-content: center; align-items: flex-start; padding: 0; margin: 0; }}
.page-container {{
    position: relative;
    width: {pageWidth}px; height: {pageHeight}px;
    flex-shrink: 0; background: #ffffff;
    box-shadow: 0 0 20px rgba(0,0,0,0.15);
    overflow: hidden; border-radius: 2px;
    transform: scale({zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)});
    transform-origin: top center;
}}
#rez {{ position: relative; width: {pageWidth}px; height: {pageHeight}px; }}
.bgcls {{ position: absolute; top: 0; left: 0; width: 100%; height: 100%; }}
img.bgcls {{ width: 100%; height: 100%; }}
pane {{ display: block; }}
pane.ori, pane.base, pane.redAnswer {{
    position: absolute; top: 0; left: 0; width: 100%; height: 100%;
}}
pane.base > *, pane.redAnswer > *, pane.ori > * {{ position: absolute; }}
.redbtn {{ cursor: pointer; opacity: .85; }}
.redbtn:hover {{ opacity: 1; }}
.hideBtn {{ display: none; }}
.tbred {{ color: #E40477 !important; }}
.hsgbtn {{ cursor: pointer; }}
pane.redAnswer {{ pointer-events: none; }}
pane.redAnswer .tbred {{ pointer-events: none; }}
hsg {{ pointer-events: auto; }}
splitpane.ext, assessment.ext, exercise.ext {{ pointer-events: auto; }}
splitpane.ext img, assessment.ext img, exercise.ext img {{ cursor: pointer; }}
splitpane.ext .dragger, assessment.ext .dragger, exercise.ext .dragger {{
    cursor: move; user-select: none;
    display: inline-block;
    padding: 1px 4px;
    background: #EEEEEE;
    border: 1px solid #888;
    border-radius: 3px;
    font-size: 11px;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
}}
splitpane.ext .btn_h, splitpane.ext .btn_h_m,
assessment.ext .btn_h, assessment.ext .btn_h_m,
exercise.ext .btn_h, exercise.ext .btn_h_m {{
    display: inline-block; cursor: pointer; padding: 1px 4px;
}}
splitpane.ext .btn_h:hover, splitpane.ext .btn_h_m:hover,
assessment.ext .btn_h:hover, assessment.ext .btn_h_m:hover,
exercise.ext .btn_h:hover, exercise.ext .btn_h_m:hover {{ background: #999; }}
splitpane.ext [contenteditable=""true""],
assessment.ext [contenteditable=""true""],
exercise.ext [contenteditable=""true""] {{
    background: #fff; border: 1px solid #888; padding: 0 2px;
    min-width: 40px; display: inline-block;
}}
</style>
</head>
<body>
<div class='page-wrapper'>
    <div class='page-container'>
        {skeletonHtml}
    </div>
</div>
<script>
window.__splitPanePositions = {posJson};
{RedAnswersRuntimeJs}
{SplitPaneControllerJs}
{DraggerRuntimeJs}
</script>
</body>
</html>";
        }

        private string SerializeSplitPanePositions()
        {
            if (_splitPanePositions.Count == 0) return "{}";
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kvp in _splitPanePositions)
            {
                if (!first) sb.Append(",");
                first = false;
                var l = kvp.Value.left.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var t = kvp.Value.top.ToString(System.Globalization.CultureInfo.InvariantCulture);
                sb.Append("\"").Append(kvp.Key.Replace("\"", "\\\"")).Append("\":")
                  .Append("{\"left\":\"").Append(l).Append("px\",\"top\":\"").Append(t).Append("px\"}");
            }
            sb.Append("}");
            return sb.ToString();
        }

        // ============================================================
        // URL rewriting
        // ============================================================
        private static readonly HashSet<string> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "images", "thumbs", "FONTS", "fonts", "Fonts",
            "LR", "appResources", "audio", "video", "resources",
            "flash", "scripts", "css", "xml", "units", "textSearch",
            "split", "svgIcon", "tools", "activepackicon", "pollingImages"
        };

        private async Task<string> RewriteRelativeUrls(string html, string pageDir)
        {
            if (string.IsNullOrEmpty(html)) return html;
            pageDir = (pageDir ?? "").Replace('\\', '/').TrimEnd('/');

            async Task<string> Resolve(string url)
            {
                if (string.IsNullOrEmpty(url)) return url;
                if (Regex.IsMatch(url, @"^(data:|https?:|//|#|javascript:|mailto:|tel:|blob:)", RegexOptions.IgnoreCase))
                    return url;

                var path = url;
                var hash = "";
                var hIdx = path.IndexOf('#');
                if (hIdx >= 0) { hash = path.Substring(hIdx); path = path.Substring(0, hIdx); }
                var query = "";
                var qIdx = path.IndexOf('?');
                if (qIdx >= 0) { query = path.Substring(qIdx); path = path.Substring(0, qIdx); }

                var clean = path;
                if (clean.StartsWith("./")) clean = clean.Substring(2);
                if (clean.StartsWith("/")) clean = clean.Substring(1);

                var segments = clean.Split('/').Where(s => s.Length > 0).ToList();

                if (segments.Count > 0 && segments[0] == "..")
                {
                    int levels = 0;
                    while (levels < segments.Count && segments[levels] == "..") levels++;
                    var rest = segments.Skip(levels).ToList();

                    if (rest.Count > 0 &&
                        (rest[0].Equals("FONTS", StringComparison.OrdinalIgnoreCase) ||
                         rest[0].Equals("fonts", StringComparison.OrdinalIgnoreCase)))
                        return await ToDataUrlIfPossible(rest, pageDir, query, hash);

                    var baseSegs = pageDir.Split('/').Where(s => s.Length > 0).ToList();
                    for (int i = 0; i < levels && baseSegs.Count > 0; i++) baseSegs.RemoveAt(baseSegs.Count - 1);
                    var combined = baseSegs.Concat(rest).ToList();
                    return await ToDataUrlIfPossible(combined, pageDir, query, hash);
                }

                while (segments.Count > 1)
                {
                    var first = segments[0];
                    if (Regex.IsMatch(first, @"^book_\d+$", RegexOptions.IgnoreCase)) { segments.RemoveAt(0); continue; }
                    if (KnownFolders.Contains(first)) break;
                    var pageLast = pageDir.Split('/').LastOrDefault();
                    if (!string.IsNullOrEmpty(pageLast) && first == pageLast) { segments.RemoveAt(0); continue; }
                    segments.RemoveAt(0);
                }

                var lastSeg = segments.LastOrDefault() ?? "";
                var isSvg = Regex.IsMatch(lastSeg, @"\.svg$", RegexOptions.IgnoreCase);

                if (isSvg)
                {
                    var rootRel = string.Join("/", segments);
                    var rootPath = Path.Combine(_currentBookPath, rootRel);
                    if (File.Exists(rootPath))
                        return await ToDataUrlFromFile(rootPath, query, hash);

                    var pageRel = string.IsNullOrEmpty(pageDir) ? rootRel : pageDir + "/" + rootRel;
                    var pagePath = Path.Combine(_currentBookPath, pageRel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(pagePath))
                        return await ToDataUrlFromFile(pagePath, query, hash);
                }

                return await ToDataUrlIfPossible(segments, pageDir, query, hash);
            }

            async Task<string> ToDataUrlIfPossible(List<string> segments, string pDir, string query, string hash)
            {
                var rel = string.Join("/", segments);
                var full = string.IsNullOrEmpty(pDir) ? rel : pDir + "/" + rel;
                var path = Path.Combine(_currentBookPath, full.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path)) return await ToDataUrlFromFile(path, query, hash);
                return rel + query + hash;
            }

            var attrRe = new Regex(@"\b(src|href)\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            var pieces = new List<string>();
            int last = 0;
            foreach (Match m in attrRe.Matches(html))
            {
                pieces.Add(html.Substring(last, m.Index - last));
                var resolved = await Resolve(m.Groups[2].Value);
                pieces.Add(m.Groups[1].Value + "=\"" + resolved + "\"");
                last = m.Index + m.Length;
            }
            pieces.Add(html.Substring(last));
            var result = string.Concat(pieces);

            var urlRe = new Regex(@"url\(\s*(['""]?)([^'""\)]+)\1\s*\)", RegexOptions.IgnoreCase);
            var urlMatches = urlRe.Matches(result).Cast<Match>().ToList();
            foreach (var m in urlMatches)
            {
                var q = m.Groups[1].Value;
                var raw = m.Groups[2].Value;
                var resolved = await Resolve(raw);
                result = result.Replace(m.Value, "url(" + q + resolved + q + ")");
            }

            return result;
        }

        private async Task<string> ToDataUrlFromFile(string path, string query, string hash)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mime = MimeFor(ext);
                return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            }
            catch { return path + query + hash; }
        }

        private static string MimeFor(string ext) => ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };

        private async Task<string> InlineFontCssAsync(string cssText, string pageDir)
        {
            if (string.IsNullOrEmpty(cssText)) return "";
            var fontIndex = await GetFontIndexAsync();

            var urlRe = new Regex(@"url\(\s*['""]?([^'""\)]+)['""]?\s*\)", RegexOptions.IgnoreCase);
            var matches = urlRe.Matches(cssText).Cast<Match>().ToList();
            var css = cssText;

            foreach (var m in matches)
            {
                var raw = m.Groups[1].Value.Trim();
                if (Regex.IsMatch(raw, @"^(data:|https?:)", RegexOptions.IgnoreCase)) continue;

                var cleaned = raw
                    .Replace("../FONTS/", "").Replace("../fonts/", "")
                    .Replace("./", "").Replace("/", "").Trim();
                var cleanedNoQ = cleaned.Split('?')[0].Split('#')[0];
                var justName = Path.GetFileName(cleanedNoQ);
                var justStem = Path.GetFileNameWithoutExtension(cleanedNoQ);
                var noSpace = justStem.Replace(" ", "").Replace("-", "").Replace("_", "");

                string fullPath = null;
                if (fontIndex.TryGetValue(justName, out var p1)) fullPath = p1;
                else if (fontIndex.TryGetValue(justStem, out var p2)) fullPath = p2;
                else if (fontIndex.TryGetValue(noSpace, out var p3)) fullPath = p3;

                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) continue;

                var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                var mime = MimeFor(ext);
                var bytes = await File.ReadAllBytesAsync(fullPath);
                var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                css = css.Replace(m.Value, "url('" + dataUri + "')");
            }
            return css;
        }

        private Dictionary<string, string> _fontIndexCache;

        private async Task<Dictionary<string, string>> GetFontIndexAsync()
        {
            if (_fontIndexCache != null) return _fontIndexCache;

            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fontsFolder = Path.Combine(_currentBookPath, "FONTS");
            if (!Directory.Exists(fontsFolder)) fontsFolder = Path.Combine(_currentBookPath, "fonts");
            if (!Directory.Exists(fontsFolder)) { _fontIndexCache = index; return index; }

            await Task.Run(() =>
            {
                foreach (var f in Directory.GetFiles(fontsFolder, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".ttf" && ext != ".otf" && ext != ".woff" && ext != ".woff2") continue;
                    var name = Path.GetFileName(f);
                    var stem = Path.GetFileNameWithoutExtension(f);
                    var noSpace = stem.Replace(" ", "").Replace("-", "").Replace("_", "");
                    if (!index.ContainsKey(name)) index[name] = f;
                    if (!index.ContainsKey(stem)) index[stem] = f;
                    if (!index.ContainsKey(noSpace)) index[noSpace] = f;
                }
            });
            _fontIndexCache = index;
            return index;
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
                    var rootFontCss = Path.Combine(_currentBookPath, "font.css");
                    if (File.Exists(rootFontCss)) fontCssPath = rootFontCss;
                }

                if (!string.IsNullOrEmpty(fontCssPath) && File.Exists(fontCssPath))
                {
                    fontCss = await File.ReadAllTextAsync(fontCssPath);
                    fontCss = await InlineFontCssAsync(fontCss, directory);
                }
                else fontCss = await GenerateFontCssFromFiles();

                fontCss += CjkMetricLockCss;
                _fontCache[directory] = fontCss;
                return fontCss;
            }
            catch (Exception ex)
            {
                Log($"Error loading fonts: {ex.Message}");
                return "";
            }
        }

        private const string CjkMetricLockCss = @"

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

        private async Task<string> GenerateFontCssFromFiles()
        {
            try
            {
                var fontCss = new StringBuilder();
                var fontsFolder = Path.Combine(_currentBookPath, "FONTS");
                if (!Directory.Exists(fontsFolder)) fontsFolder = Path.Combine(_currentBookPath, "fonts");
                if (!Directory.Exists(fontsFolder)) return "";

                foreach (var ext in new[] { ".ttf", ".otf", ".woff", ".woff2" })
                {
                    foreach (var fontFile in Directory.GetFiles(fontsFolder, "*" + ext))
                    {
                        try
                        {
                            var fontName = Path.GetFileNameWithoutExtension(fontFile);
                            var fontBytes = await File.ReadAllBytesAsync(fontFile);
                            var fontBase64 = Convert.ToBase64String(fontBytes);
                            var fext = Path.GetExtension(fontFile).ToLower();
                            var format = fext switch
                            {
                                ".ttf" => "truetype",
                                ".otf" => "opentype",
                                ".woff" => "woff",
                                ".woff2" => "woff2",
                                _ => "truetype"
                            };
                            var mime = MimeFor(fext);
                            var dataUri = $"data:{mime};base64,{fontBase64}";

                            fontCss.AppendLine($@"
@font-face {{
    font-family: '{fontName}';
    src: url('{dataUri}') format('{format}');
    font-weight: normal;
    font-style: normal;
    ascent-override: 100%;
    descent-override: 0%;
    line-gap-override: 0%;
}}");
                        }
                        catch { }
                    }
                }

                return fontCss.ToString();
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
                foreach (var name in new[] { $"steps_{stepNumber}.jpg", $"steps_{stepNumber}.png",
                                             $"step_{stepNumber}.jpg", $"step_{stepNumber}.png" })
                {
                    var imagePath = Path.Combine(imagesPath, name);
                    if (File.Exists(imagePath)) return ConvertImageToBase64HighQuality(imagePath);
                }
            }
            return GetPlaceholderImage();
        }

        private string GetPlaceholderImage()
            => "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='1024' height='1309'%3E%3Crect width='1024' height='1309' fill='%23ffffff'/%3E%3C/svg%3E";

        private string ConvertImageToBase64HighQuality(string imagePath)
        {
            try
            {
                if (_imageCache.TryGetValue(imagePath, out var cached)) return cached;
                var bytes = File.ReadAllBytes(imagePath);
                var ext = Path.GetExtension(imagePath).ToLower();
                var mime = MimeFor(ext);
                var result = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                _imageCache[imagePath] = result;
                return result;
            }
            catch { return GetPlaceholderImage(); }
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
                        if (File.Exists(alt)) { redAnswerPath = alt; break; }
                }

                if (!File.Exists(redAnswerPath)) return "";

                string content = await File.ReadAllTextAsync(redAnswerPath);
                content = Regex.Replace(content, @"display\s*:\s*none\s*;?", "", RegexOptions.IgnoreCase);
                content = Regex.Replace(content, @"visibility\s*:\s*hidden\s*;?", "", RegexOptions.IgnoreCase);

                var bodyMatch = Regex.Match(content, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                if (bodyMatch.Success) content = bodyMatch.Groups[1].Value;

                string extractedContent = "";
                string classPattern = answerType == "teacherNotes" ? "tbnote" : "sa";

                var matches = Regex.Matches(content,
                    @"<[^>]*class\s*=\s*[""'][^""']*" + classPattern + @"[^""']*[""'][^>]*>[\s\S]*?</[^>]*>",
                    RegexOptions.IgnoreCase);

                foreach (Match match in matches) extractedContent += match.Value + "\n";
                if (string.IsNullOrEmpty(extractedContent) && answerType == "studentAnswers")
                    extractedContent = content;

                return extractedContent;
            }
            catch { return ""; }
        }

        public void NavigatePrevious() { if (_currentPageIndex > 0) _ = LoadPageAsync(_currentPageIndex - 1); }
        public void NavigateNext() { if (_currentPageIndex < _pageFiles.Count - 1) _ = LoadPageAsync(_currentPageIndex + 1); }

        public void ToggleAnswers(bool show)
        {
            OnToggleAnswersRequested?.Invoke(this, show);
            OnStatusChanged?.Invoke(this, show ? "Answers shown" : "Answers hidden");
        }

        public async Task<string> BuildPageHtmlForRenderAsync(
            string filePath, bool includeAnswers, bool includeTeacherNotes, bool includeStudentAnswers)
        {
            var rawHtml = await File.ReadAllTextAsync(filePath);
            return await ProcessHtmlContent(rawHtml, filePath);
        }
    }
}
