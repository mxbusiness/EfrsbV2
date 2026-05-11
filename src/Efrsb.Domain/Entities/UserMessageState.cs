namespace Efrsb.Domain.Entities;

public sealed class UserMessageState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public Guid EfrsbMessageId { get; set; }
    public EfrsbMessage? Message { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}
