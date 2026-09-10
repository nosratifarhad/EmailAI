namespace EmailAI.Domain.Exchange;

/// <summary>
/// The Windows account EmailAI runs under, detected automatically. Safe to display
/// in the Settings UI for information only - it is never editable and never combined
/// with a password (the application never asks for or stores the Windows password).
/// </summary>
public sealed record DetectedWindowsIdentity(
    string? UserName,
    string? Domain,
    string? FullName,
    string DetectionSource,
    bool IsWindowsIntegratedAuthenticationSupported);
