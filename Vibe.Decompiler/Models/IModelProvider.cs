// SPDX-License-Identifier: MIT-0
namespace Vibe.Decompiler.Models;

public interface IModelProvider : IDisposable
{
    Task<string> RefineAsync(
        string decompiledCode,
        IEnumerable<string>? documentation = null,
        CancellationToken cancellationToken = default);
}
