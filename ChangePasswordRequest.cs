// Body of POST /api/me/password. Both fields are read from the body and nothing else (the account is the session's).
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
