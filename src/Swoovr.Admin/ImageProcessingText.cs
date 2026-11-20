namespace Swoovr.Admin;

/// <summary>Plain-language staff text for the image processing status on the Overview. Reason codes come from the API.</summary>
public static class ImageProcessingText
{
    public static string State(string state) => state switch
    {
        "healthy" => "Working normally",
        "degraded" => "Photos are waiting longer than usual",
        "unavailable" => "Not processing photos",
        _ => "Status unknown",
    };

    public static string Summary(string state) => state switch
    {
        "healthy" => "New photos are checked within a minute or two.",
        "degraded" => "The checks are running, but some photos have waited over an hour. They stay private until they're checked.",
        "unavailable" => "A checking service isn't answering. New uploads are refused or wait, and every waiting photo stays private until it's checked.",
        _ => "",
    };

    public static string Service(bool? ready) => ready switch { true => "Answering", false => "Not answering", null => "Unknown" };

    /// <summary>The last attempt's machine code, in words. Unknown codes are shown as they are.</summary>
    public static string Reason(string code) => code switch
    {
        "waiting" => "Waiting for their first check",
        "unreachable" => "Screening service can't be reached",
        "not-ready" => "Screening service is starting or not ready",
        "not-configured" => "Screening service isn't configured",
        "not-understood" => "Screening service gave an answer we couldn't read",
        "http-401" or "http-403" => "Screening service refused the API's key",
        "http-404" => "Screening service address is wrong (not found)",
        "http-413" => "Photo was too large for the screening service",
        "http-429" => "Screening service rate limit",
        "http-503" => "Screening service busy",
        "known-content-unchecked" => "Known-content check (Project Arachnid) didn't complete",
        "unknown-classification" => "Known-content check gave an unknown answer",
        "nudity-unchecked" => "Nudity check didn't complete",
        "nsfw-unchecked" => "Explicit-content check didn't complete",
        "unknown-label" => "Nudity check gave an unknown label",
        "object-unavailable" => "Stored photo couldn't be read",
        "object-changed" => "Stored photo didn't match its upload",
        _ when code.StartsWith("http-", StringComparison.Ordinal) => "Screening service error (" + code[5..] + ")",
        _ => code,
    };
}
