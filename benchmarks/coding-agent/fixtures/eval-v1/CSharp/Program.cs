using PaRuX.Benchmarks.EvalV1;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var id = args.Length == 0 ? "" : args[0];
switch (id)
{
    case "csharp-clamp-budget":
        Require(EvalLogic.ClampBudget(-1, 10) == 0, "negative must clamp to zero");
        Require(EvalLogic.ClampBudget(7, 10) == 7, "in-range must stay unchanged");
        Require(EvalLogic.ClampBudget(12, 10) == 10, "must clamp to maximum");
        break;
    case "csharp-merge-order":
        Require(EvalLogic.MergeUniquePreserveOrder(new[]{"b","a","b"}, new[]{"c","a"}).SequenceEqual(new[]{"b","a","c"}), "dedupe/order");
        break;
    case "csharp-retry-cancel":
        Require(!EvalLogic.ShouldRetry(503, true), "cancelled request must not retry");
        Require(EvalLogic.ShouldRetry(408, false), "408 transient");
        Require(EvalLogic.ShouldRetry(429, false), "429 transient");
        Require(EvalLogic.ShouldRetry(503, false), "503 transient");
        Require(!EvalLogic.ShouldRetry(500, false), "500 not in declared transient set");
        Require(!EvalLogic.ShouldRetry(400, false), "400 permanent");
        break;
    case "csharp-ledger-supersession":
        var state = EvalLogic.ResolveLatest(new[]{
            new LedgerEvent(1,"mode","old",false),
            new LedgerEvent(3,"theme","dark",false),
            new LedgerEvent(4,"mode","new",false),
            new LedgerEvent(5,"theme","",true),
        });
        Require(state.Count == 1 && state["mode"] == "new" && !state.ContainsKey("theme"), "latest/tombstone semantics");
        break;
    case "csharp-chunk-assembly":
        Require(EvalLogic.AssembleChunks(new[]{(2,"C"),(0,"A"),(1,"B")}) == "ABC", "sort chunks");
        var failed = false;
        try { _ = EvalLogic.AssembleChunks(new[]{(0,"A"),(2,"C")}); } catch (InvalidOperationException) { failed = true; }
        Require(failed, "gap must fail");
        break;
    case "csharp-path-scope":
        var baseRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "parux-root"));
        Require(EvalLogic.IsUnderRoot(baseRoot, Path.Combine(baseRoot, "src", "a.cs")), "child allowed");
        Require(!EvalLogic.IsUnderRoot(baseRoot, Path.GetFullPath(Path.Combine(baseRoot, "..", "escape.cs"))), "escape rejected");
        Require(!EvalLogic.IsUnderRoot(baseRoot, baseRoot + "-other" + Path.DirectorySeparatorChar + "x"), "prefix collision rejected");
        break;
    default:
        throw new InvalidOperationException($"Unknown case: {id}");
}
Console.WriteLine("PASS");
