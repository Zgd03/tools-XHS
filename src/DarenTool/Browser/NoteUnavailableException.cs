namespace DarenTool.Browser;

/// <summary>The official site explicitly refused this note; do not treat it as an account-wide failure.</summary>
public sealed class NoteUnavailableException(string message) : InvalidOperationException(message);
