using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
            var tempFiles = new List<string>();
            try
            {
                var totalPages = _bookService.PageFiles.Count;
                if (totalPages == 0) { OnError?.Invoke(this, "No pages to export"); return; }

                var tempDir = Path.Combine(Path.GetTempPath(), "BookViewerPdfExport");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                var converterProperties = new ConverterProperties();
                // The font provider is essential for iText7 to render text
                converterProperties.SetFontProvider(new DefaultFontProvider(true, true, true));

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    string html = await _bookService.BuildPageHtmlForRenderAsync(
                        filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    // Fix base64 images so iText7 doesn't crash
                    html = await ExtractBase64ImagesAsync(html, tempDir, i);

                    var tempPdfPath = Path.Combine(tempDir, $"page_{i}.pdf");
                    tempFiles.Add(tempPdfPath);

                    using (var writer = new PdfWriter(tempPdfPath))
                    using (var pdf = new PdfDocument(writer))
                    {
                        HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                    }
                }

                // Merge all pages
                OnProgress?.Invoke(this, 100);
                using (var writer = new PdfWriter(outputPath))
                using (var mergedPdf = new PdfDocument(writer))
                using (var merger = new PdfMerger(mergedPdf))
                {
                    foreach (var tempPdf in tempFiles)
                    {
                        using (var reader = new PdfReader(tempPdf))
                        using (var sourcePdf = new PdfDocument(reader))
                        {
                            merger.Merge(sourcePdf, 1, sourcePdf.GetNumberOfPages());
                        }
                    }
                }

                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
            }
            finally
            {
                foreach (var file in tempFiles)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }

        private async Task<string> ExtractBase64ImagesAsync(string html, string tempDir, int pageIndex)
        {
            var matches = Regex.Matches(html, @"src=""(data:image/([^;]+);base64,([^""]+))""", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                var dataUri = match.Groups[1].Value;
                var ext = match.Groups[2].Value;
                var base64 = match.Groups[3].Value;

                var fileName = $"img_{pageIndex}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.{ext}";
                var filePath = Path.Combine(tempDir, fileName);

                try
                {
                    await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(base64));
                    html = html.Replace(dataUri, filePath.Replace('\\', '/'));
                }
                catch { }
            }
            return html;
        }
    }
}
