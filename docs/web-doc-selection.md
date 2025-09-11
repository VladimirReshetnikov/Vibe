# Web Documentation Selection

## Overview
This document outlines a design for selecting a concise yet representative set of web documentation
pages about an API function. The goal is to gather online information using
`DuckDuckGoDocFetcher` and feed only the most useful subset to an LLM within a
configurable token budget.

## Requirements
- Fetch search results and content as today, but keep metadata (URL, snippet,
  raw text).
- Rank pages by relevance rather than making a binary keep/discard decision.
- Stop after the accumulated token count or page count exceeds configurable
  limits.
- Avoid sending multiple near-duplicate pages.
- Expose tunable knobs such as maximum pages examined, similarity thresholds and
  scoring weights.

## Proposed Architecture

### 1. Page relevance evaluation
`OpenAiDocPageEvaluator` changes its contract from returning `bool` to returning
a floating‑point relevance score in the `[0,1]` range. The evaluator will prompt
the OpenAI model to answer with a numeric value instead of "yes"/"no". The score
is computed for each fragment supplied by `DuckDuckGoDocFetcher`; the page score
is the maximum fragment score. Pages whose score falls below a configurable
threshold are discarded early.

### 2. Similarity filtering
To prevent redundant pages, each surviving page receives an embedding (e.g.
`text-embedding-3-small`). Cosine similarity is computed between the new page
and previously accepted ones. If similarity exceeds `SimilarityThreshold`
(default ~0.9), the page is skipped. This avoids sending multiple copies of the
same manual or blog post.

### 3. Selection process
1. Search DuckDuckGo and fetch up to `MaxPages` HTML documents.
2. Fragment each page and obtain relevance scores.
3. Sort pages by score (descending).
4. Starting from the highest scoring page, accumulate content until
   `MaxTotalTokens` or `MaxSelectedPages` is reached. Token counts are estimated
   using the same tokenizer as the target LLM.
5. Return the ordered list of selected pages for downstream prompting.

### 4. Configuration
New options are added to `DuckDuckGoDocFetcher.FindDocumentationPagesAsync`:
- `maxSelectedPages` – cap on pages returned after filtering.
- `maxTotalTokens` – token budget for the final set.
- `similarityThreshold` – cosine similarity above which a page is considered a
  duplicate.
- `scoreThreshold` – minimum relevance to keep a page.
`OpenAiDocPageEvaluator` exposes `EmbeddingModel` and `Tokenizer`
settings used for similarity and token estimation.

### 5. Extensibility
- Domain weighting: prefer official documentation domains via manual weights.
- Freshness: incorporate HTTP `Last-Modified` when available.
- Cache embeddings and scores for repeat queries.

## Pseudo-code
```csharp
var candidates = await DuckDuckGoDocFetcher.FindDocumentationPagesAsync(
    "FunctionName", maxPages, evaluator, options);

var selected = new List<Page>();
foreach (var page in candidates.OrderByDescending(p => p.Score))
{
    if (page.Score < options.ScoreThreshold) continue;
    if (selected.Sum(p => p.TokenCount) + page.TokenCount > options.MaxTotalTokens) break;
    if (selected.Any(p => CosineSimilarity(p.Embedding, page.Embedding) > options.SimilarityThreshold)) continue;
    selected.Add(page);
    if (selected.Count == options.MaxSelectedPages) break;
}
```

## Risks and Mitigations
- **API cost** – relevance scoring and embeddings require additional API calls;
  batch requests and caching can reduce cost.
- **Latency** – network calls may slow queries; design allows parallel fetch and
  evaluation.
- **Model drift** – scoring quality depends on the LLM; expose configuration so
  models can be swapped without code changes.

