using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Geom;
using iText.Kernel.Font;
using iText.Kernel.Utils;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Colors;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Path = System.IO.Path;

namespace BookViewer.Services
{
    public class PdfExportService
    {
        private readonly BookService _bookService;

        public event EventHandler<int>? OnProgress;
        public event EventHandler<string>? OnComplete;
        public event EventHandler<string>? OnError;

        private string _debugLogPath = "";

        // Export options (set at the start of ExportBookAsPdfAsync; read in CreateFieldForSpec)
        private bool _optIncludeAnswers;
        private bool _optIncludeNotes;
        private bool _optIncludeStudentAnswers;

        public PdfExportService(BookService bookService)
        {
            _bookService = bookService;
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                _debugLogPath = Path.Combine(desktop, "PDF_Debug.txt");
                File.WriteAllText(_debugLogPath, $"=== PDF Export started {DateTime.Now} ===\n");
            }
            catch { }
        }

        private void DebugLog(string msg)
        {
            try { File.AppendAllText(_debugLogPath, msg + "\n"); } catch { }
        }

        public async Task ExportBookAsPdfAsync(string outputPath, bool includeAnswers, bool includeTeacherNotes, bool includeStudentAnswers)
        {
            _optIncludeAnswers = includeAnswers;
            _optIncludeNotes = includeTeacherNotes;
            _optIncludeStudentAnswers = includeStudentAnswers;

            var tempFiles = new List<string>();
            string tempDir = "";
            try
            {
                var totalPages = _bookService.PageFiles.Count;
                DebugLog($"[MAIN] Total pages = {totalPages}");
                if (totalPages == 0) { OnError?.Invoke(this, "No pages to export"); return; }

                tempDir = Path.Combine(Path.GetTempPath(), "BookViewerPdfExport");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                var converterProperties = new ConverterProperties();
                converterProperties.SetFontProvider(new DefaultFontProvider(true, true, true));
                converterProperties.SetCreateAcroForm(false);

                var pageSize = new PageSize(768f, 1008f);

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    string html = await _bookService.BuildPageHtmlForPdfExportAsync(filePath);

                    html = InjectPdfCss(html);
                    html = await ExtractBase64ImagesAsync(html, tempDir, i);

                    var tempPdfPath = Path.Combine(tempDir, $"page_{i}.pdf");

                    using (var writer = new PdfWriter(tempPdfPath))
                    using (var pdf = new PdfDocument(writer))
                    {
                        pdf.SetDefaultPageSize(pageSize);
                        HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                    }

                    using (var reader = new PdfReader(tempPdfPath))
                    using (var pdf = new PdfDocument(reader, new PdfWriter(tempPdfPath + ".tmp")))
                    {
                        await InjectInteractiveElements(pdf, html, i, tempDir);
                    }

                    File.Delete(tempPdfPath);
                    File.Move(tempPdfPath + ".tmp", tempPdfPath);
                    tempFiles.Add(tempPdfPath);
                }

                OnProgress?.Invoke(this, 100);
                using (var writer = new PdfWriter(outputPath))
                using (var mergedPdf = new PdfDocument(writer))
                {
                    var merger = new PdfMerger(mergedPdf);
                    foreach (var tempPdf in tempFiles)
                    {
                        using (var reader = new PdfReader(tempPdf))
                        using (var sourcePdf = new PdfDocument(reader))
                        {
                            merger.Merge(sourcePdf, 1, sourcePdf.GetNumberOfPages());
                        }
                    }
                }

                try
                {
                    var fi = new FileInfo(outputPath);
                    DebugLog($"[DONE] Output={outputPath}, size={fi.Length} bytes");
                } catch { }

                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
                DebugLog($"[FATAL] {ex}");
                OnError?.Invoke(this, ex.Message);
            }
            finally
            {
                foreach (var file in tempFiles)
                {
                    try { File.Delete(file); } catch { }
                }
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        // =====================================================================
        //  Field model
        // =====================================================================
        private enum FieldKind { Text, Shape }

        private sealed class FieldSpec
        {
            public FieldKind Kind;
            public float X, Y, W, H;
            public string? Text;
            public string? PathD;
            public bool IsNote;
        }

        private async Task InjectInteractiveElements(PdfDocument pdf, string html, int pageIndex, string tempDir)
        {
            var form = PdfAcroForm.GetAcroForm(pdf, true);
            if (form == null) return;

            var pdfPage = pdf.GetPage(1);
            float pageHeight = pdfPage.GetPageSize().GetHeight();

            // ---------------- FONT ----------------
            PdfFont answerFont = await LoadAnswerFontAsync(html, tempDir, pageIndex);

            iText.Kernel.Colors.Color bookRed = new DeviceRgb(228, 4, 119);
            float pxToPt = 0.75f;

            // groupKey (has2_...) -> list of specs (text + shape)
            var groups = new Dictionary<string, List<FieldSpec>>(StringComparer.OrdinalIgnoreCase);
            var noteGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddSpec(string groupKey, FieldSpec spec)
            {
                if (!groups.TryGetValue(groupKey, out var list))
                    groups[groupKey] = list = new List<FieldSpec>();
                list.Add(spec);
            }

            // ---------------- 1) text divs (tbred / tbnote) ----------------
            var fragmentRegex = new Regex(
                @"<div\b(?<divtag>[^>]*\bclass=[""'][^""']*\b(?:tbred|tbnote)\b[^""']*[""'][^>]*)>(?<body>[\s\S]*?)</div>",
                RegexOptions.IgnoreCase);

            foreach (Match am in fragmentRegex.Matches(html))
            {
                string divTag = am.Groups["divtag"].Value;
                string body = am.Groups["body"].Value;

                var keyMatch = Regex.Match(body, @"<has2\b[^>]*\bname=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!keyMatch.Success) continue;
                string groupKey = SanitizeFieldName(keyMatch.Groups[1].Value);

                bool isNote = divTag.Contains("tbnote", StringComparison.OrdinalIgnoreCase)
                           || divTag.Contains("teacherNotes", StringComparison.OrdinalIgnoreCase);
                if (isNote) noteGroupKeys.Add(groupKey);

                string clean = Regex.Replace(body, "<[^>]+>", " ");
                clean = System.Net.WebUtility.HtmlDecode(clean);
                clean = Regex.Replace(clean, @"\s+", " ").Trim();
                if (string.IsNullOrEmpty(clean)) continue;

                float leftPx = 0f, topPx = 0f, widthPx = 50f;
                var styleMatch = Regex.Match(divTag,
                    @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px[^""']*width:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                    widthPx = float.Parse(styleMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                }

                float x = leftPx * pxToPt;
                float y = pageHeight - (topPx * pxToPt) - 18f;
                float w = Math.Max(widthPx * pxToPt, 40f);

                AddSpec(groupKey, new FieldSpec
                {
                    Kind = FieldKind.Text,
                    X = x, Y = y, W = w, H = 18f,
                    Text = clean,
                    IsNote = isNote
                });
            }

            // ---------------- 2) svg shapes (tbred / tbnote) ----------------
            var svgRegex = new Regex(
                @"<svg\b(?<tag>[^>]*\bclass=[""'][^""']*\b(?:tbred|tbnote)\b[^""']*[""'][^>]*)>(?<content>[\s\S]*?)</svg>",
                RegexOptions.IgnoreCase);

            foreach (Match sm in svgRegex.Matches(html))
            {
                string tag = sm.Groups["tag"].Value;
                string content = sm.Groups["content"].Value;

                bool isNote = tag.Contains("tbnote", StringComparison.OrdinalIgnoreCase)
                           || tag.Contains("teacherNotes", StringComparison.OrdinalIgnoreCase);

                var has2Match = Regex.Match(content, @"<has2\b[^>]*\bname=[""']([^""']+)[""']", RegexOptions.IgnoreCase);

                string groupKey;
                if (has2Match.Success)
                {
                    groupKey = SanitizeFieldName(has2Match.Groups[1].Value);
                }
                else if (isNote && noteGroupKeys.Count > 0)
                {
                    groupKey = noteGroupKeys.First();
                }
                else
                {
                    var nameMatch = Regex.Match(tag, @"\bname=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                    if (!nameMatch.Success) continue;
                    groupKey = SanitizeFieldName(nameMatch.Groups[1].Value);
                }

                if (isNote) noteGroupKeys.Add(groupKey);

                var pathMatch = Regex.Match(content, @"<path\b[^>]*\bd=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!pathMatch.Success) continue;
                string pathD = pathMatch.Groups[1].Value;

                float leftPx = 0f, topPx = 0f, widthPx = 82f, heightPx = 32f;
                var styleMatch = Regex.Match(tag,
                    @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                var widthMatch = Regex.Match(tag, @"\bwidth=[""'](\d+(?:\.\d+)?)[""']", RegexOptions.IgnoreCase);
                var heightMatch = Regex.Match(tag, @"\bheight=[""'](\d+(?:\.\d+)?)[""']", RegexOptions.IgnoreCase);

                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                if (widthMatch.Success) widthPx = float.Parse(widthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                if (heightMatch.Success) heightPx = float.Parse(heightMatch.Groups[1].Value, CultureInfo.InvariantCulture);

                float x = leftPx * pxToPt;
                float y = pageHeight - ((topPx + heightPx) * pxToPt);
                float w = widthPx * pxToPt;
                float h = heightPx * pxToPt;

                AddSpec(groupKey, new FieldSpec
                {
                    Kind = FieldKind.Shape,
                    X = x, Y = y, W = w, H = h,
                    PathD = pathD,
                    IsNote = isNote
                });
            }

            // ---------------- 3) buttons ----------------
            var buttonRegex = new Regex(
                @"<img\b(?=[^>]*\bclass=[""']?[^""']*(?:hsgbtn|has2btn|redbtn)[^""']*[""']?)(?=[^>]*\bid=[""']([^""']+)[""'])(?=[^>]*\b(?:linkname|name)=[""']([^""']+)[""'])[^>]*>",
                RegexOptions.IgnoreCase);
            var buttonMatches = buttonRegex.Matches(html);

            int btnCounter = 0;

            var answerGroupKeys = groups.Keys.Where(k => !noteGroupKeys.Contains(k)).ToList();
            var noteGroupKeysList = groups.Keys.Where(k => noteGroupKeys.Contains(k)).ToList();

            DebugLog($"[PAGE {pageIndex}] groups=[{string.Join(",", groups.Keys)}] " +
                     $"answerGroups=[{string.Join(",", answerGroupKeys)}] " +
                     $"noteGroups=[{string.Join(",", noteGroupKeysList)}] " +
                     $"buttons={buttonMatches.Count}");

            foreach (Match buttonMatch in buttonMatches)
            {
                var imgId = SanitizeFieldName(buttonMatch.Groups[1].Value);
                var linkName = SanitizeFieldName(buttonMatch.Groups[2].Value);

                bool isNoteButton = noteGroupKeys.Contains(linkName)
                                 || buttonMatch.Value.Contains("teacherNotes", StringComparison.OrdinalIgnoreCase)
                                 || buttonMatch.Value.Contains("tbnote", StringComparison.OrdinalIgnoreCase);

                bool linkIsConcreteGroup = groups.ContainsKey(linkName);

                // Synthetic "all" buttons injected by BookService
                bool isAllAnswersButton =
                    linkName == "__all_answers__"
                    || (!linkIsConcreteGroup && !isNoteButton);
                bool isAllNotesButton =
                    linkName == "__all_notes__"
                    || (!linkIsConcreteGroup && isNoteButton);

                DebugLog($"[BTN {pageIndex}] id={imgId} linkName={linkName} " +
                         $"concrete={linkIsConcreteGroup} note={isNoteButton} " +
                         $"allAns={isAllAnswersButton} allNotes={isAllNotesButton}");

                float leftPx = 0f, topPx = 0f;
                var styleMatch = Regex.Match(buttonMatch.Value,
                    @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                float bx = leftPx * pxToPt;
                float by = pageHeight - (topPx * pxToPt) - 24f;

                // Create fields for the concrete group this button controls (individual case)
                var currentButtonFields = new List<string>();
                if (linkIsConcreteGroup)
                {
                    foreach (var spec in groups[linkName])
                    {
                        var fid = CreateFieldForSpec(pdf, pdfPage, form, spec,
                            pageIndex, linkName, answerFont, bookRed, currentButtonFields.Count);
                        currentButtonFields.Add(fid);
                    }
                }

                // Build JS
                string toggleScript;

                if (isAllAnswersButton || isAllNotesButton)
                {
                    var targetGroups = isAllNotesButton ? noteGroupKeysList : answerGroupKeys;

                    if (targetGroups.Count == 0)
                    {
                        DebugLog($"[BTN {pageIndex}] '{linkName}' is an ALL button but target group list is empty — skipped");
                        continue;
                    }

                    var prefixes = targetGroups
                        .Select(g => $"A_{pageIndex}_{g}_")
                        .Concat(targetGroups.Select(g => $"S_{pageIndex}_{g}_"))
                        .ToArray();

                    var sb = new StringBuilder();
                    sb.Append("try {\n");
                    sb.Append("  var prefs = [");
                    sb.Append(string.Join(",", prefixes.Select(p => $"'{p}'")));
                    sb.Append("];\n");
                    sb.Append("  function hits(f){ if(!f) return false; for(var p=0;p<prefs.length;p++){ if(f.name.indexOf(prefs[p])===0) return true; } return false; }\n");
                    sb.Append("  var anyHidden = false;\n");
                    sb.Append("  for (var i = 0; i < this.numFields; i++) {\n");
                    sb.Append("    var f = this.getField(this.getNthFieldName(i));\n");
                    sb.Append("    if (hits(f) && f.display === 0) { anyHidden = true; break; }\n");
                    sb.Append("  }\n");
                    sb.Append("  var newDisplay = anyHidden ? 1 : 0;\n");
                    sb.Append("  for (var i = 0; i < this.numFields; i++) {\n");
                    sb.Append("    var f = this.getField(this.getNthFieldName(i));\n");
                    sb.Append("    if (!hits(f)) continue;\n");
                    sb.Append("    f.display = newDisplay;\n");
                    sb.Append("    var v = f.value; f.value = ''; f.value = v;\n");
                    sb.Append("  }\n");
                    sb.Append("} catch(e) {}\n");
                    toggleScript = sb.ToString();
                }
                else if (linkIsConcreteGroup)
                {
                    var sb = new StringBuilder();
                    sb.Append("try {\n");
                    foreach (var id in currentButtonFields)
                        sb.Append($"var f = this.getField('{id}'); if (f) {{ f.display = (f.display === 1) ? 0 : 1; var v = f.value; f.value = ''; f.value = v; }}\n");
                    sb.Append("} catch(e) {}\n");
                    toggleScript = sb.ToString();
                }
                else
                {
                    DebugLog($"[BTN {pageIndex}] '{linkName}' unknown button — skipped");
                    continue;
                }

                // Create pushbutton with JS action
                var buttonRect = new Rectangle(bx, by, 24, 24);
                var toggleFieldName = $"T_{pageIndex}_{imgId}_{btnCounter++}";
                var buttonField = new PushButtonFormFieldBuilder(pdf, toggleFieldName)
                    .SetWidgetRectangle(buttonRect)
                    .SetCaption("")
                    .CreatePushButton();

                var jsAction = PdfAction.CreateJavaScript(toggleScript);
                foreach (var w in buttonField.GetWidgets())
                {
                    w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                    w.SetFlags(PdfAnnotation.PRINT);
                    w.Put(PdfName.A, jsAction.GetPdfObject());
                    var aaDict = new PdfDictionary();
                    aaDict.Put(PdfName.U, jsAction.GetPdfObject());
                    w.Put(PdfName.AA, aaDict);
                    var mkDict = new PdfDictionary();
                    mkDict.Put(PdfName.CA, new PdfString(""));
                    w.GetPdfObject().Put(PdfName.MK, mkDict);
                    var ap = new PdfFormXObject(new Rectangle(0, 0, 24, 24));
                    w.SetAppearance(PdfName.N, ap.GetPdfObject());
                }
                form.AddField(buttonField, pdfPage);
            }
        }

        // =====================================================================
        //  Font loading (skip woff/woff2, prefer ttf/otf, log everything)
        // =====================================================================
        private async Task<PdfFont> LoadAnswerFontAsync(string html, string tempDir, int pageIndex)
        {
            try
            {
                var fontFaceMatch = Regex.Match(html, @"@font-face\s*\{[^}]*\}", RegexOptions.IgnoreCase);
                if (!fontFaceMatch.Success)
                {
                    DebugLog($"[FONT {pageIndex}] no @font-face found");
                }
                else
                {
                    var urlMatches = Regex.Matches(fontFaceMatch.Value,
                        @"url\(\s*['""]?data:(?:font|application)/(?<sub>[a-z0-9\.\-]+);base64,(?<b64>[^'""\)]+)['""]?\s*\)",
                        RegexOptions.IgnoreCase);

                    DebugLog($"[FONT {pageIndex}] @font-face has {urlMatches.Count} data URLs");

                    string chosenB64 = null;
                    string chosenExt = null;

                    foreach (Match um in urlMatches)
                    {
                        var sub = um.Groups["sub"].Value.ToLowerInvariant();
                        DebugLog($"[FONT {pageIndex}]   url subtype='{sub}'");
                        if (sub.Contains("woff")) continue;
                        if (sub.Contains("truetype") || sub == "ttf" ||
                            sub.Contains("opentype") || sub == "otf" || sub.Contains("sfnt"))
                        {
                            chosenB64 = um.Groups["b64"].Value;
                            chosenExt = (sub.Contains("opentype") || sub == "otf") ? "otf" : "ttf";
                            break;
                        }
                    }

                    if (chosenB64 == null)
                    {
                        DebugLog($"[FONT {pageIndex}] no usable ttf/otf URL");
                    }
                    else
                    {
                        var fontBytes = Convert.FromBase64String(chosenB64);
                        var fontFile = Path.Combine(tempDir, $"page_{pageIndex}_font.{chosenExt}");
                        await File.WriteAllBytesAsync(fontFile, fontBytes);
                        DebugLog($"[FONT {pageIndex}] loaded {chosenExt}, {fontBytes.Length} bytes");
                        var font = PdfFontFactory.CreateFont(fontFile, PdfEncodings.IDENTITY_H);

                        try
                        {
                            var bytes = font.ConvertToBytes("農");
                            DebugLog($"[FONT {pageIndex}] glyph '農' byte length = {bytes.Length}");
                        }
                        catch (Exception gex)
                        {
                            DebugLog($"[FONT {pageIndex}] glyph test failed: {gex.Message}");
                        }

                        return font;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[FONT {pageIndex}] LOAD FAILED: {ex.Message}");
            }

            DebugLog($"[FONT {pageIndex}] falling back to Helvetica — CJK text will be INVISIBLE");
            return PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        }

        // =====================================================================
        //  Field creation
        // =====================================================================
        private string CreateFieldForSpec(
            PdfDocument pdf, PdfPage pdfPage, PdfAcroForm form,
            FieldSpec spec, int pageIndex, string groupKey,
            PdfFont answerFont, iText.Kernel.Colors.Color bookRed, int idx)
        {
            string fieldId = (spec.Kind == FieldKind.Text ? "A_" : "S_")
                           + $"{pageIndex}_{groupKey}_{idx}";
            var rect = new Rectangle(spec.X, spec.Y, spec.W, spec.H);
        
            var textField = new TextFormFieldBuilder(pdf, fieldId)
                .SetWidgetRectangle(rect)
                .CreateText();
            textField.SetReadOnly(true);
        
            bool shouldBeVisible =
                spec.IsNote
                    ? _optIncludeNotes
                    : (_optIncludeAnswers || _optIncludeStudentAnswers);
        
            var fontPdfObj = answerFont.GetPdfObject();
            if (!fontPdfObj.IsIndirect())
                fontPdfObj = fontPdfObj.MakeIndirect(pdf);
        
            foreach (var w in textField.GetWidgets())
            {
                w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                w.SetFlags(PdfAnnotation.PRINT);
        
                var apStream = new PdfFormXObject(rect);
                var canvas = new PdfCanvas(apStream, pdf);
        
                if (spec.Kind == FieldKind.Text)
                {
                    canvas.BeginText();
                    canvas.SetFontAndSize(answerFont, 11);
                    canvas.SetTextMatrix(2, 4);
                    canvas.SetFillColor(bookRed);
                    canvas.ShowText(spec.Text ?? "");
                    canvas.EndText();
        
                    string fn = "F1";
                    try
                    {
                        var bytes = canvas.GetContentStream().GetBytes();
                        var txt = System.Text.Encoding.ASCII.GetString(bytes);
                        var mm = Regex.Match(txt, @"/([A-Za-z0-9]+)\s+11\s+Tf");
                        if (mm.Success) fn = mm.Groups[1].Value;
                    }
                    catch { }
        
                    var resources = new PdfDictionary();
                    var fontDict = new PdfDictionary();
                    fontDict.Put(new PdfName(fn), fontPdfObj);
                    resources.Put(PdfName.Font, fontDict);
                    apStream.GetPdfObject().Put(PdfName.Resources, resources);
                }
                else
                {
                    canvas.SetStrokeColor(bookRed);
                    canvas.SetLineWidth(1.0f);
                    DrawSvgPath(canvas, spec.PathD ?? "", spec.X, spec.Y, spec.H);
                    canvas.Stroke();
                }
        
                canvas.Release();
                w.SetAppearance(PdfName.N, apStream.GetPdfObject());
        
                if (!shouldBeVisible)
                    w.SetFlags(w.GetFlags() | PdfAnnotation.HIDDEN);
            }
            form.AddField(textField, pdfPage);
            return fieldId;
        }

        // =====================================================================
        //  SVG path drawing
        // =====================================================================
        private void DrawSvgPath(PdfCanvas canvas, string pathD, float baseX, float baseY, float height)
        {
            try
            {
                var matches = Regex.Matches(pathD, @"([a-zA-Z])|(-?\d*\.?\d+)");
                float currentX = 0, currentY = 0;
                float startX = 0, startY = 0;
                bool hasPoint = false;
                float pt = 0.75f;

                for (int i = 0; i < matches.Count; i++)
                {
                    var token = matches[i].Value;
                    if (char.IsLetter(token[0]))
                    {
                        char cmd = token[0];
                        bool isRel = char.IsLower(cmd);
                        char upperCmd = char.ToUpper(cmd);

                        if (upperCmd == 'Z')
                        {
                            canvas.ClosePath();
                            currentX = startX; currentY = startY;
                            continue;
                        }

                        while (i + 1 < matches.Count && !char.IsLetter(matches[i + 1].Value[0]))
                        {
                            float p1 = ParseFloat(matches[++i].Value);
                            float p2 = ParseFloat(matches[++i].Value);

                            if (upperCmd == 'M')
                            {
                                if (!hasPoint) { startX = p1; startY = p2; hasPoint = true; }
                                if (!isRel) { currentX = p1; currentY = p2; }
                                else { currentX += p1; currentY += p2; }

                                canvas.MoveTo(baseX + currentX * pt, baseY + height - (currentY * pt));
                                upperCmd = 'L';
                            }
                            else if (upperCmd == 'L')
                            {
                                if (!isRel) { currentX = p1; currentY = p2; }
                                else { currentX += p1; currentY += p2; }
                                canvas.LineTo(baseX + currentX * pt, baseY + height - (currentY * pt));
                            }
                            else if (upperCmd == 'C')
                            {
                                float p3 = ParseFloat(matches[++i].Value);
                                float p4 = ParseFloat(matches[++i].Value);
                                float p5 = ParseFloat(matches[++i].Value);
                                float p6 = ParseFloat(matches[++i].Value);

                                float x1 = p1, y1 = p2, x2 = p3, y2 = p4, x3 = p5, y3 = p6;
                                if (isRel)
                                {
                                    x1 += currentX; y1 += currentY;
                                    x2 += currentX; y2 += currentY;
                                    x3 += currentX; y3 += currentY;
                                }

                                canvas.CurveTo(
                                    baseX + x1 * pt, baseY + height - (y1 * pt),
                                    baseX + x2 * pt, baseY + height - (y2 * pt),
                                    baseX + x3 * pt, baseY + height - (y3 * pt)
                                );
                                currentX = x3; currentY = y3;
                            }

                            if (i + 1 >= matches.Count || char.IsLetter(matches[i + 1].Value[0])) break;
                        }
                    }
                }
            }
            catch { }
        }

        private float ParseFloat(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        private static string SanitizeFieldName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            return Regex.Replace(s, @"[^A-Za-z0-9_\-]", "_");
        }

        // =====================================================================
        //  CSS + base64 image extraction
        // =====================================================================
        private string InjectPdfCss(string html)
        {
            var css = @"
<style>
    @page { size: 768pt 1008pt; margin: 0 !important; }
    html, body { margin: 0 !important; padding: 0 !important; width: 768pt !important; height: 1008pt !important; overflow: visible !important; background: #fff !important; }
    .page-container { position: relative !important; width: 768pt !important; height: 1008pt !important; transform: scale(1) !important; transform-origin: top left !important; margin: 0 !important; }
    .hideBtn { display: inline-block !important; visibility: visible !important; }
    img.redbtn, img.hsgbtn, img.has2btn { display: inline-block !important; visibility: visible !important; opacity: 1 !important; }
    img { max-width: 100% !important; height: auto !important; }

    div.tbred, div.tbnote, svg.tbred, svg.tbnote { display: none !important; }
</style>
";
            if (html.Contains("<head>"))
                return html.Replace("<head>", "<head>" + css);
            else
                return css + html;
        }

        private async Task<string> ExtractBase64ImagesAsync(string html, string tempDir, int pageIndex)
        {
            var matches = Regex.Matches(html,
                @"src=""(data:image/([^;]+);base64,([^""]+))""",
                RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                var dataUri = match.Groups[1].Value;
                var ext = match.Groups[2].Value;
                var base64 = match.Groups[3].Value;

                var fileName = $"img_{pageIndex}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.{ext}";
                var filePath = Path.Combine(tempDir, fileName);

                try
                {
                    await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(base64));
                    html = html.Replace(dataUri, filePath.Replace('\\', '/'));
                }
                catch { }
            }
            return html;
        }
    }
}
