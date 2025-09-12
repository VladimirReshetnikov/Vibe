// // SPDX-License-Identifier: MIT-0

namespace Vibe.Decompiler.Web;

public interface IDocPageEvaluator : IDisposable
{
    Task<bool> IsRelevantAsync(string functionName, string content, CancellationToken cancellationToken = default);
}
