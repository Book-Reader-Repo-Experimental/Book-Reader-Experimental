using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
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

                    // 1. Capture the WebView as an image (PNG)
                    byte[]? imageBytes = null;
                    try
                    {
                        imageBytes = await RenderPageToImageAsync(html);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Capture error page {i + 1}: {ex.Message}");
                    }

                    if (imageBytes == null || imageBytes.Length == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"Skipping page {i + 1} (no image)");
                        continue;
                    }

                    // 2. Add Page
                    var pdfPage = document.AddPage();
                    pdfPage.Width = XUnit.FromPoint(pageWidthPt);
                    pdfPage.Height = XUnit.FromPoint(pageHeightPt);

                    // 3. Manually embed the image as a PDF XObject
                    var pdfImage = new PdfImage(document);
                    pdfImage.Stream.Value = imageBytes;
                    
                    // Register the image in the document
                    var xObject = document.Internals.AddObject(pdfImage);
                    pdfImage.Reference = xObject.Reference;

                    // 4. Create the drawing instructions manually
                    // We use a graphics transformation matrix to scale the image to fit the page.
                    // PDF coordinate system starts at bottom-left, so we translate to (0, 0) and scale.
                    var content = new PdfSharpCore.Pdf.PdfContent(document);
                    
                    // Save state
                    content.Append("q\n");
                    
                    // Matrix: [width 0 0 height 0 0] cm (maps image to page size)
                    // We format using InvariantCulture to avoid decimal comma issues in some locales.
                    string cmCommand = string.Format(System.Globalization.CultureInfo.InvariantCulture, 
                        "{0} 0 0 {1} 0 0 cm\n", 
                        pageWidthPt, pageHeightPt);
                    content.Append(cmCommand);
                    
                    // Draw the image ( /Im0 Do )
                    content.Append("/Im0 Do\n");
                    
                    // Restore state
                    content.Append("Q\n");

                    // Create the stream for the page content
                    var pageContent = new PdfSharpCore.Pdf.PdfContent(document);
                    pageContent.CreateStream(content.ToString());
                    
                    // Add the content stream to the page
                    pdfPage.Contents.Add(pageContent);

                    // 5. Register the image in the page's Resources dictionary
                    var resources = new PdfResources();
                    resources.Elements["/XObject"] = new PdfDictionary(document);
                    ((PdfDictionary)resources.Elements["/XObject"]).Elements["/Im0"] = xObject.Reference;
                    pdfPage.Elements["/Resources"] = resources;
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
