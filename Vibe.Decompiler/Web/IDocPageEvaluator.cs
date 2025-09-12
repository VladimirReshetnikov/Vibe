// // SPDX-License-Identifier: MIT-0

namespace Vibe.Decompiler.Web;

/// <summary>
/// Evaluates whether a downloaded documentation page contains useful
/// information about a particular API function.
/// </summary>
public interface IDocPageEvaluator : IDisposable
{
    /// <summary>
    /// Determines if the supplied page fragment is relevant to the function
    /// being searched.
    /// </summary>
    /// <param name="functionName">Name of the function being documented.</param>
    /// <param name="content">Page content to evaluate.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns><c>true</c> if the fragment contains useful information.</returns>
    Task<bool> IsRelevantAsync(string functionName, string content, CancellationToken cancellationToken = default);
}
