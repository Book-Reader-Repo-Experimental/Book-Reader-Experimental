using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
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

                // Create the writer and document
                var writer = new PdfWriter(outputPath);
                var pdf = new PdfDocument(writer);
                
                var converterProperties = new ConverterProperties();
                converterProperties.SetFontProvider(new DefaultFontProvider(true, true, true));

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    string html = await _bookService.BuildPageHtmlForRenderAsync(
                        filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    // Passing the HTML string directly prevents iText from auto-closing the document
                    // on the first page's stream completion.
                    HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                }

                // Explicitly close the document after all pages are added
                pdf.Close();
                
                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
            }
        }
    }
}
