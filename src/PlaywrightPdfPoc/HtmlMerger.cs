using System.Text.RegularExpressions;

namespace PlaywrightPdfPoc;

// Both merge strategies below need to properly extract each real document's <head> assets
// and <body> inner HTML rather than nesting raw <html> documents inside each other's <body>,
// since the harvested header.html/footer.html are full standalone documents with their own
// <style>/<link> tags that would otherwise be silently dropped.
public static class HtmlMerger
{
    // Strategy: CSS "running elements" via position:fixed.
    // Verified empirically (see RESULTS.md): fixed-position elements DO repeat on every printed
    // page in Chromium's headless print-to-PDF — contrary to the commonly-cited limitation.
    // But position:fixed removes the header/footer from the flow, so body content needs its own
    // padding reserved (matching PdfGeneratorService's native-strategy margins) or it renders
    // underneath the fixed header, producing overlapping/garbled text on page 1.
    private const string RunningElementsStyle = """
        .pw-poc-header { position: fixed; top: 0; left: 0; right: 0; }
        .pw-poc-footer { position: fixed; bottom: 0; left: 0; right: 0; }
        .pw-poc-body-content { padding-top: 20mm; padding-bottom: 15mm; }
        """;

    public static string MergeRunning(SampleInput s)
    {
        var (bodyHead, bodyInner) = ReadSplit(s.BodyHtmlPath);
        var (headerHead, headerInner) = ReadSplit(s.HeaderHtmlPath);
        var (footerHead, footerInner) = ReadSplit(s.FooterHtmlPath);

        var merged = $"""
            <html><head>
              {bodyHead}
              {headerHead}
              {footerHead}
              <style>
              {RunningElementsStyle}
              </style>
            </head><body>
              <div class="pw-poc-header">{headerInner}</div>
              <div class="pw-poc-body-content">{bodyInner}</div>
              <div class="pw-poc-footer">{footerInner}</div>
            </body></html>
            """;

        return WriteNextTo(s.BodyHtmlPath, merged);
    }

    // Strategy: wrap the body in a <table> with <thead>/<tfoot>. Browsers replicate table
    // head/foot row groups across page breaks when printing paginated content, which is the
    // one technique that reliably repeats rich HTML per printed page in Chromium (unlike
    // position:fixed, and unlike CSS Paged-Media-3 margin boxes, which Chromium never implemented).
    private const string TableRepeatingStyle = """
        .pw-poc-repeat-table { width: 100%; border-collapse: collapse; }
        .pw-poc-repeat-table > thead { display: table-header-group; }
        .pw-poc-repeat-table > tfoot { display: table-footer-group; }
        """;

    public static string MergeTableRepeating(SampleInput s)
    {
        var (bodyHead, bodyInner) = ReadSplit(s.BodyHtmlPath);
        var (headerHead, headerInner) = ReadSplit(s.HeaderHtmlPath);
        var (footerHead, footerInner) = ReadSplit(s.FooterHtmlPath);

        var merged = $"""
            <html><head>
              {bodyHead}
              {headerHead}
              {footerHead}
              <style>
              {TableRepeatingStyle}
              </style>
            </head><body>
              <table class="pw-poc-repeat-table">
                <thead><tr><td>{headerInner}</td></tr></thead>
                <tfoot><tr><td>{footerInner}</td></tr></tfoot>
                <tbody><tr><td>{bodyInner}</td></tr></tbody>
              </table>
            </body></html>
            """;

        return WriteNextTo(s.BodyHtmlPath, merged);
    }

    // Playwright's native HeaderTemplate/FooterTemplate expects a small HTML fragment, not a
    // full standalone document — passing a whole <html><head>...<body>...</body></html> file
    // straight through makes Chromium's internal template renderer fail outright
    // (Page.printToPDF: "Printing failed"). Reuse the same head/body extraction as the merge
    // strategies to build a valid fragment instead.
    //
    // Verified empirically: an external <link rel="stylesheet"> (e.g. a Google Fonts import,
    // present in every real header/footer template surveyed) doesn't just fail to apply here —
    // it makes Page.printToPDF fail outright for the whole conversion. Inline <style> blocks
    // are fine; only external <link> stylesheets need stripping.
    public static string BuildNativeTemplate(string htmlPath)
    {
        var (head, bodyInner) = ReadSplit(htmlPath);
        var headWithoutExternalStylesheets = Regex.Replace(head, @"<link[^>]*rel=[""']stylesheet[""'][^>]*>", "",
            RegexOptions.IgnoreCase);
        return $"{headWithoutExternalStylesheets}\n{bodyInner}";
    }

    private static (string head, string bodyInner) ReadSplit(string htmlPath)
    {
        var html = File.ReadAllText(htmlPath);
        var head = Regex.Match(html, @"<head[^>]*>(.*?)</head>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;
        var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var bodyInner = bodyMatch.Success ? bodyMatch.Groups[1].Value : html;
        return (head, bodyInner);
    }

    // Written next to the source body.html (not the system temp dir) so any asset paths that
    // are still relative to the sample folder keep resolving correctly.
    private static string WriteNextTo(string bodyHtmlPath, string mergedHtml)
    {
        var path = Path.Combine(Path.GetDirectoryName(bodyHtmlPath)!, $"{Guid.NewGuid()}.merged.html");
        File.WriteAllText(path, mergedHtml);
        return path;
    }
}
