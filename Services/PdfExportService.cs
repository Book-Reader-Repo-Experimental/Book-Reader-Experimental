using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace BookViewer.Services
{
    public class PdfDomElement
    {
        public string Type { get; set; } = ""; 
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public string Text { get; set; } = "";
        public string Src { get; set; } = "";
        public double FontSize { get; set; }
        public string Color { get; set; } = "#000000";
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public string FontFamily { get; set; } = "Arial";
        public string Url { get; set; } = "";
    }

    public class PdfExportService
    {
        private readonly BookService _bookService;

        public event EventHandler<int>? OnProgress;
        public event EventHandler<string>? OnComplete;
        public event EventHandler<string>? OnError;

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
                if (EvaluateJsAsync == null) { OnError?.Invoke(this, "JS evaluation callback not set"); return; }

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

                    var json = await EvaluateJsAsync(html);
                    var elements = new List<PdfDomElement>();
                    if (!string.IsNullOrEmpty(json) && json != "[]" && json != "null")
                    {
                        try { elements = JsonSerializer.Deserialize<List<PdfDomElement>>(json) ?? new(); } catch { }
                    }

                    var pdfPage = document.AddPage();
                    pdfPage.Width = XUnit.FromPoint(pageWidthPt);
                    pdfPage.Height = XUnit.FromPoint(pageHeightPt);

                    // FIX: Use XGraphicsPdfPageOptions.Append
                    var gfx = XGraphics.FromPdfPage(pdfPage, XGraphicsPdfPageOptions.Append);
                    try
                    {
                        gfx.DrawRectangle(XBrushes.White, 0, 0, pageWidthPt, pageHeightPt);

                        var annots = new PdfArray(document);

                        foreach (var el in elements)
                        {
                            double x = el.X * 0.75;
                            double y = el.Y * 0.75; 
                            double w = el.W * 0.75;
                            double h = el.H * 0.75;

                            if (el.Type == "text" && !string.IsNullOrWhiteSpace(el.Text))
                            {
                                var font = new XFont("Courier New", Math.Max(1, el.FontSize * 0.75), XFontStyle.Regular);
                                
                                var rect = new XRect(x, y, w, h);
                                gfx.DrawString(el.Text.Trim(), font, XBrushes.Black, rect, XStringFormats.TopLeft);

                                if (!string.IsNullOrEmpty(el.Url))
                                {
                                    double pdfY = pageHeightPt - (y + h);
                                    annots.Elements.Add(BuildLinkAnnot(document, x, pdfY, w, h, el.Url, el.Text));
                                }
                            }
                            else if (el.Type == "image")
                            {
                                try
                                {
                                    var imageBytes = await LoadImageBytesAsync(el.Src);
                                    if (imageBytes != null && imageBytes.Length > 0)
                                    {
                                        using var ms = new MemoryStream(imageBytes);
                                        var img = XImage.FromStream(() => ms);
                                        gfx.DrawImage(img, x, y, w, h);
                                    }
                                }
                                catch { }
                            }
                        }

                        if (annots.Elements.Count > 0)
                        {
                            if (pdfPage.Elements.ContainsKey("/Annots"))
                                pdfPage.Elements["/Annots"] = annots;
                            else
                                pdfPage.Elements.Add("/Annots", annots);
                        }
                    }
                    finally
                    {
                        gfx.Dispose();
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

        private async Task<byte[]?> LoadImageBytesAsync(string src)
        {
            try
            {
                if (src.StartsWith("data:image"))
                {
                    var base64 = src.Substring(src.IndexOf(",") + 1);
                    return Convert.FromBase64String(base64);
                }
                else if (src.StartsWith("file://"))
                {
                    var path = Uri.UnescapeDataString(src.Substring(7));
                    if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
                }
            }
            catch { }
            return null;
        }

        private static PdfDictionary BuildLinkAnnot(PdfDocument doc, double x, double y, double w, double h, string url, string tooltip)
        {
            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Link");
            annot.Elements["/Rect"] = new PdfArray(doc, new PdfReal(x), new PdfReal(y), new PdfReal(x + w), new PdfReal(y + h));
            annot.Elements["/Border"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));

            var action = new PdfDictionary(doc);
            action.Elements["/Type"] = new PdfName("/Action");
            action.Elements["/S"] = new PdfName("/URI");
            action.Elements["/URI"] = new PdfString(url);
            annot.Elements["/A"] = action;

            return annot;
        }
    }
}
