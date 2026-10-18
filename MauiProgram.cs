using Microsoft.Extensions.Logging;
using PdfSharpCore.Fonts;
using System;
using System.IO;
using BookViewer.Services;

namespace BookViewer.Maui
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            try
            {
                // Initialize the resolver
                var resolver = new PdfFontResolver();
                
                // If we're on iOS, we must extract a dummy TTF first to stop PdfSharpCore from crashing
                // before it even builds the PDF. We'll overwrite this with real fonts later.
                string dummyPath = Path.Combine(FileSystem.CacheDirectory, "dummy.ttf");
                if (!File.Exists(dummyPath))
                {
                    // Minimal valid TTF header to prevent parse errors
                    File.WriteAllBytes(dummyPath, new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 });
                }
                resolver.RegisterFont("Arial", false, false, dummyPath);
                
                GlobalFontSettings.FontResolver = resolver;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PDF Font Resolver setup failed: {ex.Message}");
            }

            return builder.Build();
        }
    }
}
