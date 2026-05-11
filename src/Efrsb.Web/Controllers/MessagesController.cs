using System.Security.Claims;
using Efrsb.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Efrsb.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/messages")]
public sealed class MessagesController : ControllerBase
{
    private readonly ICompanyTrackingService _service;
    public MessagesController(ICompanyTrackingService service) => _service = service;

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var result = await _service.GetMessageDetailsAsync(UserId, id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        await _service.MarkMessageReadAsync(UserId, id, ct);
        return NoContent();
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
