using System.Text;
using System.Text.Json;
using RedigiAI.Infrastructure.Anthropic;

namespace RedigiAI.UnitTests.Infrastructure;

/// <summary>Leitura da resposta em streaming da API de mensagens (Server-Sent Events).</summary>
public class ClaudeStreamTests
{
    private static Task<JsonDocument> ReadAsync(params string[] events) => ReadAsync(new ClaudeStreamProgress(), events);

    private static Task<JsonDocument> ReadAsync(ClaudeStreamProgress progress, params string[] events)
    {
        var text = string.Join("\n", events.Select(data => $"event: x\ndata: {data}\n"));
        return ClaudeStream.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), progress, CancellationToken.None);
    }

    private const string Start =
        """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":1220,"output_tokens":1,"cache_read_input_tokens":9899,"cache_creation_input_tokens":0}}}""";

    private const string ToolStart =
        """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"registrar_correcao","input":{}}}""";

    private static string Piece(string json) =>
        JsonSerializer.Serialize(new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = json } });

    private static string End(string stopReason, int outputTokens) =>
        JsonSerializer.Serialize(new { type = "message_delta", delta = new { stop_reason = stopReason }, usage = new { output_tokens = outputTokens } });

    private const string Stop = """{"type":"message_stop"}""";

    [Fact]
    public async Task Tool_input_is_joined_from_the_pieces_with_the_usage_and_stop_reason()
    {
        using var message = await ReadAsync(
            Start,
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
            """{"type":"content_block_stop","index":0}""",
            ToolStart,
            Piece("""{"zero": {"appl"""),
            Piece("""ies": false}, "nota": "Não é \"assim\""}"""),
            """{"type":"content_block_stop","index":1}""",
            """{"type":"ping"}""",
            End("tool_use", 3120),
            Stop);
        var root = message.RootElement;

        Assert.Equal("tool_use", root.GetProperty("stop_reason").GetString());
        Assert.False(ClaudeMessagesApi.IsTruncated(root));
        var input = ClaudeMessagesApi.ToolInputOf(root, "registrar_correcao")!.Value;
        Assert.False(input.GetProperty("zero").GetProperty("applies").GetBoolean());
        Assert.Equal("Não é \"assim\"", input.GetProperty("nota").GetString());
        // A entrada vem do message_start; a saída, do total no message_delta.
        Assert.Equal(new RedigiAI.Application.Abstractions.AiTokens(1220, 3120, 9899, 0), ClaudeMessagesApi.TokensOf(root));
    }

    [Fact]
    public async Task Response_cut_at_max_tokens_is_marked_truncated_and_has_no_tool_input()
    {
        using var message = await ReadAsync(Start, ToolStart, Piece("""{"zero": {"applies": fal"""), End("max_tokens", 32000), Stop);
        var root = message.RootElement;

        Assert.True(ClaudeMessagesApi.IsTruncated(root));
        Assert.Null(ClaudeMessagesApi.ToolInputOf(root, "registrar_correcao"));
        Assert.Equal(32000, ClaudeMessagesApi.TokensOf(root).Output);
    }

    [Fact]
    public async Task Error_event_in_the_middle_of_the_stream_is_raised_with_its_type()
    {
        var error = await Assert.ThrowsAsync<ClaudeStreamError>(() => ReadAsync(
            Start, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));

        Assert.Equal("overloaded_error", error.ErrorType);
    }

    [Fact]
    public async Task Stream_that_ends_before_message_stop_is_an_error_and_keeps_what_was_charged()
    {
        var progress = new ClaudeStreamProgress();

        await Assert.ThrowsAsync<IOException>(() => ReadAsync(progress, Start, ToolStart, Piece("""{"zero":""")));

        // A IA já começou a responder: a entrada é conhecida, a saída ainda não.
        Assert.True(progress.Started);
        Assert.False(progress.OutputKnown);
        Assert.Equal(1220, progress.Usage["input_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Failure_before_message_start_charges_nothing()
    {
        var progress = new ClaudeStreamProgress();

        await Assert.ThrowsAsync<IOException>(() => ReadAsync(progress, """{"type":"ping"}"""));

        Assert.False(progress.Started);
    }

    [Theory]
    [InlineData("não é json")]
    [InlineData("""{"sem":"tipo"}""")]
    [InlineData("""{"type":"content_block_start","index":"um","content_block":{"type":"text"}}""")]
    public async Task Malformed_event_is_a_stream_error(string data)
    {
        var error = await Assert.ThrowsAsync<ClaudeStreamError>(() => ReadAsync(Start, data, Stop));

        Assert.Equal("malformed_event", error.ErrorType);
    }
}
