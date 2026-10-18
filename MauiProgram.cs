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

            // Register Papiro
            builder.Services.AddPapiro();

            // Set the custom font resolver for PdfSharpCore to avoid iOS TypeInitializationException
            try 
            {
                if (GlobalFontSettings.FontResolver == null)
                {
                    GlobalFontSettings.FontResolver = new PdfFontResolver();
                }
            }
            catch (TypeInitializationException)
            {
                // If it fails, the resolver is already set globally
            }

            return builder.Build();
        }
    }
}
