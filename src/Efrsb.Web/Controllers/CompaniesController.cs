using System.Security.Claims;
using Efrsb.Application.Abstractions;
using Efrsb.Contracts.Companies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Efrsb.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/companies")]
public sealed class CompaniesController : ControllerBase
{
    private readonly ICompanyTrackingService _service;
    public CompaniesController(ICompanyTrackingService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _service.GetCompaniesAsync(UserId, ct));

    [HttpPost]
    public async Task<IActionResult> Create(CreateTrackedCompanyRequest request, CancellationToken ct) => Ok(await _service.AddCompanyAsync(UserId, request.Query, ct));

    [HttpPost("{id:guid}/sync")]
    public async Task<IActionResult> Sync(Guid id, CancellationToken ct) => Ok(new { loaded = await _service.SyncCompanyAsync(UserId, id, ct) });

    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> Messages(Guid id, CancellationToken ct) => Ok(await _service.GetMessagesAsync(UserId, id, ct));

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
