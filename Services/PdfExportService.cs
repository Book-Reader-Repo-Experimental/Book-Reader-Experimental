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

                    var pdfPage = document.AddPage();
                    pdfPage.Width = PdfSharpCore.Drawing.XUnit.FromPoint(pageWidthPt);
                    pdfPage.Height = PdfSharpCore.Drawing.XUnit.FromPoint(pageHeightPt);

                    // MANUAL IMAGE EMBEDDING
                    var pdfImage = new PdfImage(document);
                    pdfImage.Stream.Value = imageBytes;
                    pdfImage.Width = (int)pageWidthPt;
                    pdfImage.Height = (int)pageHeightPt;
                    
                    var xObject = document.Internals.AddObject(pdfImage);
                    
                    var content = new PdfSharpCore.Pdf.PdfContent(document);
                    content.Append("q\n");
                    
                    string cmCommand = string.Format(System.Globalization.CultureInfo.InvariantCulture, 
                        "{0} 0 0 {1} 0 0 cm\n", pageWidthPt, pageHeightPt);
                    content.Append(cmCommand);
                    
                    content.Append("/Im0 Do\n");
                    content.Append("Q\n");

                    var pageContent = new PdfSharpCore.Pdf.PdfContent(document);
                    pageContent.CreateStream(content.ToString());
                    pdfPage.Contents.Add(pageContent);

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
