using System.Text.Json.Serialization;

namespace Oxira.Design.Application.Common;

/// <summary>
/// Every reply carries success and, when it failed, a reason code the website switches on. The API turns a failed reply
/// into the matching HTTP status (see ReasonStatus in the Api project) but keeps the body, so extra fields such as
/// remaining renders still reach the page.
/// </summary>
public interface IReply
{
    bool Success { get; }
    string? Reason { get; }
}

public record Reply(bool Success = true, string? Reason = null) : IReply
{
    public static readonly Reply Ok = new();
}

public record IdReply(bool Success, object? Id, string? Reason = null) : IReply;

public record CommentDto(string Role, string Text, DateTimeOffset At, bool Mine);

public record FileContent(string Name, string Mime, Stream Content);

public static class Reasons
{
    /// <summary>HTTP status for a reason code</summary>
    public static int Status(string? reason) => reason switch
    {
        "session" => 401,
        "credits" => 402,
        "denied" or "forbidden" => 403,
        "not_found" => 404,
        "exists" or "not_available" or "taken" => 409,
        "too_big" => 413,
        "unreadable" or "parse" => 422,
        // the AI or payment provider failed: 424 rather than 5xx so proxies keep the JSON body
        "failed" or "ai" or "payments" => 424,
        "limit" => 429,
        _ => 400,
    };
}

/// <summary>Moyasar invoice metadata kinds</summary>
public static class PaymentKinds
{
    public const string Design = "design", Redesign = "redesign", Listing = "listing";
}

public record PayReply(bool Success, int? OrderId, bool Paid, string? Reason = null) : IReply;

/// <summary>shape of the order form reply</summary>
public record OrderReply(bool Success, int OrderId, string? PayUrl = null, string? Reason = null) : IReply;

/// <summary>studio render reply</summary>
public record RenderReply(bool Success, string? Image, int Remaining, int? Credits, string? Reason = null) : IReply;

/// <summary>room redesign reply</summary>
public record RoomReply(bool Success, string? Image,
    [property: JsonPropertyName("free_left")] int FreeLeft, int? Credits, string? Reason = null) : IReply;

public record WalletReply(bool Success, int Credits, [property: JsonPropertyName("free_left")] int FreeLeft, string? Reason = null) : IReply;
