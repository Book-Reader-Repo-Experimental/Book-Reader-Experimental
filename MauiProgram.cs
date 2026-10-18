using Microsoft.Extensions.Logging;

namespace BookViewer.Maui
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            // Add this line BEFORE building the app
            Environment.SetEnvironmentVariable(
                "ITEXT_BOUNCY_CASTLE_FACTORY_NAME", 
                "bouncy-castle");
        
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

            return builder.Build();
        }
    }
}
