# POC: Replace wkhtmltopdf with Playwright (Chromium) for PDF Report Generation

> This document is self-contained. It does not assume access to any other repository — everything needed to build and evaluate the POC is inlined below.

## Context

An existing .NET reporting service converts HTML reports (body + header + footer, generated from templates) to PDF by shelling out to `wkhtmltopdf.exe` as a child process. wkhtmltopdf is an unmaintained project built on a patched, ancient WebKit fork. The goal of this POC is to prove that Microsoft Playwright (driving headless Chromium) can replace it — producing PDFs with equivalent or better visual fidelity, while measuring the practical deltas (render time, output file size, and the biggest risk area: header/footer rendering parity).

This is a **standalone POC repo** with its own solution — it does not need to build against, or be added to, the original service's solution. It should stay a plain console app.

## Decisions already locked in

- **Runtime: .NET 8 / C#** using the `Microsoft.Playwright` NuGet package (not Node/TypeScript) — keeps it portable back into a future .NET service if the POC succeeds.
- **Header/footer strategy: test both, then recommend one.**
  1. Playwright's native `HeaderTemplate` / `FooterTemplate` PDF options.
  2. A "CSS running elements" approach: merge header/footer into the body document as `position: fixed` elements with `@page` margin reserved, so they repeat on every printed page.
- **Sample scope: a few representative report types**, not one, not all — specifically pick samples that stress the risky dimensions:
  - one simple report (plain body, minimal header/footer)
  - one header/footer-heavy report (rich HTML/CSS in the header or footer)
  - one landscape/non-default page-size report

## Behavior spec of the system being replaced

This is the exact behavior the POC must be able to reproduce, extracted from the original service's PDF-generation code so it can be replicated without needing access to that codebase:

- Each report is rendered as **three separate HTML files**: `body.html`, `header.html`, `footer.html`. These are generated from templates into a temp/output folder, converted, then normally deleted (a debug flag on the original service skips deletion, which is how real sample HTML can be harvested — see "Harvesting real samples" below).
- A per-report-type `settings.xml` file (living next to that report's template folder) declares page settings as simple key/value XML elements under a `<page>` root, e.g.:
  ```xml
  <page>
    <page-size>A4</page-size>
    <orientation>Landscape</orientation>
  </page>
  ```
  The original code reads this generically via XPath `//page/*`, lowercases values, and turns each into a `--key value` wkhtmltopdf CLI flag. Not every key necessarily has a Chromium/Playwright equivalent — flag any that don't during evaluation.
- The wkhtmltopdf invocation is built as a single CLI argument string equivalent to:
  ```
  {pagesettings} --margin-bottom 15 --disable-javascript --dpi 96 --lowquality
  --image-quality 80 --image-dpi 300 --no-outline --disable-internal-links
  "<body.html>" --header-html "<header.html>" --header-spacing 0
  --footer-html "<footer.html>" --footer-spacing 0 "<output.pdf>"
  ```
  Key semantics to preserve/evaluate:
  - `--header-html` / `--footer-html` let wkhtmltopdf render **entire arbitrary HTML files** (own CSS, own DOM) as the header/footer — this is richer than Playwright's native header/footer templates, which accept inline HTML only (no external stylesheets, limited script, scaled at 0 by default). This gap is the main open question the POC must resolve.
  - `--disable-javascript` — the original disables JS entirely during conversion.
  - `--dpi 96 --lowquality --image-quality 80 --image-dpi 300` — deliberate output-size/quality tuning for embedded raster images. Chromium's PDF export has no direct equivalent knob; measure the file-size difference instead of trying to match the flag.
  - `--no-outline --disable-internal-links` — no PDF outline/bookmarks, internal links disabled.
  - `--margin-bottom 15` plus header/footer spacing — reserve vertical space for header/footer content.
- The process is run **once per conversion job** (process-per-job, not a long-lived server), with stdout/stderr redirected and captured into a single "process output" string (wkhtmltopdf only writes to stderr on error, but the original code captures it unconditionally, regardless of whether the run succeeded — so a non-empty output does not, by itself, mean failure).
- Success is judged strictly by process exit code `== 0`. On failure, the (corrupt) output PDF is deleted. On success, the temp `body/header/footer.html` files are deleted (unless running in a debug mode that preserves them for inspection).
- The result of a conversion is modeled as a plain object with: whether it succeeded, the captured process output, the output file path, and how long the conversion took.
- Asset/resource paths referenced from the HTML are **absolute `file:///` paths**, and the original system deliberately strips spaces from generated file/folder names because wkhtmltopdf cannot resolve paths containing them. Preserve this constraint when harvesting or generating sample folders (no spaces in paths) so both engines are being tested against equally resolvable input.

## Repo / project layout

```
PlaywrightPdfPoc/                      <- new git repo root
  PlaywrightPdfPoc.sln
  src/PlaywrightPdfPoc/
    PlaywrightPdfPoc.csproj             (net8.0, OutputType Exe, PackageReference Microsoft.Playwright)
    Program.cs                          harness: loops sample folders, runs both strategies, prints a comparison table
    PdfEngine.cs                        readiness check / browser bootstrap
    PdfGeneratorService.cs              core conversion logic (both strategies), browser lifecycle
    PageSettings.cs                     reads settings.xml, maps to Playwright PagePdfOptions
    HtmlMerger.cs                       builds the "CSS running elements" merged document for strategy B
    SampleInput.cs                      small record: paths for body/header/footer/settings/output per sample
    GenerationResult.cs                 result model (success, output, timing, size, strategy used)
  samples/
    <ReportTypeName>/
      body.html  header.html  footer.html  settings.xml  assets/...
      wkhtml-reference.pdf              the original wkhtmltopdf output, for side-by-side comparison
  RESULTS.md                            write-up of findings (created after running the harness)
```

## Build steps

### 1. Scaffold

```powershell
md PlaywrightPdfPoc; cd PlaywrightPdfPoc
git init
dotnet new sln -n PlaywrightPdfPoc
dotnet new console -o src/PlaywrightPdfPoc -n PlaywrightPdfPoc
dotnet sln add src/PlaywrightPdfPoc/PlaywrightPdfPoc.csproj
cd src/PlaywrightPdfPoc
dotnet add package Microsoft.Playwright --version 1.48.0
dotnet build
pwsh bin/Debug/net8.0/playwright.ps1 install chromium
```
Add a standard `.gitignore` for .NET, plus a `samples/` folder at the repo root (sibling to `src/`).

### 2. `GenerationResult.cs`

```csharp
namespace PlaywrightPdfPoc;

public class GenerationResult
{
    public bool EndedInSuccess;
    public string ProcessOutput = "";
    public string OutputFile = "";
    public TimeSpan ProcessTime;
    public long OutputFileSizeBytes;
    public string Strategy = ""; // "native" | "css-running"
}
```

### 3. `PdfEngine.cs` — readiness check

```csharp
using Microsoft.Playwright;

namespace PlaywrightPdfPoc;

public static class PdfEngine
{
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var pw = await Playwright.CreateAsync();
            await using var b = await pw.Chromium.LaunchAsync();
            return true;
        }
        catch { return false; }
    }
}
```

### 4. `PageSettings.cs` — reuse the same settings.xml shape

```csharp
using System.Xml;
using Microsoft.Playwright;

namespace PlaywrightPdfPoc;

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
        if (dict.TryGetValue("orientation", out var o))  opts.Landscape = o == "landscape";
        // Extend here for any other settings.xml keys found in real samples;
        // note in RESULTS.md any key that has no Chromium/Playwright equivalent.
    }
}
```

### 5. `SampleInput.cs`

```csharp
namespace PlaywrightPdfPoc;

public record SampleInput(
    string TemplateFolder,   // folder containing settings.xml
    string BodyHtmlPath,
    string HeaderHtmlPath,
    string FooterHtmlPath,
    string OutputPdf)
{
    public static SampleInput FromFolder(string folder, string outputDir) => new(
        TemplateFolder: folder,
        BodyHtmlPath: Path.Combine(folder, "body.html"),
        HeaderHtmlPath: Path.Combine(folder, "header.html"),
        FooterHtmlPath: Path.Combine(folder, "footer.html"),
        OutputPdf: Path.Combine(outputDir, $"{Path.GetFileName(folder)}.pdf"));

    public SampleInput WithOutputSuffix(string suffix) =>
        this with { OutputPdf = Path.ChangeExtension(OutputPdf, null) + $".{suffix}.pdf" };
}
```

### 6. `HtmlMerger.cs` — strategy B (CSS running elements)

Build this once you have a real header/footer sample to test against. Approach:
1. Read `body.html`, `header.html`, `footer.html` as strings.
2. Wrap header content in `<div style="position:fixed;top:0;left:0;right:0;">…</div>`, footer similarly with `bottom:0`.
3. Inject a `<style>@page { margin-top: <header height>; margin-bottom: <footer height>; }</style>` (or set via `PagePdfOptions.Margin` instead, whichever renders more reliably in practice — decide empirically).
4. Splice header-div + body content + footer-div into one HTML document; write to a temp file; return its path.

```csharp
namespace PlaywrightPdfPoc;

public static class HtmlMerger
{
    public static string MergeRunning(SampleInput s)
    {
        var body = File.ReadAllText(s.BodyHtmlPath);
        var header = File.ReadAllText(s.HeaderHtmlPath);
        var footer = File.ReadAllText(s.FooterHtmlPath);

        var merged = $"""
            <html><head>
              <style>
                @page {{ margin-top: 25mm; margin-bottom: 25mm; }}
                .pw-poc-header {{ position: fixed; top: 0; left: 0; right: 0; }}
                .pw-poc-footer {{ position: fixed; bottom: 0; left: 0; right: 0; }}
              </style>
            </head><body>
              <div class="pw-poc-header">{header}</div>
              {body}
              <div class="pw-poc-footer">{footer}</div>
            </body></html>
            """;

        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.merged.html");
        File.WriteAllText(tempPath, merged);
        return tempPath;
    }
}
```
Note: `position: fixed` in print CSS does not reliably repeat per-page across all engines — this is exactly the thing the POC needs to empirically verify in Chromium's print-to-PDF path. If it doesn't repeat correctly, the fallback is CSS `@page` running elements via named page margin boxes (more limited browser support) or accept that strategy A (native templates) is the only viable path for rich headers.

### 7. `PdfGeneratorService.cs` — core conversion

```csharp
using System.Diagnostics;
using System.Text;
using Microsoft.Playwright;

namespace PlaywrightPdfPoc;

public class PdfGeneratorService : IAsyncDisposable
{
    private readonly IPlaywright _pw;
    private readonly IBrowser _browser;

    private PdfGeneratorService(IPlaywright pw, IBrowser browser) { _pw = pw; _browser = browser; }

    public static async Task<PdfGeneratorService> CreateAsync()
    {
        var pw = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync();
        return new PdfGeneratorService(pw, browser);
    }

    public Task<GenerationResult> ConvertNativeAsync(SampleInput s, CancellationToken ct)
        => ConvertToPdf(s, useNativeHeaderFooter: true, ct);

    public Task<GenerationResult> ConvertCssRunningAsync(SampleInput s, CancellationToken ct)
        => ConvertToPdf(s, useNativeHeaderFooter: false, ct);

    private async Task<GenerationResult> ConvertToPdf(SampleInput s, bool useNativeHeaderFooter, CancellationToken ct)
    {
        var log = new StringBuilder();
        var startedAt = Stopwatch.GetTimestamp();
        var strategy = useNativeHeaderFooter ? "native" : "css-running";

        // fresh BrowserContext per job: isolation without a full process cold-start per conversion
        await using var ctx = await _browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await ctx.NewPageAsync();
        page.Console   += (_, m) => log.AppendLine($"[console] {m.Text}");
        page.PageError += (_, e) => log.AppendLine($"[pageerror] {e}");

        var opts = new PagePdfOptions
        {
            Path = s.OutputPdf,
            PrintBackground = true,
            Outline = false,
            Margin = new() { Top = "0", Bottom = "15mm", Left = "0", Right = "0" }
        };
        PageSettings.ApplyTo(opts, s.TemplateFolder);

        var bodyToLoad = s.BodyHtmlPath;

        if (useNativeHeaderFooter)
        {
            opts.DisplayHeaderFooter = true;
            opts.HeaderTemplate = await File.ReadAllTextAsync(s.HeaderHtmlPath, ct);
            opts.FooterTemplate = await File.ReadAllTextAsync(s.FooterHtmlPath, ct);
        }
        else
        {
            bodyToLoad = HtmlMerger.MergeRunning(s);
            opts.DisplayHeaderFooter = false;
        }

        bool ok;
        try
        {
            await page.GotoAsync(new Uri(bodyToLoad).AbsoluteUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.PdfAsync(opts);
            ok = File.Exists(s.OutputPdf);
        }
        catch (Exception ex) { log.AppendLine(ex.ToString()); ok = false; }

        return new GenerationResult
        {
            EndedInSuccess = ok,
            ProcessOutput = log.ToString(),
            OutputFile = s.OutputPdf,
            ProcessTime = Stopwatch.GetElapsedTime(startedAt),
            OutputFileSizeBytes = ok ? new FileInfo(s.OutputPdf).Length : 0,
            Strategy = strategy
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _pw.Dispose();
    }
}
```

### 8. `Program.cs` — harness

```csharp
using PlaywrightPdfPoc;

if (!await PdfEngine.IsAvailableAsync())
{
    Console.WriteLine("Chromium not available — run: pwsh playwright.ps1 install chromium");
    return 1;
}

var samplesRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples");
var outputDir = Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outputDir);

await using var svc = await PdfGeneratorService.CreateAsync();

Console.WriteLine($"{"Report",-28} {"Strategy",-12} {"OK",-6} {"ms",-8} {"KB",-8}");
foreach (var dir in Directory.GetDirectories(samplesRoot))
{
    var input = SampleInput.FromFolder(dir, outputDir);
    foreach (var run in new Func<Task<GenerationResult>>[]
    {
        () => svc.ConvertNativeAsync(input.WithOutputSuffix("native"), default),
        () => svc.ConvertCssRunningAsync(input.WithOutputSuffix("css"), default),
    })
    {
        var r = await run();
        Console.WriteLine($"{Path.GetFileName(dir),-28} {r.Strategy,-12} {r.EndedInSuccess,-6} " +
                          $"{r.ProcessTime.TotalMilliseconds,-8:F0} {r.OutputFileSizeBytes / 1024,-8}");
        if (!r.EndedInSuccess) Console.WriteLine(r.ProcessOutput);
    }
}
return 0;
```

## Harvesting real sample HTML

The three chosen report types' real `body.html` / `header.html` / `footer.html` / `settings.xml` should come from an actual run of the original reporting service, not hand-written stand-ins — this is what makes the POC results trustworthy.

If you have access to the original service's source/binaries:
- It's a .NET reporting worker with a `-debug` startup flag that (a) skips its normal cache cleanup and (b) skips deleting the generated `body/header/footer.html` temp files after a successful conversion — so running it once per report type with `-debug` and triggering a conversion leaves the real HTML on disk to copy out (its output folder is `<service bin dir>/Output/C_<correlationId>/...`).
- Each report type's `settings.xml` lives next to that report's HTML template folder (grouped by report type name).
- Also copy the wkhtmltopdf-produced PDF from that same run as `wkhtml-reference.pdf` for visual side-by-side comparison.

If that access isn't available in this environment, hand-author representative samples instead (plain body + simple header/footer; a header/footer with embedded images/CSS styling; a landscape A3/A2-sized page) and clearly mark them as synthetic in `RESULTS.md`, since findings from synthetic samples about visual fidelity are weaker evidence than from real ones.

Constraint to preserve either way: keep all sample folder/file paths **free of spaces** and use only relative asset references within each sample folder, so both engines are being tested against equally resolvable input.

## Evaluation & success criteria

After running the harness, write `RESULTS.md` covering, per sample × strategy:
- Success/failure, render time (ms), output file size (KB), and a visual comparison verdict against `wkhtml-reference.pdf` (open both side-by-side).
- Which header/footer strategy (native vs CSS running) wins overall, and whether it depends on how rich the header/footer content is.
- Any `settings.xml` key encountered that has no Playwright/Chromium equivalent.
- The image quality/size delta versus wkhtmltopdf's `--lowquality`/`--image-dpi` tuning (Chromium's PDF export has no direct equivalent knob).
- The Chromium browser binary's install size (~150 MB) as a deployment-cost note, plus a perf note on browser-reused/context-per-job vs process-per-job now that both are numerically measured.

Conclude with a clear recommendation: is Playwright viable as a wkhtmltopdf replacement, and if so, which header/footer strategy should the real port use.

## Verification

1. `dotnet build` succeeds.
2. `pwsh bin/Debug/net8.0/playwright.ps1 install chromium` completes without error.
3. `dotnet run` against the `samples/` folder produces a PDF per sample × strategy in `out/`, with a printed comparison table with no unexpected failures.
4. Open each generated PDF and its matching `wkhtml-reference.pdf` side-by-side to sanity-check visual parity before writing conclusions in `RESULTS.md`.
