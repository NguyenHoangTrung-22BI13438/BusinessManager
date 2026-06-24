using RagFlowApi.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace RagFlowApi.Services;

/// <summary>
/// Groups layout elements into retrieval-ready chunks keyed on section headers.
///
/// Rules:
///   • A new Title or SectionHeader starts a new section — the previous
///     section is flushed first.
///   • Within a section, text/list/caption/footnote elements accumulate
///     into a single text chunk.
///   • Each Table in a section becomes its own chunk (split by rows if
///     large), but always carries the section header so it is self-contained.
///   • A section with no content before the next header never produces a
///     header-only chunk — the header is simply carried forward.
///   • PageHeader / PageFooter are discarded.
///   • Picture elements are discarded (no text to embed).
///   • Page boundaries are NOT used as flush triggers — a section that
///     spans two pages stays in one chunk.
/// </summary>
public class LayoutChunker
{
    // Hard ceiling: a text chunk this long (~512 tokens) gets flushed early
    // so the embedding model doesn't receive an oversized input.
    private const int MaxCharsPerChunk = 8000;

    public List<IngestionChunk> Chunk(List<LayoutElement> elements)
    {
        var result   = new List<IngestionChunk>();
        string? header = null;                 // current section heading text
        var     textBuf = new List<LayoutElement>(); // text/list/etc. accumulator

        // ── Emit the accumulated text buffer as one chunk ─────────────────────
        void FlushText()
        {
            if (textBuf.Count == 0) return;

            var body = string.Join("\n\n",
                textBuf
                    .Where(e => !string.IsNullOrWhiteSpace(e.Text))
                    .Select(e => e.Text!.Trim()));

            if (!string.IsNullOrWhiteSpace(body))
            {
                var content = header != null
                    ? $"## {header}\n\n{body}"
                    : body;

                result.Add(new IngestionChunk(
                    Content:     content,
                    ContentType: "text",
                    PageNumber:  textBuf[0].Page,
                    Bbox:        Combine(textBuf.Select(e => e.Bbox)),
                    SectionPath: header));
            }

            textBuf.Clear();
        }

        // ── Emit a table element as one-or-more chunks ────────────────────────
        void FlushTable(LayoutElement el)
        {
            var tableChunks = SplitTable(el, header);
            result.AddRange(tableChunks);
        }

        // ── Flush everything accumulated under the current header ─────────────
        void FlushSection()
        {
            FlushText();
            // (tables were emitted immediately when encountered — nothing extra here)
        }

        foreach (var el in elements)
        {
            switch (el.Category)
            {
                // ── Noise: discard ────────────────────────────────────────────
                case LayoutCategory.PageHeader:
                case LayoutCategory.PageFooter:
                case LayoutCategory.Picture:
                    break;

                // ── New section boundary ──────────────────────────────────────
                case LayoutCategory.Title:
                case LayoutCategory.SectionHeader:
                    FlushSection();                     // close previous section
                    header = el.Text?.Trim();           // open new section
                    break;

                // ── Table: flush text first, then emit table immediately ──────
                // This ensures paragraph+table within one section each get
                // their own chunk, both stamped with the same header.
                case LayoutCategory.Table:
                    FlushText();                        // emit preceding text
                    FlushTable(el);                     // emit the table
                    break;

                // ── Text content: accumulate ──────────────────────────────────
                default: // Text, ListItem, Caption, Footnote, Formula, Unknown
                    textBuf.Add(el);
                    // Safety split if the buffer grows very large
                    if (textBuf.Sum(e => e.Text?.Length ?? 0) >= MaxCharsPerChunk)
                        FlushText();
                    break;
            }
        }

        // Flush whatever remains in the last section
        FlushSection();

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BBox? Combine(IEnumerable<BBox?> boxes)
    {
        var list = boxes.Where(b => b is not null).Select(b => b!).ToList();
        if (list.Count == 0) return null;
        return new BBox(
            list.Min(b => b.X1), list.Min(b => b.Y1),
            list.Max(b => b.X2), list.Max(b => b.Y2));
    }

    /// <summary>
    /// Emits a table element as one or more chunks. Small tables (≤ 1500 chars)
    /// are a single chunk. Larger tables are split row-by-row with the header
    /// row repeated in each split so every chunk is self-contained.
    /// The section heading is prepended to every produced chunk.
    /// </summary>
    private static List<IngestionChunk> SplitTable(LayoutElement el, string? section)
    {
        var html = el.Text ?? "";

        string Prefix() => section != null ? $"## {section}\n\n" : "";

        // Small table — emit as-is
        if (html.Length <= 1500)
            return [new IngestionChunk(
                Content:     $"{Prefix()}{html}",
                ContentType: "table",
                PageNumber:  el.Page,
                Bbox:        el.Bbox,
                SectionPath: section)];

        // Large table — split by <tr> rows, repeat the header row in each batch
        var rows = Regex.Matches(html, @"<tr[\s\S]*?</tr>", RegexOptions.IgnoreCase)
                        .Select(m => m.Value)
                        .ToList();

        // No parseable rows — fall back to character-window splits
        if (rows.Count == 0)
        {
            return [.. Enumerable
                .Range(0, (html.Length + 1499) / 1500)
                .Select(i => new IngestionChunk(
                    Content:     $"{Prefix()}{html.Substring(i * 1500, Math.Min(1500, html.Length - i * 1500))}",
                    ContentType: "table",
                    PageNumber:  el.Page,
                    Bbox:        el.Bbox,
                    SectionPath: section))];
        }

        var headerRow = rows[0];   // first <tr> treated as the column-header row
        var result    = new List<IngestionChunk>();
        var batch     = new StringBuilder();

        foreach (var row in rows.Skip(1))
        {
            if (batch.Length + row.Length > 1200)
            {
                result.Add(new IngestionChunk(
                    Content:     $"{Prefix()}<table>{headerRow}{batch}</table>",
                    ContentType: "table",
                    PageNumber:  el.Page,
                    Bbox:        el.Bbox,
                    SectionPath: section));
                batch.Clear();
            }
            batch.Append(row);
        }

        if (batch.Length > 0)
            result.Add(new IngestionChunk(
                Content:     $"{Prefix()}<table>{headerRow}{batch}</table>",
                ContentType: "table",
                PageNumber:  el.Page,
                Bbox:        el.Bbox,
                SectionPath: section));

        return result;
    }
}
