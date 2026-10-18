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

            // MUST be set before any PDF operations and BEFORE the app initializes
            try 
            {
                if (GlobalFontSettings.FontResolver == null)
                {
                    GlobalFontSettings.FontResolver = new PdfFontResolver();
                }
            }
            catch (TypeInitializationException ex)
            {
                // If PdfSharpCore's default resolver tries to load and throws,
                // we catch it here, but the resolver is already set to our custom one.
                System.Diagnostics.Debug.WriteLine($"FontResolver init error: {ex.Message}");
            }

            return builder.Build();
        }
    }
}
