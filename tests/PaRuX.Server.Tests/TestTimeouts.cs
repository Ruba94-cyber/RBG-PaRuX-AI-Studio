namespace PaRuX.Server.Tests;

/// <summary>
/// How long a test waits for a background job to reach a terminal state.
///
/// These are hang-guards, not performance gates: every use is a poll waiting for a service to
/// finish work it has already been told to do, and the assertion that follows is about *what*
/// happened, never how fast. That distinction matters because the values were 5-10 seconds, which
/// is a duration a loaded machine can genuinely exceed through scheduling delay alone - and one of
/// them did, failing a full-suite run while passing on its own re-run.
///
/// A number this large cannot be reached by scheduling pressure, only by something that is
/// actually stuck - which is the only thing these are meant to catch. It costs nothing when tests
/// pass, because the poll returns as soon as the state changes.
///
/// If a test ever does need to assert that work finishes *quickly*, it must assert on measured
/// elapsed time explicitly rather than by giving this a small value: a tight timeout is a
/// performance assertion that reports itself as a hang, which is the worst of both.
/// </summary>
internal static class TestTimeouts
{
    internal static readonly TimeSpan JobCompletion = TimeSpan.FromSeconds(60);
}
