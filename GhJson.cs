using System.Text.Json.Serialization;

namespace ReviewFixLoop;

// DTOs live at namespace scope so the System.Text.Json source generator can see them under AOT.

internal sealed record GhUser(string? Login);

internal sealed record GhIssueComment(
    string? Body,
    GhUser? User,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

internal sealed record GhReview(
    string? Body,
    GhUser? User,
    [property: JsonPropertyName("submitted_at")] DateTimeOffset? SubmittedAt);

internal sealed record GhCommit([property: JsonPropertyName("commit")] GhCommitDetail? Detail);

internal sealed record GhCommitDetail(GhSignature? Author, GhSignature? Committer);

internal sealed record GhSignature(DateTimeOffset? Date);

internal sealed record GhPrView(int Number, string? State, string? HeadRefOid, string? Body, string? Url, bool IsDraft);

internal sealed record GhPrListItem(int Number, string? Title, string? Url, string? HeadRefName);

internal sealed record GhRateLimit(GhRateLimitCore? Rate);

internal sealed record GhRateLimitCore(int Limit, int Remaining, long Reset);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GhPrView))]
[JsonSerializable(typeof(GhRateLimit))]
[JsonSerializable(typeof(List<GhPrListItem>))]
[JsonSerializable(typeof(List<List<GhIssueComment>>))]
[JsonSerializable(typeof(List<List<GhReview>>))]
[JsonSerializable(typeof(List<List<GhCommit>>))]
internal partial class GhJson : JsonSerializerContext;
