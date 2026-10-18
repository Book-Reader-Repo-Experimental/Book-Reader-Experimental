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
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            // 1. Set the custom font resolver globally BEFORE any PDF operations.
            // This prevents PdfSharpCore from trying to scan system fonts (which crashes on iOS).
            if (GlobalFontSettings.FontResolver == null)
            {
                var resolver = new PdfFontResolver();
                GlobalFontSettings.FontResolver = resolver;

                // 2. Optionally register a CJK font from Resources/Raw/Fonts/
                // So Chinese text can be drawn natively.
                try
                {
                    var cjkPath = Path.Combine(FileSystem.AppDataDirectory, "Fonts", "NotoSansCJK.ttf");
                    
                    // On some platforms, we can read directly from the app package:
                    using (var stream = FileSystem.OpenAppPackageFileAsync("Fonts/NotoSansCJK.ttf").Result)
                    {
                        if (stream != null)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(cjkPath));
                            using var fs = File.Create(cjkPath);
                            stream.CopyTo(fs);
                        }
                    }

                    if (File.Exists(cjkPath))
                    {
                        resolver.RegisterFont("Noto Sans CJK", false, false, cjkPath);
                        resolver.RegisterFont("Noto Sans CJK", true, false, cjkPath);
                        System.Diagnostics.Debug.WriteLine("Registered CJK font.");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"CJK font registration failed: {ex.Message}");
                }
            }

            return builder.Build();
        }
    }
}
