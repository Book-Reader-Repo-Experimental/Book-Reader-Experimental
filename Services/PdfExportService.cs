using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using iText.Kernel.Geom;
using iText.Forms;
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

                    html = InjectAnswerFormFields(html, i);
                    html = InjectPdfCss(html);
                    html = await ExtractBase64ImagesAsync(html, tempDir, i);

                    var tempPdfPath = System.IO.Path.Combine(tempDir, $"page_{i}.pdf");
                    tempFiles.Add(tempPdfPath);

                    using (var writer = new PdfWriter(tempPdfPath))
                    using (var pdf = new PdfDocument(writer))
                    {
                        pdf.SetDefaultPageSize(pageSize);
                        HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                        InjectToggleActions(pdf, html, i);
                    }
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
            var regex = new Regex(@"<div\b[^>]*class=""[^""]*\b(?:tbred|tbnote)\b[^""]*""[^>]*>([\s\S]*?)</div>", RegexOptions.IgnoreCase);
            var matchCount = 0;

            return regex.Replace(html, m =>
            {
                var content = m.Groups[1].Value;
                var fieldId = $"AnswerField_{pageIndex}_{matchCount++}";
                return $"<button id=\"{fieldId}\" class=\"pdf-answer-field\" style=\"display:block; border:none; background:none;\">{content}</button>";
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

                if (form.GetField(fieldId) != null)
                {
                    var toggleScript = $"var f = this.getField(\"{fieldId}\"); if (f) f.display = (f.display == display.hidden) ? display.visible : display.hidden;";
                    var action = PdfAction.CreateJavaScript(toggleScript);

                    var styleMatch = Regex.Match(buttonMatch.Value, @"style=""[^""]*left:\s*([\d\.]+)px[^""]*top:\s*([\d\.]+)px");
                    if (styleMatch.Success)
                    {
                        float x = float.Parse(styleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 0.75f;
                        float y = 1008 - (float.Parse(styleMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 0.75f) - 24;

                        var rect = new Rectangle(x, y, 24, 24);
                        
                        // Create a raw dictionary for the button widget
                        var widgetDict = new PdfDictionary(pdf);
                        widgetDict.Put(PdfName.Type, PdfName.Annot);
                        widgetDict.Put(PdfName.Subtype, PdfName.Widget);
                        widgetDict.Put(PdfName.FT, PdfName.Btn);
                        widgetDict.Put(PdfName.Rect, new PdfArray(pdf, new PdfNumber(rect.GetLeft()), new PdfNumber(rect.GetBottom()), new PdfNumber(rect.GetRight()), new PdfNumber(rect.GetTop())));
                        widgetDict.Put(PdfName.T, new PdfString($"Toggle_{pageIndex}_{imgId}_{btnCounter++}"));
                        widgetDict.Put(PdfName.F, new PdfNumber(4)); // Print flag
                        widgetDict.Put(PdfName.Ff, new PdfNumber(65536)); // Pushbutton flag
                        
                        // Required for the widget appearance
                        var apDict = new PdfDictionary(pdf);
                        apDict.Put(PdfName.N, new PdfDictionary(pdf));
                        widgetDict.Put(PdfName.AP, apDict);
                        
                        widgetDict.Put(PdfName.A, action.GetPdfObject());

                        // Convert to a PdfAnnotation and add it to the page
                        var widgetAnnot = PdfAnnotation.MakeAnnotation(widgetDict);
                        pdfPage.AddAnnotation(widgetAnnot);
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
    button.pdf-answer-field { display: block !important; width: 100% !important; border: none !important; background: none !important; color: #000 !important; text-align: left !important; font-family: inherit !important; font-size: inherit !important; }
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
