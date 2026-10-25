using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Builds the single grouped notice for the candidates whose notice is still pending. Read-only: it never
/// downloads, evaluates or promotes anything, and a persisted Later/Ignore choice keeps a group out of the notice.</summary>
internal static class ModelDiscoveryGroupedNoticeBuilder
{
    internal static ModelDiscoveryGroupedNotice? Build(ModelDiscoveryCatalog catalog, bool autoEvaluate,
        DateTime nowUtc, string? noticeRoot = null)
    {
        var groups = new List<ModelDiscoveryNoticeGroup>();
        var pendingKeys = new List<string>();
        foreach (IGrouping<string, ModelDiscoveryCandidate> group in catalog.Candidates
                     .Where(c => c.Lifecycle == "Candidate" && c.Disposition == "active" && c.NoticePending)
                     .GroupBy(c => c.RepoId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            ModelDiscoveryGroupNoticeStore.Row? row;
            try { row = ModelDiscoveryGroupNoticeStore.Get(group.Key, noticeRoot); }
            catch (InvalidDataException) { row = null; }
            if (row is not null && !ModelDiscoveryGroupNoticeStore.ShouldNotify(group.Key, nowUtc, noticeRoot)) continue;
            ModelDiscoveryCandidate[] items = group.ToArray();
            long? smallest = items.Where(c => c.DeclaredSizeBytes > 0).Select(c => c.DeclaredSizeBytes).Min();
            string? licence = items.Select(c => c.DeclaredLicense).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            groups.Add(new ModelDiscoveryNoticeGroup
            {
                GroupKey = group.Key, CandidateCount = items.Length, SmallestDeclaredSizeBytes = smallest,
                DeclaredLicense = licence, Choice = row?.Choice ?? "none", LaterUntilUtc = row?.LaterUntilUtc,
                Text = ModelDiscoveryNoticeText.Group(group.Key, items.Length, smallest, licence)
            });
            pendingKeys.AddRange(items.Select(c => c.Key));
        }
        if (groups.Count == 0) return null;
        pendingKeys.Sort(StringComparer.Ordinal);
        return new ModelDiscoveryGroupedNotice
        {
            NoticeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', pendingKeys))))
                .ToLowerInvariant(),
            Headline = ModelDiscoveryNoticeText.Headline(groups.Count, pendingKeys.Count),
            Detail = ModelDiscoveryNoticeText.Detail(autoEvaluate), AutoEvaluate = autoEvaluate, Groups = groups
        };
    }
}
