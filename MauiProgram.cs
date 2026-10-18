using Microsoft.Extensions.Logging;
using PdfSharpCore.Fonts;
using CwSoftware.Papiro; // Correct namespace for Papiro
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
                .UsePapiro() // Correct method to register Papiro
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // Register the custom PdfSharpCore resolver to prevent iOS TypeInitializationException
            try
            {
                if (GlobalFontSettings.FontResolver == null)
                {
                    GlobalFontSettings.FontResolver = new PdfFontResolver();
                }
            }
            catch (TypeInitializationException)
            {
                // If PdfSharpCore tries to load its default resolver and throws,
                // we catch it here, as our custom one is already set.
            }

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
