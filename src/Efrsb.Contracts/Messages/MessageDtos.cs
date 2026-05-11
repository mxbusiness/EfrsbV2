namespace Efrsb.Contracts.Messages;

public sealed record EfrsbMessageDto(Guid Id, string FedresursGuid, string Number, DateTime DatePublish, string Type, bool IsRead, bool? HasViolation, bool IsLocked, bool IsAnnulled);
public sealed record EfrsbMessageDetailsDto(Guid Id, string FedresursGuid, string Number, DateTime DatePublish, string Type, string? ContentXml, bool IsRead, IReadOnlyList<MessageFileDto> Files);
public sealed record MessageFileDto(Guid Id, string FileName, long SizeBytes);
