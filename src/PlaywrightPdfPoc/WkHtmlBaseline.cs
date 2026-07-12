using System.Diagnostics;
using System.Xml;

namespace PlaywrightPdfPoc;

// Shells out to the real wkhtmltopdf.exe using the exact CLI arg string built by the
// original service's ArgumentsForWkHtmlToPdf/GetPageSettings (ReportGeneratorService.cs in
// ajoursystem-build-converterservice), so the harness has a genuine before/after baseline
// instead of only comparing Playwright's strategies against each other.
public static class WkHtmlBaseline
{
    private const string ExePath = @"C:\Program Files\wkhtmltopdf\bin\wkhtmltopdf.exe";

    public static bool IsAvailable => File.Exists(ExePath);

    public static async Task<GenerationResult> ConvertAsync(SampleInput s, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var pageSettings = GetPageSettingsFlags(s.TemplateFolder);
        var arguments = $"{pageSettings} --margin-bottom 15 --disable-javascript --dpi 96 --lowquality " +
                         $"--image-quality 80 --image-dpi 300 --no-outline --disable-internal-links " +
                         $"\"{s.BodyHtmlPath}\" --header-html \"{s.HeaderHtmlPath}\" --header-spacing 0 " +
                         $"--footer-html \"{s.FooterHtmlPath}\" --footer-spacing 0 \"{s.OutputPdf}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        string output;
        bool ok;
        try
        {
            using var process = Process.Start(startInfo)!;
            var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            output = await stdOutTask + await stdErrTask;
            ok = process.ExitCode == 0;
        }
        catch (Exception ex) { output = ex.ToString(); ok = false; }

        // Success judged strictly by exit code, matching the original service; a non-empty
        // output does not by itself mean failure since wkhtmltopdf writes to stderr on
        // warnings too.
        if (!ok) TryDelete(s.OutputPdf);

        return new GenerationResult
        {
            EndedInSuccess = ok,
            ProcessOutput = output,
            OutputFile = s.OutputPdf,
            ProcessTime = Stopwatch.GetElapsedTime(startedAt),
            OutputFileSizeBytes = ok && File.Exists(s.OutputPdf) ? new FileInfo(s.OutputPdf).Length : 0,
            Strategy = "wkhtmltopdf-baseline"
        };
    }

    private static string GetPageSettingsFlags(string templateFolderPath)
    {
        var path = Path.Combine(templateFolderPath, "settings.xml");
        if (!File.Exists(path)) return "";
        try
        {
            var dict = new Dictionary<string, string>();
            var doc = new XmlDocument();
            doc.Load(path);
            foreach (XmlNode n in doc.SelectNodes("//page/*")!)
                dict[n.Name] = n.InnerText.ToLowerInvariant();
            return string.Join(" ", dict.Select(x => $"--{x.Key} {x.Value}"));
        }
        catch { return ""; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* mirrors the original: corrupt output is removed on failure */ }
    }
}
