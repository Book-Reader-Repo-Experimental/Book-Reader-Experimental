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
        private readonly Dictionary<string, string> _fontCache = new();
        private readonly Dictionary<string, string> _assetCache = new();
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
        .tbnote { color: #E40477 !important; }
        .tbnote * { color: #E40477 !important; }
        .hsgbtn { cursor: pointer; }
        pane.redAnswer { pointer-events: none; }
        pane.redAnswer .tbred { pointer-events: none; }
        hsg { pointer-events: auto; }
        img.hsgbtn[redanswertype='teacherNotes'], img.redbtn[redanswertype='teacherNotes'], img.has2btn[redanswertype='teacherNotes'] { pointer-events: auto !important; cursor: pointer !important; }
        .copyright { display: none !important; }
        
        /* Hide the splitpane author-tool panel (grey box with move/delete + Book UID / Section / Step fields) */
        tohide,
        tohide *,
        splitpane tohide,
        splitpane tohide * {
            display: none !important;
            visibility: hidden !important;
            width: 0 !important;
            height: 0 !important;
            overflow: hidden !important;
        }
        
        /* Fallback: hide the author-tool anchors by class, in case they escape <tohide> */
        .btn_h, .btn_h_m { display: none !important; visibility: hidden !important; }
        ";
        private const string ReaderJsTemplate = @"
        window.__readerRedAnswersVisible = false;
        window.__readerNotesVisible = false;
        window.__redAnswerOriginalDisplay = new WeakMap();
        window.__lastSplitPaneLog = '';
        
        function rememberDisplay(el){
          if (!window.__redAnswerOriginalDisplay.has(el)) {
            window.__redAnswerOriginalDisplay.set(el, el.style.display || '');
          }
        }
        
        window.__resolveHsgId = function(arg, ev){
          if (typeof arg === 'string') return arg;
          if (arg && arg.id) return arg.id;
          if (arg && arg.tagName && arg.tagName.toLowerCase() === 'hsg') return arg.id;
          var icon = ev && ev.currentTarget;
          if (icon && icon.id) {
            var m = icon.id.match(/^(.*)_img$/);
            if (m) return m[1];
          }
          return null;
        };
        
        window.__toggleByName = function(lid, turnOn){
          if (!lid) return 0;
          if (turnOn) {
            var rp = document.querySelector('pane.redAnswer');
            if (rp && rp.style.display === 'none') rp.style.display = '';
          }
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
        
        window.__setNotes = function(show){
          window.__readerNotesVisible = !!show;
          if (show) {
            var rp = document.querySelector('pane.redAnswer');
            if (rp && rp.style.display === 'none') rp.style.display = '';
          }
          document.querySelectorAll('hsg[redanswertype=""teacherNotes""]').forEach(function(g){
            var turnOn = !!show;
            g.setAttribute('on', turnOn ? 'true' : 'false');
            g.querySelectorAll('hastext[linkname]').forEach(function(h){
              var lid = h.getAttribute('linkname');
              if (lid) window.__toggleByName(lid, turnOn);
            });
          });
        };
        
        window.CH_toggleHSG = function(arg, ev){
          var id = window.__resolveHsgId(arg, ev);
          if (!id) return;
          var g = document.getElementById(id);
          if (!g) return;
          var isOn = g.getAttribute('on') === 'true';
          var turnOn = !isOn;
          g.setAttribute('on', turnOn ? 'true' : 'false');
          if (turnOn) {
            var rp = document.querySelector('pane.redAnswer');
            if (rp && rp.style.display === 'none') rp.style.display = '';
          }
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
        
        // ---- Split-pane controller: move button opens the target book ----
        window.getSplitPaneInfo = function(pane){
          if (!pane) return { id:'', bookUID:'', section:'', unit:'', step:'', page:'', split:'50%', tooltip:'' };
          var id = pane.id || '';

          function trim(s){ return (s == null ? '' : ('' + s)).trim(); }

          function byId(idList){
            for (var i = 0; i < idList.length; i++) {
              var el = document.getElementById(idList[i]);
              if (el) {
                var v = trim(el.innerText || el.textContent);
                if (v) return v;
              }
            }
            return '';
          }

          function byAttr(attrs){
            for (var i = 0; i < attrs.length; i++) {
              var v = trim(pane.getAttribute(attrs[i]));
              if (v) return v;
            }
            return '';
          }

          function bySuffix(suffixes){
            for (var i = 0; i < suffixes.length; i++) {
              var el = pane.querySelector('[id$=""' + suffixes[i] + '""]');
              if (el) {
                var v = trim(el.innerText || el.textContent);
                if (v) return v;
              }
            }
            return '';
          }

          function allPaneText(){
            var out = [];
            if (document.createTreeWalker) {
              var walker = document.createTreeWalker(pane, NodeFilter.SHOW_TEXT, null, false);
              var n;
              while ((n = walker.nextNode())) {
                var t = trim(n.nodeValue);
                if (t) out.push(t);
              }
            }
            return out;
          }

          function extractNumber(haystack, keywords){
            if (!haystack) return '';
            var lower = ('' + haystack).toLowerCase();
            for (var k = 0; k < keywords.length; k++) {
              var kw = keywords[k].toLowerCase();
              var idx = lower.indexOf(kw);
              while (idx >= 0) {
                var after = ('' + haystack).substring(idx + kw.length, idx + kw.length + 30);
                var m = after.match(/(\d{1,6})/);
                if (m) return m[1];
                idx = lower.indexOf(kw, idx + 1);
              }
            }
            return '';
          }

          var bookUID = byId([
              'BY_SplitPaneBookUID_' + id,
              'BY_SplitPaneBookUID'
          ]) || byAttr(['data-book-uid','data-bookuid','bookuid','book-uid'])
             || bySuffix(['BookUID','bookUID','bookuid','book_uid']);

          var section = byId([
              'BY_SplitPaneSectionNum_' + id,
              'BY_SplitPaneSection_' + id,
              'BY_SplitPaneSectionNum'
          ]) || byAttr(['data-section','data-section-num','section'])
             || bySuffix(['SectionNum','Section','section','section_num']);

          var unit = byId([
              'BY_SplitPaneUnitNum_' + id,
              'BY_SplitPaneUnit_' + id,
              'BY_SplitPaneUnitNum'
          ]) || byAttr(['data-unit','data-unit-num','unit'])
             || bySuffix(['UnitNum','Unit','unit','unit_num']);

          var step = byId([
              'BY_SplitPaneStepNum_' + id,
              'BY_SplitPaneStep_' + id,
              'BY_SplitPaneStepNum'
          ]) || byAttr(['data-step','data-step-num','step'])
             || bySuffix(['StepNum','Step','step','step_num']);

          var page = byId([
              'BY_SplitPanePageNum_' + id,
              'BY_SplitPanePage_' + id,
              'BY_SplitPanePageNum'
          ]) || byAttr(['data-page','data-page-num','page'])
             || bySuffix(['PageNum','Page','page','page_num']);

          if (!step && page) step = page;

          if (!step || !section || !bookUID) {
            var joined = allPaneText().join(' ');
            var outer = trim(pane.outerHTML);
            var blobs = [joined, outer];

            if (!step) {
              for (var i = 0; i < blobs.length && !step; i++) {
                step = extractNumber(blobs[i], [
                  'Step #','Step#','Step Number','StepNum','Step',
                  'Page #','Page#','Page Number','PageNum','Page',
                  '\u6B65\u9AA4','\u9801','\u9875'
                ]);
              }
            }
            if (!section) {
              for (var i = 0; i < blobs.length && !section; i++) {
                section = extractNumber(blobs[i], [
                  'Section #','Section#','Section Number','SectionNum','Section'
                ]);
              }
              if (section && !/^s_/i.test(section)) section = 's_' + section;
            }
            if (!bookUID) {
              for (var i = 0; i < blobs.length && !bookUID; i++) {
                bookUID = extractNumber(blobs[i], [
                  'Book UID','BookUID','Book Uid','Book_UID','bookuid'
                ]);
              }
            }
            if (!unit) {
              for (var i = 0; i < blobs.length && !unit; i++) {
                unit = extractNumber(blobs[i], [
                  'Unit #','Unit#','Unit Number','UnitNum','Unit'
                ]);
              }
            }
          }

          var split = byId([
              'BY_SplitPaneRatio_' + id,
              'BY_SplitPaneRatio'
          ]) || byAttr(['data-split','data-ratio'])
             || bySuffix(['Ratio','ratio','Split','split'])
             || '50%';

          var tooltip = byId([
              'BY_SplitPaneTooltips_' + id,
              'BY_SplitPaneTooltips'
          ]) || byAttr(['data-tooltip','title'])
             || bySuffix(['Tooltips','Tooltip','tooltip']);

          if (!section && unit) section = 's_' + unit;

          return {
            id: id,
            bookUID: bookUID,
            section: section,
            unit: unit,
            step: step,
            page: page,
            split: split,
            tooltip: tooltip
          };
        };
        
        window.BY_SplitPaneATController = {
          removeSplitPaneFrame: function(id){
            var el = document.getElementById(id);
            if (!el) return;
            var info = window.getSplitPaneInfo(el);
            try { window.location.href = 'bookviewer://splitpane-delete?bookUID=' + encodeURIComponent(info.bookUID); } catch(e){}
            if (el.parentNode) el.parentNode.removeChild(el);
          },
          validateSplitRatio: function(id){
            var el = document.getElementById('BY_SplitPaneRatio_' + id);
            if (!el) return;
            var n = parseInt((el.innerText || '').replace('%','').trim(), 10);
            if (isNaN(n)) n = 50;
            if (n < 1) n = 1; if (n > 99) n = 99;
            el.innerText = n + '%';
          },
          setTooltips: function(id){
            var el = document.getElementById('BY_SplitPaneTooltips_' + id);
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
            if (!sp) {
              window.__lastSplitPaneLog = 'no element for id=' + id;
              return;
            }
            var info = window.getSplitPaneInfo(sp);
            window.__lastSplitPaneLog = 'info=' + JSON.stringify(info);
            var url = 'bookviewer://splitpane-open'
                    + '?bookUID=' + encodeURIComponent(info.bookUID)
                    + '&section=' + encodeURIComponent(info.section)
                    + '&unit=' + encodeURIComponent(info.unit)
                    + '&step=' + encodeURIComponent(info.step);
            window.__lastSplitPaneLog += ' url=' + url;
            try { window.location.href = url; } catch(e){ window.__lastSplitPaneLog += ' err=' + e; }
          }
        };
        
        window.BY_HtmlController = window.BY_HtmlController || {
          showExerciseView: function(id, poolType){
            var el = document.getElementById(id);
            if (!el) return;
            var gid = '';
            var a = document.getElementById('BY_assessmentQuestion_' + id);
            if (a) gid = (a.innerText || '').trim();
            try {
              window.location.href = 'bookviewer://assessment-open'
                + '?id=' + encodeURIComponent(id)
                + '&poolType=' + encodeURIComponent(poolType || '')
                + '&group=' + encodeURIComponent(gid);
            } catch(e){}
          },
          closeExerciseView: function(){
            try { window.location.href = 'bookviewer://assessment-close'; } catch(e){}
          }
        };
        
        // ---- Force every toggle icon visible and clickable ----
        (function(){
          function makeClickable(){
            document.querySelectorAll('img.redbtn, img.has2btn, img.hsgbtn').forEach(function(b){
              b.classList.remove('hideBtn');
              b.style.display = '';
              b.style.pointerEvents = 'auto';
              b.style.cursor = 'pointer';
              b.style.zIndex = '9999';
            });
            document.querySelectorAll('splitpane.ext img, assessment img, exercise img').forEach(function(b){
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
                _assetCache.Clear();
                _fontCache.Clear();

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

        /// <summary>
        /// Find a page by (section identifier, step number).
        /// The section can be a folder name (e.g. "s_1"), a section id from book.xml,
        /// or just a number. Matches pages where the parent folder corresponds to
        /// the section and the file is steps_N.html (or step_N.html).
        /// </summary>
        public int FindPageIndex(string section, int step)
        {
            if (step <= 0) return -1;
            var wanted = "steps_" + step;
            var wantedAlt = "step_" + step;

            string sectionKey = (section ?? "").Replace('\\', '/').Trim();
            if (sectionKey.Contains('/'))
                sectionKey = sectionKey.Substring(sectionKey.LastIndexOf('/') + 1);

            var acceptableFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(sectionKey))
            {
                acceptableFolders.Add(sectionKey);

                var numMatch = Regex.Match(sectionKey, @"\d+");
                if (numMatch.Success)
                {
                    var num = numMatch.Value;
                    acceptableFolders.Add(num);
                    acceptableFolders.Add("s_" + num);
                    acceptableFolders.Add("section_" + num);
                }
            }

            // PASS 1: match folder + filename
            for (int i = 0; i < _pageFiles.Count; i++)
            {
                var name = Path.GetFileNameWithoutExtension(_pageFiles[i]);
                if (name != wanted && name != wantedAlt) continue;

                var dir = Path.GetFileName(Path.GetDirectoryName(_pageFiles[i]) ?? "");
                if (acceptableFolders.Count > 0 && !acceptableFolders.Contains(dir))
                    continue;

                Log($"[FindPageIndex] PASS1 hit: section='{sectionKey}' step={step} -> idx={i} file={_pageFiles[i]}");
                return i;
            }

            // PASS 2: match folder only — pick closest step
            if (acceptableFolders.Count > 0)
            {
                int bestIdx = -1;
                int bestDelta = int.MaxValue;
                for (int i = 0; i < _pageFiles.Count; i++)
                {
                    var dir = Path.GetFileName(Path.GetDirectoryName(_pageFiles[i]) ?? "");
                    if (!acceptableFolders.Contains(dir)) continue;

                    var name = Path.GetFileNameWithoutExtension(_pageFiles[i]);
                    var m = Regex.Match(name ?? "", @"^steps?_(\d+)$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;

                    if (int.TryParse(m.Groups[1].Value, out int fileStep))
                    {
                        int delta = Math.Abs(fileStep - step);
                        if (delta < bestDelta) { bestDelta = delta; bestIdx = i; }
                    }
                }
                if (bestIdx >= 0)
                {
                    Log($"[FindPageIndex] PASS2 hit: section='{sectionKey}' step={step} -> idx={bestIdx} file={_pageFiles[bestIdx]}");
                    return bestIdx;
                }
            }

            // PASS 3: filename-only fallback
            for (int i = 0; i < _pageFiles.Count; i++)
            {
                var name = Path.GetFileNameWithoutExtension(_pageFiles[i]);
                if (name == wanted || name == wantedAlt)
                {
                    Log($"[FindPageIndex] PASS3 (filename-only) hit: section='{sectionKey}' step={step} -> idx={i} file={_pageFiles[i]}");
                    return i;
                }
            }

            Log($"[FindPageIndex] NO MATCH: section='{sectionKey}' step={step}");
            return -1;
        }
                
        public string FindPoolFile(string groupId)
        {
            if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(_currentBookPath))
                return null;
        
            var candidates = new List<string>
            {
                Path.Combine(_currentBookPath, "ola", groupId + ".html"),
                Path.Combine(_currentBookPath, "pools", groupId + ".html"),
                Path.Combine(_currentBookPath, "pools", groupId, "index.html"),
                Path.Combine(_currentBookPath, "ola", groupId, "index.html"),
                Path.Combine(_currentBookPath, "ola", "q" + groupId + ".html"),
                Path.Combine(_currentBookPath, "ola", "question_" + groupId + ".html"),
            };
        
            if (Directory.Exists(_currentBookPath))
            {
                foreach (var s in Directory.GetDirectories(_currentBookPath, "s_*"))
                {
                    candidates.Add(Path.Combine(s, "ola", groupId + ".html"));
                    candidates.Add(Path.Combine(s, "pools", groupId + ".html"));
                    candidates.Add(Path.Combine(s, "ola", groupId, "index.html"));
                }
            }
        
            foreach (var c in candidates)
                if (File.Exists(c)) { Log($"[pool] found {groupId} at {c}"); return c; }
        
            try
            {
                var hits = Directory.GetFiles(_currentBookPath, groupId + ".html", SearchOption.AllDirectories);
                if (hits.Length > 0) { Log($"[pool] found via search: {hits[0]}"); return hits[0]; }
        
                hits = Directory.GetFiles(_currentBookPath, "*" + groupId + "*.html", SearchOption.AllDirectories);
                if (hits.Length > 0) { Log($"[pool] fuzzy match: {hits[0]}"); return hits[0]; }
            }
            catch { }
        
            Log($"[pool] not found: {groupId}");
            return null;
        }
        
        public async Task<string> BuildPoolHtmlAsync(string poolFilePath, string groupId)
        {
            try
            {
                var raw = await File.ReadAllTextAsync(poolFilePath);
                var poolDir = Path.GetDirectoryName(poolFilePath) ?? _currentBookPath;
        
                if (!Regex.IsMatch(raw, @"<html", RegexOptions.IgnoreCase))
                {
                    raw = "<!DOCTYPE html><html><head>" +
                          "<meta charset='UTF-8'>" +
                          "<meta name='viewport' content='width=device-width, initial-scale=1.0'>" +
                          "<style>body{margin:0;padding:16px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;}</style>" +
                          "</head><body>" + raw + "</body></html>";
                }
        
                raw = InlineAllAssets(raw, poolDir);
        
                raw = raw.Replace("</body>",
                    "<script>" +
                    "window.__assessment_complete = function(){ window.location.href='bookviewer://assessment-complete'; };" +
                    "</script></body>");
        
                return raw;
            }
            catch (Exception ex)
            {
                Log($"BuildPoolHtmlAsync err: {ex.Message}");
                return "<html><body>Error loading activity</body></html>";
            }
        }

        private async Task<string> ProcessHtmlContent(string htmlContent, string filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileName(filePath) ?? "";

                if (!IsPureStepsFile(fileName))
                    return htmlContent;

                string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                string baseHtml = StripAnswerDivs(await ReadIfExists(Path.Combine(directory, nameWithoutExt + "_base.html")));
                string oriHtml  = StripAnswerDivs(await ReadIfExists(Path.Combine(directory, nameWithoutExt + "_ori.html")));
                string redHtml  = await ReadIfExists(Path.Combine(directory, nameWithoutExt + "_redAnswer.html"));
                string fontCss  = await GetFontCssWithEmbeddedFonts(directory);

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

                skeleton = InlineAllAssets(skeleton, directory);

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

        private static string StripAnswerDivs(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;

            html = Regex.Replace(html,
                @"<div\b[^>]*class=""[^""]*\b(?:tbred|tbnote)\b[^""]*""[^>]*>[\s\S]*?</div>",
                "", RegexOptions.IgnoreCase);

            html = Regex.Replace(html,
                @"<svg\b[^>]*class=""[^""]*\btbred\b[^""]*""[^>]*>[\s\S]*?</svg>",
                "", RegexOptions.IgnoreCase);

            return html;
        }

        private string InlineAllAssets(string html, string pageDir)
        {
            if (string.IsNullOrEmpty(html)) return html;

            string FindOnDisk(string raw)
            {
                if (string.IsNullOrEmpty(raw)) return null;
                if (raw.StartsWith("data:") || raw.StartsWith("http") || raw.StartsWith("//") || raw.StartsWith("file://"))
                    return null;

                var clean = raw;
                var q = clean.IndexOfAny(new[] { '?', '#' });
                if (q >= 0) clean = clean.Substring(0, q);
                clean = clean.Replace('\\', '/').TrimStart('.', '/');

                var segments = clean.Split('/').ToList();

                for (int drop = 0; drop <= 3 && drop < segments.Count; drop++)
                {
                    var tail = string.Join("/", segments.Skip(drop));
                    if (string.IsNullOrEmpty(tail)) continue;

                    var c = Path.GetFullPath(Path.Combine(pageDir, tail));
                    if (File.Exists(c)) return c;

                    var up = pageDir;
                    for (int i = 0; i < 2; i++)
                    {
                        up = Path.GetDirectoryName(up) ?? up;
                        c = Path.GetFullPath(Path.Combine(up, tail));
                        if (File.Exists(c)) return c;
                    }

                    c = Path.GetFullPath(Path.Combine(_currentBookPath, tail));
                    if (File.Exists(c)) return c;

                    var fname = Path.GetFileName(tail);
                    c = Path.GetFullPath(Path.Combine(_currentBookPath, "images", fname));
                    if (File.Exists(c)) return c;

                    c = Path.GetFullPath(Path.Combine(pageDir, "images", fname));
                    if (File.Exists(c)) return c;

                    var sects = Directory.Exists(_currentBookPath)
                        ? Directory.GetDirectories(_currentBookPath, "s_*")
                        : Array.Empty<string>();
                    foreach (var s in sects)
                    {
                        c = Path.GetFullPath(Path.Combine(s, "images", fname));
                        if (File.Exists(c)) return c;
                    }
                }

                return null;
            }

            string ToDataUri(string diskPath)
            {
                if (_assetCache.TryGetValue(diskPath, out var cached)) return cached;

                try
                {
                    var bytes = File.ReadAllBytes(diskPath);
                    var ext = Path.GetExtension(diskPath).ToLowerInvariant();
                    var mime = ext switch
                    {
                        ".svg" => "image/svg+xml",
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        ".bmp" => "image/bmp",
                        ".ico" => "image/x-icon",
                        _ => "application/octet-stream"
                    };
                    var uri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                    _assetCache[diskPath] = uri;
                    return uri;
                }
                catch { return null; }
            }

            string Resolve(string raw)
            {
                if (string.IsNullOrEmpty(raw)) return raw;
                if (raw.StartsWith("data:") || raw.StartsWith("http") || raw.StartsWith("//"))
                    return raw;

                var disk = FindOnDisk(raw);
                if (disk == null)
                {
                    Log($"[asset-miss] {raw} (pageDir={pageDir})");
                    return raw;
                }
                var uri = ToDataUri(disk);
                return uri ?? raw;
            }

            html = Regex.Replace(html,
                @"(\bsrc\s*=\s*"")([^""]+)("")",
                m => m.Groups[1].Value + Resolve(m.Groups[2].Value) + m.Groups[3].Value,
                RegexOptions.IgnoreCase);
            html = Regex.Replace(html,
                @"(\bsrc\s*=\s*')([^']+)(')",
                m => m.Groups[1].Value + Resolve(m.Groups[2].Value) + m.Groups[3].Value,
                RegexOptions.IgnoreCase);

            html = Regex.Replace(html,
                @"(\bhref\s*=\s*"")([^""]+)("")",
                m => m.Groups[1].Value + Resolve(m.Groups[2].Value) + m.Groups[3].Value,
                RegexOptions.IgnoreCase);
            html = Regex.Replace(html,
                @"(\bhref\s*=\s*')([^']+)(')",
                m => m.Groups[1].Value + Resolve(m.Groups[2].Value) + m.Groups[3].Value,
                RegexOptions.IgnoreCase);

            html = Regex.Replace(html,
                @"url\(\s*['""]?([^'"")\s]+)['""]?\s*\)",
                m => "url('" + Resolve(m.Groups[1].Value) + "')",
                RegexOptions.IgnoreCase);

            return html;
        }

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

                    fontCss = Regex.Replace(fontCss,
                        @"url\(\s*['""]?([^'"")\s]+)['""]?\s*\)",
                        m =>
                        {
                            var raw = m.Groups[1].Value;
                            if (raw.StartsWith("data:") || raw.StartsWith("http")) return m.Value;

                            var name = Path.GetFileName(raw.Replace('\\', '/'));

                            var candidates = new List<string>();
                            candidates.Add(Path.Combine(_currentBookPath, "FONTS", name));
                            candidates.Add(Path.Combine(_currentBookPath, "fonts", name));
                            candidates.Add(Path.Combine(directory, "FONTS", name));
                            candidates.Add(Path.Combine(directory, "fonts", name));
                            candidates.Add(Path.Combine(directory, name));
                            candidates.Add(Path.Combine(directory, "images", name));
                            candidates.Add(Path.Combine(_currentBookPath, "images", name));
                            candidates.Add(Path.Combine(_currentBookPath, name));
                            if (Directory.Exists(_currentBookPath))
                            {
                                foreach (var s in Directory.GetDirectories(_currentBookPath, "s_*"))
                                {
                                    candidates.Add(Path.Combine(s, "images", name));
                                    candidates.Add(Path.Combine(s, name));
                                }
                            }

                            foreach (var c in candidates)
                            {
                                if (File.Exists(c))
                                {
                                    try
                                    {
                                        var bytes = File.ReadAllBytes(c);
                                        var ext = Path.GetExtension(c).ToLowerInvariant();
                                        var mime = ext switch
                                        {
                                            ".ttf" => "font/ttf",
                                            ".otf" => "font/otf",
                                            ".woff" => "font/woff",
                                            ".woff2" => "font/woff2",
                                            ".svg" => "image/svg+xml",
                                            ".png" => "image/png",
                                            ".jpg" or ".jpeg" => "image/jpeg",
                                            ".gif" => "image/gif",
                                            ".webp" => "image/webp",
                                            _ => "application/octet-stream"
                                        };
                                        var uri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                                        return "url('" + uri + "')";
                                    }
                                    catch { }
                                }
                            }

                            Log($"[css-asset-miss] {raw} (dir={directory})");
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
