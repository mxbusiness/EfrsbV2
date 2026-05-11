using Microsoft.AspNetCore.Identity;

namespace Efrsb.Domain.Entities;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
