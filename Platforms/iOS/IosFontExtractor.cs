#if IOS
using CoreText;
using Foundation;
using System;
using System.IO;

namespace BookViewer.Platforms.iOS
{
    public static class IosFontExtractor
    {
        public static string GetFontFilePath(string familyName, bool isBold, bool isItalic)
        {
            try
            {
                // Ask iOS CoreText for the actual font descriptor matching the CSS family
                var font = UIFont.FromName(familyName, 12);
                if (font == null)
                {
                    // Fallback to system font if the specific family isn't found
                    font = UIFont.SystemFontOfSize(12);
                }

                var descriptor = font.FontDescriptor;
                if (isBold && isItalic) descriptor = descriptor.CreateWithTraits(CTFontSymbolicTraits.Bold | CTFontSymbolicTraits.Italic);
                else if (isBold) descriptor = descriptor.CreateWithTraits(CTFontSymbolicTraits.Bold);
                else if (isItalic) descriptor = descriptor.CreateWithTraits(CTFontSymbolicTraits.Italic);

                using var ctFont = new CTFont(descriptor, 12);
                var postScriptName = ctFont.PostScriptName;

                // iOS provides the raw font data via CGFont
                using var cgFont = ctFont.CopyGraphicsFont();
                if (cgFont == null) return null;

                using var data = cgFont.DataProvider.CopyData();
                if (data == null) return null;

                // Save it to our cache so PdfSharpCore can read it
                var cacheDir = FileSystem.CacheDirectory;
                var fileName = $"{postScriptName}_{(isBold ? "B" : "")}{(isItalic ? "I" : "")}.ttf";
                var path = Path.Combine(cacheDir, fileName);

                if (!File.Exists(path))
                {
                    NSData.FromBytes(data.Bytes, data.Length).Save(path, true);
                }

                return path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IosFontExtractor error: {ex.Message}");
                return null;
            }
        }
    }
}
#endif
