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
