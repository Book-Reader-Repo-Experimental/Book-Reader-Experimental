#if IOS
using CoreText;
using Foundation;
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
                // 1. Get the exact UIFont the OS is using for this family
                var font = UIKit.UIFont.FromName(familyName, 12);
                if (font == null) return null;

                var traits = font.FontDescriptor.SymbolicTraits;
                // 2. Ensure the weight/style matches the CSS request
                var requestedTraits = traits;
                if (isBold) requestedTraits |= UIFontDescriptorSymbolicTraits.Bold;
                if (isItalic) requestedTraits |= UIFontDescriptorSymbolicTraits.Italic;
                
                var descriptor = font.FontDescriptor.CreateWithTraits(requestedTraits);
                using var ctFont = new CTFont(descriptor, 12);
                
                // 3. Extract the raw font bytes
                var cgFont = ctFont.CopyGraphicsFont();
                if (cgFont == null || cgFont.DataProvider == null) return null;
                
                using var data = cgFont.DataProvider.CopyData();
                if (data == null) return null;

                // 4. Save it to disk so PdfSharpCore can load it
                var path = Path.Combine(FileSystem.CacheDirectory, $"{ctFont.PostScriptName}_{isBold}_{isItalic}.ttf");
                if (!File.Exists(path))
                {
                    NSData.FromBytes(data.Bytes, data.Length).Save(path, true);
                }

                return path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Font extraction failed for {familyName}: {ex.Message}");
                return null;
            }
        }
    }
}
#endif
