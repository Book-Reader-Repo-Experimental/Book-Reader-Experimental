using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using PdfSharpCore.Pdf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace BookViewer.Services
{
    public class PdfDomElement
    {
        public string Type { get; set; } = ""; // "text" or "image"
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
        private readonly PdfFontResolver _fontResolver;

        public event EventHandler<int>? OnProgress;
        public event EventHandler<string>? OnComplete;
        public event EventHandler<string>? OnError;

        public Func<string, Task<string?>>? EvaluateJsAsync { get; set; }

        public PdfExportService(BookService bookService)
        {
            _bookService = bookService;
            
            // Cast the global resolver to our custom type so we can register book fonts
            _fontResolver = GlobalFontSettings.FontResolver as PdfFontResolver ?? new PdfFontResolver();
        }

        public async Task ExportBookAsPdfAsync(string outputPath, bool includeAnswers, bool includeTeacherNotes, bool includeStudentAnswers)
        {
            try
            {
                var totalPages = _bookService.PageFiles.Count;
                if (totalPages == 0) { OnError?.Invoke(this, "No pages to export"); return; }
                if (EvaluateJsAsync == null) { OnError?.Invoke(this, "JS evaluation callback not set"); return; }

                RegisterBookFonts(_bookService.CurrentBookPath);

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

                    var json = await EvaluateJsAsync(html);
                    var elements = new List<PdfDomElement>();
                    if (!string.IsNullOrEmpty(json) && json != "[]" && json != "null")
                    {
                        try { elements = JsonSerializer.Deserialize<List<PdfDomElement>>(json) ?? new(); } catch { }
                    }

                    var pdfPage = document.AddPage();
                    pdfPage.Width = XUnit.FromPoint(pageWidthPt);
                    pdfPage.Height = XUnit.FromPoint(pageHeightPt);

                    using var gfx = XGraphics.FromPdfPage(pdfPage);
                    gfx.DrawRectangle(XBrushes.White, 0, 0, pageWidthPt, pageHeightPt);

                    var annots = new PdfArray(document);

                    foreach (var el in elements)
                    {
                        double x = el.X * 0.75;
                        double y = pageHeightPt - ((el.Y + el.H) * 0.75);
                        double w = el.W * 0.75;
                        double h = el.H * 0.75;

                        if (el.Type == "text" && !string.IsNullOrWhiteSpace(el.Text))
                        {
                            var brush = ParseColor(el.Color);

                            var style = XFontStyle.Regular;
                            if (el.IsBold && el.IsItalic) style = XFontStyle.BoldItalic;
                            else if (el.IsBold) style = XFontStyle.Bold;
                            else if (el.IsItalic) style = XFontStyle.Italic;

                            var font = new XFont(el.FontFamily, Math.Max(1, el.FontSize * 0.75), style);
                            var rect = new XRect(x, y, w, h);
                            gfx.DrawString(el.Text.Trim(), font, brush, rect, XStringFormats.TopLeft);

                            if (!string.IsNullOrEmpty(el.Url))
                            {
                                annots.Elements.Add(BuildLinkAnnot(document, x, y, w, h, el.Url, el.Text));
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

        private void RegisterBookFonts(string bookPath)
        {
            try
            {
                var fontDirs = new[] { Path.Combine(bookPath, "FONTS"), Path.Combine(bookPath, "fonts") };
                foreach (var dir in fontDirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    foreach (var file in Directory.GetFiles(dir, "*.ttf").Concat(Directory.GetFiles(dir, "*.otf")))
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file).ToLower();
                        bool isBold = fileName.Contains("bold") || fileName.Contains("bd");
                        bool isItalic = fileName.Contains("italic") || fileName.Contains("it");

                        var familyName = fileName
                            .Replace("-bold", "").Replace("-italic", "").Replace("-bd", "").Replace("-it", "")
                            .Replace("bold", "").Replace("italic", "").Trim();

                        _fontResolver.RegisterFont(familyName, isBold, isItalic, file);
                        System.Diagnostics.Debug.WriteLine($"Registered font: {familyName} ({isBold}/{isItalic}) -> {file}");
                    }
                }

#if ANDROID
                var systemFonts = new Dictionary<string, string>
                {
                    { "sans-serif", "/system/fonts/Roboto-Regular.ttf" },
                    { "sans-serif", "/system/fonts/Roboto-Bold.ttf" },
                    { "serif", "/system/fonts/NotoSerif-Regular.ttf" },
                };
                foreach (var kvp in systemFonts)
                {
                    if (File.Exists(kvp.Value))
                    {
                        bool isBold = kvp.Value.Contains("Bold");
                        _fontResolver.RegisterFont(kvp.Key, isBold, false, kvp.Value);
                    }
                }
#endif
#if IOS || MACCATALYST
                var iosFonts = new Dictionary<string, string>
                {
                    { "Helvetica", "/System/Library/Fonts/Helvetica.ttc" },
                    { "Helvetica-Bold", "/System/Library/Fonts/Helvetica.ttc" },
                    { "PingFang SC", "/System/Library/Fonts/PingFang.ttc" },
                };
                foreach (var kvp in iosFonts)
                {
                    if (File.Exists(kvp.Value))
                    {
                        bool isBold = kvp.Key.Contains("Bold");
                        _fontResolver.RegisterFont(kvp.Key.Replace("-Bold", ""), isBold, false, kvp.Value);
                    }
                }
#endif
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RegisterBookFonts error: {ex.Message}");
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

        private static XBrush ParseColor(string cssColor)
        {
            try
            {
                if (cssColor.StartsWith("rgb"))
                {
                    var parts = cssColor.Substring(cssColor.IndexOf("(") + 1).TrimEnd(')').Split(',');
                    return new XSolidBrush(XColor.FromArgb(
                        int.Parse(parts[0].Trim()),
                        int.Parse(parts[1].Trim()),
                        int.Parse(parts[2].Trim())));
                }
                if (cssColor.StartsWith("#"))
                {
                    var hex = cssColor.Substring(1);
                    if (hex.Length == 3)
                        return new XSolidBrush(XColor.FromArgb(
                            Convert.ToInt32($"{hex[0]}{hex[0]}", 16),
                            Convert.ToInt32($"{hex[1]}{hex[1]}", 16),
                            Convert.ToInt32($"{hex[2]}{hex[2]}", 16)));
                    if (hex.Length >= 6)
                        return new XSolidBrush(XColor.FromArgb(
                            Convert.ToInt32(hex.Substring(0, 2), 16),
                            Convert.ToInt32(hex.Substring(2, 2), 16),
                            Convert.ToInt32(hex.Substring(4, 2), 16)));
                }
            }
            catch { }
            return XBrushes.Black;
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
