using System.Text.Json;
using FilmStudio.Core.Models;
using FilmStudio.Engine;
using Xunit;

namespace FilmStudio.Tests;

/// <summary>
/// Covers the parts of the new provider clients that don't require a live API call: response
/// parsing (given a sample payload, does it extract the right text/image/video-uri) and the
/// routing decision the multi-provider dispatchers make (via SupportedModelCatalog, which is
/// what their private Resolve() methods call — this exercises the same decision without
/// needing to construct a full HttpClient-backed client graph).
/// </summary>
public class MultiProviderClientTests
{
    // ── Anthropic response parsing ──────────────────────────────────────────

    [Fact]
    public void Anthropic_extracts_single_text_block()
    {
        using var doc = JsonDocument.Parse("""
            { "content": [{ "type": "text", "text": "hello world" }] }
            """);
        var text = AnthropicChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.Equal("hello world", text);
    }

    [Fact]
    public void Anthropic_joins_multiple_text_blocks_and_skips_non_text()
    {
        using var doc = JsonDocument.Parse("""
            { "content": [
                { "type": "text", "text": "first" },
                { "type": "tool_use", "id": "x", "name": "y", "input": {} },
                { "type": "text", "text": "second" }
            ] }
            """);
        var text = AnthropicChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.Equal("first\nsecond", text);
    }

    // ── Gemini chat response parsing ────────────────────────────────────────

    [Fact]
    public void Gemini_extracts_text_from_first_candidate()
    {
        using var doc = JsonDocument.Parse("""
            { "candidates": [
                { "content": { "role": "model", "parts": [{ "text": "hello" }, { "text": " world" }] } }
            ] }
            """);
        var text = GeminiChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.Equal("hello\n world", text);
    }

    [Fact]
    public void Gemini_falls_back_to_raw_json_when_shape_is_unrecognized()
    {
        using var doc = JsonDocument.Parse("""{ "unexpected": "shape" }""");
        var text = GeminiChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.Contains("unexpected", text);
    }

    // ── Gemini image response parsing ───────────────────────────────────────

    [Fact]
    public void Gemini_image_extracts_inline_data_camelCase()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var json = $$"""
            { "candidates": [
                { "content": { "parts": [
                    { "inlineData": { "mimeType": "image/png", "data": "{{b64}}" } }
                ] } }
            ] }
            """;
        var bytes = GeminiImageClient.ExtractInlineImage(json);
        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
    }

    [Fact]
    public void Gemini_image_extracts_inline_data_snake_case_fallback()
    {
        var b64 = Convert.ToBase64String(new byte[] { 9, 9 });
        var json = $$"""
            { "candidates": [
                { "content": { "parts": [
                    { "inline_data": { "mime_type": "image/png", "data": "{{b64}}" } }
                ] } }
            ] }
            """;
        var bytes = GeminiImageClient.ExtractInlineImage(json);
        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 9, 9 }, bytes);
    }

    [Fact]
    public void Gemini_image_returns_null_for_text_only_response()
    {
        var json = """{ "candidates": [{ "content": { "parts": [{ "text": "no image" }] } }] }""";
        Assert.Null(GeminiImageClient.ExtractInlineImage(json));
    }

    // ── Gemini video (Veo) operation response parsing ───────────────────────

    [Fact]
    public void Veo_extracts_uri_from_generateVideoResponse_shape()
    {
        using var doc = JsonDocument.Parse("""
            { "done": true, "response": { "generateVideoResponse": { "generatedSamples": [
                { "video": { "uri": "https://example.com/a.mp4" } }
            ] } } }
            """);
        var uri = GeminiVideoClient.ExtractVideoUri(doc.RootElement);
        Assert.Equal("https://example.com/a.mp4", uri);
    }

    [Fact]
    public void Veo_extracts_uri_from_videos_array_shape()
    {
        using var doc = JsonDocument.Parse("""
            { "done": true, "response": { "videos": [{ "uri": "https://example.com/b.mp4" }] } }
            """);
        var uri = GeminiVideoClient.ExtractVideoUri(doc.RootElement);
        Assert.Equal("https://example.com/b.mp4", uri);
    }

    [Fact]
    public void Veo_extracts_uri_from_single_video_shape()
    {
        using var doc = JsonDocument.Parse("""
            { "done": true, "response": { "video": { "uri": "https://example.com/c.mp4" } } }
            """);
        var uri = GeminiVideoClient.ExtractVideoUri(doc.RootElement);
        Assert.Equal("https://example.com/c.mp4", uri);
    }

    [Fact]
    public void Veo_returns_null_when_not_done_or_shape_unrecognized()
    {
        using var notDone = JsonDocument.Parse("""{ "done": false }""");
        Assert.Null(GeminiVideoClient.ExtractVideoUri(notDone.RootElement));

        using var unknownShape = JsonDocument.Parse("""{ "done": true, "response": { "surprise": 1 } }""");
        Assert.Null(GeminiVideoClient.ExtractVideoUri(unknownShape.RootElement));
    }

    // ── Dispatcher routing decisions (via the same catalog call Resolve() makes) ──

    [Theory]
    [InlineData("claude-sonnet-5", ModelProviderFamily.Anthropic)]
    [InlineData("gemini-3-pro", ModelProviderFamily.Google)]
    [InlineData("grok-4.5", ModelProviderFamily.Xai)]
    [InlineData("grok-4", ModelProviderFamily.Xai)]
    [InlineData("some-unknown-future-model", ModelProviderFamily.Xai)] // forward-compat default
    public void Chat_routing_resolves_expected_provider(string model, ModelProviderFamily expected)
    {
        var provider = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Chat).Provider;
        Assert.Equal(expected, provider);
    }

    [Theory]
    [InlineData("gemini-3-pro-image", ModelProviderFamily.Google)]
    [InlineData("grok-imagine-image-quality", ModelProviderFamily.Xai)]
    public void Image_routing_resolves_expected_provider(string model, ModelProviderFamily expected)
    {
        var provider = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Image).Provider;
        Assert.Equal(expected, provider);
    }

    [Theory]
    [InlineData("veo-3.1", ModelProviderFamily.Google)]
    [InlineData("grok-imagine-video", ModelProviderFamily.Xai)]
    public void Video_routing_resolves_expected_provider(string model, ModelProviderFamily expected)
    {
        var provider = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video).Provider;
        Assert.Equal(expected, provider);
    }

    // ── Video dispatcher request-id tagging (submit → poll must route consistently) ──

    [Fact]
    public void Video_request_id_tag_roundtrips_gemini()
    {
        var (provider, id) = MultiProviderVideoClient.ParseTaggedRequestId("gemini:models/veo-3.1/operations/abc123");
        Assert.Equal(ModelProviderFamily.Google, provider);
        Assert.Equal("models/veo-3.1/operations/abc123", id);
    }

    [Fact]
    public void Video_request_id_tag_roundtrips_grok()
    {
        var (provider, id) = MultiProviderVideoClient.ParseTaggedRequestId("grok:req_abc123");
        Assert.Equal(ModelProviderFamily.Xai, provider);
        Assert.Equal("req_abc123", id);
    }

    [Fact]
    public void Video_untagged_request_id_defaults_to_grok()
    {
        // Pre-dispatcher ids never had a colon prefix — must not be misrouted to Gemini.
        var (provider, id) = MultiProviderVideoClient.ParseTaggedRequestId("req_legacy_no_tag");
        Assert.Equal(ModelProviderFamily.Xai, provider);
        Assert.Equal("req_legacy_no_tag", id);
    }

    // ── Download routing by URL host (no cross-provider fallback) ──

    [Theory]
    [InlineData("https://api.x.ai/v1/videos/abc/content", ModelProviderFamily.Xai)]
    [InlineData("https://cdn.x.ai/media/clip.mp4", ModelProviderFamily.Xai)]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/files/xyz", ModelProviderFamily.Google)]
    [InlineData("https://storage.googleapis.com/bucket/video.mp4", ModelProviderFamily.Google)]
    [InlineData("https://lh3.googleusercontent.com/a/video", ModelProviderFamily.Google)]
    public void Video_download_url_infers_provider(string url, ModelProviderFamily expected)
    {
        Assert.Equal(expected, MultiProviderVideoClient.InferProviderFromDownloadUrl(url));
    }

    [Theory]
    [InlineData("https://cdn.example.com/unsigned.mp4")]
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData(null)]
    public void Video_download_url_unknown_host_returns_null(string? url)
    {
        Assert.Null(MultiProviderVideoClient.InferProviderFromDownloadUrl(url));
    }
}
