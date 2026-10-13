using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;

namespace Oxira.Design.Infrastructure.Services;

/// <summary>"Anthropic" and "OpenAi" sections; keys come from environment variables (Anthropic__ApiKey, OpenAi__ApiKey).</summary>
public class AnthropicOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string Model { get; set; } = "claude-sonnet-5";
}

public class OpenAiOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com";
}

public class AnthropicClient(HttpClient http, IOptions<AnthropicOptions> opt) : IAnthropic
{
    public async Task<AiResponse> CreateMessageAsync(AiRequest r, CancellationToken ct = default)
    {
        var o = opt.Value;
        if (string.IsNullOrEmpty(o.ApiKey)) throw new InvalidOperationException("Anthropic:ApiKey is not set");
        var body = new JsonObject
        {
            ["model"] = r.Model ?? o.Model,
            ["max_tokens"] = r.MaxTokens,
            ["system"] = r.System,
            ["messages"] = r.Messages.DeepClone(),
        };
        if (r.Tools is { Count: > 0 }) body["tools"] = r.Tools.DeepClone();
        foreach (var (k, v) in r.Extra ?? []) body[k] = v?.DeepClone();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/v1/messages") { Content = JsonContent.Create(body) };
        req.Headers.Add("x-api-key", o.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Anthropic {(int)res.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
        var json = JsonNode.Parse(text)!.AsObject();
        return new AiResponse(json["content"]?.AsArray() ?? [], (string?)json["stop_reason"] ?? "end_turn");
    }
}

/// <summary>OpenAI image edits (multipart). With two images the second one is the style reference.</summary>
public class OpenAiImageEditor(HttpClient http, IOptions<OpenAiOptions> opt) : IImageEditor
{
    private static string Ext(string mime) => mime.EndsWith("png") ? ".png" : mime.EndsWith("webp") ? ".webp" : ".jpg";

    public async Task<byte[]> EditAsync(ImageEdit r, CancellationToken ct = default)
    {
        var o = opt.Value;
        if (string.IsNullOrEmpty(o.ApiKey)) throw new InvalidOperationException("OpenAi:ApiKey is not set");
        using var form = new MultipartFormDataContent();
        var field = r.Images.Count > 1 ? "image[]" : "image";
        for (var i = 0; i < r.Images.Count; i++)
        {
            var file = new ByteArrayContent(r.Images[i].Data);
            file.Headers.ContentType = new MediaTypeHeaderValue(r.Images[i].Mime);
            form.Add(file, field, (i == 0 ? "view" : "ref") + Ext(r.Images[i].Mime));
        }
        form.Add(new StringContent(r.Model), "model");
        form.Add(new StringContent(r.Prompt), "prompt");
        form.Add(new StringContent(r.Size), "size");
        form.Add(new StringContent(r.Quality), "quality");
        form.Add(new StringContent("jpeg"), "output_format");
        form.Add(new StringContent(r.Compression.ToString(System.Globalization.CultureInfo.InvariantCulture)), "output_compression");
        form.Add(new StringContent("1"), "n");
        if (!string.IsNullOrEmpty(r.InputFidelity)) form.Add(new StringContent(r.InputFidelity), "input_fidelity");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/v1/images/edits") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"OpenAI {(int)res.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
        using var doc = JsonDocument.Parse(text);
        var b64 = doc.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
        return Convert.FromBase64String(b64 ?? throw new HttpRequestException("OpenAI returned no image"));
    }
}
