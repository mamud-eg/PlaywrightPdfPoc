using System.Xml;
using Microsoft.Playwright;

namespace PlaywrightPdfPoc;

// Real settings.xml (from the original service) is <settings><page>...</page></settings>.
// The historical wkhtmltopdf integration reads it via XPath "//page/*", which matches
// regardless of how deep <page> is nested, so no root-shape assumption is needed here.
public static class PageSettings
{
    public static void ApplyTo(PagePdfOptions opts, string templateFolderPath)
    {
        var path = Path.Combine(templateFolderPath, "settings.xml");
        if (!File.Exists(path)) return;

        var dict = new Dictionary<string, string>();
        try
        {
            var doc = new XmlDocument();
            doc.Load(path);
            foreach (XmlNode n in doc.SelectNodes("//page/*")!)
                dict[n.Name.ToLowerInvariant()] = n.InnerText.Trim().ToLowerInvariant();
        }
        catch { return; }

        if (dict.TryGetValue("page-size", out var size)) opts.Format = size.ToUpperInvariant();
        if (dict.TryGetValue("orientation", out var o)) opts.Landscape = o == "landscape";

        // A report's own margin-bottom must win over whatever strategy-default margin the
        // caller already set on opts.Margin, mirroring the original service prepending
        // settings.xml-derived flags ahead of its hardcoded --margin-bottom 15 (wkhtmltopdf's
        // last-flag-wins parsing means the per-report value overrides the default there too).
        if (dict.TryGetValue("margin-bottom", out var mb))
        {
            opts.Margin ??= new Margin();
            opts.Margin.Bottom = WithMillimeterUnitIfBare(mb);
        }

        // Extend here for any other settings.xml keys found in real samples;
        // note in RESULTS.md any key that has no Chromium/Playwright equivalent.
    }

    // wkhtmltopdf treats a bare number (e.g. "15") as millimeters; settings.xml values are
    // sometimes already unit-suffixed (e.g. "17mm"). Playwright's Margin accepts CSS-style
    // unit strings directly, so only bare numbers need a unit appended.
    private static string WithMillimeterUnitIfBare(string value) =>
        double.TryParse(value, out _) ? $"{value}mm" : value;
}
