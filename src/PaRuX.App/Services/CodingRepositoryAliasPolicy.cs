using System;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// A repository alias becomes a Windows directory name, a Git branch
    /// component and eventually a cmd.exe pushd argument. Enforce the same
    /// conservative identity everywhere, before creating paths. No relative
    /// segments, Unicode lookalikes, PATHEXT/device names or shell operators.
    /// </summary>
    public static class CodingRepositoryAliasPolicy
    {
        public const int MaximumLength = 32;

        public static bool IsValid(string? alias) =>
            PaRuX.Contracts.AgentRepositoryAliasValidation.IsValid(alias);

        public static bool IsSafeTaskId(string? taskId) =>
            !string.IsNullOrEmpty(taskId)
            && taskId.Length <= 128
            && (IsAsciiLetter(taskId[0])
                || (taskId[0] >= '0' && taskId[0] <= '9'))
            && !taskId.EndsWith(".", StringComparison.Ordinal)
            && !PaRuX.Contracts.AgentRepositoryAliasValidation.IsWindowsReservedDeviceName(taskId)
            && taskId.Skip(1).All(ch => IsAsciiLetter(ch)
                || (ch >= '0' && ch <= '9') || ch is '.' or '_' or '-');

        private static bool IsAsciiLetter(char ch) =>
            ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }
}
