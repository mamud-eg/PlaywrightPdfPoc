using System.Diagnostics;
using System.Text;
using Microsoft.Playwright;

namespace PlaywrightPdfPoc;

public class PdfGeneratorService : IAsyncDisposable
{
    private readonly IPlaywright _pw;
    private readonly IBrowser _browser;

    // Rough default header height reserved for Playwright's native HeaderTemplate. Chromium
    // does not auto-size this the way wkhtmltopdf sizes header space; there's no settings.xml
    // key for it either, so this is a fixed POC approximation — see RESULTS.md for whether it
    // needs per-report tuning.
    private const string NativeHeaderHeight = "20mm";
    private const string DefaultFooterMargin = "15mm"; // mirrors the original --margin-bottom 15

    private PdfGeneratorService(IPlaywright pw, IBrowser browser) { _pw = pw; _browser = browser; }

    public static async Task<PdfGeneratorService> CreateAsync()
    {
        var pw = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync();
        return new PdfGeneratorService(pw, browser);
    }

    public Task<GenerationResult> ConvertNativeAsync(SampleInput s, CancellationToken ct)
        => ConvertToPdf(s, HeaderFooterStrategy.Native, ct);

    public Task<GenerationResult> ConvertCssRunningAsync(SampleInput s, CancellationToken ct)
        => ConvertToPdf(s, HeaderFooterStrategy.CssRunning, ct);

    public Task<GenerationResult> ConvertTableRepeatingAsync(SampleInput s, CancellationToken ct)
        => ConvertToPdf(s, HeaderFooterStrategy.TableRepeating, ct);

    private enum HeaderFooterStrategy { Native, CssRunning, TableRepeating }

    private async Task<GenerationResult> ConvertToPdf(SampleInput s, HeaderFooterStrategy strategy, CancellationToken ct)
    {
        var log = new StringBuilder();
        var startedAt = Stopwatch.GetTimestamp();
        var strategyName = strategy switch
        {
            HeaderFooterStrategy.Native => "native",
            HeaderFooterStrategy.CssRunning => "css-running",
            HeaderFooterStrategy.TableRepeating => "table-repeating",
            _ => throw new ArgumentOutOfRangeException(nameof(strategy))
        };

        // Fresh BrowserContext per job: isolation without a full process cold-start per
        // conversion. JavaScript stays disabled to mirror the original --disable-javascript.
        await using var ctx = await _browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await ctx.NewPageAsync();
        page.Console   += (_, m) => log.AppendLine($"[console] {m.Text}");
        page.PageError += (_, e) => log.AppendLine($"[pageerror] {e}");

        var opts = new PagePdfOptions
        {
            Path = s.OutputPdf,
            PrintBackground = true,
            Outline = false,
        };

        string bodyToLoad;
        string? mergedTempFile = null;
        if (strategy == HeaderFooterStrategy.Native)
        {
            opts.Margin = new() { Top = NativeHeaderHeight, Bottom = DefaultFooterMargin, Left = "0", Right = "0" };
            opts.DisplayHeaderFooter = true;
            opts.HeaderTemplate = AdaptWkHtmlPageNumberClasses(HtmlMerger.BuildNativeTemplate(s.HeaderHtmlPath));
            opts.FooterTemplate = AdaptWkHtmlPageNumberClasses(HtmlMerger.BuildNativeTemplate(s.FooterHtmlPath));
            bodyToLoad = s.BodyHtmlPath;
        }
        else
        {
            // Header/footer live in-flow inside the merged document, so no margin needs
            // reserving for them.
            opts.Margin = new() { Top = "0", Bottom = "0", Left = "0", Right = "0" };
            opts.DisplayHeaderFooter = false;
            mergedTempFile = strategy == HeaderFooterStrategy.CssRunning
                ? HtmlMerger.MergeRunning(s)
                : HtmlMerger.MergeTableRepeating(s);
            bodyToLoad = mergedTempFile;
        }

        // A report's settings.xml can still override the margin set above (e.g. a custom
        // margin-bottom) — same settings-win-over-defaults precedence as the original service.
        PageSettings.ApplyTo(opts, s.TemplateFolder);

        bool ok;
        try
        {
            await page.GotoAsync(new Uri(bodyToLoad).AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.PdfAsync(opts);
            ok = File.Exists(s.OutputPdf);
        }
        catch (Exception ex) { log.AppendLine(ex.ToString()); ok = false; }
        finally
        {
            if (mergedTempFile != null) TryDelete(mergedTempFile);
        }

        return new GenerationResult
        {
            EndedInSuccess = ok,
            ProcessOutput = log.ToString(),
            OutputFile = s.OutputPdf,
            ProcessTime = Stopwatch.GetElapsedTime(startedAt),
            OutputFileSizeBytes = ok ? new FileInfo(s.OutputPdf).Length : 0,
            Strategy = strategyName
        };
    }

    // The original service's header/footer templates get page numbers via wkhtmltopdf's own
    // per-page querystring reload + a substitutePdfVariables() script that fills in
    // class="page"/class="topage" spans. Chromium's native HeaderTemplate/FooterTemplate has
    // no such reload (and JS is disabled anyway) — it instead recognizes its own fixed set of
    // placeholder classes (pageNumber/totalPages/date/title/url). Remapping the known classes
    // is what makes native strategy's page numbers actually render instead of staying blank.
    private static string AdaptWkHtmlPageNumberClasses(string html) =>
        html.Replace("class=\"page\"", "class=\"pageNumber\"")
            .Replace("class=\"topage\"", "class=\"totalPages\"");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup of the merged temp file */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _pw.Dispose();
    }
}
