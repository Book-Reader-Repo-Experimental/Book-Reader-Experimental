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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Path = System.IO.Path;   // ← ADD THIS LINE: resolves Path conflict with iText.Kernel.Geom.Path

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
            // CANARY 1: Prove the method runs
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            File.WriteAllText(Path.Combine(desktop, "PDF_RAN.txt"), "New logic executed at " + DateTime.Now.ToString());

            var tempFiles = new List<string>();
            try
            {
                var totalPages = _bookService.PageFiles.Count;
                if (totalPages == 0) { OnError?.Invoke(this, "No pages to export"); return; }

                var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BookViewerPdfExport");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                var converterProperties = new ConverterProperties();
                converterProperties.SetFontProvider(new DefaultFontProvider(true, true, true));
                // CRITICAL: keep <input> elements as real AcroForm fields instead of flattening them
                converterProperties.SetCreateAcroForm(true);

                var pageSize = new PageSize(768f, 1008f);

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    string html = await _bookService.BuildPageHtmlForRenderAsync(
                        filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    html = InjectAnswerFormFields(html, i);
                    html = InjectPdfCss(html);
                    html = await ExtractBase64ImagesAsync(html, tempDir, i);

                    var tempPdfPath = System.IO.Path.Combine(tempDir, $"page_{i}.pdf");

                    using (var writer = new PdfWriter(tempPdfPath))
                    using (var pdf = new PdfDocument(writer))
                    {
                        pdf.SetDefaultPageSize(pageSize);
                        HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                    }

                    using (var reader = new PdfReader(tempPdfPath))
                    using (var pdf = new PdfDocument(reader, new PdfWriter(tempPdfPath + ".tmp")))
                    {
                        InjectToggleActions(pdf, html, i);

                        var form = PdfAcroForm.GetAcroForm(pdf, false);
                        if (form != null)
                            form.SetNeedAppearances(true);
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

                    var mergedForm = PdfAcroForm.GetAcroForm(mergedPdf, false);
                    if (mergedForm != null)
                        mergedForm.SetNeedAppearances(true);
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

        private string InjectAnswerFormFields(string html, int pageIndex)
        {
            var regex = new Regex(
                @"<div\b[^>]*class=""[^""]*\b(?:tbred|tbnote)\b[^""]*""([^>]*)>([\s\S]*?)</div>",
                RegexOptions.IgnoreCase);

            return regex.Replace(html, m =>
            {
                var attributes = m.Groups[1].Value;
                var content = m.Groups[2].Value;

                var linkNameMatch = Regex.Match(attributes, @"linkname=""([^""]+)""", RegexOptions.IgnoreCase);
                var linkName = linkNameMatch.Success
                    ? linkNameMatch.Groups[1].Value
                    : Guid.NewGuid().ToString("N").Substring(0, 6);
                linkName = SanitizeFieldName(linkName);

                var fieldId = $"AnswerField_{pageIndex}_{linkName}";

                var escaped = content
                    .Replace("&", "&amp;")
                    .Replace("\"", "&quot;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");

                return
                    $"<input type=\"text\" id=\"{fieldId}\" name=\"{fieldId}\" " +
                    $"class=\"pdf-answer-field\" value=\"{escaped}\" readonly=\"readonly\" " +
                    $"style=\"display:block; width:100%; height:auto; " +
                    $"border:none; background:transparent; color:#000; " +
                    $"font-family:inherit; font-size:inherit; padding:0; margin:0;\" />";
            });
        }
        
        private void InjectToggleActions(PdfDocument pdf, string html, int pageIndex)
        {
            var form = PdfAcroForm.GetAcroForm(pdf, true);
            if (form == null) return;
        
            // We look for the buttons. We also capture the text inside the answer divs 
            // so we can manually create the text fields without relying on iText's HTML parser.
            var buttonRegex = new Regex(
                @"<img\b(?=[^>]*\bclass=""[^""]*(?:redbtn|hsgbtn|has2btn)[^""]*"")(?=[^>]*\bid=""([^""]+)"")(?=[^>]*\blinkname=""([^""]+)"")[^>]*>",
                RegexOptions.IgnoreCase);
        
            var matches = buttonRegex.Matches(html);
            if (matches.Count == 0) return;
        
            // Create a dictionary of all answers found in the HTML so we can assign them to the fields
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
                    answers[SanitizeFieldName(linkMatch.Groups[1].Value)] = content;
                }
            }
        
            int btnCounter = 0;
            var pdfPage = pdf.GetPage(1);
            float pageHeight = pdfPage.GetPageSize().GetHeight();
        
            foreach (Match buttonMatch in matches)
            {
                var imgId = SanitizeFieldName(buttonMatch.Groups[1].Value);
                var linkName = SanitizeFieldName(buttonMatch.Groups[2].Value);
                var fieldId = $"AnswerField_{pageIndex}_{linkName}";
        
                float leftPx = 0f, topPx = 0f;
                var styleMatch = Regex.Match(buttonMatch.Value, @"style=""[^""]*left:\s*([\d\.]+)px[^""]*top:\s*([\d\.]+)px", RegexOptions.IgnoreCase);
                if (styleMatch.Success)
                {
                    leftPx = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
        
                float x = leftPx * 0.75f;
                float y = pageHeight - (topPx * 0.75f) - 24f;
        
                // 1. CREATE THE TEXT FIELD DIRECTLY IN C#
                var textRect = new Rectangle(x + 30, y, 200, 24); // Place text field next to the button
                var textField = new TextFormFieldBuilder(pdf, fieldId)
                    .SetWidgetRectangle(textRect)
                    .CreateText();
                
                // Set the answer text and make it read-only
                string answerText = answers.ContainsKey(linkName) ? answers[linkName] : "";
                // Strip HTML tags from the answer just in case
                answerText = Regex.Replace(answerText, "<.*?>", string.Empty).Trim();
                
                textField.SetValue(answerText);
                textField.SetReadOnly(true);
                
                // Make the text field borderless so it looks like normal text
                foreach (var w in textField.GetWidgets())
                {
                    w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                    w.SetFlags(PdfAnnotation.PRINT);
                }
                form.AddField(textField, pdfPage);
        
                // 2. CREATE THE INTERACTIVE BUTTON
                var toggleScript =
                    $"var f = this.getField(\"{fieldId}\"); " +
                    $"if (f) {{ f.display = (f.display === 1) ? 0 : 1; }}";
                var action = PdfAction.CreateJavaScript(toggleScript);
        
                var buttonRect = new Rectangle(x, y, 24, 24);
                var toggleFieldName = $"Toggle_{pageIndex}_{imgId}_{btnCounter++}";
        
                var buttonField = new PushButtonFormFieldBuilder(pdf, toggleFieldName)
                    .SetWidgetRectangle(buttonRect)
                    .SetCaption("")
                    .CreatePushButton();
        
                foreach (var w in buttonField.GetWidgets())
                {
                    w.Put(PdfName.A, action.GetPdfObject());
                    w.SetFlags(PdfAnnotation.PRINT);
                    
                    // Make it invisible so it doesn't cover your red button image
                    w.SetBorder(new PdfArray(new PdfNumber[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                    
                    // Give it an empty appearance stream so it is clickable
                    var ap = new PdfFormXObject(new Rectangle(0, 0, 24, 24));
                    w.SetAppearance(PdfName.N, ap.GetPdfObject());
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

    div.tbred, div.tbnote { display: block !important; visibility: visible !important; }

    input.pdf-answer-field {
        display: block !important;
        width: 100% !important;
        height: auto !important;
        border: none !important;
        background: transparent !important;
        color: #000 !important;
        font-family: inherit !important;
        font-size: inherit !important;
        padding: 0 !important;
        margin: 0 !important;
    }
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
                var filePath = System.IO.Path.Combine(tempDir, fileName);

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
