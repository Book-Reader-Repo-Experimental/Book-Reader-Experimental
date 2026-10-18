using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using System;

namespace BookViewer.Services
{
    public class PdfFontResolver : IFontResolver
    {
        public string DefaultFontName => "Arial";

        public byte[]? GetFont(string faceName)
        {
            // We aren't drawing text, so return null to use built-in Arial
            return null;
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var sim = XStyleSimulations.None;
            if (isBold && isItalic) sim = XStyleSimulations.BoldItalicSimulation;
            else if (isBold) sim = XStyleSimulations.BoldSimulation;
            else if (isItalic) sim = XStyleSimulations.ItalicSimulation;

            return new FontResolverInfo("Arial", sim);
        }
    }
}
