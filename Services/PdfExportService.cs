using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace BookViewer.Services
{
    public class PdfInteractiveElement
    {
        public string Type { get; set; } = ""; // "link", "answer"
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Url { get; set; } = "";
        public string Tooltip { get; set; } = "";
    }

    public class PdfExportService
    {
        private readonly BookService _bookService;

        public event EventHandler<int>? OnProgress;
        public event EventHandler<string>? OnComplete;
        public event EventHandler<string>? OnError;

        public Func<string, Task<byte[]?>>? RenderPageToImageAsync { get; set; }
        public Func<string, Task<string?>>? EvaluateJsAsync { get; set; }

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
                    string html = await _bookService.BuildPageHtmlForRenderAsync(
                        filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    byte[]? imageBytes = null;
                    try { imageBytes = await RenderPageToImageAsync(html); } catch { }
                    if (imageBytes == null || imageBytes.Length == 0) continue;

                    var interactiveElements = new List<PdfInteractiveElement>();
                    try
                    {
                        var json = await EvaluateJsAsync(html);
                        if (!string.IsNullOrEmpty(json) && json != "[]" && json != "null")
                            interactiveElements = JsonSerializer.Deserialize<List<PdfInteractiveElement>>(json) ?? new();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract elements page {i + 1}: {ex.Message}");
                    }

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

                    // Inject link annotations manually — PdfSharpCore's public API doesn't
                    // expose PdfLinkAnnotation, so we build the /Annots dictionary ourselves.
                    var annots = new PdfArray(document);
                    foreach (var el in interactiveElements)
                    {
                        double x = el.X * 0.75;
                        double y = pageHeightPt - ((el.Y + el.Height) * 0.75);
                        double w = el.Width * 0.75;
                        double h = el.Height * 0.75;

                        if (el.Type == "link" && !string.IsNullOrEmpty(el.Url))
                        {
                            var annot = BuildLinkAnnot(document, x, y, w, h, el.Url, el.Tooltip);
                            annots.Elements.Add(annot);
                        }
                        else if (el.Type == "answer")
                        {
                            var annot = BuildTextAnnot(document, x, y, w, h, el.Tooltip);
                            annots.Elements.Add(annot);
                        }
                    }

                    if (annots.Elements.Count > 0)
                    {
                        if (pdfPage.Elements.ContainsKey("/Annots"))
                            pdfPage.Elements["/Annots"] = annots;
                        else
                            pdfPage.Elements.Add("/Annots", annots);
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

        private static PdfDictionary BuildLinkAnnot(PdfDocument doc, double x, double y, double w, double h, string url, string tooltip)
        {
            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Link");
            annot.Elements["/Rect"] = BuildRect(doc, x, y, x + w, y + h);
            annot.Elements["/Border"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));

            var action = new PdfDictionary(doc);
            action.Elements["/Type"] = new PdfName("/Action");
            action.Elements["/S"] = new PdfName("/URI");
            action.Elements["/URI"] = new PdfString(url);
            annot.Elements["/A"] = action;

            if (!string.IsNullOrEmpty(tooltip))
                annot.Elements["/Contents"] = new PdfString(tooltip);

            return annot;
        }

        private static PdfDictionary BuildTextAnnot(PdfDocument doc, double x, double y, double w, double h, string contents)
        {
            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Text");
            annot.Elements["/Rect"] = BuildRect(doc, x, y, x + w, y + h);
            annot.Elements["/Name"] = new PdfName("/Note");
            annot.Elements["/Contents"] = new PdfString(contents ?? "");
            annot.Elements["/Open"] = new PdfBoolean(false);
            return annot;
        }

        private static PdfArray BuildRect(PdfDocument doc, double x1, double y1, double x2, double y2)
        {
            return new PdfArray(doc,
                new PdfReal(x1),
                new PdfReal(y1),
                new PdfReal(x2),
                new PdfReal(y2));
        }
    }
}
