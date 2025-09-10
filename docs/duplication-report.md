# Code Duplication Analysis

This document summarizes repeated implementation patterns found in the repository and proposes refactorings to reduce duplication and improve maintainability.

## 1. HTTP client setup in documentation fetchers

Both `DuckDuckGoDocFetcher` and `Win32DocFetcher` create a static `HttpClient` with identical configuration: a 30‑second timeout and a custom `User-Agent` header.

- `DuckDuckGoDocFetcher` configures the client in its static constructor and stores it in a static field.
- `Win32DocFetcher` repeats the same configuration independently.

**Files:**
- `Vibe.Decompiler/DuckDuckGoDocFetcher.cs`
- `Vibe.Decompiler/Win32DocFetcher.cs`

**Proposal:** Introduce a shared helper (e.g., `DocHttpClientFactory` or a common base class) that provides a preconfigured `HttpClient`. Both fetchers can then reuse this client, ensuring consistent settings and easier updates.

## 2. Error handling for network requests

The documentation fetchers and other classes contain similar try/catch blocks that handle `HttpRequestException` and `OperationCanceledException`, rethrowing if the cancellation token was triggered and otherwise returning empty results.

**Files:**
- `Vibe.Decompiler/DuckDuckGoDocFetcher.cs`
- `Vibe.Decompiler/Win32DocFetcher.cs`
- `Vibe.Decompiler/LlmProviders.cs`

**Proposal:** Extract a utility method such as `HttpHelpers.TryGetStringAsync` that encapsulates this error-handling logic. Callers can pass a URL and cancellation token, receiving either the response string or a `null`/empty result when the request fails.

## 3. OpenAI API interaction

`OpenAiDocPageEvaluator` and `OpenAiLlmProvider` each contain near-identical code to send chat completion requests to the OpenAI API and parse the first result.

**Files:**
- `Vibe.Decompiler/DuckDuckGoDocFetcher.cs` (`OpenAiDocPageEvaluator`)
- `Vibe.Decompiler/LlmProviders.cs` (`OpenAiLlmProvider`)

**Proposal:** Create an `OpenAiClient` class that exposes a generic `SendChatAsync` method. Both the documentation evaluator and the code‑refinement provider could build their prompts and delegate the HTTP communication and JSON parsing to this shared client, removing duplicate serialization and error checking.

## 4. LLM provider structure

The OpenAI and Anthropic providers follow the same high‑level pattern: build JSON from a prompt, POST it to the service, validate the HTTP response, parse the JSON body, and return the model's text.

**Files:**
- `Vibe.Decompiler/LlmProviders.cs` (`OpenAiLlmProvider`, `AnthropicLlmProvider`)

**Proposal:** Introduce a base class (e.g., `LlmProviderBase`) that owns the `HttpClient` and provides helper methods for `PostJsonAsync`, status checking, and extracting the textual content. Concrete providers would only specify the endpoint, authorization headers, and model‑specific payload fields.

## Benefits of refactoring

- **Consistency:** Centralizing HTTP configuration and error handling ensures uniform behavior across modules.
- **Maintainability:** Bug fixes or updates (e.g., changing user agent or timeout) occur in one place.
- **Testability:** Smaller, focused helpers are easier to unit‑test than repeated inline code.

Implementing these refactorings would reduce duplication and simplify the addition of new documentation fetchers or LLM providers in the future.

