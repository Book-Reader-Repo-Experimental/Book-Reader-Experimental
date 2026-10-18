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

                    var imageBytes = await RenderPageToImageAsync(html);
                    if (imageBytes == null || imageBytes.Length == 0) continue;

                    var pdfPage = document.AddPage();
                    pdfPage.Width = XUnit.FromPoint(pageWidthPt);
                    pdfPage.Height = XUnit.FromPoint(pageHeightPt);

                    // MANUAL IMAGE EMBEDDING
                    var image = new PdfImage(document);
                    image.Stream.Value = imageBytes;
                    image.Width = (int)pageWidthPt;
                    image.Height = (int)pageHeightPt;
                    
                    // Create the XObject
                    var xObject = document.Internals.AddObject(image);
                    
                    // Build the content stream manually
                    var content = new PdfSharpCore.Pdf.Content.Objects.CSequence();
                    content.Add(new PdfSharpCore.Pdf.Content.Objects.CComment("Page content"));
                    
                    // Save graphics state, apply transform, draw image, restore state
                    var ops = new PdfSharpCore.Pdf.Content.Objects.CArray();
                    ops.Add(new PdfSharpCore.Pdf.Content.Objects.CInteger(1));
                    ops.Add(new PdfSharpCore.Pdf.Content.Objects.CInteger(0));
                    ops.Add(new PdfSharpCore.Pdf.Content.Objects.CInteger(0));
                    ops.Add(new PdfSharpCore.Pdf.Content.Objects.CInteger(1));
                    ops.Add(new PdfSharpCore.Pdf.Content.Objects.CInteger(0));
                    ops.Add(new PdfSharpCore.Pdf.Content.Objects.CInteger(0));
                    
                    // cm operator (concatenate matrix)
                    content.Add(new PdfSharpCore.Pdf.Content.Objects.COperator("cm", ops));
                    
                    // Do operator (draw XObject)
                    content.Add(new PdfSharpCore.Pdf.Content.Objects.COperator("Do", new PdfSharpCore.Pdf.Content.Objects.CName(xObject.Reference)));
                    
                    // Q operator (restore state)
                    content.Add(new PdfSharpCore.Pdf.Content.Objects.COperator("Q"));
                    
                    // Write the content
                    var contentStream = new PdfSharpCore.Pdf.PdfContent(document);
                    contentStream.CreateStream(content);
                    pdfPage.Contents.Add(contentStream);
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
