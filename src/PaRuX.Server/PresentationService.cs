using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace PaRuX.Server;

/// <summary>
/// Native PPTX generation: outline via the local LLM, slides built directly with
/// the OpenXml SDK already used elsewhere in this codebase for reading .docx/.pptx
/// (DocumentTextExtractor). The spec's suggested engine was Presenton, a separate
/// service PaRuX would have to install, run, and point at the local model - for
/// a one-shot "outline plus bullet slides" deliverable, building the PPTX directly
/// keeps everything in-process, with no new external dependency, no new service to
/// keep alive, and no separate cloud-AI-disable step to audit. AI-generated slide
/// imagery (via the existing ComfyUI media adapter) is a natural next step, not
/// included in this pass.
/// </summary>
public sealed class PresentationService
{
    private static readonly HttpClient Http = new(ManagedRuntimePeers.CreateHandler()) { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const long SlideWidth = 12192000;
    private const long SlideHeight = 6858000;

    private readonly LocalRuntimeService _runtime;

    public PresentationService(LocalRuntimeService runtime) => _runtime = runtime;

    public async Task<(List<PresentationSlideDto> slides, byte[] pptxBytes)> GenerateAsync(
        string topic, int slideCount, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(topic)) throw new InvalidDataException("A presentation topic is required.");
        slideCount = Math.Clamp(slideCount <= 0 ? 10 : slideCount, 3, 30);
        await _runtime.EnsureReadyAsync(ct);

        List<PresentationSlideDto> slides = await GenerateOutlineAsync(topic, slideCount, ct);
        byte[] pptx = BuildPptx(topic, slides);
        return (slides, pptx);
    }

    private async Task<List<PresentationSlideDto>> GenerateOutlineAsync(string topic, int slideCount, CancellationToken ct)
    {
        string prompt = "Topic: " + topic + "\n\nCreate an outline for exactly " + slideCount + " presentation slides "
            + "(the first slide is the cover/title slide). Reply with ONLY a JSON array, nothing else, shaped like:\n"
            + "[{\"title\":\"...\",\"bullets\":[\"...\",\"...\"],\"speakerNotes\":\"...\"}]\n"
            + "Cover slide: title only, bullets can be a short subtitle-style array. Keep bullets short (under ~12 words each), 3-5 per slide.";

        string raw = await CompleteAsync(
            "You write clear, concise presentation outlines. Reply with only a JSON array, no markdown fences, no explanation.",
            prompt, ct);

        List<PresentationSlideDto> slides = ParseOutline(raw);
        if (slides.Count == 0)
            slides.Add(new PresentationSlideDto { Title = topic, Bullets = new List<string>() });
        return slides;
    }

    private static List<PresentationSlideDto> ParseOutline(string raw)
    {
        var result = new List<PresentationSlideDto>();
        int start = raw.IndexOf('[');
        int end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return result;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw[start..(end + 1)]);
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                var slide = new PresentationSlideDto
                {
                    Title = item.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty,
                    SpeakerNotes = item.TryGetProperty("speakerNotes", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty
                };
                if (item.TryGetProperty("bullets", out JsonElement bullets) && bullets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement bullet in bullets.EnumerateArray())
                        if (bullet.ValueKind == JsonValueKind.String) slide.Bullets.Add(bullet.GetString() ?? string.Empty);
                }
                if (!string.IsNullOrWhiteSpace(slide.Title)) result.Add(slide);
            }
        }
        catch (JsonException)
        {
            return new List<PresentationSlideDto>();
        }
        return result;
    }

    internal static byte[] BuildPptx(string topic, List<PresentationSlideDto> slides)
    {
        using var stream = new MemoryStream();
        using (PresentationDocument document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            PresentationPart presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();

            SlideMasterPart masterPart = presentationPart.AddNewPart<SlideMasterPart>();
            SlideLayoutPart layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
            layoutPart.SlideLayout = BuildBlankLayout();
            masterPart.SlideMaster = BuildSlideMaster(masterPart.GetIdOfPart(layoutPart));

            ThemePart themePart = masterPart.AddNewPart<ThemePart>();
            themePart.Theme = BuildMinimalTheme();

            var slideIdList = new SlideIdList();
            uint slideId = 256;
            uint relIdSeed = 1;

            foreach (PresentationSlideDto slideModel in slides)
            {
                SlidePart slidePart = presentationPart.AddNewPart<SlidePart>("rSlide" + relIdSeed++);
                slidePart.Slide = BuildSlide(slideModel);
                slidePart.AddPart(layoutPart);

                if (!string.IsNullOrWhiteSpace(slideModel.SpeakerNotes))
                {
                    NotesSlidePart notesPart = slidePart.AddNewPart<NotesSlidePart>();
                    notesPart.NotesSlide = BuildNotesSlide(slideModel.SpeakerNotes);
                }

                slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            }

            presentationPart.Presentation.Append(
                new SlideMasterIdList(new SlideMasterId { Id = 2147483648, RelationshipId = presentationPart.GetIdOfPart(masterPart) }),
                slideIdList,
                new SlideSize { Cx = (int)SlideWidth, Cy = (int)SlideHeight },
                new NotesSize { Cx = (int)SlideHeight, Cy = (int)SlideWidth });

            presentationPart.Presentation.Save();
        }
        return stream.ToArray();
    }

    private static P.Slide BuildSlide(PresentationSlideDto model)
    {
        var shapeTree = new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));

        shapeTree.Append(BuildTextShape(2, "Title", model.Title, 685800, 457200, 10820400, 1143000, 3600, true));

        if (model.Bullets.Count > 0)
        {
            string body = string.Join("\n", model.Bullets.Select(b => "• " + b));
            shapeTree.Append(BuildTextShape(3, "Body", body, 685800, 1828800, 10820400, 4800600, 2000, false));
        }

        return new P.Slide(new CommonSlideData(shapeTree));
    }

    private static P.Shape BuildTextShape(uint id, string name, string text, long x, long y, long cx, long cy, int fontSizeHundredths, bool bold)
    {
        var shape = new P.Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = name },
                new NonVisualShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            new P.TextBody(
                new A.BodyProperties { Wrap = A.TextWrappingValues.Square, Anchor = A.TextAnchoringTypeValues.Top },
                new A.ListStyle()));

        var paragraphs = text.Split('\n');
        var textBody = shape.TextBody!;
        foreach (string line in paragraphs)
        {
            textBody.Append(new A.Paragraph(
                new A.Run(
                    new A.RunProperties { Language = "en-US", FontSize = fontSizeHundredths, Bold = bold },
                    new A.Text(line))));
        }
        return shape;
    }

    private static NotesSlide BuildNotesSlide(string notes) =>
        new(new CommonSlideData(new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()),
            BuildTextShape(2, "Notes", notes, 685800, 457200, 10820400, 5943600, 1200, false))));

    private static SlideLayout BuildBlankLayout() =>
        new(new CommonSlideData(new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()))))
        { Type = SlideLayoutValues.Blank };

    private static SlideMaster BuildSlideMaster(string layoutRelationshipId) =>
        new(
            new CommonSlideData(new ShapeTree(
                new NonVisualGroupShapeProperties(
                    new NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                    new NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new A.TransformGroup()))),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3, Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new SlideLayoutIdList(new SlideLayoutId { Id = 2147483649, RelationshipId = layoutRelationshipId }));

    private static A.Theme BuildMinimalTheme() =>
        new(new A.ThemeElements(
            new A.ColorScheme(
                new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText }),
                new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window }),
                new A.Dark2Color(new A.RgbColorModelHex { Val = "44546A" }),
                new A.Light2Color(new A.RgbColorModelHex { Val = "E7E6E6" }),
                new A.Accent1Color(new A.RgbColorModelHex { Val = "6C5CE7" }),
                new A.Accent2Color(new A.RgbColorModelHex { Val = "22D3EE" }),
                new A.Accent3Color(new A.RgbColorModelHex { Val = "4C3FBF" }),
                new A.Accent4Color(new A.RgbColorModelHex { Val = "0FA9C7" }),
                new A.Accent5Color(new A.RgbColorModelHex { Val = "8B7BFF" }),
                new A.Accent6Color(new A.RgbColorModelHex { Val = "56DFF5" }),
                new A.Hyperlink(new A.RgbColorModelHex { Val = "0563C1" }),
                new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" })) { Name = "PaRuX" },
            new A.FontScheme(
                new A.MajorFont(new A.LatinFont { Typeface = "Segoe UI" }, new A.EastAsianFont { Typeface = string.Empty }, new A.ComplexScriptFont { Typeface = string.Empty }),
                new A.MinorFont(new A.LatinFont { Typeface = "Segoe UI" }, new A.EastAsianFont { Typeface = string.Empty }, new A.ComplexScriptFont { Typeface = string.Empty })) { Name = "PaRuX" },
            new A.FormatScheme(
                new A.FillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })),
                new A.LineStyleList(
                    new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })) { Width = 6350 },
                    new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })) { Width = 12700 },
                    new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })) { Width = 19050 }),
                new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                new A.BackgroundFillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Light1 }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Light1 }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Light1 }))
            ) { Name = "PaRuX" }));

    private async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = "local-default",
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _runtime.ProviderEndpoint + "/v1/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
        };
        await using MediaExecutionCoordinator.Lease textLease = await _runtime
            .AcquireTextInferenceAsync("presentation:outline", ct).ConfigureAwait(false);
        using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return string.Empty;
        JsonElement message = choices[0].GetProperty("message");
        return message.TryGetProperty("content", out JsonElement content) ? content.GetString() ?? string.Empty : string.Empty;
    }
}
