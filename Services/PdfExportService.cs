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
                        InjectInteractiveElements(pdf, html, i);
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
        
        private void InjectInteractiveElements(PdfDocument pdf, string html, int pageIndex)
        {
            var form = PdfAcroForm.GetAcroForm(pdf, true);
            if (form == null) return;
        
            var pdfPage = pdf.GetPage(1);
            float pageHeight = pdfPage.GetPageSize().GetHeight();
            var helveticaFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        
            // ---- Toggle buttons (only has2btn/hsgbtn) ----
            var buttonRegex = new Regex(
                @"<img\b(?=[^>]*\bclass=[""']?[^""']*(?:hsgbtn|has2btn)[^""']*[""']?)(?=[^>]*\bid=[""']([^""']+)[""'])(?=[^>]*\b(?:linkname|name)=[""']([^""']+)[""'])[^>]*>",
                RegexOptions.IgnoreCase);
            var buttonMatches = buttonRegex.Matches(html);
            DebugLog($"[INJ] Page {pageIndex}: toggle buttons matched = {buttonMatches.Count}");
            if (buttonMatches.Count == 0) return;
        
            // ---- For each button, gather its fragment divs in DOM order ----
            // The fragment divs are the ones whose inner <has2> matches the button's linkname.
            var fragmentRegex = new Regex(
                @"<div\b(?<divtag>[^>]*\bclass=[""'][^""']*\b(?:tbred|tbnote)\b[^""']*[""'][^>]*)>" +
                @"(?<body>[\s\S]*?)</div>",
                RegexOptions.IgnoreCase);
        
            // Build a dictionary: linkname -> list of fragments
            var fragmentsByKey = new Dictionary<string, List<FragmentInfo>>(StringComparer.OrdinalIgnoreCase);
        
            foreach (Match fm in fragmentRegex.Matches(html))
            {
                string divTag = fm.Groups["divtag"].Value;
                string body = fm.Groups["body"].Value;
        
                // Get the group key from <has2 name="...">
                var keyMatch = Regex.Match(body, @"<has2\b[^>]*\bname=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!keyMatch.Success) continue;
        
                string key = SanitizeFieldName(keyMatch.Groups[1].Value);
        
                // Extract text (strip has2 tags)
                string clean = Regex.Replace(body, "<[^>]+>", " ");
                clean = System.Net.WebUtility.HtmlDecode(clean);
                clean = Regex.Replace(clean, @"\s+", " ").Trim();
                if (string.IsNullOrEmpty(clean)) continue;
        
                // Extract position and width from the div's own style
                float leftPx = 0f, topPx = 0f, widthPx = 0f;
                var styleMatch = Regex.Match(divTag,
                    @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px[^""']*width:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                if (!styleMatch.Success) continue;
        
                leftPx = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                topPx = float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                widthPx = float.Parse(styleMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        
                if (!fragmentsByKey.TryGetValue(key, out var list))
                {
                    list = new List<FragmentInfo>();
                    fragmentsByKey[key] = list;
                }
        
                list.Add(new FragmentInfo
                {
                    Text = clean,
                    LeftPx = leftPx,
                    TopPx = topPx,
                    WidthPx = widthPx
                });
            }
        
            DebugLog($"[INJ] Page {pageIndex}: fragments grouped = {fragmentsByKey.Count}");
            foreach (var kv in fragmentsByKey.Take(3))
                DebugLog($"[INJ]   group '{kv.Key}' -> {kv.Value.Count} fragment(s): '{kv.Value[0].Text}' at ({kv.Value[0].LeftPx}, {kv.Value[0].TopPx})");
        
            // ---- Create answer fields for each fragment, toggle buttons for each button ----
            int btnCounter = 0;
            foreach (Match buttonMatch in buttonMatches)
            {
                var imgId = SanitizeFieldName(buttonMatch.Groups[1].Value);
                var linkName = SanitizeFieldName(buttonMatch.Groups[2].Value);
        
                if (!fragmentsByKey.TryGetValue(linkName, out var fragments) || fragments.Count == 0)
                {
                    DebugLog($"[INJ] Page {pageIndex} {linkName}: no fragments");
                    continue;
                }
        
                // Create one form field per fragment, all toggled by the single button
                var fieldIdsForThisGroup = new List<string>();
                for (int f = 0; f < fragments.Count; f++)
                {
                    var frag = fragments[f];
                    var fieldId = $"A_{pageIndex}_{linkName}_f{f}";
        
                    // Fragment div gives us the exact position and width of the blank
                    float x = frag.LeftPx * 0.75f;
                    float y = pageHeight - (frag.TopPx * 0.75f) - 18f;
                    float w = Math.Max(frag.WidthPx * 0.75f, 40f); // at least 40pt wide
                    float h = 18f;
        
                    var textRect = new Rectangle(x, y, w, h);
                    var textField = new TextFormFieldBuilder(pdf, fieldId)
                        .SetWidgetRectangle(textRect)
                        .CreateText();
        
                    textField.SetValue(frag.Text);
                    textField.SetReadOnly(true);
        
                    foreach (var wg in textField.GetWidgets())
                    {
                        wg.SetBorder(new PdfArray(new PdfNumber[] {
                            new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                        wg.SetFlags(PdfAnnotation.PRINT);
        
                        var apStream = new PdfFormXObject(textRect);
                        var canvas = new PdfCanvas(apStream, pdf);
                        canvas.BeginText();
                        canvas.SetFontAndSize(helveticaFont, 11);
                        canvas.SetTextMatrix(1, 2);
                        canvas.ShowText(frag.Text);
                        canvas.EndText();
                        canvas.Release();
                        wg.SetAppearance(PdfName.N, apStream.GetPdfObject());
                    }
                    form.AddField(textField, pdfPage);
                    fieldIdsForThisGroup.Add(fieldId);
                }
        
                // Create the toggle button at the button's own position
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
        
                var buttonRect = new Rectangle(bx, by, 24, 24);
                var toggleFieldName = $"T_{pageIndex}_{imgId}_{btnCounter++}";
                var buttonField = new PushButtonFormFieldBuilder(pdf, toggleFieldName)
                    .SetWidgetRectangle(buttonRect)
                    .SetCaption("")
                    .CreatePushButton();
        
                // JS toggles ALL fields in this group
                var toggleScript = "try {";
                foreach (var id in fieldIdsForThisGroup)
                {
                    toggleScript += $"var f_{id.Replace("-", "_")} = this.getField('{id}'); " +
                                    $"if (f_{id.Replace("-", "_")}) {{ " +
                                    $"f_{id.Replace("-", "_")}.display = (f_{id.Replace("-", "_")}.display === 1) ? 0 : 1; " +
                                    $"var v = f_{id.Replace("-", "_")}.value; " +
                                    $"f_{id.Replace("-", "_")}.value = ''; " +
                                    $"f_{id.Replace("-", "_")}.value = v; }} ";
                }
                toggleScript += "} catch(e) {}";
        
                var jsAction = PdfAction.CreateJavaScript(toggleScript);
        
                foreach (var wg in buttonField.GetWidgets())
                {
                    wg.SetHighlightMode(PdfAnnotation.HIGHLIGHT_PUSH);
                    wg.SetFlags(PdfAnnotation.PRINT);
                    wg.SetBorder(new PdfArray(new PdfNumber[] {
                        new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                    wg.Put(PdfName.A, jsAction.GetPdfObject());
                    var aaDict = new PdfDictionary();
                    aaDict.Put(PdfName.U, jsAction.GetPdfObject());
                    wg.Put(PdfName.AA, aaDict);
                    var mkDict = new PdfDictionary();
                    mkDict.Put(PdfName.CA, new PdfString(""));
                    wg.GetPdfObject().Put(PdfName.MK, mkDict);
                    var ap = new PdfFormXObject(new Rectangle(0, 0, 24, 24));
                    wg.SetAppearance(PdfName.N, ap.GetPdfObject());
                }
                var fieldAA = new PdfDictionary();
                fieldAA.Put(PdfName.U, jsAction.GetPdfObject());
                buttonField.GetPdfObject().Put(PdfName.AA, fieldAA);
        
                form.AddField(buttonField, pdfPage);
            }
        }
        
        private class FragmentInfo
        {
            public string Text { get; set; } = "";
            public float LeftPx { get; set; }
            public float TopPx { get; set; }
            public float WidthPx { get; set; }
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

    div.tbred, div.tbnote { display: none !important; }
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
