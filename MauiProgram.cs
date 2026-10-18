using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using System;
using System.IO;

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

            // Migrate any per-book resources to the shared folder on startup.
            MigrateSharedResources();

            return builder.Build();
        }

        /// <summary>
        /// One-shot migration: move any existing per-book resources/ and
        /// resource/ folders to the shared Books/resources/ folder so
        /// %RESOURCES% links keep working without re-download.
        /// Safe to run on every launch (idempotent — won't overwrite).
        /// </summary>
        private static void MigrateSharedResources()
        {
            try
            {
                var booksRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "BookViewer", "Books");
                if (!Directory.Exists(booksRoot)) return;

                var shared = Path.Combine(booksRoot, "resources");
                Directory.CreateDirectory(shared);

                foreach (var bookDir in Directory.GetDirectories(booksRoot, "book_*"))
                {
                    foreach (var candidate in new[]
                    {
                        Path.Combine(bookDir, "resources"),
                        Path.Combine(bookDir, "resource"),
                    })
                    {
                        if (!Directory.Exists(candidate)) continue;

                        try
                        {
                            foreach (var file in Directory.GetFiles(candidate, "*", SearchOption.AllDirectories))
                            {
                                var rel = Path.GetRelativePath(candidate, file);
                                var dest = Path.Combine(shared, rel);
                                if (File.Exists(dest)) continue;

                                var dir = Path.GetDirectoryName(dest);
                                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                                File.Copy(file, dest);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"MigrateSharedResources ({candidate}): {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MigrateSharedResources: {ex.Message}");
            }
        }
    }
}
