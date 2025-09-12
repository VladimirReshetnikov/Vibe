// SPDX-License-Identifier: MIT-0

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vibe.Decompiler.Tests;

public class OpenAiLlmProviderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

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

    [Fact]
    public async Task RefineAsync_UsesResponsesEndpointAndParsesOutput()
    {
        var jsonResponse = "{\"output\":[{\"content\":[{\"text\":\"result code\"}]}]}";
        var handler = new FakeHandler(jsonResponse);
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");

        using var provider = new OpenAiLlmProvider("test");
        var field = typeof(OpenAiLlmProvider).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
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

