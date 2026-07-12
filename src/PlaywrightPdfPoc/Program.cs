using PlaywrightPdfPoc;

if (!await PdfEngine.IsAvailableAsync())
{
    Console.WriteLine("Chromium not available — run: pwsh bin/Debug/net8.0/playwright.ps1 install chromium");
    return 1;
}

var samplesRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples");
var outputDir = Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outputDir);

if (!Directory.Exists(samplesRoot))
{
    Console.WriteLine($"No samples folder found at {Path.GetFullPath(samplesRoot)} — see PLAN.md's harvesting steps.");
    return 1;
}

var wkhtmlAvailable = WkHtmlBaseline.IsAvailable;
if (!wkhtmlAvailable)
    Console.WriteLine("wkhtmltopdf.exe not found — skipping baseline runs.");

await using var svc = await PdfGeneratorService.CreateAsync();

Console.WriteLine($"{"Report",-28} {"Strategy",-18} {"OK",-6} {"ms",-8} {"KB",-8}");
foreach (var dir in Directory.GetDirectories(samplesRoot))
{
    var input = SampleInput.FromFolder(dir, outputDir);

    var runs = new List<Func<Task<GenerationResult>>>
    {
        () => svc.ConvertNativeAsync(input.WithOutputSuffix("native"), default),
        () => svc.ConvertCssRunningAsync(input.WithOutputSuffix("css-running"), default),
        () => svc.ConvertTableRepeatingAsync(input.WithOutputSuffix("table-repeating"), default),
    };
    if (wkhtmlAvailable)
        runs.Add(() => WkHtmlBaseline.ConvertAsync(input.WithOutputSuffix("wkhtmltopdf"), default));

    foreach (var run in runs)
    {
        var r = await run();
        Console.WriteLine($"{Path.GetFileName(dir),-28} {r.Strategy,-18} {r.EndedInSuccess,-6} " +
                           $"{r.ProcessTime.TotalMilliseconds,-8:F0} {r.OutputFileSizeBytes / 1024,-8}");
        if (!r.EndedInSuccess) Console.WriteLine(r.ProcessOutput);
    }
}

return 0;
