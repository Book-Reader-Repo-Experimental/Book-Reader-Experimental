using Microsoft.Extensions.Logging;

namespace BookViewer.Maui
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            // REQUIRED for iText7 to initialize without throwing PdfException
            Environment.SetEnvironmentVariable("ITEXT_BOUNCY_CASTLE_FACTORY_NAME", "bouncy-castle");

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif
            return builder.Build();
        }
    }
}
