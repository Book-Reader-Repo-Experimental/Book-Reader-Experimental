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

                    int rawRedbtnCount = Regex.Matches(html, @"class=""[^""]*\bredbtn\b", RegexOptions.IgnoreCase).Count;
                    int rawHas2btnCount = Regex.Matches(html, @"class=""[^""]*\bhas2btn\b", RegexOptions.IgnoreCase).Count;
                    int rawSvgCount = Regex.Matches(html, @"<svg\b[^>]*\bclass=""[^""]*\btbred\b", RegexOptions.IgnoreCase).Count;
                    DebugLog($"[HTML] Page {i}: len={html.Length}, redbtn={rawRedbtnCount}, has2btn={rawHas2btnCount}, svg={rawSvgCount}");

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

            // 1. Try to extract the exact font from the HTML's CSS
            PdfFont answerFont = null;
            try
            {
                var fontFaceMatch = Regex.Match(html, @"@font-face\s*\{[^}]*\}", RegexOptions.IgnoreCase);
                if (fontFaceMatch.Success)
                {
                    var fontFace = fontFaceMatch.Value;
                    var base64Match = Regex.Match(fontFace, @"base64,([^""']+)\)", RegexOptions.IgnoreCase);
                    if (base64Match.Success)
                    {
                        var fontBytes = Convert.FromBase64String(base64Match.Groups[1].Value);
                        var fontFile = Path.Combine(tempDir, $"page_{pageIndex}_font.ttf");
                        await File.WriteAllBytesAsync(fontFile, fontBytes);
                        
                        // Determine if the font is CID (TrueType) or simple
                        answerFont = PdfFontFactory.CreateFont(fontFile, PdfEncodings.IDENTITY_H);
                        DebugLog($"[INJ] Page {pageIndex}: Extracted custom font.");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[INJ] Page {pageIndex}: Font extraction failed. {ex.Message}");
            }
            if (answerFont == null)
                answerFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

            // 2. Buttons (hsgbtn, has2btn, redbtn)
            var buttonRegex = new Regex(
                @"<img\b(?=[^>]*\bclass=[""']?[^""']*(?:hsgbtn|has2btn|redbtn)[^""']*[""']?)(?=[^>]*\bid=[""']([^""']+)[""'])(?=[^>]*\b(?:linkname|name)=[""']([^""']+)[""'])[^>]*>",
                RegexOptions.IgnoreCase);
            var buttonMatches = buttonRegex.Matches(html);

            // 3. SVG circles (red outlines)
            var svgRegex = new Regex(
                @"<svg\b(?=[^>]*\bclass=[""'][^""']*\btbred\b[^""']*[""'])(?=[^>]*\bname=[""']([^""']+)[""'])[^>]*>[\s\S]*?</svg>",
                RegexOptions.IgnoreCase);
            var svgMatches = svgRegex.Matches(html);

            DebugLog($"[INJ] Page {pageIndex}: buttons={buttonMatches.Count}, svg circles={svgMatches.Count}");

            // 4. Parse text answers
            var answerGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var fragmentRegex = new Regex(
                @"<div\b(?<divtag>[^>]*\bclass=[""'][^""']*\b(?:tbred|tbnote)\b[^""']*[""'][^>]*)>(?<body>[\s\S]*?)</div>",
                RegexOptions.IgnoreCase);

            foreach (Match am in fragmentRegex.Matches(html))
            {
                string divTag = am.Groups["divtag"].Value;
                string body = am.Groups["body"].Value;
                var keyMatch = Regex.Match(body, @"<has2\b[^>]*\bname=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!keyMatch.Success) continue;

                string key = SanitizeFieldName(keyMatch.Groups[1].Value);
                string clean = Regex.Replace(body, "<[^>]+>", " ");
                clean = System.Net.WebUtility.HtmlDecode(clean);
                clean = Regex.Replace(clean, @"\s+", " ").Trim();
                if (string.IsNullOrEmpty(clean)) continue;

                if (!answerGroups.TryGetValue(key, out var list))
                    answerGroups[key] = list = new List<string>();
                list.Add(clean);
            }

            var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in answerGroups)
            {
                bool allShort = kv.Value.All(v => v.Length <= 2);
                answers[kv.Key] = allShort ? string.Concat(kv.Value) : string.Join(" ", kv.Value);
            }

            var fieldsByGroup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void AddToGroup(string groupKey, string fieldId)
            {
                if (!fieldsByGroup.TryGetValue(groupKey, out var list))
                    fieldsByGroup[groupKey] = list = new List<string>();
                list.Add(fieldId);
            }

            int btnCounter = 0;

            // 5. Create answer text fields + toggle buttons
            foreach (Match buttonMatch in buttonMatches)
            {
                var imgId = SanitizeFieldName(buttonMatch.Groups[1].Value);
                var linkName = SanitizeFieldName(buttonMatch.Groups[2].Value);
                var btnClass = buttonMatch.Value.ToLower();

                string answerText = answers.TryGetValue(linkName, out var at) ? at : "";
                bool isCircle = btnClass.Contains("redbtn") && !btnClass.Contains("has2btn") && !btnClass.Contains("hsgbtn");

                if (isCircle && string.IsNullOrEmpty(answerText))
                    continue; // Skip decorative redbtn

                float leftPx = 0f, topPx = 0f;
                var styleMatch = Regex.Match(buttonMatch.Value,
                    @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                float x = leftPx * 0.75f;
                float y = pageHeight - (topPx * 0.75f) - 24f;

                if (isCircle)
                {
                    // Draw a small checkable box for the circle answer
                    var fieldId = $"A_{pageIndex}_{linkName}";
                    var textRect = new Rectangle(x + 4, y + 4, 16, 16); 
                    var chk = new CheckBoxFormFieldBuilder(pdf, fieldId)
                        .SetWidgetRectangle(textRect)
                        .CreateCheckBox();
                    chk.SetValue("Yes");

                    foreach (var w in chk.GetWidgets())
                    {
                        w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(1), new PdfNumber(0), new PdfNumber(0) }));
                        w.SetColor(ColorConstants.RED);
                        w.SetFlags(PdfAnnotation.PRINT);
                    }
                    form.AddField(chk, pdfPage);
                    AddToGroup(linkName, fieldId);
                }
                else
                {
                    // Create the text answer field
                    var fieldId = $"A_{pageIndex}_{linkName}";
                    var textRect = new Rectangle(x + 25, y, 500, 24);
                    var textField = new TextFormFieldBuilder(pdf, fieldId)
                        .SetWidgetRectangle(textRect)
                        .CreateText();
                    textField.SetValue(answerText);
                    textField.SetReadOnly(true);

                    foreach (var w in textField.GetWidgets())
                    {
                        w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                        w.SetFlags(PdfAnnotation.PRINT);

                        var apStream = new PdfFormXObject(textRect);
                        var canvas = new PdfCanvas(apStream, pdf);
                        canvas.BeginText();
                        canvas.SetFontAndSize(answerFont, 10);
                        canvas.SetTextMatrix(5, 5);
                        canvas.SetFillColor(ColorConstants.RED); // RED TEXT
                        canvas.ShowText(answerText ?? "");
                        canvas.EndText();
                        canvas.Release();
                        w.SetAppearance(PdfName.N, apStream.GetPdfObject());
                    }
                    form.AddField(textField, pdfPage);
                    AddToGroup(linkName, fieldId);
                }

                var buttonRect = new Rectangle(x, y, 24, 24);
                var toggleFieldName = $"T_{pageIndex}_{imgId}_{btnCounter++}";
                var buttonField = new PushButtonFormFieldBuilder(pdf, toggleFieldName)
                    .SetWidgetRectangle(buttonRect)
                    .SetCaption("")
                    .CreatePushButton();

                form.AddField(buttonField, pdfPage);
                AddToGroup(linkName, toggleFieldName);
            }

            // 6. Draw SVG circles and wire them to the same group toggles
            foreach (Match svgMatch in svgMatches)
            {
                var svgName = SanitizeFieldName(svgMatch.Groups[1].Value);

                float leftPx = 0f, topPx = 0f, widthPx = 82f, heightPx = 32f;
                var styleMatch = Regex.Match(svgMatch.Value,
                    @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                var widthMatch = Regex.Match(svgMatch.Value, @"\bwidth=[""'](\d+)[""']", RegexOptions.IgnoreCase);
                var heightMatch = Regex.Match(svgMatch.Value, @"\bheight=[""'](\d+)[""']", RegexOptions.IgnoreCase);
                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                if (widthMatch.Success) widthPx = float.Parse(widthMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (heightMatch.Success) heightPx = float.Parse(heightMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

                float cx = leftPx * 0.75f;
                float cy = pageHeight - ((topPx + heightPx) * 0.75f);
                float cw = widthPx * 0.75f;
                float ch = heightPx * 0.75f;

                var circleFieldName = $"C_{pageIndex}_{svgName}";
                var circleRect = new Rectangle(cx, cy, cw, ch);

                var apStream = new PdfFormXObject(circleRect);
                var canvas = new PdfCanvas(apStream, pdf);
                canvas.SetStrokeColor(ColorConstants.RED);
                canvas.SetLineWidth(1.5f);
                canvas.Ellipse(cx + 2, cy + 2, cx + cw - 2, cy + ch - 2);
                canvas.Stroke();
                canvas.Release();

                var chk = new CheckBoxFormFieldBuilder(pdf, circleFieldName)
                    .SetWidgetRectangle(circleRect)
                    .CreateCheckBox();
                chk.SetValue("Yes");

                foreach (var w in chk.GetWidgets())
                {
                    w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                    w.SetFlags(PdfAnnotation.PRINT);
                    w.SetAppearance(PdfName.N, apStream.GetPdfObject());
                }
                form.AddField(chk, pdfPage);

                AddToGroup(svgName, circleFieldName);
            }

            // 7. Wire up the JavaScript for all buttons
            foreach (var kv in fieldsByGroup)
            {
                var groupKey = kv.Key;
                var ids = kv.Value;

                var toggleFieldName = $"T_{pageIndex}_" + ids.FirstOrDefault(i => i.StartsWith("T_"))?.Substring(4);

                if (toggleFieldName != null)
                {
                    var buttonField = form.GetField(toggleFieldName) as PdfButtonFormField;
                    if (buttonField != null)
                    {
                        string toggleScript;
                        if (groupKey.Contains("all"))
                        {
                            toggleScript = "var anyVisible = false;\n";
                            foreach (var id in ids)
                                toggleScript += $"var f = this.getField('{id}'); if (f && f.display === 0) anyVisible = true;\n";
                            toggleScript += "var newDisplay = anyVisible ? 1 : 0;\n";
                            foreach (var id in ids)
                                toggleScript += $"var f = this.getField('{id}'); if (f) {{ f.display = newDisplay; var v = f.value; f.value = ''; f.value = v; }}\n";
                        }
                        else
                        {
                            toggleScript = "try {";
                            foreach (var id in ids)
                            {
                                toggleScript += $"var f = this.getField('{id}'); if (f) {{ f.display = (f.display === 1) ? 0 : 1; var v = f.value; f.value = ''; f.value = v; }}";
                            }
                            toggleScript += "} catch(e) {}";
                        }

                        var jsAction = PdfAction.CreateJavaScript(toggleScript);

                        foreach (var w in buttonField.GetWidgets())
                        {
                            w.SetHighlightMode(PdfAnnotation.HIGHLIGHT_PUSH);
                            w.SetFlags(PdfAnnotation.PRINT);
                            w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
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
                    }
                }
            }
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
