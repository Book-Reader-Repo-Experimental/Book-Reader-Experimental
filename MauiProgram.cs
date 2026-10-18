using Microsoft.Extensions.Logging;
using PdfSharpCore.Fonts;
using System;
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

            if (GlobalFontSettings.FontResolver == null)
            {
                GlobalFontSettings.FontResolver = new PdfFontResolver();
            }

            return builder.Build();
        }
    }
}
