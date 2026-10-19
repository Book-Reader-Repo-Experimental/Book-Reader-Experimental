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

                    int rawRedbtnCount = Regex.Matches(html, @"class=""[^""]*\bredbtn\b", RegexOptions.IgnoreCase).Count;
                    int rawHas2btnCount = Regex.Matches(html, @"class=""[^""]*\bhas2btn\b", RegexOptions.IgnoreCase).Count;
                    DebugLog($"[HTML] Page {i}: len={html.Length}, redbtn={rawRedbtnCount}, has2btn={rawHas2btnCount}");

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

            // 1. Extract Font
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
                        answerFont = PdfFontFactory.CreateFont(fontFile, PdfEncodings.IDENTITY_H);
                    }
                }
            }
            catch { }
            if (answerFont == null)
                answerFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

            // 2. Parse Text Answers
            var fragmentRegex = new Regex(
                @"<div\b(?<divtag>[^>]*\bclass=[""'][^""']*\b(?:tbred|tbnote)\b[^""']*[""'][^>]*)>(?<body>[\s\S]*?)</div>",
                RegexOptions.IgnoreCase);

            var answerData = new Dictionary<string, List<(string text, float x, float y, float w)>>(StringComparer.OrdinalIgnoreCase);

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

                float leftPx = 0f, topPx = 0f, widthPx = 50f;
                var styleMatch = Regex.Match(divTag,
                    @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px[^""']*width:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    widthPx = float.Parse(styleMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                }

                float pxToPt = 0.75f;
                float x = leftPx * pxToPt;
                float y = pageHeight - (topPx * pxToPt) - 18f; 
                float w = Math.Max(widthPx * pxToPt, 40f);

                if (!answerData.TryGetValue(key, out var list))
                    answerData[key] = list = new List<(string, float, float, float)>();
                list.Add((clean, x, y, w));
            }

            // 3. Parse SVG Circles
            var svgRegex = new Regex(
                @"<svg\b(?<tag>[^>]*\bclass=[""'][^""']*\btbred\b[^""']*[""'][^>]*)>(?<content>[\s\S]*?)</svg>",
                RegexOptions.IgnoreCase);

            var svgData = new Dictionary<string, List<(string pathD, float x, float y, float w, float h)>>(StringComparer.OrdinalIgnoreCase);

            foreach (Match sm in svgRegex.Matches(html))
            {
                string tag = sm.Groups["tag"].Value;
                string content = sm.Groups["content"].Value;
                
                var nameMatch = Regex.Match(tag, @"\bname=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!nameMatch.Success) continue;
                string name = SanitizeFieldName(nameMatch.Groups[1].Value);

                var pathMatch = Regex.Match(content, @"<path\b[^>]*\bd=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!pathMatch.Success) continue;
                string pathD = pathMatch.Groups[1].Value;

                float leftPx = 0f, topPx = 0f, widthPx = 82f, heightPx = 32f;
                var styleMatch = Regex.Match(tag,
                    @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                var widthMatch = Regex.Match(tag, @"\bwidth=[""'](\d+)[""']", RegexOptions.IgnoreCase);
                var heightMatch = Regex.Match(tag, @"\bheight=[""'](\d+)[""']", RegexOptions.IgnoreCase);
                
                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                if (widthMatch.Success) widthPx = float.Parse(widthMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (heightMatch.Success) heightPx = float.Parse(heightMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

                float x = leftPx * 0.75f;
                float y = pageHeight - ((topPx + heightPx) * 0.75f);
                float w = widthPx * 0.75f;
                float h = heightPx * 0.75f;

                if (!svgData.TryGetValue(name, out var list))
                    svgData[name] = list = new List<(string, float, float, float, float)>();
                list.Add((pathD, x, y, w, h));
            }

            // 4. Parse Buttons
            var buttonRegex = new Regex(
                @"<img\b(?=[^>]*\bclass=[""']?[^""']*(?:hsgbtn|has2btn|redbtn)[^""']*[""']?)(?=[^>]*\bid=[""']([^""']+)[""'])(?=[^>]*\b(?:linkname|name)=[""']([^""']+)[""'])[^>]*>",
                RegexOptions.IgnoreCase);
            var buttonMatches = buttonRegex.Matches(html);

            var allFieldIds = new List<string>();
            int btnCounter = 0;

            foreach (Match buttonMatch in buttonMatches)
            {
                var imgId = SanitizeFieldName(buttonMatch.Groups[1].Value);
                var linkName = SanitizeFieldName(buttonMatch.Groups[2].Value);

                float leftPx = 0f, topPx = 0f;
                var styleMatch = Regex.Match(buttonMatch.Value,
                    @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                float bx = leftPx * 0.75f;
                float by = pageHeight - (topPx * 0.75f) - 24f;

                // Text fields
                if (answerData.TryGetValue(linkName, out var answersForThisButton))
                {
                    for (int f = 0; f < answersForThisButton.Count; f++)
                    {
                        var ans = answersForThisButton[f];
                        var fieldId = $"A_{pageIndex}_{linkName}_{f}";
                        allFieldIds.Add(fieldId);

                        var textRect = new Rectangle(ans.x, ans.y, ans.w, 18f);
                        var textField = new TextFormFieldBuilder(pdf, fieldId)
                            .SetWidgetRectangle(textRect)
                            .CreateText();

                        textField.SetValue(ans.text);
                        textField.SetReadOnly(true);

                        foreach (var w in textField.GetWidgets())
                        {
                            w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                            w.SetFlags(PdfAnnotation.PRINT);

                            var apStream = new PdfFormXObject(textRect);
                            var canvas = new PdfCanvas(apStream, pdf);
                            canvas.BeginText();
                            canvas.SetFontAndSize(answerFont, 11);
                            canvas.SetTextMatrix(2, 4);
                            canvas.SetFillColor(ColorConstants.RED);
                            canvas.ShowText(ans.text);
                            canvas.EndText();
                            canvas.Release();
                            w.SetAppearance(PdfName.N, apStream.GetPdfObject());
                        }
                        form.AddField(textField, pdfPage);
                    }
                }

                // SVG Shapes (1:1)
                if (svgData.TryGetValue(linkName, out var shapesForThisButton))
                {
                    for (int f = 0; f < shapesForThisButton.Count; f++)
                    {
                        var shape = shapesForThisButton[f];
                        var fieldId = $"S_{pageIndex}_{linkName}_{f}";
                        allFieldIds.Add(fieldId);

                        var shapeRect = new Rectangle(shape.x, shape.y, shape.w, shape.h);
                        
                        var textField = new TextFormFieldBuilder(pdf, fieldId)
                            .SetWidgetRectangle(shapeRect)
                            .CreateText();
                        textField.SetReadOnly(true);

                        foreach (var w in textField.GetWidgets())
                        {
                            w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                            w.SetFlags(PdfAnnotation.PRINT);

                            var apStream = new PdfFormXObject(shapeRect);
                            var canvas = new PdfCanvas(apStream, pdf);
                            
                            canvas.SetStrokeColor(ColorConstants.RED);
                            canvas.SetLineWidth(1.0f);
                            
                            // This is the 1:1 SVG path parser
                            DrawSvgPath(canvas, shape.pathD, shape.x, shape.y, shape.h);
                            canvas.Stroke();
                            canvas.Release();
                            w.SetAppearance(PdfName.N, apStream.GetPdfObject());
                        }
                        form.AddField(textField, pdfPage);
                    }
                }

                // Hitbox
                var buttonRect = new Rectangle(bx, by, 24, 24);
                var toggleFieldName = $"T_{pageIndex}_{imgId}_{btnCounter++}";
                
                var triggerField = new TextFormFieldBuilder(pdf, toggleFieldName)
                    .SetWidgetRectangle(buttonRect)
                    .CreateText();
                triggerField.SetReadOnly(false);

                string toggleScript;
                if (linkName.Contains("all"))
                {
                    toggleScript = "var anyVisible = false;\n";
                    foreach (var id in allFieldIds)
                        toggleScript += $"var f = this.getField('{id}'); if (f && f.display === 0) anyVisible = true;\n";
                    toggleScript += "var newDisplay = anyVisible ? 1 : 0;\n";
                    foreach (var id in allFieldIds)
                        toggleScript += $"var f = this.getField('{id}'); if (f) {{ f.display = newDisplay; var v = f.value; f.value = ''; f.value = v; }}\n";
                }
                else
                {
                    toggleScript = "try {";
                    if (answersForThisButton != null)
                    {
                        for (int f = 0; f < answersForThisButton.Count; f++)
                        {
                            var id = $"A_{pageIndex}_{linkName}_{f}";
                            toggleScript += $"var f = this.getField('{id}'); if (f) {{ f.display = (f.display === 1) ? 0 : 1; var v = f.value; f.value = ''; f.value = v; }}";
                        }
                    }
                    if (shapesForThisButton != null)
                    {
                        for (int f = 0; f < shapesForThisButton.Count; f++)
                        {
                            var id = $"S_{pageIndex}_{linkName}_{f}";
                            toggleScript += $"var f = this.getField('{id}'); if (f) {{ f.display = (f.display === 1) ? 0 : 1; var v = f.value; f.value = ''; f.value = v; }}";
                        }
                    }
                    toggleScript += "} catch(e) {}";
                }

                var jsAction = PdfAction.CreateJavaScript(toggleScript);
                foreach (var w in triggerField.GetWidgets())
                {
                    w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                    w.SetFlags(PdfAnnotation.PRINT);

                    var aaDict = new PdfDictionary();
                    aaDict.Put(PdfName.U, jsAction.GetPdfObject());
                    w.GetPdfObject().Put(PdfName.AA, aaDict);

                    var ap = new PdfFormXObject(new Rectangle(0, 0, 24, 24));
                    w.SetAppearance(PdfName.N, ap.GetPdfObject());
                }
                form.AddField(triggerField, pdfPage);
            }
        }

        // Accurate SVG Path Parser (Handles relative/absolute M, L, C, Z)
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
                                
                                // MoveTo
                                canvas.MoveTo(baseX + currentX * pt, baseY + height - (currentY * pt));
                                upperCmd = 'L'; // Subsequent pairs are LineTo
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
                            
                            // Break if next token is a letter
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
