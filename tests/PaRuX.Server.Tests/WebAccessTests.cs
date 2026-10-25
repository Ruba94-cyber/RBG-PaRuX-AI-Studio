using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The browser door, which is the one place where "it came from loopback" stops being evidence.
///
/// With a reverse proxy on this machine - the configuration this feature exists for - a request
/// that travelled from the public Internet reaches PaRuX from 127.0.0.1, indistinguishable from a
/// process on the user's own desktop. Every guarantee therefore rests on the passphrase, the
/// session and the CSRF token rather than on the source address, and these tests pin exactly that.
/// </summary>
public sealed class WebAccessServiceTests
{
    private static WebAccessService NewService(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "parux-web-" + Guid.NewGuid().ToString("N") + ".json");
        return new WebAccessService(path);
    }

    private const string GoodPassphrase = "correct horse battery staple";

    [Fact]
    public void Browser_access_ships_off_and_cannot_be_switched_on_without_a_passphrase()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            Assert.False(web.Enabled);
            Assert.False(web.PassphraseSet);

            InvalidDataException refused = Assert.Throws<InvalidDataException>(() => web.SetEnabled(true));

            Assert.Contains("passphrase", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(web.Enabled);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_short_passphrase_is_refused_rather_than_hashed()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            Assert.Throws<InvalidDataException>(() => web.SetPassphrase("short"));
            Assert.False(web.PassphraseSet);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void The_passphrase_is_never_written_to_disk_in_a_recoverable_form()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            string stored = File.ReadAllText(path);

            Assert.DoesNotContain(GoodPassphrase, stored, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("battery", stored, StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void The_right_passphrase_opens_a_session_and_the_wrong_one_does_not()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            web.SetEnabled(true);

            Assert.Null(web.TryAuthenticate("not the passphrase"));

            WebAccessService.Session? session = web.TryAuthenticate(GoodPassphrase);

            Assert.NotNull(session);
            Assert.NotEqual(session!.Id, session.CsrfToken);
            Assert.True(session.Id.Length >= 40, "A session id must not be guessable.");
            Assert.True(session.CsrfToken.Length >= 40, "A CSRF token must not be guessable.");
            Assert.NotNull(web.TryResume(session.Id));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Repeated_wrong_guesses_lock_the_door()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            web.SetEnabled(true);

            for (int attempt = 0; attempt < 5; attempt++) Assert.Null(web.TryAuthenticate("wrong"));

            Assert.True(web.IsLockedOut(out TimeSpan retryAfter));
            Assert.True(retryAfter > TimeSpan.Zero);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_session_cannot_be_resumed_once_browser_access_is_switched_off()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            web.SetEnabled(true);
            WebAccessService.Session session = web.TryAuthenticate(GoodPassphrase)!;
            Assert.NotNull(web.TryResume(session.Id));

            web.SetEnabled(false);

            Assert.Null(web.TryResume(session.Id));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Changing_the_passphrase_signs_out_whoever_was_using_the_old_one()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            web.SetEnabled(true);
            WebAccessService.Session session = web.TryAuthenticate(GoodPassphrase)!;

            web.SetPassphrase("an entirely different passphrase");

            Assert.Null(web.TryResume(session.Id));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Signing_out_ends_that_session_and_leaves_the_others_alone()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            web.SetEnabled(true);
            WebAccessService.Session first = web.TryAuthenticate(GoodPassphrase)!;
            WebAccessService.Session second = web.TryAuthenticate(GoodPassphrase)!;

            web.Revoke(first.Id);

            Assert.Null(web.TryResume(first.Id));
            Assert.NotNull(web.TryResume(second.Id));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Reset_returns_PaRuX_to_the_state_it_ships_in()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            web.SetEnabled(true);

            web.Reset();

            Assert.False(web.Enabled);
            Assert.False(web.PassphraseSet);
            Assert.Null(web.TryAuthenticate(GoodPassphrase));
        }
        finally { Delete(path); }
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>
/// The same boundary seen from the wire, where the interesting question is what an unauthenticated
/// browser can reach.
/// </summary>
public sealed class WebAccessEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    [Theory]
    [InlineData("GET", "/app/login", true)]
    [InlineData("POST", "/api/v1/web/session", true)]
    [InlineData("GET", "/api/v1/web/session", false)]
    [InlineData("DELETE", "/api/v1/web/session", false)]
    [InlineData("POST", "/app/login", false)]
    public void Unauthenticated_web_paths_are_loopback_only_and_method_scoped(
        string method, string path, bool publicOnLoopback)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(publicOnLoopback, WebAccessEndpoints.IsPublicPath(context.Request, loopback: true));
        Assert.False(WebAccessEndpoints.IsPublicPath(context.Request, loopback: false));
    }

    [Fact]
    public async Task Session_inspection_recognizes_cookie_and_signout_requires_csrf()
    {
        WebAccessService web = _factory.Services.GetRequiredService<WebAccessService>();
        web.SetPassphrase("correct horse battery staple");
        web.SetEnabled(true);
        try
        {
            using HttpClient browser = _factory.CreateClient();
            HttpResponseMessage signIn = await browser.PostAsJsonAsync("/api/v1/web/session",
                new WebSessionRequest { Passphrase = "correct horse battery staple" });
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            WebSessionResponse session = (await signIn.Content.ReadFromJsonAsync<WebSessionResponse>())!;
            browser.DefaultRequestHeaders.Add("Cookie", signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0]);

            WebSessionResponse inspected = (await browser.GetFromJsonAsync<WebSessionResponse>("/api/v1/web/session"))!;
            Assert.True(inspected.Authenticated);
            Assert.Equal(session.CsrfToken, inspected.CsrfToken);

            HttpResponseMessage missingCsrf = await browser.DeleteAsync("/api/v1/web/session");
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
            Assert.True((await browser.GetFromJsonAsync<WebSessionResponse>("/api/v1/web/session"))!.Authenticated);

            using var signOut = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/web/session");
            signOut.Headers.Add(WebAccessService.CsrfHeaderName, session.CsrfToken);
            Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(signOut)).StatusCode);
            Assert.False((await browser.GetFromJsonAsync<WebSessionResponse>("/api/v1/web/session"))!.Authenticated);
        }
        finally { web.SetEnabled(false); }
    }

    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public WebAccessEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private WebAccessService Web => _factory.Services.GetRequiredService<WebAccessService>();

    private HttpClient DesktopClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    /// <summary>
    /// With the feature off, the sign-in paths stop being public and fall back to the same
    /// unauthorised answer every other route gives. That uniformity is the point: a 404 here would
    /// distinguish "this route exists but is switched off" from "this route does not exist", and
    /// tell a stranger something about the machine. 401 tells them nothing.
    /// </summary>
    [Fact]
    public async Task While_browser_access_is_off_the_sign_in_paths_answer_like_every_other_route()
    {
        Web.Reset();
        HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage page = await anonymous.GetAsync("/app/login");
        HttpResponseMessage login = await anonymous.PostAsJsonAsync(
            "/api/v1/web/session", new WebSessionRequest { Passphrase = "anything at all" });

        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_browser_reaches_the_sign_in_page_and_nothing_else()
    {
        Web.Reset();
        Web.SetPassphrase("correct horse battery staple");
        Web.SetEnabled(true);
        try
        {
            HttpClient anonymous = _factory.CreateClient();

            HttpResponseMessage page = await anonymous.GetAsync("/app/login");
            HttpResponseMessage client = await anonymous.GetAsync("/app");
            HttpResponseMessage conversations = await anonymous.GetAsync("/api/v1/conversations");

            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, client.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, conversations.StatusCode);
        }
        finally { Web.Reset(); }
    }

    /// <summary>
    /// Found live on the physical PC: opening /app in a fresh browser produced a bare
    /// {"error":"Unauthorized"} with nothing to say the door was at /app/login. A person who typed
    /// the address of their own PaRuX had no way forward.
    /// </summary>
    [Fact]
    public async Task A_browser_that_is_not_signed_in_is_sent_to_the_sign_in_page()
    {
        Web.Reset();
        Web.SetPassphrase("correct horse battery staple");
        Web.SetEnabled(true);
        try
        {
            HttpClient anonymous = _factory.CreateClient(
                new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var navigation = new HttpRequestMessage(HttpMethod.Get, "/app");
            navigation.Headers.Add("Accept", "text/html,application/xhtml+xml");

            HttpResponseMessage response = await anonymous.SendAsync(navigation);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/app/login", response.Headers.Location!.ToString());
        }
        finally { Web.Reset(); }
    }

    /// <summary>A script or a CLI keeps the 401 it can act on - a redirect would turn a clear
    /// failure into a confusing page of HTML.</summary>
    [Fact]
    public async Task An_api_call_without_credentials_still_gets_401_rather_than_a_redirect()
    {
        Web.Reset();
        Web.SetPassphrase("correct horse battery staple");
        Web.SetEnabled(true);
        try
        {
            HttpClient anonymous = _factory.CreateClient(
                new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            HttpResponseMessage response = await anonymous.GetAsync("/api/v1/conversations");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally { Web.Reset(); }
    }

    [Fact]
    public async Task A_signed_in_browser_gets_an_HttpOnly_Secure_SameSite_cookie()
    {
        Web.Reset();
        Web.SetPassphrase("correct horse battery staple");
        Web.SetEnabled(true);
        try
        {
            HttpClient anonymous = _factory.CreateClient();

            HttpResponseMessage response = await anonymous.PostAsJsonAsync(
                "/api/v1/web/session", new WebSessionRequest { Passphrase = "correct horse battery staple" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            Assert.Contains("parux_session=", cookie, StringComparison.Ordinal);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

            WebSessionResponse? body = await response.Content.ReadFromJsonAsync<WebSessionResponse>();
            Assert.True(body!.Authenticated);
            Assert.False(string.IsNullOrWhiteSpace(body.CsrfToken));
        }
        finally { Web.Reset(); }
    }

    [Fact]
    public async Task A_wrong_passphrase_is_rejected_and_sets_no_cookie()
    {
        Web.Reset();
        Web.SetPassphrase("correct horse battery staple");
        Web.SetEnabled(true);
        try
        {
            HttpClient anonymous = _factory.CreateClient();

            HttpResponseMessage response = await anonymous.PostAsJsonAsync(
                "/api/v1/web/session", new WebSessionRequest { Passphrase = "not the passphrase" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        finally { Web.Reset(); }
    }

    [Fact]
    public async Task A_browser_session_cannot_change_the_passphrase_that_let_it_in()
    {
        Web.Reset();
        Web.SetPassphrase("correct horse battery staple");
        Web.SetEnabled(true);
        try
        {
            HttpClient browser = _factory.CreateClient();
            HttpResponseMessage signIn = await browser.PostAsJsonAsync(
                "/api/v1/web/session", new WebSessionRequest { Passphrase = "correct horse battery staple" });
            WebSessionResponse body = (await signIn.Content.ReadFromJsonAsync<WebSessionResponse>())!;
            string cookie = signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/web/access")
            {
                Content = JsonContent.Create(new WebAccessConfigRequest { Passphrase = "a passphrase of my own" })
            };
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add(WebAccessService.CsrfHeaderName, body.CsrfToken);
            HttpResponseMessage response = await browser.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.NotNull(Web.TryAuthenticate("correct horse battery staple"));
        }
        finally { Web.Reset(); }
    }

    [Fact]
    public async Task A_state_changing_request_without_the_CSRF_header_is_refused()
    {
        Web.Reset();
        Web.SetPassphrase("correct horse battery staple");
        Web.SetEnabled(true);
        try
        {
            HttpClient browser = _factory.CreateClient();
            HttpResponseMessage signIn = await browser.PostAsJsonAsync(
                "/api/v1/web/session", new WebSessionRequest { Passphrase = "correct horse battery staple" });
            string cookie = signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];

            // Exactly what a cross-site form can do: send the cookie, but not a custom header.
            using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/v1/conversations")
            {
                Content = JsonContent.Create(new { title = "created by another site" })
            };
            forged.Headers.Add("Cookie", cookie);
            HttpResponseMessage response = await browser.SendAsync(forged);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally { Web.Reset(); }
    }

    [Fact]
    public async Task A_signed_in_browser_cannot_make_the_desktop_probe_AI_backends()
    {
        Web.Reset();
        Web.SetPassphrase("correct horse battery staple");
        Web.SetEnabled(true);
        try
        {
            HttpClient browser = _factory.CreateClient();
            HttpResponseMessage signIn = await browser.PostAsJsonAsync(
                "/api/v1/web/session", new WebSessionRequest { Passphrase = "correct horse battery staple" });
            string cookie = signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];

            using var detect = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ai/backends/detect");
            detect.Headers.Add("Cookie", cookie);
            using var external = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ai/backends/external");
            external.Headers.Add("Cookie", cookie);

            Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(detect)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(external)).StatusCode);
        }
        finally { Web.Reset(); }
    }

    [Fact]
    public async Task The_desktop_owns_the_browser_door()
    {
        Web.Reset();
        try
        {
            HttpClient desktop = DesktopClient();

            HttpResponseMessage set = await desktop.PostAsJsonAsync(
                "/api/v1/web/access",
                new WebAccessConfigRequest { Passphrase = "correct horse battery staple", Enabled = true });
            WebAccessStatus? status = await set.Content.ReadFromJsonAsync<WebAccessStatus>();

            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.True(status!.Enabled);
            Assert.True(status.PassphraseSet);
        }
        finally { Web.Reset(); }
    }

    [Fact]
    public async Task The_desktop_is_told_when_a_passphrase_is_too_short_instead_of_it_being_accepted()
    {
        Web.Reset();
        try
        {
            HttpClient desktop = DesktopClient();

            HttpResponseMessage response = await desktop.PostAsJsonAsync(
                "/api/v1/web/access", new WebAccessConfigRequest { Passphrase = "short" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(Web.PassphraseSet);
        }
        finally { Web.Reset(); }
    }
}
