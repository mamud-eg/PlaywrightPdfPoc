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
