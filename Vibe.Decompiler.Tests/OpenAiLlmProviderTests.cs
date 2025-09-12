// SPDX-License-Identifier: MIT-0

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vibe.Decompiler.Models;
using Xunit;

namespace Vibe.Decompiler.Tests;

/// <summary>
/// Tests for the <see cref="OpenAiModelProvider"/> to ensure HTTP requests are
/// formed correctly and the JSON response is parsed as expected.
/// </summary>
public class OpenAiLlmProviderTests
{
    /// <summary>
    /// Fake <see cref="HttpMessageHandler"/> used to capture outgoing requests
    /// and supply a predefined JSON response without contacting the real API.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        /// <summary>The last request issued by the provider.</summary>
        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>The serialized body of the last request.</summary>
        public string? LastRequestBody { get; private set; }

        /// <summary>
        /// Initializes the handler with the JSON payload to return.
        /// </summary>
        /// <param name="responseJson">JSON string to send back to callers.</param>
        public FakeHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json")
            };
            return resp;
        }
    }

    /// <summary>
    /// Ensures that <see cref="OpenAiModelProvider.RefineAsync"/> targets the
    /// <c>/responses</c> endpoint and that the resulting text is extracted from
    /// the nested JSON structure.
    /// </summary>
    [Fact]
    public async Task RefineAsync_UsesResponsesEndpointAndParsesOutput()
    {
        var jsonResponse = "{\"output\":[{\"type\":\"message\",\"content\":[{\"text\":\"result code\"}]}]}";
        var handler = new FakeHandler(jsonResponse);
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");

        using var provider = new OpenAiModelProvider("test");
        var field = typeof(OpenAiModelProvider).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(provider, client);

        string refined = await provider.RefineAsync("int main() {}");
        Assert.Equal("result code", refined);

        Assert.Equal("https://api.openai.com/v1/responses", handler.LastRequest!.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("You rewrite decompiled machine code into clear and idiomatic C code.", doc.RootElement.GetProperty("instructions").GetString());
        Assert.True(doc.RootElement.TryGetProperty("input", out var input) && input.GetArrayLength() > 0);
    }
}

