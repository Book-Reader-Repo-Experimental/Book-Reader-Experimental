using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using iText.Kernel.Geom;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Xobject;
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

        public PdfExportService(BookService bookService)
        {
            _bookService = bookService;
        }

        public async Task ExportBookAsPdfAsync(string outputPath, bool includeAnswers, bool includeTeacherNotes, bool includeStudentAnswers)
        {
            var tempFiles = new List<string>();
            try
            {
                var totalPages = _bookService.PageFiles.Count;
                if (totalPages == 0) { OnError?.Invoke(this, "No pages to export"); return; }

                var tempDir = Path.Combine(Path.GetTempPath(), "BookViewerPdfExport");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                var converterProperties = new ConverterProperties();
                converterProperties.SetFontProvider(new DefaultFontProvider(true, true, true));
                // We manually create form fields in C#, so we don't need iText to parse <input> tags
                converterProperties.SetCreateAcroForm(false);

                var pageSize = new PageSize(768f, 1008f);

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    
                    // USE THE NEW METHOD that preserves answers and buttons in the HTML
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

                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
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

        private static string SanitizeFieldName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            return Regex.Replace(s, @"[^A-Za-z0-9_\-]", "_");
        }
        
        private void InjectInteractiveElements(PdfDocument pdf, string html, int pageIndex)
        {
            var form = PdfAcroForm.GetAcroForm(pdf, true);
            if (form == null) return;
        
            var pdfPage = pdf.GetPage(1);
            float pageHeight = pdfPage.GetPageSize().GetHeight();
            int btnCounter = 0;
        
            var buttonRegex = new Regex(
                @"<img\b(?=[^>]*\bclass=""[^""]*(?:redbtn|hsgbtn|has2btn)[^""]*"")(?=[^>]*\bid=""([^""]+)"")(?=[^>]*\blinkname=""([^""]+)"")[^>]*>",
                RegexOptions.IgnoreCase);
            var matches = buttonRegex.Matches(html);
            if (matches.Count == 0) return;
        
            var answers = new Dictionary<string, string>();
            var answerRegex = new Regex(
                @"<div\b[^>]*class=""[^""]*\b(?:tbred|tbnote)\b[^""]*""([^>]*)>([\s\S]*?)</div>",
                RegexOptions.IgnoreCase);
        
            foreach (Match m in answerRegex.Matches(html))
            {
                var attrs = m.Groups[1].Value;
                var content = m.Groups[2].Value;
                var linkMatch = Regex.Match(attrs, @"linkname=""([^""]+)""", RegexOptions.IgnoreCase);
                if (linkMatch.Success)
                {
                    string cleanText = Regex.Replace(content, "<.*?>", string.Empty).Trim();
                    answers[SanitizeFieldName(linkMatch.Groups[1].Value)] = cleanText;
                }
            }
        
            var allFieldIds = new List<string>();
        
            foreach (Match buttonMatch in matches)
            {
                var imgId = SanitizeFieldName(buttonMatch.Groups[1].Value);
                var linkName = SanitizeFieldName(buttonMatch.Groups[2].Value);
                var fieldId = $"AnswerField_{pageIndex}_{linkName}";
        
                float leftPx = 0f, topPx = 0f;
                var styleMatch = Regex.Match(buttonMatch.Value,
                    @"style=""[^""]*left:\s*([\d\.]+)px[^""]*top:\s*([\d\.]+)px",
                    RegexOptions.IgnoreCase);
                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
        
                float x = leftPx * 0.75f;
                float y = pageHeight - (topPx * 0.75f) - 24f;
        
                // Text Field
                var textRect = new Rectangle(x + 25, y, 250, 24);
                var textField = new TextFormFieldBuilder(pdf, fieldId)
                    .SetWidgetRectangle(textRect)
                    .CreateText();
        
                string answerText = answers.ContainsKey(linkName) ? answers[linkName] : "";
                textField.SetValue(answerText);
        
                // CRITICAL: Read-only forces Acrobat to generate an appearance stream
                // from the value, so the answer text is actually visible.
                textField.SetReadOnly(true);
        
                foreach (var w in textField.GetWidgets())
                {
                    w.SetBorder(new PdfArray(new PdfNumber[] {
                        new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                    w.SetFlags(PdfAnnotation.PRINT);
                    w.SetColor(ColorConstants.BLACK);
                }
                form.AddField(textField, pdfPage);
                allFieldIds.Add(fieldId);
        
                // Button
                var buttonRect = new Rectangle(x, y, 24, 24);
                var toggleFieldName = $"Toggle_{pageIndex}_{imgId}_{btnCounter++}";
                var buttonField = new PushButtonFormFieldBuilder(pdf, toggleFieldName)
                    .SetWidgetRectangle(buttonRect)
                    .SetCaption("")
                    .CreatePushButton();
        
                foreach (var w in buttonField.GetWidgets())
                {
                    w.SetHighlightMode(PdfAnnotation.HIGHLIGHT_PUSH);
                    w.SetFlags(PdfAnnotation.PRINT);
                    w.SetBorder(new PdfArray(new PdfNumber[] {
                        new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                    var ap = new PdfFormXObject(new Rectangle(0, 0, 24, 24));
                    w.SetAppearance(PdfName.N, ap.GetPdfObject());
                }
        
                string toggleScript;
                if (linkName.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                    linkName.Equals("allanswers", StringComparison.OrdinalIgnoreCase))
                {
                    toggleScript = "var anyVisible = false;\n";
                    foreach (var id in allFieldIds)
                        toggleScript += $"var f_{id} = this.getField(\"{id}\"); if (f_{id} && f_{id}.display === 0) anyVisible = true;\n";
                    toggleScript += "var newDisplay = anyVisible ? 1 : 0;\n";
                    foreach (var id in allFieldIds)
                        toggleScript += $"var f_{id} = this.getField(\"{id}\"); if (f_{id}) f_{id}.display = newDisplay;\n";
                }
                else
                {
                    toggleScript =
                        $"var f = this.getField(\"{fieldId}\"); " +
                        $"if (f) {{ f.display = (f.display === 1) ? 0 : 1; }}";
                }
        
                var action = PdfAction.CreateJavaScript(toggleScript);
        
                foreach (var w in buttonField.GetWidgets())
                {
                    w.Put(PdfName.A, action.GetPdfObject());
                    var aaDict = new PdfDictionary();
                    aaDict.Put(PdfName.U, action.GetPdfObject());
                    w.Put(PdfName.AA, aaDict);
                }
                buttonField.SetAdditionalAction(PdfName.U, action);
        
                form.AddField(buttonField, pdfPage);
            }
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

    /* Keep answers hidden in the background so we can overlay the interactive fields */
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
