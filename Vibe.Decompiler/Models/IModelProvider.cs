// SPDX-License-Identifier: MIT-0
namespace Vibe.Decompiler.Models;

/// <summary>
/// Abstraction over a large language model capable of turning raw decompiler
/// output into more idiomatic C/C++ code.
/// </summary>
public interface IModelProvider : IDisposable
{
    /// <summary>
    /// Asks the model to rewrite the provided decompiled text into cleaner code,
    /// optionally taking additional documentation into account.
    /// </summary>
    /// <param name="decompiledCode">Raw output from the decompiler.</param>
    /// <param name="language">Programming language the refined code should target.</param>
    /// <param name="documentation">Optional snippets of reference documentation.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>The refined source code as a string.</returns>
    Task<string> RefineAsync(
        string decompiledCode,
        string language,
        IEnumerable<string>? documentation = null,
        CancellationToken cancellationToken = default);
}
