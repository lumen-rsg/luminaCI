namespace Lumina.Shared.Models;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Developer";
    public bool IsActive { get; set; } = true;
    public int FailedLoginAttempts { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    // Number of completed lockout cycles for this account. Drives the
    // exponential backoff applied on each successive lockout
    // (LockoutMinutes * 2^LockoutCount, capped) so a sustained brute-force
    // attempt progressively costs the attacker exponentially more wall-clock
    // time per guess (SEC-012 follow-up).
    public int LockoutCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
