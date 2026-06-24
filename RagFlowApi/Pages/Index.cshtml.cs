using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RagFlowApi.Pages;

public class IndexModel : PageModel
{
    // Upload now lives inside the chat (Ask page); root just redirects there.
    public IActionResult OnGet() => RedirectToPage("Ask");
}
