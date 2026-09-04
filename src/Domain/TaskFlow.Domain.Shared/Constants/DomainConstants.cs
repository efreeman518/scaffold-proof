namespace TaskFlow.Domain.Shared.Constants;

/// <summary>Models domain constants domain behavior and invariants.</summary>
public static class DomainConstants
{
    // Validation rules
    public const int RULE_DEFAULT_NAME_LENGTH_MIN = 3;
    public const int RULE_DEFAULT_NAME_LENGTH_MAX = 200;
    public const int RULE_DEFAULT_DESCRIPTION_LENGTH_MAX = 2000;
    public const int RULE_TAG_NAME_LENGTH_MAX = 50;
    public const int RULE_TAG_COLOR_LENGTH_MAX = 7;
    public const int RULE_CATEGORY_NAME_LENGTH_MAX = 100;
    public const int RULE_CATEGORY_DESCRIPTION_LENGTH_MAX = 500;
    public const int RULE_COMMENT_BODY_LENGTH_MAX = 2000;
    public const int RULE_ATTACHMENT_FILENAME_LENGTH_MAX = 255;
    public const int RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX = 100;
    public const int RULE_ATTACHMENT_STORAGEURI_LENGTH_MAX = 2000;

    // Max UTF8 plaintext bytes for the encrypted secure properties (D-023): ciphertext = 12 nonce + plaintext + 16 tag <= 256.
    public const int RULE_SECURE_PROPERTY_MAX_BYTES = 200;
}
