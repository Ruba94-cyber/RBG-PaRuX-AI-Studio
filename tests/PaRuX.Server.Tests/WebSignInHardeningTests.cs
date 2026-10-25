using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Hardening of the browser sign-in: lockout accounting under concurrent guesses, the session lifetime under a concurrent
/// revocation, and a browser-access file that cannot be read. Each test pins a behaviour the code it names used to get wrong.
/// </summary>
public sealed class WebSignInHardeningTests
{
    private const string GoodPassphrase = "correct horse battery staple";

    private static WebAccessService NewService(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "parux-web-" + Guid.NewGuid().ToString("N") + ".json");
        return new WebAccessService(path);
    }

    [Fact]
    public async Task Concurrent_wrong_guesses_cannot_run_more_verifications_than_the_lockout_allows()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            web.SetEnabled(true);

            var start = new ManualResetEventSlim(false);
            Task<WebAccessService.Session?>[] guesses = Enumerable.Range(0, 50)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    return web.TryAuthenticate("a wrong passphrase, guessed in parallel");
                }))
                .ToArray();
            start.Set();
            await Task.WhenAll(guesses);

            Assert.All(guesses, guess => Assert.Null(guess.Result));
            Assert.True(web.PassphraseVerificationCount <= 5,
                $"Expected at most 5 passphrase verifications, saw {web.PassphraseVerificationCount}.");
            Assert.True(web.IsLockedOut(out _));
            Assert.Null(web.TryAuthenticate(GoodPassphrase));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_session_revoked_in_the_middle_of_a_resume_is_not_brought_back()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            web.SetEnabled(true);
            WebAccessService.Session session = web.TryAuthenticate(GoodPassphrase)!;

            // The revocation lands in the window between a resume reading the session and writing its slid copy back.
            web.BeforeResumeWriteForTests = () =>
            {
                web.BeforeResumeWriteForTests = null;
                web.Revoke(session.Id);
            };

            Assert.Null(web.TryResume(session.Id));
            Assert.Null(web.TryResume(session.Id));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Parallel_resumes_racing_a_revocation_never_bring_the_session_back()
    {
        WebAccessService web = NewService(out string path);
        try
        {
            web.SetPassphrase(GoodPassphrase);
            web.SetEnabled(true);
            WebAccessService.Session session = web.TryAuthenticate(GoodPassphrase)!;

            // revoked is set only after Revoke has returned, so a resume that reads it set began after the revocation.
            int revoked = 0;
            int resumesBeforeRevoke = 0;
            int resumesAfterRevoke = 0;
            var start = new ManualResetEventSlim(false);

            Task[] resumers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                start.Wait();
                int afterRevoke = 0;
                for (int i = 0; i < 200_000 && afterRevoke < 200; i++)
                {
                    bool revokedBeforeCall = Volatile.Read(ref revoked) == 1;
                    WebAccessService.Session? resumed = web.TryResume(session.Id);
                    if (!revokedBeforeCall)
                    {
                        if (resumed is not null) Interlocked.Increment(ref resumesBeforeRevoke);
                        continue;
                    }
                    afterRevoke++;
                    if (resumed is not null) Interlocked.Increment(ref resumesAfterRevoke);
                }
            })).ToArray();

            Task revoker = Task.Run(() =>
            {
                start.Wait();
                // Revoke only once live resumes are succeeding, so it lands in the middle of them rather than before them.
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (Volatile.Read(ref resumesBeforeRevoke) < 200 && DateTime.UtcNow < deadline) Thread.Yield();
                web.Revoke(session.Id);
                Volatile.Write(ref revoked, 1);
            });

            start.Set();
            await Task.WhenAll(resumers.Append(revoker));

            Assert.True(resumesBeforeRevoke > 0, "No resume succeeded before the revocation, so the race was never exercised.");
            Assert.Equal(0, Volatile.Read(ref resumesAfterRevoke));
            Assert.Null(web.TryResume(session.Id));
            Assert.Equal(0, web.ActiveSessionCount);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void A_corrupt_browser_access_file_keeps_access_off_and_is_not_overwritten()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-web-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"Enabled\": tru");
            byte[] before = File.ReadAllBytes(path);

            var web = new WebAccessService(path);

            Assert.False(web.Enabled);
            // The unreadable file is named first: "set a passphrase" would send the person to fix the wrong thing.
            InvalidDataException enable = Assert.Throws<InvalidDataException>(() => web.SetEnabled(true));
            Assert.Contains("could not be read", enable.Message);
            Assert.Throws<InvalidDataException>(() => web.SetPassphrase(GoodPassphrase));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData("{\"Enabled\": true, \"PassphraseHash\": null, \"Salt\": \"\", \"Iterations\": 210000}")]
    [InlineData("{\"Enabled\": true, \"PassphraseHash\": \"\", \"Salt\": null, \"Iterations\": 210000}")]
    [InlineData("{\"Enabled\": true, \"PassphraseHash\": \"not base64 at all!\", \"Salt\": \"\", \"Iterations\": 210000}")]
    public void A_browser_access_file_with_a_null_or_malformed_secret_is_unreadable_not_a_blank_store(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-web-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, contents);
            byte[] before = File.ReadAllBytes(path);

            var web = new WebAccessService(path);

            Assert.False(web.Enabled);
            Assert.False(web.PassphraseSet);
            Assert.Null(web.TryAuthenticate(GoodPassphrase));
            Assert.Throws<InvalidDataException>(() => web.SetEnabled(true));
            Assert.Throws<InvalidDataException>(() => web.SetPassphrase(GoodPassphrase));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Delete(path); }
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
