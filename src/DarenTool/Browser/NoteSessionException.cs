namespace DarenTool.Browser;

/// <summary>A session/verification/rate-limit response must not trigger additional fallback requests.</summary>
public sealed class NoteSessionException(string message) : InvalidOperationException(message);
