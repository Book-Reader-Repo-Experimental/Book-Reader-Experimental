using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using System;
using System.Collections.Generic;
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

        public Func<string, Task<byte[]?>>? RenderPageToImageAsync { get; set; }

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
                if (RenderPageToImageAsync == null) { OnError?.Invoke(this, "Render callback not set"); return; }

                double pageWidthPt = 1024 * 0.75;
                double pageHeightPt = 1344 * 0.75;

                var document = new PdfDocument();
                document.Info.Title = _bookService.BookTitle;

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    string html = await _bookService.BuildPageHtmlForRenderAsync(
                        filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    byte[]? imageBytes = null;
                    try
                    {
                        imageBytes = await RenderPageToImageAsync(html);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Capture error page {i + 1}: {ex.Message}");
                    }

                    if (imageBytes == null || imageBytes.Length == 0) continue;

                    var pdfPage = document.AddPage();
                    pdfPage.Width = XUnit.FromPoint(pageWidthPt);
                    pdfPage.Height = XUnit.FromPoint(pageHeightPt);

                    using (var gfx = XGraphics.FromPdfPage(pdfPage))
                    using (var ms = new MemoryStream(imageBytes))
                    {
                        var img = XImage.FromStream(() => ms);
                        gfx.DrawImage(img, 0, 0, pageWidthPt, pageHeightPt);
                        img.Dispose();
                    }
                }

                document.Save(outputPath);
                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
            }
        }
    }
}
