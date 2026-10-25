using PaRuX.Contracts;
using PaRuX.Server.DesktopIntegration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Reading someone's mail and notes is the most invasive thing PaRuX can do, so what these tests pin
/// is not that it works - it is that it cannot happen by accident. Nothing is connected until it is
/// connected, a connected source reads one path and no other, a source that could never work is
/// refused at the point of connecting rather than silently returning nothing forever, and the model
/// is not told these tools exist while there is nothing behind them.
/// </summary>
public sealed class DesktopIntegrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-desktop-svc-" + Guid.NewGuid().ToString("N"));
    private readonly string _state;

    public DesktopIntegrationServiceTests()
    {
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(_state);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DesktopIntegrationService NewService() => new(_state);

    private string Vault(params (string Name, string Body)[] notes)
    {
        string vault = Path.Combine(_root, "vault");
        Directory.CreateDirectory(vault);
        foreach ((string name, string body) in notes) File.WriteAllText(Path.Combine(vault, name), body);
        return vault;
    }

    [Fact]
    public void Nothing_is_connected_until_the_user_connects_it()
    {
        List<DesktopIntegrationStatus> statuses = NewService().GetStatuses();

        Assert.NotEmpty(statuses);
        Assert.All(statuses, s => Assert.False(s.Connected));
        Assert.All(statuses, s => Assert.False(string.IsNullOrWhiteSpace(s.WhatItReads)));
    }

    [Fact]
    public void Every_offered_source_states_what_it_would_read_before_it_is_connected()
    {
        // A consent prompt that says "connect Thunderbird?" and nothing else is not consent.
        foreach (DesktopIntegrationStatus status in NewService().GetStatuses())
        {
            Assert.False(string.IsNullOrWhiteSpace(status.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(status.Kind));
            Assert.True(status.WhatItReads.Length > 20, status.Id + " does not say what it reads.");
        }
    }

    [Fact]
    public void Connecting_a_notes_folder_makes_exactly_that_folder_readable()
    {
        string vault = Vault(("one.md", "# One\nalpha"), ("two.md", "# Two\nbravo"));
        DesktopIntegrationService service = NewService();

        DesktopIntegrationStatus status = service.Connect("notes-folder", vault);

        Assert.True(status.Connected);
        Assert.Equal(Path.GetFullPath(vault), status.ConnectedPath);

        List<DesktopItem> items = service.Read("notes-folder");
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.StartsWith(Path.GetFullPath(vault), Path.GetFullPath(i.Location), StringComparison.Ordinal));
    }

    [Fact]
    public void A_path_that_does_not_exist_is_refused_at_connect_time()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => NewService().Connect("notes-folder", Path.Combine(_root, "nope")));

        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_folder_source_pointed_at_a_file_is_refused_rather_than_connected_and_empty()
    {
        string file = Path.Combine(_root, "single.md");
        File.WriteAllText(file, "# note");

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => NewService().Connect("notes-folder", file));

        Assert.Contains("folder", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bookmarks_source_pointed_at_a_folder_is_refused()
    {
        string folder = Vault(("x.md", "x"));

        Assert.Throws<InvalidDataException>(() => NewService().Connect("edge-bookmarks", folder));
    }

    [Fact]
    public void An_unknown_source_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => NewService().Connect("my-diary", _root));
    }

    [Fact]
    public void A_source_with_no_standard_location_says_so_and_will_not_guess()
    {
        DesktopIntegrationStatus notesFolder = NewService().GetStatuses().Single(s => s.Id == "notes-folder");
        Assert.True(notesFolder.RequiresExplicitPath);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => NewService().Connect("notes-folder", null));
        Assert.Contains("Choose the folder", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reading_a_source_that_is_not_connected_is_an_error_not_an_empty_list()
    {
        // An empty list would read as "you have no notes", which is a different and wrong answer.
        Assert.Throws<InvalidOperationException>(() => NewService().Read("notes-folder"));
    }

    [Fact]
    public void Disconnecting_stops_the_source_being_read()
    {
        string vault = Vault(("one.md", "# One"));
        DesktopIntegrationService service = NewService();
        service.Connect("notes-folder", vault);

        service.Disconnect("notes-folder");

        Assert.False(service.GetStatuses().Single(s => s.Id == "notes-folder").Connected);
        Assert.Empty(service.ReadAllConnected());
    }

    [Fact]
    public void The_connection_survives_a_restart_of_core()
    {
        string vault = Vault(("one.md", "# One"));
        NewService().Connect("notes-folder", vault);

        // A second instance over the same state directory is what a restarted Core looks like.
        Assert.True(NewService().GetStatuses().Single(s => s.Id == "notes-folder").Connected);
    }

    [Fact]
    public void A_read_records_what_it_found_so_settings_can_report_it_afterwards()
    {
        string vault = Vault(("one.md", "# One"), ("two.md", "# Two"));
        DesktopIntegrationService service = NewService();
        service.Connect("notes-folder", vault);

        service.Read("notes-folder");

        DesktopIntegrationStatus status = service.GetStatuses().Single(s => s.Id == "notes-folder");
        Assert.Equal(2, status.LastItemCount);
        Assert.NotNull(status.LastReadUtc);
        Assert.Null(status.LastError);
    }

    [Fact]
    public void One_unreadable_source_does_not_take_the_others_down_with_it()
    {
        string vault = Vault(("one.md", "# One"));
        string mail = Path.Combine(_root, "mail");
        Directory.CreateDirectory(mail);

        DesktopIntegrationService service = NewService();
        service.Connect("notes-folder", vault);
        service.Connect("mail-export", mail);
        Directory.Delete(mail, recursive: true); // the folder goes away under us, as folders do

        List<DesktopItem> items = service.ReadAllConnected();

        Assert.Single(items);
        Assert.Equal(DesktopItem.KindNote, items[0].Kind);
    }

    [Fact]
    public void Reading_can_be_narrowed_to_one_kind()
    {
        DesktopIntegrationService service = NewService();
        service.Connect("notes-folder", Vault(("one.md", "# One")));

        Assert.Single(service.ReadAllConnected("notes"));
        Assert.Empty(service.ReadAllConnected("mail"));
    }

    // ---- what the model is told ----

    [Fact]
    public void The_model_is_not_offered_desktop_tools_while_nothing_is_connected()
    {
        // Advertising a tool that can only fail is how a model ends up inventing the mail it
        // "found" instead of saying it has no access.
        Assert.Empty(new DesktopIntegrationTools(NewService()).GetToolSchemas());
    }

    [Fact]
    public void The_tools_appear_once_something_is_connected()
    {
        DesktopIntegrationService service = NewService();
        service.Connect("notes-folder", Vault(("one.md", "# One")));

        var names = new DesktopIntegrationTools(service).GetToolSchemas()
            .Select(s => s?["function"]?["name"]?.GetValue<string>())
            .ToList();

        Assert.Contains(DesktopIntegrationTools.SearchToolName, names);
        Assert.Contains(DesktopIntegrationTools.ReadToolName, names);
    }

    [Fact]
    public async Task A_search_returns_the_matching_item_with_the_file_it_came_from()
    {
        DesktopIntegrationService service = NewService();
        string vault = Vault(
            ("invoice.md", "# Invoice Q3\nAcme owes 1200 EUR"),
            ("recipe.md", "# Carbonara\nguanciale, pecorino"));
        service.Connect("notes-folder", vault);

        string result = await new DesktopIntegrationTools(service)
            .CallAsync(DesktopIntegrationTools.SearchToolName, "{\"query\":\"Acme\"}", CancellationToken.None);

        Assert.Contains("Invoice Q3", result, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(vault, "invoice.md"), result, StringComparison.Ordinal);
        Assert.DoesNotContain("Carbonara", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_that_matches_nothing_says_so_rather_than_returning_everything()
    {
        DesktopIntegrationService service = NewService();
        service.Connect("notes-folder", Vault(("one.md", "# One\nalpha")));

        string result = await new DesktopIntegrationTools(service)
            .CallAsync(DesktopIntegrationTools.SearchToolName, "{\"query\":\"zebra\"}", CancellationToken.None);

        Assert.Contains("Nothing", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alpha", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_read_tool_will_not_open_a_file_that_is_not_in_a_connected_source()
    {
        DesktopIntegrationService service = NewService();
        service.Connect("notes-folder", Vault(("one.md", "# One")));

        string secret = Path.Combine(_root, "private.txt");
        File.WriteAllText(secret, "not for the model");

        string result = await new DesktopIntegrationTools(service)
            .CallAsync(DesktopIntegrationTools.ReadToolName,
                "{\"location\":\"" + secret.Replace("\\", "\\\\") + "\"}", CancellationToken.None);

        Assert.DoesNotContain("not for the model", result, StringComparison.Ordinal);
        Assert.Contains("not in any connected app", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_tool_arguments_fail_visibly_rather_than_silently()
    {
        string result = await new DesktopIntegrationTools(NewService())
            .CallAsync(DesktopIntegrationTools.SearchToolName, "{not json", CancellationToken.None);

        Assert.StartsWith("Desktop tool failed:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_match_outranks_a_passing_mention()
    {
        var titled = new DesktopItem("s", "note", "1", "Invoice", null, null, "unrelated body", "loc1");
        var mentioned = new DesktopItem("s", "note", "2", "Shopping", null, null, "pay the invoice", "loc2");

        Assert.True(DesktopIntegrationTools.Score(titled, new[] { "invoice" })
                  > DesktopIntegrationTools.Score(mentioned, new[] { "invoice" }));
    }

    [Fact]
    public void An_excerpt_is_taken_around_the_hit_rather_than_from_the_top()
    {
        // The top of a mail is a greeting; the reason it matched is usually further down.
        string body = new string('x', 4000) + " THE-MATCH " + new string('y', 4000);

        string excerpt = DesktopIntegrationTools.Excerpt(body, new[] { "THE-MATCH" });

        Assert.Contains("THE-MATCH", excerpt, StringComparison.Ordinal);
        Assert.True(excerpt.Length < 1000);
    }
}
