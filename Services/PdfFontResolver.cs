using PdfSharpCore.Fonts;
using System;
using System.Collections.Generic;
using System.IO;

namespace BookViewer.Services
{
    public class PdfFontResolver : IFontResolver
    {
        public string DefaultFontName => "Arial";

        // This maps the family name to the physical font file.
        // Key format: "FamilyName#Style" (e.g., "Microsoft JhengHei#Bold")
        private readonly Dictionary<string, string> _fontFileMap = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterFont(string familyName, bool isBold, bool isItalic, string filePath)
        {
            var style = isBold && isItalic ? "BoldItalic" : isBold ? "Bold" : isItalic ? "Italic" : "Regular";
            var key = $"{familyName}#{style}";
            if (File.Exists(filePath))
            {
                _fontFileMap[key] = filePath;
            }
        }

        public byte[] GetFont(string faceName)
        {
            if (_fontFileMap.TryGetValue(faceName, out var filePath) && File.Exists(filePath))
            {
                try
                {
                    return File.ReadAllBytes(filePath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load font {filePath}: {ex.Message}");
                }
            }

            // Fallback to standard Arial if the exact font isn't registered
            // PdfSharpCore handles built-in Arial metrics by returning null
            return null; 
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var style = isBold && isItalic ? "BoldItalic" : isBold ? "Bold" : isItalic ? "Italic" : "Regular";
            var key = $"{familyName}#{style}";

            // If we have registered this exact font file, use it
            if (_fontFileMap.ContainsKey(key))
            {
                return new FontResolverInfo(key);
            }

            // If we only have the regular weight, use it (fake bold/italic)
            var regularKey = $"{familyName}#Regular";
            if (_fontFileMap.ContainsKey(regularKey))
            {
                return new FontResolverInfo(regularKey);
            }

            // Fallback to Arial
            return new FontResolverInfo("Arial", 
                isBold && isItalic ? XFontStyle.BoldItalic : 
                isBold ? XFontStyle.Bold : 
                isItalic ? XFontStyle.Italic : XFontStyle.Regular);
        }
    }
}
