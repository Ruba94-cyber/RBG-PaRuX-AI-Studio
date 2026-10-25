using System;
using System.IO;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Selects the Core-owned primary repository for a single-repository apply.</summary>
    public static class AgentRepositoryApplyAuthority
    {
        public static string RequireSinglePrimaryRoot(AgentRepositorySetDto set, string taskId)
        {
            if (set == null || !string.Equals(set.TaskId, taskId, StringComparison.Ordinal)
                || set.Repositories == null || set.Repositories.Count != 1)
                throw new InvalidDataException("The task has no unambiguous single-repository authority.");

            AgentRepositoryAuthorityDto primary = set.Repositories[0];
            if (!primary.IsPrimary || string.IsNullOrWhiteSpace(primary.WorkspaceRoot))
                throw new InvalidDataException("The task has no primary repository authority.");

            return primary.WorkspaceRoot;
        }
    }
}
