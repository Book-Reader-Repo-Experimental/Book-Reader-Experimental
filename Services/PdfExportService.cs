using iText.Html2pdf;
using iText.Kernel.Pdf;
using System;
using System.IO;
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

                // iText7 uses a writer to create the PDF
                using (var writer = new PdfWriter(outputPath))
                using (var pdf = new PdfDocument(writer))
                {
                    // We convert the combined HTML of all pages into one PDF
                    var converterProperties = new ConverterProperties();
                    
                    // For iText7 to support CJK, we need to set the font provider
                    // Assuming you have a font file, e.g., NotoSansCJK.ttf in Resources/Raw
                    // converterProperties.SetFontProvider(new DefaultFontProvider(true, true, true));

                    for (int i = 0; i < totalPages; i++)
                    {
                        OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                        var filePath = _bookService.PageFiles[i];
                        string html = await _bookService.BuildPageHtmlForRenderAsync(
                            filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                        // iText7's HtmlConverter processes the HTML and appends to the PDF
                        HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                    }
                }

                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
            }
        }
    }
}
