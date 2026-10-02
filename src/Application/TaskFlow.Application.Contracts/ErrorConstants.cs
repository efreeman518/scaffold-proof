namespace TaskFlow.Application.Contracts;

/// <summary>Provides error constants behavior for the Application layer.</summary>
public static class ErrorConstants
{
    public const string ERROR_NAME_EXISTS = "Item name '{0}' already exists";
    public const string ERROR_ITEM_NOTFOUND = "Item not found";
    public const string ERROR_URL_BODY_ID_MISMATCH = "Url Id does not match payload Id";
    public const string ERROR_RULE_NAME_INVALID_MESSAGE = "Name is invalid; required pattern: '{0}'";
    public const string ERROR_RULE_INVALID_MESSAGE = "Item is invalid";

    // Idempotent create and paging contract (GR-17, GR-18); the If-Match texts come from EF.AspNetCore.Concurrency.
    public const string ERROR_CURSOR_INVALID = "Cursor is invalid, expired, or does not match the requested sort mode.";
    public const string ERROR_PAGE_SIZE_RANGE = "Page size must be between {0} and {1}.";
    public const string ERROR_CONTENT_TYPE_FILTER_INVALID = "The content type filter must hold 1 to {0} media types of the form type/subtype, each at most {1} characters.";
    public const string ERROR_TAG_NAME_FILTER_INVALID = "The tag name filter must be 1 to {0} characters.";
    public const string ERROR_IDEMPOTENCY_KEY_INVALID = "The Idempotency-Key header must be one non-empty value of at most {0} characters.";

    // Fixed client text for failed writes; the provider exception (schema, table, key values) is logged only.
    public const string ERROR_SAVE_FAILED = "The change could not be saved.";
    public const string ERROR_BLOB_UPLOAD_FAILED = "The file could not be uploaded.";
}
