using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using iText.Kernel.Geom;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

                var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BookViewerPdfExport");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                var converterProperties = new ConverterProperties();
                converterProperties.SetFontProvider(new DefaultFontProvider(true, true, true));

                var pageSize = new PageSize(768f, 1008f);

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    string html = await _bookService.BuildPageHtmlForRenderAsync(
                        filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    // 1. Preprocess HTML (Convert hidden answers to form fields)
                    html = InjectAnswerFormFields(html, i);

                    // 2. Inject CSS to enforce layout
                    html = InjectPdfCss(html);

                    // 3. Extract base64 images to disk
                    html = await ExtractBase64ImagesAsync(html, tempDir, i);

                    var tempPdfPath = System.IO.Path.Combine(tempDir, $"page_{i}.pdf");

                    // 4. Generate the unflattened PDF
                    using (var writer = new PdfWriter(tempPdfPath))
                    using (var pdf = new PdfDocument(writer))
                    {
                        pdf.SetDefaultPageSize(pageSize);
                        HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                    }

                    // 5. Re-open and inject the interactive toggle buttons
                    using (var reader = new PdfReader(tempPdfPath))
                    using (var pdf = new PdfDocument(reader, new PdfWriter(tempPdfPath + ".tmp")))
                    {
                        InjectToggleActions(pdf, html, i);
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
        
        private string InjectAnswerFormFields(string html, int pageIndex)
        {
            var regex = new Regex(@"<div\b[^>]*class=""[^""]*\b(?:tbred|tbnote)\b[^""]*""([^>]*)>([\s\S]*?)</div>", RegexOptions.IgnoreCase);
        
            return regex.Replace(html, m =>
            {
                var attributes = m.Groups[1].Value;
                var content = m.Groups[2].Value;
        
                var linkNameMatch = Regex.Match(attributes, @"linkname=""([^""]+)""");
                var linkName = linkNameMatch.Success ? linkNameMatch.Groups[1].Value : Guid.NewGuid().ToString("N").Substring(0, 6);
        
                var fieldId = $"AnswerField_{pageIndex}_{linkName}";
        
                return $"<input type=\"text\" id=\"{fieldId}\" name=\"{fieldId}\" class=\"pdf-answer-field\" value=\"{content.Replace("\"", "&quot;")}\" readonly=\"readonly\" style=\"display:block; width:100%; border:none; background:none;\" />";
            });
        }
                        
        private void InjectToggleActions(PdfDocument pdf, string html, int pageIndex)
        {
            var form = PdfAcroForm.GetAcroForm(pdf, true);
            var buttonRegex = new Regex(@"<img[^>]+class=""[^""]*(?:redbtn|hsgbtn|has2btn)[^""]*""[^>]*id=""([^""]+)""[^>]*linkname=""([^""]+)""", RegexOptions.IgnoreCase);
            var buttonMatches = buttonRegex.Matches(html);
        
            int btnCounter = 0;
            var pdfPage = pdf.GetPage(1);
        
            foreach (Match buttonMatch in buttonMatches)
            {
                var imgId = buttonMatch.Groups[1].Value;
                var linkName = buttonMatch.Groups[2].Value;
                
                var fieldId = $"AnswerField_{pageIndex}_{linkName}";
                var targetField = form.GetField(fieldId);
        
                if (targetField != null)
                {
                    var actualFieldId = targetField.GetFieldName().ToString();
                    
                    var toggleScript = $"var f = this.getField(\"{actualFieldId}\"); if (f) f.display = (f.display == display.hidden) ? display.visible : display.hidden;";
                    var action = PdfAction.CreateJavaScript(toggleScript);
        
                    var styleMatch = Regex.Match(buttonMatch.Value, @"style=""[^""]*left:\s*([\d\.]+)px[^""]*top:\s*([\d\.]+)px");
                    if (styleMatch.Success)
                    {
                        float x = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 0.75f;
                        float y = 1008 - (float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 0.75f) - 24;
        
                        var rect = new Rectangle(x, y, 24, 24);
        
                        // Use the parameterless or document-only constructor and set properties via methods
                        var buttonField = new PdfButtonFormField(pdf);
                        buttonField.SetFieldName($"Toggle_{pageIndex}_{imgId}_{btnCounter++}");
                        buttonField.SetAdditionalAction(PdfName.U, action);
                        
                        foreach (var w in buttonField.GetWidgets())
                        {
                            w.SetRectangle(rect);
                            w.SetFlags(PdfAnnotation.PRINT);
                        }
        
                        form.AddField(buttonField, pdfPage);
                    }
                }
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
    
    /* Style the text field to look like normal text */
    input.pdf-answer-field {
        display: block !important;
        width: 100% !important;
        border: none !important;
        background: none !important;
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
            var matches = Regex.Matches(html, @"src=""(data:image/([^;]+);base64,([^""]+))""", RegexOptions.IgnoreCase);
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
