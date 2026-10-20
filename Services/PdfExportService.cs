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
            var tempFiles = new List<string>();
            try
            {
                var totalPages = _bookService.PageFiles.Count;
                DebugLog($"[MAIN] Total pages = {totalPages}");
                if (totalPages == 0) { OnError?.Invoke(this, "No pages to export"); return; }

                var tempDir = Path.Combine(Path.GetTempPath(), "BookViewerPdfExport");
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
            }
        }

        private async Task InjectInteractiveElements(PdfDocument pdf, string html, int pageIndex, string tempDir)
        {
            var form = PdfAcroForm.GetAcroForm(pdf, true);
            if (form == null) return;
        
            var pdfPage = pdf.GetPage(1);
            float pageHeight = pdfPage.GetPageSize().GetHeight();
        
            // ---- font (unchanged) ----
            PdfFont answerFont = null;
            try
            {
                var fontFaceMatch = Regex.Match(html, @"@font-face\s*\{[^}]*\}", RegexOptions.IgnoreCase);
                if (fontFaceMatch.Success)
                {
                    var base64Match = Regex.Match(fontFaceMatch.Value, @"base64,([^""']+)\)", RegexOptions.IgnoreCase);
                    if (base64Match.Success)
                    {
                        var fontBytes = Convert.FromBase64String(base64Match.Groups[1].Value);
                        var fontFile = Path.Combine(tempDir, $"page_{pageIndex}_font.ttf");
                        await File.WriteAllBytesAsync(fontFile, fontBytes);
                        answerFont = PdfFontFactory.CreateFont(fontFile, PdfEncodings.IDENTITY_H);
                    }
                }
            }
            catch { }
            if (answerFont == null)
                answerFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        
            iText.Kernel.Colors.Color bookRed = new DeviceRgb(228, 4, 119);
            float pxToPt = 0.75f;
        
            // =====================================================================
            // A single dictionary: groupKey -> list of field descriptors.
            // groupKey is the <has2 name="..."> value (e.g. "has2_5_1", "has2_5_2").
            // Field descriptor carries type (text/shape), rect, payload, isNote.
            // =====================================================================
            var groups = new Dictionary<string, List<FieldSpec>>(StringComparer.OrdinalIgnoreCase);
            var noteGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
            void AddSpec(string groupKey, FieldSpec spec)
            {
                if (!groups.TryGetValue(groupKey, out var list))
                    groups[groupKey] = list = new List<FieldSpec>();
                list.Add(spec);
            }
        
            // ---------- 1) text divs (tbred / tbnote) ----------
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
        
            // ---------- 2) svg shapes (tbred / tbnote) ----------
            var svgRegex = new Regex(
                @"<svg\b(?<tag>[^>]*\bclass=[""'][^""']*\b(?:tbred|tbnote)\b[^""']*[""'][^>]*)>(?<content>[\s\S]*?)</svg>",
                RegexOptions.IgnoreCase);
        
            foreach (Match sm in svgRegex.Matches(html))
            {
                string tag = sm.Groups["tag"].Value;
                string content = sm.Groups["content"].Value;
        
                // group key: prefer the <has2 name="..."> inside content, else the svg's own name
                string groupKey = null;
                var has2Match = Regex.Match(content, @"<has2\b[^>]*\bname=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (has2Match.Success)
                    groupKey = SanitizeFieldName(has2Match.Groups[1].Value);
                else
                {
                    var nameMatch = Regex.Match(tag, @"\bname=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                    if (nameMatch.Success)
                        groupKey = SanitizeFieldName(nameMatch.Groups[1].Value);
                }
                if (groupKey == null) continue;
        
                bool isNote = tag.Contains("tbnote", StringComparison.OrdinalIgnoreCase)
                           || tag.Contains("teacherNotes", StringComparison.OrdinalIgnoreCase);
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
        
            // ---------- 3) buttons ----------
            var buttonRegex = new Regex(
                @"<img\b(?=[^>]*\bclass=[""']?[^""']*(?:hsgbtn|has2btn|redbtn)[^""']*[""']?)(?=[^>]*\bid=[""']([^""']+)[""'])(?=[^>]*\b(?:linkname|name)=[""']([^""']+)[""'])[^>]*>",
                RegexOptions.IgnoreCase);
            var buttonMatches = buttonRegex.Matches(html);
        
            int btnCounter = 0;
        
            // collect the set of group keys present on this page (for "all" buttons)
            var allGroupKeys = groups.Keys.ToList();
            var answerGroupKeys = allGroupKeys.Where(k => !noteGroupKeys.Contains(k)).ToList();
            var noteGroupKeysList = allGroupKeys.Where(k =>  noteGroupKeys.Contains(k)).ToList();
        
            foreach (Match buttonMatch in buttonMatches)
            {
                var imgId = SanitizeFieldName(buttonMatch.Groups[1].Value);
                var linkName = SanitizeFieldName(buttonMatch.Groups[2].Value);
                bool isNoteButton = noteGroupKeys.Contains(linkName)
                                 || buttonMatch.Value.Contains("teacherNotes", StringComparison.OrdinalIgnoreCase)
                                 || buttonMatch.Value.Contains("tbnote", StringComparison.OrdinalIgnoreCase);
        
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
        
                // Which groups does this individual button control?
                var controlledGroups = new List<string>();
        
                // direct group match (e.g. linkName == "has2_5_1")
                if (groups.ContainsKey(linkName))
                    controlledGroups.Add(linkName);
        
                // "all answers" / "all notes" detection:
                //   linkName contains "all" AND (contains "note"/"teacher" for notes, else answers)
                bool isAllButton = linkName.IndexOf("all", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isAllNotes = isAllButton && (
                    linkName.IndexOf("note", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    linkName.IndexOf("teacher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    isNoteButton);
        
                // Create the fields for every controlled group (individual mode)
                var currentButtonFields = new List<string>();
                foreach (var gk in controlledGroups)
                {
                    foreach (var spec in groups[gk])
                    {
                        var fieldId = CreateFieldForSpec(pdf, pdfPage, form, spec,
                            pageIndex, gk, answerFont, bookRed, currentButtonFields.Count);
                        currentButtonFields.Add(fieldId);
                    }
                }
        
                // ---------- build JS ----------
                string toggleScript;
                if (isAllButton)
                {
                    // Toggle EVERY field belonging to the answer (or note) groups on THIS page.
                    // Field names are  A_{page}_{group}_{idx}  /  S_{page}_{group}_{idx}
                    var targetGroups = isAllNotes ? noteGroupKeysList : answerGroupKeys;
                    if (targetGroups.Count == 0)
                    {
                        toggleScript = "app.alert('No fields to toggle');";
                    }
                    else
                    {
                        // Build a matcher list: each group key, prefixed and suffixed to avoid partial matches.
                        var prefixes = targetGroups
                            .Select(g => $"A_{pageIndex}_{g}_")
                            .Concat(targetGroups.Select(g => $"S_{pageIndex}_{g}_"))
                            .ToArray();
        
                        var sb = new System.Text.StringBuilder();
                        sb.Append("try {\n");
                        sb.Append("  var prefs = [");
                        sb.Append(string.Join(",", prefixes.Select(p => $"'{p}'")));
                        sb.Append("];\n");
                        sb.Append("  var anyVisible = false;\n");
                        sb.Append("  for (var i = 0; i < this.numFields; i++) {\n");
                        sb.Append("    var f = this.getField(this.getNthFieldName(i));\n");
                        sb.Append("    if (!f) continue;\n");
                        sb.Append("    var hit = false;\n");
                        sb.Append("    for (var p = 0; p < prefs.length; p++) {\n");
                        sb.Append("      if (f.name.indexOf(prefs[p]) === 0) { hit = true; break; }\n");
                        sb.Append("    }\n");
                        sb.Append("    if (hit && f.display === 0) anyVisible = true;\n");
                        sb.Append("  }\n");
                        sb.Append("  var newDisplay = anyVisible ? 1 : 0;\n");
                        sb.Append("  for (var i = 0; i < this.numFields; i++) {\n");
                        sb.Append("    var f = this.getField(this.getNthFieldName(i));\n");
                        sb.Append("    if (!f) continue;\n");
                        sb.Append("    var hit = false;\n");
                        sb.Append("    for (var p = 0; p < prefs.length; p++) {\n");
                        sb.Append("      if (f.name.indexOf(prefs[p]) === 0) { hit = true; break; }\n");
                        sb.Append("    }\n");
                        sb.Append("    if (!hit) continue;\n");
                        sb.Append("    f.display = newDisplay;\n");
                        sb.Append("    var v = f.value; f.value = ''; f.value = v;\n");
                        sb.Append("  }\n");
                        sb.Append("} catch(e) {}\n");
                        toggleScript = sb.ToString();
                    }
                }
                else
                {
                    // INDIVIDUAL BUTTON: toggle only its own fields
                    var sb = new System.Text.StringBuilder();
                    sb.Append("try {\n");
                    foreach (var id in currentButtonFields)
                        sb.Append($"var f = this.getField('{id}'); if (f) {{ f.display = (f.display === 1) ? 0 : 1; var v = f.value; f.value = ''; f.value = v; }}\n");
                    sb.Append("} catch(e) {}\n");
                    toggleScript = sb.ToString();
                }
        
                // ---------- create pushbutton with JS action ----------
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
        
        // ---- helpers -------------------------------------------------------------
        
        private enum FieldKind { Text, Shape }
        
        private sealed class FieldSpec
        {
            public FieldKind Kind;
            public float X, Y, W, H;
            public string? Text;
            public string? PathD;
            public bool IsNote;
        }
        
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
            }
            form.AddField(textField, pdfPage);
            return fieldId;
        }
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

    div.tbred, div.tbnote, svg.tbred { display: none !important; }
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
