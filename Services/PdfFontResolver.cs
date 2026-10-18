using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using System;
using System.Collections.Generic;
using System.IO;

namespace BookViewer.Services
{
    public class PdfFontResolver : IFontResolver
    {
        public string DefaultFontName => "Arial";
        private readonly Dictionary<string, string> _fontFileMap = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterFont(string familyName, bool isBold, bool isItalic, string filePath)
        {
            var style = isBold && isItalic ? "BoldItalic" : isBold ? "Bold" : isItalic ? "Italic" : "Regular";
            var key = $"{familyName}#{style}";
            if (File.Exists(filePath))
            {
                _fontFileMap[key] = filePath;
                if (!_fontFileMap.ContainsKey($"{familyName}#Regular"))
                    _fontFileMap[$"{familyName}#Regular"] = filePath;
            }
        }

        public byte[]? GetFont(string faceName)
        {
            if (_fontFileMap.TryGetValue(faceName, out var filePath) && File.Exists(filePath))
                return File.ReadAllBytes(filePath);
            return null; 
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var style = isBold && isItalic ? "BoldItalic" : isBold ? "Bold" : isItalic ? "Italic" : "Regular";
            var key = $"{familyName}#{style}";

            if (_fontFileMap.ContainsKey(key))
                return new FontResolverInfo(key);

            var regularKey = $"{familyName}#Regular";
            if (_fontFileMap.ContainsKey(regularKey))
                return new FontResolverInfo(regularKey);

            var sim = XStyleSimulations.None;
            if (isBold && isItalic) sim = XStyleSimulations.BoldItalicSimulation;
            else if (isBold) sim = XStyleSimulations.BoldSimulation;
            else if (isItalic) sim = XStyleSimulations.ItalicSimulation;

            return new FontResolverInfo("Arial", sim);
        }
    }
}
