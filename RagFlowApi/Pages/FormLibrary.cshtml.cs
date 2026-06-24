using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

[Authorize]
public class FormLibraryModel : PageModel
{
    private readonly FormLibraryStore _library;

    public List<FormTemplate> Templates   { get; private set; } = [];
    public string?            ErrorMessage { get; private set; }

    public FormLibraryModel(FormLibraryStore library)
    {
        _library = library;
    }

    public async Task OnGetAsync()
        => Templates = await _library.GetAllAsync();

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        var entry = await _library.GetByIdAsync(id);
        if (entry is not null &&
            (User.IsInRole("admin") || entry.UploadedBy == User.Identity!.Name))
        {
            await _library.DeleteAsync(id);
        }
        else
        {
            ErrorMessage = "You do not have permission to delete this template.";
            Templates = await _library.GetAllAsync();
            return Page();
        }

        return RedirectToPage();
    }
}
