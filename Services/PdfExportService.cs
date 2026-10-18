using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
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
            try
            {
                var totalPages = _bookService.PageFiles.Count;
                if (totalPages == 0) { OnError?.Invoke(this, "No pages to export"); return; }

                // Create a temp folder for the extracted HTML
                var tempDir = Path.Combine(Path.GetTempPath(), "BookViewerPdfExport");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                using (var writer = new PdfWriter(outputPath))
                using (var pdf = new PdfDocument(writer))
                {
                    var converterProperties = new ConverterProperties();
                    
                    // Set up a basic font provider to prevent it from choking on missing system fonts
                    converterProperties.SetFontProvider(new DefaultFontProvider(true, true, true));

                    for (int i = 0; i < totalPages; i++)
                    {
                        OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                        var filePath = _bookService.PageFiles[i];
                        string html = await _bookService.BuildPageHtmlForRenderAsync(
                            filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                        // 1. iText7 hates massive base64 URIs in HTML. 
                        // We strip them out or replace them with local paths if possible.
                        // For a quick fix, we replace base64 images with a 1x1 transparent pixel 
                        // so iText doesn't try to decode 10MB of base64.
                        html = Regex.Replace(html, @"src=""data:image/[^""]+""", @"src=""data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7""");
                        
                        // 2. Save the HTML to a temp file
                        var tempHtmlPath = Path.Combine(tempDir, $"page_{i}.html");
                        await File.WriteAllTextAsync(tempHtmlPath, html);

                        // 3. Convert the temp file
                        using (var htmlStream = new FileStream(tempHtmlPath, FileMode.Open, FileAccess.Read))
                        {
                            HtmlConverter.ConvertToPdf(htmlStream, pdf, converterProperties);
                        }
                    }
                }

                try { Directory.Delete(tempDir, true); } catch { }
                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
            }
        }
    }
}
