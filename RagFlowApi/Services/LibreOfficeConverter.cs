namespace RagFlowApi.Services;

// Shared headless LibreOffice conversion used by both IngestionPipeline (docx preview)
// and DocxFormFillerService (form preview). Isolated here so the path-resolution and
// per-job profile logic live in exactly one place.
//
// Requires LibreOffice installed. soffice.exe is resolved directly rather than
// relying on PATH because winget installs don't always register it there.
// Each call gets its own isolated LO user profile so a running GUI instance
// does not intercept the headless conversion (without this, LO silently exits 0
// and produces no output file).
internal static class LibreOfficeConverter
{
    private static readonly string[] SofficeCandidates =
    [
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        "soffice"
    ];

    internal static async Task<byte[]?> ConvertToPdfAsync(byte[] docxBytes, string fileName)
    {
        var sofficePath = SofficeCandidates.FirstOrDefault(File.Exists) ?? "soffice";
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var docxPath = Path.Combine(tempDir, fileName);
        try
        {
            await File.WriteAllBytesAsync(docxPath, docxBytes);
            var loProfile = "file:///" + tempDir.Replace('\\', '/') + "/lo-profile";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = sofficePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // Use ArgumentList (not Arguments) so .NET handles per-token quoting —
            // string-style Arguments fails when file paths contain spaces.
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add($"-env:UserInstallation={loProfile}");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add("pdf");
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(tempDir);
            psi.ArgumentList.Add(docxPath);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync();
            var pdfPath = Path.ChangeExtension(docxPath, ".pdf");
            return File.Exists(pdfPath) ? await File.ReadAllBytesAsync(pdfPath) : null;
        }
        catch { return null; }
        finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
    }
}
