namespace AprCpu.Core.JsonSpec;

/// <summary>
/// Raised when a spec file fails structural / semantic validation.
/// </summary>
public sealed class SpecValidationException : Exception
{
    public string? FilePath { get; }
    public string? JsonPath { get; }

    public SpecValidationException(string message, string? filePath = null, string? jsonPath = null)
        : base(BuildMessage(message, filePath, jsonPath))
    {
        FilePath = filePath;
        JsonPath = jsonPath;
    }

    public SpecValidationException(string message, Exception inner, string? filePath = null, string? jsonPath = null)
        : base(BuildMessage(message, filePath, jsonPath), inner)
    {
        FilePath = filePath;
        JsonPath = jsonPath;
    }

    private static string BuildMessage(string message, string? filePath, string? jsonPath)
    {
        if (filePath is null && jsonPath is null) return message;
        var loc = (filePath, jsonPath) switch
        {
            (not null, not null) => $" [{filePath}:{jsonPath}]",
            (not null, null)     => $" [{filePath}]",
            (null,     not null) => $" [{jsonPath}]",
            _                    => ""
        };
        return message + loc;
    }
}
