namespace PaRuX.Server;

/// <summary>
/// Tracks one planned set. A new download request reconstructs landed members from the managed
/// library, so completion survives a Core restart without persisting process state.
/// </summary>
internal sealed class SplitSetCompletionTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _expected;
    private readonly HashSet<string> _landed;
    private bool _completionClaimed;

    public SplitSetCompletionTracker(
        IEnumerable<string> expectedFileNames,
        IEnumerable<string> alreadyLandedFileNames,
        string firstPartPath,
        string label)
    {
        _expected = new HashSet<string>(expectedFileNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
        if (_expected.Count == 0) throw new ArgumentException("A split-set tracker requires at least one planned file.", nameof(expectedFileNames));
        _landed = new HashSet<string>(alreadyLandedFileNames.Where(_expected.Contains), StringComparer.OrdinalIgnoreCase);
        FirstPartPath = firstPartPath;
        Label = label;
    }

    /// <summary>The path a runtime opens: the first part, which reads the rest from the same folder.</summary>
    public string FirstPartPath { get; }
    public string Label { get; }

    /// <summary>True once, when this member makes every planned file present.</summary>
    public bool TryMarkLanded(string fileName)
    {
        lock (_gate)
        {
            if (!_expected.Contains(fileName)) return false;
            _landed.Add(fileName);
            if (_completionClaimed || _landed.Count != _expected.Count) return false;
            _completionClaimed = true;
            return true;
        }
    }
}
