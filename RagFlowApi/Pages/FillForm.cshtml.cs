using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

[Authorize]
public class FillFormModel : PageModel
{
    private readonly DocxFormFillerService _filler;
    private readonly FormTemplateCache     _cache;
    private readonly FormLibraryStore      _library;

    public List<FormField> Fields        { get; private set; } = [];
    public string?         TemplateId    { get; private set; }
    public string?         ErrorMessage  { get; private set; }
    public string?         SuccessMessage { get; private set; }
    public bool            FromCache     { get; private set; }
    public bool            FromLibrary   { get; private set; }

    public FillFormModel(DocxFormFillerService filler, FormTemplateCache cache,
        FormLibraryStore library)
    {
        _filler  = filler;
        _cache   = cache;
        _library = library;
    }

    public void OnGet() { }

    // ── Step 1: upload template → detect fields → suggest values ─────────────
    public async Task<IActionResult> OnPostDetectAsync(IFormFile? template)
    {
        var ext = Path.GetExtension(template?.FileName ?? "").ToLowerInvariant();
        if (template == null || template.Length == 0 || (ext != ".docx" && ext != ".txt"))
        {
            ErrorMessage = "Please upload a .docx or .txt file.";
            return Page();
        }

        try
        {
            // Read bytes once — needed for both hashing and saving
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await template.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            // Cache hit: skip detection + AI suggestion entirely
            var cached = await _cache.GetAsync(hash);
            if (cached != null)
            {
                TemplateId = await _filler.SaveTemplateBytesAsync(bytes, template.FileName);
                Fields     = cached;
                FromCache  = true;
                return Page();
            }

            // Cache miss: full detection + suggestion pipeline
            TemplateId = await _filler.SaveTemplateBytesAsync(bytes, template.FileName);
            Fields     = _filler.DetectFields(TemplateId);

            if (Fields.Count == 0)
            {
                ErrorMessage =
                    "No form fields detected. Supported patterns: Content Controls, " +
                    "{{Field Name}} placeholders, MERGEFIELD, or \"Label: ……\" blanks.";
                _filler.DeleteTemplate(TemplateId);
                TemplateId = null;
                return Page();
            }

            Fields = await _filler.SuggestValuesAsync(Fields);

            // Store result so future uploads of the same file are instant
            await _cache.SetAsync(hash, Fields);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not process file: {ex.Message}";
        }

        return Page();
    }

    // ── Step 2: accept (possibly edited) values → fill → download ────────────
    public IActionResult OnPostFill(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return BadRequest();

        var values = new Dictionary<string, string>();
        foreach (var formKey in Request.Form.Keys)
        {
            if (!formKey.StartsWith("v_")) continue;
            values[formKey[2..]] = Request.Form[formKey].ToString();
        }

        try
        {
            var contentType  = _filler.GetTemplateContentType(templateId);
            var downloadName = _filler.GetTemplateDownloadName(templateId);
            var filled       = _filler.FillTemplate(templateId, values);
            return File(filled, contentType, downloadName);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fill failed: {ex.Message}";
            return Page();
        }
    }

    // ── Step 2b: save current template to the library ─────────────────────────
    public async Task<IActionResult> OnPostSaveToLibraryAsync(
        string templateId, string libraryName)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return BadRequest();
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            ErrorMessage = "Please enter a name for the library entry.";
            return Page();
        }

        try
        {
            var path  = _filler.GetTemplatePath(templateId);
            var bytes = await System.IO.File.ReadAllBytesAsync(path);
            var fileName = Path.GetFileName(path);

            var fields = _filler.DetectFields(templateId);
            await _library.AddAsync(libraryName, fileName, bytes, fields,
                User.Identity!.Name!);

            SuccessMessage = $"Saved \"{libraryName}\" to the form library.";
            TemplateId = templateId;
            Fields = fields;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not save to library: {ex.Message}";
        }

        return Page();
    }

    // ── Load from library → detect step bypassed ──────────────────────────────
    public async Task<IActionResult> OnGetFromLibraryAsync(string id)
    {
        var entry = await _library.GetByIdAsync(id);
        if (entry is null)
        {
            ErrorMessage = "Template not found in library.";
            return Page();
        }

        var bytes = _library.GetBytes(id);
        if (bytes is null)
        {
            ErrorMessage = "Template file missing from library.";
            return Page();
        }

        TemplateId  = await _filler.SaveTemplateBytesAsync(bytes, entry.FileName);
        Fields      = entry.Fields;
        FromLibrary = true;
        return Page();
    }
}
