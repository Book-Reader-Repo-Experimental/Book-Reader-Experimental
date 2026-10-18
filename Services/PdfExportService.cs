using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Annotations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BookViewer.Models;

namespace BookViewer.Services
{
    public class PdfExportService
    {
        private readonly BookService _bookService;

        public event EventHandler<int> OnProgress;
        public event EventHandler<string> OnComplete;
        public event EventHandler<string> OnError;

        public Func<string, Task<byte[]>> RenderPageToImageAsync { get; set; }
        public Func<string, Task<string>> EvaluateJsAsync { get; set; } // NEW

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
                if (EvaluateJsAsync == null) { OnError?.Invoke(this, "JS evaluation callback not set"); return; }

                double pageWidthPt = 1024 * 0.75;
                double pageHeightPt = 1344 * 0.75;

                var document = new PdfDocument();
                document.Info.Title = _bookService.BookTitle;
                document.Info.Creator = "BookViewer";

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    string html = await _bookService.BuildPageHtmlForRenderAsync(filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    // 1. Get the image
                    byte[] imageBytes = null;
                    try { imageBytes = await RenderPageToImageAsync(html); } catch { }
                    if (imageBytes == null || imageBytes.Length == 0) continue;

                    // 2. Extract interactive elements via JS
                    var interactiveElements = new List<PdfInteractiveElement>();
                    try
                    {
                        var json = await EvaluateJsAsync(html);
                        if (!string.IsNullOrEmpty(json) && json != "[]")
                        {
                            interactiveElements = JsonSerializer.Deserialize<List<PdfInteractiveElement>>(json);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract elements page {i+1}: {ex.Message}");
                    }

                    // 3. Build PDF Page
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

                    // 4. Draw interactive overlays
                    foreach (var el in interactiveElements)
                    {
                        // Convert 1024x1344 pixel space to PDF points
                        double x = el.X * 0.75;
                        double y = el.Y * 0.75;
                        double w = el.Width * 0.75;
                        double h = el.Height * 0.75;

                        if (el.Type == "link" && !string.IsNullOrEmpty(el.Url))
                        {
                            var linkAnnot = new PdfLinkAnnotation(pdfPage, new PdfRectangle(x, y, x + w, y + h));
                            var action = new PdfUriAction(el.Url);
                            linkAnnot.Actions.Add(action);
                            linkAnnot.Contents = el.Tooltip;
                            pdfPage.Annotations.Add(linkAnnot);
                        }
                        else if (el.Type == "answer")
                        {
                            // For PDF, toggles aren't natively supported without AcroForms.
                            // We create a hidden button that highlights the area with a tooltip.
                            var textAnnot = new PdfTextAnnotation(pdfPage, new PdfRectangle(x, y, x + w, y + h));
                            textAnnot.Title = "Answer Note";
                            textAnnot.Contents = el.Tooltip;
                            pdfPage.Annotations.Add(textAnnot);
                        }
                    }

                    OnProgress?.Invoke(this, (int)(((i + 1) / (double)totalPages) * 100));
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
