namespace PlaywrightPdfPoc;

public class GenerationResult
{
    public bool EndedInSuccess;
    public string ProcessOutput = "";
    public string OutputFile = "";
    public TimeSpan ProcessTime;
    public long OutputFileSizeBytes;
    public string Strategy = ""; // "native" | "css-running" | "table-repeating" | "wkhtmltopdf-baseline"
}
