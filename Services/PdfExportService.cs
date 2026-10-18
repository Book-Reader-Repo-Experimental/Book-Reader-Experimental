using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Papiro;
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

                var exportDir = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
                var tempFiles = new List<string>();
                
                // Get the Papiro service from the DI container
                var papiro = Handler!.MauiContext!.Services.GetRequiredService<IHtmlToPdfService>();

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    string html = await _bookService.BuildPageHtmlForRenderAsync(
                        filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    var tempPath = Path.Combine(exportDir, $"temp_{i}.pdf");

                    // Use Papiro for native, 1:1 rendering
                    var result = await papiro.ConvertAndSaveAsync(html, tempPath);
                    
                    if (result.IsSuccess)
                    {
                        tempFiles.Add(tempPath);
                    }
                }

                // Use PdfSharpCore strictly to merge the PDFs
                using (var outputDoc = new PdfDocument())
                {
                    foreach (var tempFile in tempFiles)
                    {
                        using (var inputDoc = PdfReader.Open(tempFile, PdfDocumentOpenMode.Import))
                        {
                            for (int i = 0; i < inputDoc.PageCount; i++)
                            {
                                outputDoc.AddPage(inputDoc.Pages[i]);
                            }
                        }
                    }
                    outputDoc.Save(outputPath);
                }

                // Cleanup
                foreach (var tempFile in tempFiles)
                {
                    try { File.Delete(tempFile); } catch { }
                }

                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
            }
        }
    }
}
