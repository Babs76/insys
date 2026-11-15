/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Tests
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Ai;
using Insys.BuildingBlocks.Cloud;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Tests;

/// <summary>
/// The Claude adapter (ai-assist.md §5, batch 7.3), against a local fake of the Messages API: no real model in CI (AI Assist SRS §9.2). The fake keeps the
/// request it got, so the tests read what would leave INSYS, and answers as told.
/// </summary>
public sealed class ClaudeModelProviderTests
{
    private const string Instructions = "Explain using only the facts between the data tags.";
    private const string Data = "<insys-data>\ncode: CURRENCY_MISMATCH\n</insys-data>";

    [Fact]
    public async Task TheTemplateIsTheSystemPrompt_AndTheDataTheOnlyUserMessage_WithNoTools_AIA060_AIA061()
    {
        await using var api = new FakeMessagesApi(Answer("end_turn", """{"input_tokens":120,"output_tokens":30,"inference_geo":"global"}"""));
        var answer = await Provider(api.Url).Complete(new AiModelRequest("claude-opus-5-5", "refusal-v1", Instructions, Data), CancellationToken.None);

        using var sent = JsonDocument.Parse(api.Requests.Single());
        var body = sent.RootElement;
        body.GetProperty("model").GetString().ShouldBe("claude-opus-5-5");
        body.GetProperty("max_tokens").GetInt32().ShouldBe(1024);
        SystemText(body).ShouldBe(Instructions);
        var messages = body.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(1);
        messages[0].GetProperty("role").GetString().ShouldBe("user");
        MessageText(messages[0]).ShouldBe(Data);
        body.TryGetProperty("tools", out _).ShouldBeFalse("the model gets no tool, so it can't write, approve, pay or send");
        body.GetProperty("output_config").GetProperty("effort").GetString().ShouldBe("medium");
        body.TryGetProperty("inference_geo", out _).ShouldBeFalse("no region is asked for unless the deployment sets one");

        answer.Text.ShouldBe("A fixed explanation.");
        answer.InputTokens.ShouldBe(120);
        answer.OutputTokens.ShouldBe(30);
        answer.ServedModel.ShouldBe("claude-opus-5-5");
        answer.InferenceRegion.ShouldBe("global", "as the route reports it");
    }

    [Fact]
    public async Task ARegionTheRouteDoesntReport_IsRecordedAsUnknown_NeverAssumed_AIA050()
    {
        await using var api = new FakeMessagesApi(Answer("end_turn", """{"input_tokens":10,"output_tokens":5}"""));
        var answer = await Provider(api.Url).Complete(new AiModelRequest("claude-opus-5-5", "refusal-v1", Instructions, Data), CancellationToken.None);
        answer.InferenceRegion.ShouldBeNull();
    }

    [Fact]
    public async Task ARegionTheDeploymentSets_IsAskedFor()
    {
        await using var api = new FakeMessagesApi(Answer("end_turn", """{"input_tokens":10,"output_tokens":5,"inference_geo":"us"}"""));
        var answer = await Provider(api.Url, o => o.InferenceGeo = "us").Complete(new AiModelRequest("claude-opus-5-5", "refusal-v1", Instructions, Data), CancellationToken.None);
        using var sent = JsonDocument.Parse(api.Requests.Single());
        sent.RootElement.GetProperty("inference_geo").GetString().ShouldBe("us");
        answer.InferenceRegion.ShouldBe("us");
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    public async Task ARefusal_OrAnAnswerCutShort_IsUnavailable_SoThePlainBreakdownIsShown_P8(string stopReason)
    {
        await using var api = new FakeMessagesApi(Answer(stopReason, """{"input_tokens":10,"output_tokens":5}"""));
        await Should.ThrowAsync<AiModelUnavailableException>(() => Provider(api.Url).Complete(new AiModelRequest("claude-opus-5-5", "refusal-v1", Instructions, Data), CancellationToken.None));
    }

    [Fact]
    public async Task AnApiError_IsUnavailable_AndNothingOfTheRequestIsInTheError_AIA051()
    {
        await using var api = new FakeMessagesApi((HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"code: CURRENCY_MISMATCH"}}"""));
        var error = await Should.ThrowAsync<AiModelUnavailableException>(() =>
            Provider(api.Url).Complete(new AiModelRequest("claude-opus-5-5", "refusal-v1", Instructions, Data), CancellationToken.None));
        error.Message.ShouldNotContain("CURRENCY_MISMATCH");
        api.Requests.Count.ShouldBe(1, "a request refused as invalid isn't retried");
    }

    [Fact]
    public void WithoutAKey_TheAdapterRefusesToStart_AIA053()
    {
        var error = Should.Throw<InvalidOperationException>(() => new ClaudeModelProvider(Options.Create(new ClaudeModelOptions()), NullLogger<ClaudeModelProvider>.Instance));
        error.Message.ShouldContain("Assist:Claude:ApiKey");
    }

    private static ClaudeModelProvider Provider(string url, Action<ClaudeModelOptions>? configure = null)
    {
        var options = new ClaudeModelOptions { ApiKey = "test-key-not-real", BaseUrl = url, TimeoutSeconds = 10 };
        configure?.Invoke(options);
        return new ClaudeModelProvider(Options.Create(options), NullLogger<ClaudeModelProvider>.Instance);
    }

    private static (HttpStatusCode, string) Answer(string stopReason, string usage) => (HttpStatusCode.OK, $$"""
        {"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"text","text":"A fixed explanation."}],
         "stop_reason":"{{stopReason}}","stop_sequence":null,"usage":{{usage}}}
        """);

    private static string SystemText(JsonElement body) =>
        body.GetProperty("system") is { ValueKind: JsonValueKind.String } text ? text.GetString()! : string.Concat(body.GetProperty("system").EnumerateArray().Select(b => b.GetProperty("text").GetString()));

    private static string MessageText(JsonElement message) =>
        message.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()! : string.Concat(message.GetProperty("content").EnumerateArray().Select(b => b.GetProperty("text").GetString()));

    /// <summary>A local HTTP listener that answers every request the same way and keeps each request's body.</summary>
    private sealed class FakeMessagesApi : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly List<string> _requests = [];

        public FakeMessagesApi((HttpStatusCode Status, string Body) answer)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception) when (!_listener.IsListening)
                    {
                        return;
                    }

                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        var body = await reader.ReadToEndAsync();
                        lock (_requests)
                        {
                            _requests.Add(body);
                        }
                    }

                    context.Response.StatusCode = (int)answer.Status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(answer.Body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public string Url { get; }

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await _loop;
        }

        private static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }
    }
}
