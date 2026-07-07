using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using RagFlowApi.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RagFlowApi.Services;

/// <param name="Source">"profile" = value came from the user's saved profile; "ai" = suggested by RAGFlow; "" = no suggestion yet.</param>
public record FormField(string Key, string Label, string SuggestedValue = "", string Source = "");

public class DocxFormFillerService
{
    private readonly RagFlowService _ragflow;
    private readonly UserContext _userContext;
    private readonly ILogger<DocxFormFillerService> _log;
    private readonly string _tempDir;

    // Matches 4+ consecutive dots or Unicode ellipsis chars (avoids abbreviations like "v.v.")
    private static readonly Regex DotRunRx        = new(@"^[\.…\s]+$", RegexOptions.Compiled);
    private static readonly Regex HasDotsRx       = new(@"[\.…]{4,}", RegexOptions.Compiled);
    // "Label:" at end of accumulated paragraph text
    private static readonly Regex LabelColonRx    = new(@"([^:：\r\n]{2,60})[：:]\s*$", RegexOptions.Compiled);
    // "Label: ……" in the same text run — matches 1+ dots/ellipsis containing at least one …
    // The lookahead (?=[\.…]*…) ensures we don't match ASCII-period-only sequences like "v.v."
    private static readonly Regex InlineRx        = new(@"([^:：]{2,60})[：:]\s*((?=[\.…]*…)[\.…]+)", RegexOptions.Compiled);
    // "Label: từ ngày…/…/…" — blank embedded in surrounding text (2+ ellipsis chars anywhere after colon)
    private static readonly Regex MultiEllipsisRx = new(@"([^:：]{2,60})[：:].{0,12}(?:.*?…){2,}", RegexOptions.Compiled);
    // MERGEFIELD instruction text
    private static readonly Regex MergeFieldRx    = new(@"MERGEFIELD\s+(\S+)", RegexOptions.Compiled);

    public DocxFormFillerService(
        RagFlowService ragflow,
        UserContext userContext,
        ILogger<DocxFormFillerService> log,
        IWebHostEnvironment env)
    {
        _ragflow = ragflow;
        _userContext = userContext;
        _log = log;
        _tempDir = Path.Combine(env.ContentRootPath, "temp-forms");
        Directory.CreateDirectory(_tempDir);
    }

    // ── Save uploaded template (preserves .docx or .txt extension) ──────────
    public async Task<string> SaveTemplateAsync(IFormFile file)
    {
        CleanupStaleFiles();
        var id  = Guid.NewGuid().ToString("N");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        using var fs = File.Create(Path.Combine(_tempDir, $"{id}{ext}"));
        await file.CopyToAsync(fs);
        return id;
    }

    /// <summary>Save pre-read bytes as a template (avoids re-reading when the caller already has the bytes).</summary>
    public async Task<string> SaveTemplateBytesAsync(byte[] bytes, string fileName)
    {
        CleanupStaleFiles();
        var id  = Guid.NewGuid().ToString("N");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, $"{id}{ext}"), bytes);
        return id;
    }

    public string GetTemplateContentType(string templateId)
    {
        var path = GetTemplatePath(templateId);
        return path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? "text/plain; charset=utf-8"
            : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    }

    public string GetTemplateDownloadName(string templateId)
    {
        var path = GetTemplatePath(templateId);
        return path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? "filled-form.txt"
            : "filled-form.docx";
    }

    private static readonly string[] SofficeCandidates =
    [
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        "soffice"
    ];

    // ── Form preview (PDF for docx, HTML for txt) ─────────────────────────────
    public async Task<(byte[] Data, string ContentType)?> GetPreviewAsync(string templateId)
    {
        var path = GetTemplatePath(templateId);

        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var text = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
            var escaped = System.Net.WebUtility.HtmlEncode(text);
            var html = $"<html><body style=\"font-family:monospace;white-space:pre-wrap;" +
                       $"padding:20px;background:#1e2335;color:#e8eaf2;font-size:13px;" +
                       $"line-height:1.7\">{escaped}</body></html>";
            return (System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
        }

        // Check cached preview
        var pdfCache = Path.Combine(_tempDir, templateId + "-preview.pdf");
        if (File.Exists(pdfCache))
            return (await File.ReadAllBytesAsync(pdfCache), "application/pdf");

        var pdfBytes = await ConvertDocxToPdfAsync(path);
        if (pdfBytes is null) return null;

        await File.WriteAllBytesAsync(pdfCache, pdfBytes);
        return (pdfBytes, "application/pdf");
    }

    private static async Task<byte[]?> ConvertDocxToPdfAsync(string docxPath)
    {
        var sofficePath = SofficeCandidates.FirstOrDefault(File.Exists) ?? "soffice";
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempDocx = Path.Combine(tempDir, "form.docx");
        try
        {
            File.Copy(docxPath, tempDocx, overwrite: true);
            var loProfile = "file:///" + tempDir.Replace('\\', '/') + "/lo-profile";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = sofficePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add($"-env:UserInstallation={loProfile}");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add("pdf");
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(tempDir);
            psi.ArgumentList.Add(tempDocx);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync();
            var pdf = Path.Combine(tempDir, "form.pdf");
            return File.Exists(pdf) ? await File.ReadAllBytesAsync(pdf) : null;
        }
        catch { return null; }
        finally { try { Directory.Delete(tempDir, recursive: true); } catch { } }
    }

    // ── Detect fields — dispatches by file type ───────────────────────────────
    public List<FormField> DetectFields(string templateId)
    {
        var path = GetTemplatePath(templateId);
        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return DetectTxtFields(path);

        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;

        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<FormField>();

        void Add(string key, string label)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(label) && seen.Add(key))
                fields.Add(new FormField(Key: key, Label: label));
        }

        // 1. Content Controls (SdtBlock / SdtRun with Alias or Tag)
        foreach (var props in GetAllSdtProperties(body))
        {
            var alias = props.GetFirstChild<SdtAlias>()?.Val?.Value;
            var tag   = props.GetFirstChild<Tag>()?.Val?.Value;
            var label = alias ?? tag;
            if (!string.IsNullOrWhiteSpace(label))
                Add(tag ?? Slugify(label), label);
        }

        // 2. MERGEFIELD (simple <w:fldSimple> and complex <w:instrText>)
        foreach (var sf in body.Descendants<SimpleField>())
        {
            var m = MergeFieldRx.Match(sf.Instruction?.Value ?? "");
            if (m.Success) Add(m.Groups[1].Value, HumanizeMergeKey(m.Groups[1].Value));
        }
        foreach (var instrText in body.Descendants<FieldCode>())
        {
            var m = MergeFieldRx.Match(instrText.InnerText);
            if (m.Success) Add(m.Groups[1].Value, HumanizeMergeKey(m.Groups[1].Value));
        }

        // 3. {{Placeholder}} patterns
        var fullText = string.Concat(body.Descendants<Text>().Select(t => t.Text));
        foreach (Match m in Regex.Matches(fullText, @"\{\{([^}]+)\}\}"))
        {
            var label = m.Groups[1].Value.Trim();
            Add(Slugify(label), label);
        }

        // 4. "Label: ……………" (dots in same run or dots-only next run)
        // 5. "Label:" with only whitespace after (empty colon fields)
        var paraList = body.Descendants<Paragraph>().ToList();
        DetectParagraphFields(paraList, seen, fields);

        return fields;
    }

    // ── Suggest values: profile first, then RAG for any remaining gaps ───────
    public async Task<List<FormField>> SuggestValuesAsync(List<FormField> fields)
    {
        if (fields.Count == 0) return fields;

        // Step 1 — Fill from the user's saved profile (instant, no network call).
        var record        = await _userContext.GetRecordAsync();
        var profileValues = BuildProfileValues(record);

        var result = fields.Select(f =>
        {
            if (profileValues.TryGetValue(f.Key, out var pv) && !string.IsNullOrWhiteSpace(pv))
                return f with { SuggestedValue = pv, Source = "profile" };

            // Preserve any value already carried in (e.g., loaded from the form library).
            return string.IsNullOrWhiteSpace(f.SuggestedValue)
                ? f
                : f with { Source = "ai" };
        }).ToList();

        // Step 2 — For fields still empty, ask RAGFlow.
        var needsAi = result.Where(f => string.IsNullOrWhiteSpace(f.SuggestedValue)).ToList();
        if (needsAi.Count == 0) return result;

        var assistantId = await _userContext.EnsureAssistantAsync();
        var sessionId   = await _ragflow.CreateSessionAsync(
            assistantId, $"__formfill_{Guid.NewGuid():N}");
        if (sessionId == null) return result;

        try
        {
            var fieldList = string.Join("\n", needsAi.Select(f => $"  - {f.Key}: {f.Label}"));
            var question  =
                "You are a form-filling assistant. Based on the documents in the knowledge base, " +
                "provide values for the following form fields.\n" +
                "Reply ONLY with a valid JSON object mapping each field key to its value. " +
                "Use an empty string when the information is not in the documents.\n\n" +
                $"Fields:\n{fieldList}\n\n" +
                "Example: {\"field_key\": \"value\"}";

            var completionJson = await _ragflow.AskQuestionAsync(assistantId, sessionId, question);
            var answerText     = ExtractAnswerText(completionJson);
            var jsonMatch      = Regex.Match(answerText, @"\{[\s\S]*\}");

            if (jsonMatch.Success)
            {
                using var jdoc = JsonDocument.Parse(jsonMatch.Value);
                result = result.Select(f =>
                {
                    if (!string.IsNullOrWhiteSpace(f.SuggestedValue)) return f;   // already filled
                    var val = jdoc.RootElement.TryGetProperty(f.Key, out var v)
                              ? v.GetString() ?? "" : "";
                    return string.IsNullOrWhiteSpace(val) ? f : f with { SuggestedValue = val.Trim(), Source = "ai" };
                }).ToList();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Form-fill RAG suggestion failed");
        }
        finally
        {
            try { await _ragflow.DeleteSessionAsync(assistantId, sessionId!); } catch { }
        }

        return result;
    }

    // ── Build a lookup from slugified field keys → user profile values ────────
    private static Dictionary<string, string> BuildProfileValues(UserRecord user)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Set(string value, params string[] keys)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (var k in keys) map[k] = value;
        }

        Set(user.FullName,
            "ho_va_ten", "ho_ten", "ten", "full_name", "name",
            "nguoi_lao_dong", "ho_va_ten_nguoi_lao_dong", "hovaten",
            "ho_va_ten_nguoi_duoc_dao_tao", "nguoi_duoc_dao_tao");

        Set(user.DateOfBirth,
            "ngay_sinh", "ngay_thang_nam_sinh", "sinh_ngay",
            "date_of_birth", "dob", "nam_sinh", "ngaysinh");

        Set(user.PlaceOfBirth,
            "noi_sinh", "que_quan", "place_of_birth", "noi_sinh_nguyen_quan");

        Set(user.Nationality,
            "quoc_tich", "nationality", "dan_toc");

        Set(user.IdNumber,
            "so_cmnd", "so_cccd", "cmnd", "cccd",
            "can_cuoc", "id_number", "so_chung_minh_nhan_dan",
            "so_chung_minh", "so_cmndcccd", "cmndcccd",
            "so_giay_to", "giay_to_tuy_than");

        Set(user.IdIssuedDate,
            "ngay_cap", "cap_ngay", "issued_date", "ngay_cap_cmnd", "ngay_cap_cccd");

        Set(user.IdIssuedPlace,
            "noi_cap", "co_quan_cap", "issued_place", "doi_cap", "noi_cap_cmnd");

        Set(user.JobTitle,
            "chuc_vu", "chuc_danh", "vi_tri", "role",
            "job_title", "position", "nghe_nghiep", "nghe_nghiep_hien_tai");

        Set(user.Department,
            "phong_ban", "don_vi", "department", "to_chuc", "co_quan",
            "co_quan_cong_tac", "don_vi_cong_tac");

        Set(user.PhoneNumber,
            "so_dien_thoai", "dien_thoai", "phone", "phone_number",
            "sdt", "dien_thoai_di_dong", "so_dtdd");

        Set(user.Email,
            "email", "thu_dien_tu", "e_mail", "dia_chi_email", "dia_chi_thu_dien_tu");

        Set(user.Address,
            "dia_chi", "noi_o", "noi_cu_tru", "address",
            "dia_chi_thuong_tru", "dia_chi_lien_lac", "dia_chi_hien_tai",
            "dia_chi_noi_o", "ho_khau_thuong_tru");

        return map;
    }

    // ── Fill template — dispatches by file type ───────────────────────────────
    public byte[] FillTemplate(string templateId, Dictionary<string, string> values)
    {
        var path = GetTemplatePath(templateId);
        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return FillTxt(path, values);

        var source = File.ReadAllBytes(path);
        var ms = new MemoryStream();
        ms.Write(source);
        ms.Position = 0;

        // Must dispose doc before reading ms — the OpenXML package only flushes
        // the ZIP content back to the underlying MemoryStream on Dispose/Close.
        // Using `using var` would call Dispose AFTER ms.ToArray(), returning stale bytes.
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var body = doc.MainDocumentPart!.Document!.Body!;

            FillContentControls(body, values);
            FillMergeFields(body, values);
            FillPlaceholders(body, values);
            FillParagraphBlanks(body, values);
            FillColonWhitespaceBlanks(body, values);

            doc.MainDocumentPart.Document.Save();
        }

        return ms.ToArray();
    }

    public void DeleteTemplate(string templateId)
    {
        try { File.Delete(GetTemplatePath(templateId)); } catch { }
    }

    // ── Detection helpers ─────────────────────────────────────────────────────

    // Handles both "Label: ………" and "Label:" with empty-space-only value
    private static void DetectParagraphFields(
        IList<Paragraph> paraList, HashSet<string> seen, List<FormField> fields)
    {
        // fromDots=true: label came from an unambiguous dot/ellipsis blank → skip word-count limit
        void Add(string label, bool fromDots = false)
        {
            label = CleanLabel(label);
            if (label.Length < 2) return;
            if (label.Contains(',') || label.Contains('，')) return;
            // Word-count guard only for colon-only fields (dots are unambiguous blank markers)
            if (!fromDots && label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 7) return;
            var key = Slugify(label);
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                fields.Add(new FormField(Key: key, Label: label));
        }

        for (int pi = 0; pi < paraList.Count; pi++)
        {
            var para = paraList[pi];

            // Detect whether this paragraph and the next are numbered list items.
            // Used to skip "Label:" lines that introduce bullet lists rather than blank fields.
            var pPr = para.GetFirstChild<ParagraphProperties>();
            bool paraHasNumPr = pPr?.GetFirstChild<NumberingProperties>() != null;
            bool nextHasNumPr = pi + 1 < paraList.Count &&
                paraList[pi + 1].GetFirstChild<ParagraphProperties>()
                                ?.GetFirstChild<NumberingProperties>() != null;

            int dotRunCount = para.Elements<Run>().Count(r =>
            {
                var t = string.Concat(r.Elements<Text>().Select(x => x.Text));
                return HasDotsRx.IsMatch(t) && DotRunRx.IsMatch(t);
            });
            bool isTemplateLine = dotRunCount >= 3;

            var accumulated = new StringBuilder();
            bool hasNonWhitespaceAfterColon = false;
            bool colonSeen = false;

            foreach (var run in para.Descendants<Run>())
            {
                var text = string.Concat(run.Elements<Text>().Select(t => t.Text));

                // Case A: label and dots in the same run, e.g. "Tên: …………………"
                foreach (Match m in InlineRx.Matches(text))
                    Add(m.Groups[1].Value, fromDots: true);

                // Case G: label followed by text with 2+ ellipsis chars but not pure-dot
                // e.g. "Thời gian đào tạo: từ ngày…/…/…đến ngày…/…/…"
                if (text.Count(c => c == '…') >= 2 && !InlineRx.IsMatch(text))
                {
                    var gm = MultiEllipsisRx.Match(text);
                    if (gm.Success) Add(gm.Groups[1].Value, fromDots: true);
                }

                // Case B: dots-only run — label is in accumulated text (colon required)
                if (!isTemplateLine && DotRunRx.IsMatch(text) && HasDotsRx.IsMatch(text))
                {
                    var lm = LabelColonRx.Match(accumulated.ToString());
                    if (lm.Success) Add(lm.Groups[1].Value, fromDots: true);
                }

                // Case F: single standalone ellipsis run — label is the last word in accumulated
                // (date pattern: "ngày… tháng… năm …" with no colon before the dots)
                if (text == "…" && !colonSeen)
                {
                    var wm = Regex.Match(accumulated.ToString(), @"(\p{L}+)\s*$");
                    if (wm.Success)
                    {
                        var w = wm.Groups[1].Value;
                        if (w.Length >= 2 && w.Length <= 15) Add(w, fromDots: true);
                    }
                }

                accumulated.Append(text);

                if (text.Contains(':') || text.Contains('：'))
                {
                    colonSeen = true;
                    var afterColon = text[(text.LastIndexOfAny([':', '：']) + 1)..];
                    hasNonWhitespaceAfterColon = !string.IsNullOrWhiteSpace(afterColon)
                                                 && !HasDotsRx.IsMatch(afterColon);
                }
                else if (colonSeen && !string.IsNullOrWhiteSpace(text) && !HasDotsRx.IsMatch(text))
                {
                    hasNonWhitespaceAfterColon = true;
                }
            }

            if (!colonSeen) continue;

            var paraText = accumulated.ToString().Trim();
            if (paraText.Length < 2 || Regex.IsMatch(paraText, @"^[\.…\-_=\s]{4,}$")) continue;

            // Case G (post-loop): full paragraph text matches label+colon+2+ellipsis pattern.
            // Catches split-run cases where label and dots live in separate runs, e.g.:
            //   "Bên B được hưởng phụ cấp đào tạo: ………….đ/tháng"  (dots run has trailing text)
            //   "Thời gian đào tạo: từ ngày…/…/…đến ngày…/…/…"     (date pattern split across runs)
            if (paraText.Count(c => c == '…') >= 2)
            {
                var gm = MultiEllipsisRx.Match(paraText);
                if (gm.Success) Add(gm.Groups[1].Value, fromDots: true);
            }

            // Case C: whole paragraph is "Label:" with nothing after (simple empty field).
            // Skip if the paragraph introduces a list (next para is a list item) — that means
            // the colon ends a section heading like "Bên A có các quyền sau:", not a blank.
            // Also skip if the paragraph itself is a numbered clause item ending with ":" —
            // those are clause headings (e.g., "5. Phụ cấp đào tạo:"), not blank fields.
            if (!hasNonWhitespaceAfterColon)
            {
                bool isHeading = nextHasNumPr || paraHasNumPr;
                if (!isHeading)
                {
                    var lm = Regex.Match(paraText, @"^([^:：]{2,60})[：:]\s*$");
                    if (lm.Success) { Add(lm.Groups[1].Value); continue; }

                    // Case C2: multi-colon line — extract the LAST clean label before end
                    // e.g. "Số CMND/CCCD  :  cấp ngày:nơi cấp: Điện thoại  :"
                    // → extracts "Điện thoại" (and in the multi-scan below, all others)
                    var lastColon = paraText.LastIndexOfAny([':', '：']);
                    if (lastColon > 1 && string.IsNullOrWhiteSpace(paraText[(lastColon + 1)..]))
                    {
                        var before = paraText[..lastColon];
                        var last   = Regex.Match(before, @"([^\s,，:：][^,，:：]{0,38}?)\s*$");
                        if (last.Success) Add(last.Groups[1].Value);
                    }
                }
            }

            // Case D: multi-colon paragraph — detect every blank field.
            // Split by colons: parts[i+1] = text between colon i and colon i+1 (the "value slot").
            // A slot is blank when it is:
            //   (a) empty / whitespace-only, OR
            //   (b) whitespace + a short label-like word (= blank + next label concatenated)
            // This correctly detects "Số CMND:      cấp ngày:nơi cấp: Điện thoại:"
            // while ignoring "Bên B (Người lao động): Ông/Bà          Giới tính:" (filled content)
            int colonCount = paraText.Count(c => c == ':' || c == '：');
            if (colonCount >= 2)
            {
                var parts = Regex.Split(paraText, @"[：:]");
                for (int si = 0; si < parts.Length - 1; si++)
                {
                    var valueSlot = parts[si + 1];
                    var slotCore  = valueSlot.Trim();   // strip surrounding whitespace

                    // Blank if: empty/whitespace, or a short clean label (next label has no gap)
                    bool isBlank = string.IsNullOrWhiteSpace(valueSlot) ||
                                   (slotCore.Length > 0 && slotCore.Length <= 35 &&
                                    !slotCore.Contains(',') && !slotCore.Contains('，') &&
                                    !Regex.IsMatch(slotCore, @"\s{2,}"));  // no internal multi-space
                    if (!isBlank) continue;

                    // Extract the LAST word-cluster from parts[si] as the label.
                    // A word-cluster = 1–4 words separated by single spaces, bounded by 2+ spaces or string boundary.
                    var candidate = parts[si];
                    var clusters  = Regex.Matches(candidate,
                        @"(?<!\S)(\S+(?:[ \t]\S+){0,3})(?=[ \t]{2,}|$)");
                    if (clusters.Count > 0) Add(clusters[^1].Groups[1].Value.Trim());
                }
            }
        }
    }

    // ── Fill helpers ──────────────────────────────────────────────────────────

    private static void FillContentControls(Body body, Dictionary<string, string> values)
    {
        foreach (var sdt in body.Descendants<SdtBlock>().Cast<OpenXmlCompositeElement>()
                                .Concat(body.Descendants<SdtRun>()))
        {
            var props = sdt.GetFirstChild<SdtProperties>();
            if (props == null) continue;
            var tag   = props.GetFirstChild<Tag>()?.Val?.Value;
            var alias = props.GetFirstChild<SdtAlias>()?.Val?.Value;
            var key   = tag ?? (alias != null ? Slugify(alias) : null);
            if (key == null || !values.TryGetValue(key, out var val)) continue;
            var textEl = sdt.Descendants<Text>().FirstOrDefault();
            if (textEl != null) textEl.Text = val;
        }
    }

    private static void FillMergeFields(Body body, Dictionary<string, string> values)
    {
        // Simple fields: replace the entire <w:fldSimple> element with a plain run so Word
        // cannot refresh the field back to the placeholder name on open.
        foreach (var sf in body.Descendants<SimpleField>().ToList())
        {
            var m = MergeFieldRx.Match(sf.Instruction?.Value ?? "");
            if (!m.Success) continue;
            if (!values.TryGetValue(m.Groups[1].Value, out var val) || string.IsNullOrWhiteSpace(val)) continue;

            var displayRpr = sf.Descendants<Run>().FirstOrDefault()?.GetFirstChild<RunProperties>();
            var newRun = new Run();
            if (displayRpr != null) newRun.AppendChild((RunProperties)displayRpr.CloneNode(true));
            newRun.AppendChild(new Text(val) { Space = SpaceProcessingModeValues.Preserve });
            sf.InsertBeforeSelf(newRun);
            sf.Remove();
        }

        // Complex fields (begin → instrText → [separate + display runs] → end):
        // "unlink" each field group — replace all field runs with a single plain text run.
        // This prevents Word from refreshing the field on open (which would wipe the value).
        foreach (var para in body.Descendants<Paragraph>())
            UnlinkComplexMergeFieldsInPara(para, values);
    }

    // Replaces each complete MERGEFIELD group in `para` with a plain text run.
    // Uses a restart-after-replacement loop because we modify the collection while iterating.
    private static void UnlinkComplexMergeFieldsInPara(Paragraph para, Dictionary<string, string> values)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            // Elements<Run>() gives only direct-child Run elements — correct for field runs
            // that live directly inside a paragraph (as is standard for MERGEFIELD mail-merge).
            var runs = para.Elements<Run>().ToList();

            for (int i = 0; i < runs.Count; i++)
            {
                // Find a begin fldChar
                var fc = runs[i].GetFirstChild<FieldChar>();
                if (fc?.FieldCharType?.Value != FieldCharValues.Begin) continue;

                // Scan forward for instrText, optional separate+display, and end
                string?         fieldName   = null;
                RunProperties?  displayRpr  = null;
                int             endIdx      = -1;
                bool            pastSep     = false;

                for (int j = i + 1; j < runs.Count; j++)
                {
                    var fc2 = runs[j].GetFirstChild<FieldChar>();
                    if (fc2 != null)
                    {
                        var fct = fc2.FieldCharType?.Value;
                        if      (fct == FieldCharValues.Separate) pastSep = true;
                        else if (fct == FieldCharValues.End)      { endIdx = j; break; }
                        continue;
                    }

                    var instrEl = runs[j].GetFirstChild<FieldCode>();
                    if (instrEl != null)
                    {
                        var m = MergeFieldRx.Match(instrEl.InnerText);
                        if (m.Success) fieldName = m.Groups[1].Value;
                        continue;
                    }

                    // Regular run — first one after separate holds the display formatting
                    if (pastSep && displayRpr == null)
                        displayRpr = runs[j].GetFirstChild<RunProperties>();
                }

                if (fieldName == null || endIdx < 0) continue;
                if (!values.TryGetValue(fieldName, out var val) || string.IsNullOrWhiteSpace(val)) continue;

                // Build a plain-text replacement run, preserving display formatting when available
                var newRun = new Run();
                var rpr = displayRpr ?? runs[i].GetFirstChild<RunProperties>();
                if (rpr != null) newRun.AppendChild((RunProperties)rpr.CloneNode(true));
                newRun.AppendChild(new Text(val) { Space = SpaceProcessingModeValues.Preserve });

                // If the run immediately before begin is a standalone …/dots placeholder, remove it
                // (e.g. "tháng… [MERGEFIELD Tháng_ký]" → after fill becomes "tháng 6" not "tháng…6")
                if (i > 0)
                {
                    var prevText = string.Concat(runs[i - 1].Elements<Text>().Select(t => t.Text));
                    if (DotRunRx.IsMatch(prevText) && prevText.Trim().Length > 0)
                        runs[i - 1].Remove();
                }

                runs[i].InsertBeforeSelf(newRun);   // insert before begin
                for (int k = endIdx; k >= i; k--)   // remove begin..end (reverse order is safe)
                    runs[k].Remove();

                changed = true;
                break;  // restart scan — indices are stale after removal
            }
        }
    }

    // Fills multi-colon inline blanks where the value slot is whitespace or zero-length.
    // Handles paragraphs like "Số CMND:      cấp ngày:nơi cấp: Điện thoại:"
    private static void FillColonWhitespaceBlanks(Body body, Dictionary<string, string> values)
    {
        foreach (var para in body.Descendants<Paragraph>())
        {
            var runs = para.Descendants<Run>().ToList();
            var paraText = string.Concat(runs.SelectMany(r => r.Elements<Text>()).Select(t => t.Text));
            if (paraText.Count(c => c is ':' or '：') < 2) continue;

            var accumulated = new StringBuilder();

            for (int ri = 0; ri < runs.Count; ri++)
            {
                var textEl = runs[ri].Elements<Text>().FirstOrDefault();
                if (textEl == null) continue;
                var text = textEl.Text;

                var colonPos = text.LastIndexOfAny([':', '：']);
                if (colonPos >= 0)
                {
                    var afterColon = text[(colonPos + 1)..];
                    if (string.IsNullOrWhiteSpace(afterColon))   // colon at end (or trailing spaces)
                    {
                        var labelArea = accumulated.ToString() + text[..colonPos];
                        var lm = LabelColonRx.Match(labelArea + ":");
                        if (lm.Success)
                        {
                            var key = Slugify(CleanLabel(lm.Groups[1].Value));
                            if (values.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                            {
                                if (afterColon.Length > 0)
                                {
                                    // Trailing whitespace in the SAME run — replace it in-run
                                    textEl.Text = text[..(colonPos + 1)] + " " + val;
                                }
                                else
                                {
                                    // Colon is at end of run — look for a whitespace-only run next
                                    bool filled = false;
                                    for (int ni = ri + 1; ni < runs.Count; ni++)
                                    {
                                        var ntEl = runs[ni].Elements<Text>().FirstOrDefault();
                                        if (ntEl == null) continue;
                                        var nt = ntEl.Text;
                                        if (string.IsNullOrEmpty(nt)) continue;  // truly empty — skip
                                        if (string.IsNullOrWhiteSpace(nt))       // spaces-only run
                                        {
                                            ntEl.Text = " " + val;
                                            // Erase subsequent whitespace runs for this field
                                            for (int xi = ni + 1; xi < runs.Count; xi++)
                                            {
                                                var xtEl = runs[xi].Elements<Text>().FirstOrDefault();
                                                if (xtEl == null) continue;
                                                if (!string.IsNullOrEmpty(xtEl.Text) && string.IsNullOrWhiteSpace(xtEl.Text))
                                                    xtEl.Text = "";
                                                else break;
                                            }
                                            filled = true;
                                            break;
                                        }
                                        else break;  // non-whitespace = no blank between labels
                                    }
                                    if (!filled)
                                        textEl.Text = text + " " + val;  // zero-length blank: append to colon run
                                }
                            }
                        }
                    }
                }

                accumulated.Append(textEl.Text);  // use current (possibly modified) text
            }
        }
    }

    private static void FillPlaceholders(Body body, Dictionary<string, string> values)
    {
        foreach (var textEl in body.Descendants<Text>())
        {
            if (!textEl.Text.Contains("{{")) continue;
            textEl.Text = Regex.Replace(textEl.Text, @"\{\{([^}]+)\}\}", m =>
            {
                var key = Slugify(m.Groups[1].Value.Trim());
                return values.TryGetValue(key, out var v) ? v : m.Value;
            });
        }
    }

    // Fills "Label: ……" and "Label:" (empty) fields in paragraphs.
    // Uses an indexed loop so Case C can look ahead to the next paragraph.
    private static void FillParagraphBlanks(Body body, Dictionary<string, string> values)
    {
        var paragraphs = body.Descendants<Paragraph>().ToList();

        for (int pi = 0; pi < paragraphs.Count; pi++)
        {
            var para       = paragraphs[pi];
            var accumulated = new StringBuilder();
            var runs        = para.Descendants<Run>().ToList();
            bool justFilledDots = false;   // true after Case B fills a dots run
            bool anyFillDone    = false;   // true once any case successfully writes a value

            for (int ri = 0; ri < runs.Count; ri++)
            {
                var run    = runs[ri];
                var textEl = run.Elements<Text>().FirstOrDefault();
                if (textEl == null) { accumulated.Append('\t'); justFilledDots = false; continue; }
                var text = textEl.Text;

                // Case A: inline "Label: ……" → replace dots portion with value
                if (InlineRx.IsMatch(text))
                {
                    textEl.Text = InlineRx.Replace(text, m =>
                    {
                        var key = Slugify(CleanLabel(m.Groups[1].Value));
                        return values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                            ? m.Groups[1].Value + (m.Value.Contains('：') ? "：" : ":") + " " + v
                            : m.Value;
                    });
                    accumulated.Append(textEl.Text);
                    justFilledDots = false;
                    continue;
                }

                // Case B: dots-only run OR standalone … → replace completely with value
                bool isSingleEllipsis = (text == "…");
                bool isDotRun = DotRunRx.IsMatch(text) && (HasDotsRx.IsMatch(text) || isSingleEllipsis);
                if (isDotRun)
                {
                    if (justFilledDots) { textEl.Text = ""; continue; }

                    var accStr = accumulated.ToString();
                    string? key = null;

                    // Try colon-based lookup first
                    var lm = LabelColonRx.Match(accStr);
                    if (lm.Success)
                        key = Slugify(CleanLabel(lm.Groups[1].Value));

                    // Fallback: no-colon last-word lookup (for date lines: ngày… tháng… năm …)
                    if (key == null || !values.ContainsKey(key))
                    {
                        var wm = Regex.Match(accStr, @"(\p{L}+)\s*$");
                        if (wm.Success) key = Slugify(wm.Groups[1].Value);
                    }

                    if (key != null && values.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                    {
                        // Add a space before the value if the preceding text doesn't end with one
                        var spBefore = accumulated.Length > 0 && accumulated[^1] != ' ' ? " " : "";
                        // Add a space after if the next run starts directly with a letter
                        var spAfter = "";
                        if (ri + 1 < runs.Count)
                        {
                            var nxt = string.Concat(runs[ri + 1].Elements<Text>().Select(t => t.Text));
                            if (nxt.Length > 0 && char.IsLetter(nxt[0])) spAfter = " ";
                        }
                        var filledVal = spBefore + val + spAfter;
                        textEl.Text    = filledVal;
                        justFilledDots = true;
                        anyFillDone    = true;
                        accumulated.Append(filledVal);
                        continue;
                    }
                    justFilledDots = false;
                }
                else
                {
                    justFilledDots = false;
                }

                // Case E: run whose text is "Label:   spaces  " (label+colon+whitespace inline)
                // or "Label:  " where the spaces represent a blank field.
                // Handles paragraphs like "Giới tính:                             "
                {
                    var em = Regex.Match(text, @"^(.*[^:：\s][^:：]*)([：:])(\s+)$");
                    if (em.Success)
                    {
                        var labelPart = em.Groups[1].Value;
                        // Only fill if this looks like the field's own label (short, no comma)
                        var cleanLbl = CleanLabel(labelPart.Trim());
                        if (!cleanLbl.Contains(',') && cleanLbl.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 7)
                        {
                            var key = Slugify(cleanLbl);
                            if (values.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                            {
                                textEl.Text = em.Groups[1].Value + em.Groups[2].Value + " " + val;
                                accumulated.Append(textEl.Text);
                                justFilledDots = false;
                                anyFillDone    = true;
                                continue;
                            }
                        }
                    }
                }

                accumulated.Append(text);
            }

            // Case H: full-paragraph MultiEllipsis fill for split-run patterns not caught
            // by A/B/E — label in one run, dots/date-placeholders in separate runs, e.g.:
            //   "Bên B được hưởng phụ cấp đào tạo: ………….đ/tháng"
            //   "Thời gian đào tạo: từ ngày…/…/…đến ngày…/…/…"
            var paraText = accumulated.ToString().Trim();
            if (!anyFillDone && paraText.Count(c => c == '…') >= 2)
            {
                var mh = MultiEllipsisRx.Match(paraText);
                if (mh.Success)
                {
                    var hKey = Slugify(CleanLabel(mh.Groups[1].Value));
                    if (values.TryGetValue(hKey, out var hVal) && !string.IsNullOrWhiteSpace(hVal))
                    {
                        bool hPlaced = false;
                        var hAccum  = new StringBuilder();
                        foreach (var fr in para.Descendants<Run>())
                        {
                            var fte = fr.Elements<Text>().FirstOrDefault();
                            if (fte == null) continue;

                            if (!hPlaced)
                            {
                                // Skip (and track) runs before the first dots-bearing run
                                if (!fte.Text.Contains('…') && !HasDotsRx.IsMatch(fte.Text))
                                {
                                    hAccum.Append(fte.Text);
                                    continue;
                                }

                                // Add a leading space when preceding text ends with a letter/digit
                                var hPrefix = hAccum.Length > 0 && hAccum[^1] != ' ' ? " " : "";
                                // Preserve non-dot suffix e.g. "đ/tháng" in "………….đ/tháng",
                                // but not "/…/…" leftover in "…/…/…" (which still contains …)
                                var sfxMatch = Regex.Match(fte.Text, @"^[\.…/]+(.+)$");
                                var sfx = sfxMatch.Success && !sfxMatch.Groups[1].Value.Contains('…')
                                          ? sfxMatch.Groups[1].Value : "";
                                fte.Text = hPrefix + hVal + sfx;
                                hPlaced  = true;
                            }
                            else
                            {
                                // Blank everything after the fill point — clears "đến ngày",
                                // second …/…/… etc. so the value stands alone.
                                fte.Text = "";
                            }
                        }
                    }
                }
            }

            // Case C: paragraph ends with "Label:" — value goes either in the next
            // paragraph (if it is a dots-only paragraph) or appended here.
            if (paraText.Length < 2) continue;
            var caseC = Regex.Match(paraText, @"^([^:：]{2,60})[：:]\s*$");
            if (!caseC.Success) continue;

            var cKey = Slugify(CleanLabel(caseC.Groups[1].Value));
            if (!values.TryGetValue(cKey, out var cVal) || string.IsNullOrWhiteSpace(cVal)) continue;

            // Look ahead: if the next paragraph consists only of dots/underscores,
            // replace its content instead of appending to this paragraph.
            bool replacedNext = false;
            if (pi + 1 < paragraphs.Count)
            {
                var next     = paragraphs[pi + 1];
                var nextText = string.Concat(next.Descendants<Text>().Select(t => t.Text));
                if (nextText.Length > 0 && Regex.IsMatch(nextText.Trim(), @"^[\.…_\s]+$"))
                {
                    // Replace first text element, blank out the rest
                    var nextTexts = next.Descendants<Text>().ToList();
                    if (nextTexts.Count > 0)
                    {
                        nextTexts[0].Text = cVal;
                        foreach (var extra in nextTexts.Skip(1)) extra.Text = "";
                        replacedNext = true;
                    }
                }
            }

            if (!replacedNext)
            {
                var lastRun = para.Descendants<Run>().LastOrDefault();
                var newRun  = new Run();
                var rpr     = lastRun?.GetFirstChild<RunProperties>();
                if (rpr != null) newRun.AppendChild((RunProperties)rpr.CloneNode(true));
                newRun.AppendChild(new Text(cVal) { Space = SpaceProcessingModeValues.Preserve });
                para.AppendChild(newRun);
            }
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static IEnumerable<SdtProperties> GetAllSdtProperties(Body body)
    {
        foreach (var sdt in body.Descendants<SdtBlock>())
        {
            var p = sdt.GetFirstChild<SdtProperties>();
            if (p != null) yield return p;
        }
        foreach (var sdt in body.Descendants<SdtRun>())
        {
            var p = sdt.GetFirstChild<SdtProperties>();
            if (p != null) yield return p;
        }
    }

    internal string GetTemplatePath(string templateId)
    {
        if (!Regex.IsMatch(templateId, @"^[a-f0-9]{32}$"))
            throw new ArgumentException("Invalid template ID.");
        foreach (var ext in new[] { ".docx", ".txt" })
        {
            var p = Path.Combine(_tempDir, $"{templateId}{ext}");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("Template not found or expired.");
    }

    private static string ExtractAnswerText(string completionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(completionJson);
            return doc.RootElement.GetProperty("data").GetProperty("answer").GetString() ?? "";
        }
        catch { return ""; }
    }

    // "Tháng_ký" → "Tháng ký", "Pháp_nhân_in_hoa" → "Pháp nhân in hoa"
    private static string HumanizeMergeKey(string key) =>
        key.Replace("_", " ").Trim();

    private static string CleanLabel(string s) =>
        s.Trim().TrimStart('-', '–', '•', '*', ' ').Trim();

    private static string Slugify(string s)
    {
        var step1 = Regex.Replace(s.ToLowerInvariant().Trim(), @"\s+", "_");
        var step2 = Regex.Replace(step1, @"[^\p{L}\p{N}_]+", "");
        return step2.Trim('_');
    }

    // ── Plain-text detect ─────────────────────────────────────────────────────

    private static List<FormField> DetectTxtFields(string path)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<FormField>();

        void Add(string raw)
        {
            var label = CleanLabel(raw);
            if (label.Length < 2) return;
            if (label.Contains(',') || label.Contains('，')) return;
            if (label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5) return;
            var key = Slugify(label);
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                fields.Add(new FormField(Key: key, Label: label));
        }

        // Patterns in plain text
        // A: {{Field Name}}
        var PlaceholderRx = new Regex(@"\{\{([^}]+)\}\}");
        // B: Label: ________  or  Label: …………
        var InlineBlankRx = new Regex(@"([^,，:：]{2,60})[：:]\s*[_\.…]{4,}\s*$");
        // C: Label: (end of line — value goes on next blank/underscore line)
        var ColonEndRx    = new Regex(@"^([^,，:：]{2,60})[：:]\s*$");

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            foreach (Match m in PlaceholderRx.Matches(line))
            {
                var label = m.Groups[1].Value.Trim();
                var key   = Slugify(label);
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    fields.Add(new FormField(Key: key, Label: label));
            }

            var mb = InlineBlankRx.Match(line);
            if (mb.Success) { Add(mb.Groups[1].Value); continue; }

            var mc = ColonEndRx.Match(line);
            if (mc.Success) Add(mc.Groups[1].Value);
        }

        return fields;
    }

    // ── Plain-text fill ───────────────────────────────────────────────────────

    private static byte[] FillTxt(string path, Dictionary<string, string> values)
    {
        var PlaceholderRx = new Regex(@"\{\{([^}]+)\}\}");
        var InlineBlankRx = new Regex(@"(([^,，:：]{2,60})[：:]\s*)([_\.…]{4,})(\s*)$");
        var ColonEndRx    = new Regex(@"^(([^,，:：]{2,60})[：:]\s*)$");
        var BlankLineRx   = new Regex(@"^[\s_\.…]*$");

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        string? pendingKey = null;   // key from a "Label:" line, expecting blank next line

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Fill the blank line that follows a "Label:" line
            if (pendingKey != null)
            {
                if (BlankLineRx.IsMatch(line) &&
                    values.TryGetValue(pendingKey, out var pv) && !string.IsNullOrWhiteSpace(pv))
                {
                    lines[i] = pv;
                    pendingKey = null;
                    continue;
                }
                pendingKey = null;
            }

            // {{Placeholder}}
            line = PlaceholderRx.Replace(line, m =>
            {
                var key = Slugify(m.Groups[1].Value.Trim());
                return values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : m.Value;
            });

            // Label: ________  →  Label: VALUE
            var mb = InlineBlankRx.Match(line);
            if (mb.Success)
            {
                var key = Slugify(CleanLabel(mb.Groups[2].Value));
                if (values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                    line = mb.Groups[1].Value + v;
                lines[i] = line;
                continue;
            }

            // Label:  →  Label: VALUE  (or set pending for next-line blank)
            var mc = ColonEndRx.Match(line);
            if (mc.Success)
            {
                var key = Slugify(CleanLabel(mc.Groups[2].Value));
                if (values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                    line = mc.Groups[1].Value + v;
                else
                    pendingKey = key;
            }

            lines[i] = line;
        }

        return Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines));
    }

    private void CleanupStaleFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var f in Directory.GetFiles(_tempDir))
                if (File.GetCreationTimeUtc(f) < cutoff)
                    File.Delete(f);
        }
        catch { }
    }
}
