namespace TaskFlow.Application.Contracts;

/// <summary>Provides error constants behavior for the Application layer.</summary>
public static class ErrorConstants
{
    public const string ERROR_NAME_EXISTS = "Item name '{0}' already exists";
    public const string ERROR_ITEM_NOTFOUND = "Item not found";
    public const string ERROR_URL_BODY_ID_MISMATCH = "Url Id does not match payload Id";
    public const string ERROR_RULE_NAME_INVALID_MESSAGE = "Name is invalid; required pattern: '{0}'";
    public const string ERROR_RULE_INVALID_MESSAGE = "Item is invalid";

    // Concurrency, idempotent create, and paging contract (GR-16, GR-17, GR-18).
    public const string ERROR_IF_MATCH_REQUIRED = "If-Match header is required for this write.";
    public const string ERROR_IF_MATCH_MALFORMED = "If-Match must be a strong ETag such as \"12\" or the wildcard *.";
    public const string ERROR_ID_NOT_UUID_V7 = "Id '{0}' is not a UUIDv7; caller-supplied create ids must be UUIDv7.";
    public const string ERROR_CURSOR_INVALID = "Cursor is invalid, expired, or does not match the requested sort mode.";
    public const string ERROR_PAGE_SIZE_RANGE = "Page size must be between {0} and {1}.";
}
