namespace DDSharp;

/// <summary>Raw result returned by <see cref="DDKeyboardInterface.SendCommandSync"/>.</summary>
public record CallResult(bool Success, string Error, byte[] Data);
