#if IOS
using System;
using System.IO;

namespace BookViewer.Platforms.iOS
{
    public static class IosFontExtractor
    {
        public static string GetExactFontPath(string familyName, bool isBold, bool isItalic)
        {
            try
            {
                // Apple allows reading these specific directories in a sandboxed app
                string[] fontDirs = {
                    "/System/Library/Fonts/",
                    "/System/Library/Fonts/Supplemental/"
                };

                // Normalize the CSS family name to the iOS file name
                string fileName = familyName.ToLower()
                    .Replace(" ", "")
                    .Replace("pingfangsc", "PingFang")
                    .Replace("pingfanghk", "PingFang")
                    .Replace("helvetica", "Helvetica")
                    .Replace("arial", "Arial")
                    .Replace("timesnewroman", "Times New Roman")
                    .Replace("couriernew", "Courier New");

                if (isBold) fileName += " Bold";
                if (isItalic) fileName += " Italic";

                foreach (var dir in fontDirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    // Look for .ttf or .ttc
                    foreach (var ext in new[] { ".ttf", ".ttc" })
                    {
                        var fullPath = Path.Combine(dir, fileName + ext);
                        if (File.Exists(fullPath))
                        {
                            // Copy to our cache so PdfSharpCore can read it
                            var cachePath = Path.Combine(FileSystem.CacheDirectory, Path.GetFileName(fullPath));
                            if (!File.Exists(cachePath))
                            {
                                File.Copy(fullPath, cachePath, true);
                            }
                            return cachePath;
                        }
                    }
                }

                // Fallback: try to find any font containing the family name
                foreach (var dir in fontDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    var files = Directory.GetFiles(dir, "*.ttf");
                    foreach (var file in files)
                    {
                        if (Path.GetFileName(file).ToLower().Contains(fileName.Replace(" ", "").ToLower()))
                        {
                            var cachePath = Path.Combine(FileSystem.CacheDirectory, Path.GetFileName(file));
                            if (!File.Exists(cachePath)) File.Copy(file, cachePath, true);
                            return cachePath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IosFontExtractor error: {ex.Message}");
            }
            return null;
        }
    }
}
#endif
