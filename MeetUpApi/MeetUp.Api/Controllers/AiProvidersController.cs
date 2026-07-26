using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetUp.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/ai")]
public class AiProvidersController : ControllerBase
{
    private readonly AnalysisProviderRegistry _registry;

    public AiProvidersController(AnalysisProviderRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Providers that actually have an API key configured — the rest are never offered.</summary>
    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<AnalysisProviderDto>> GetProviders()
    {
        return Ok(_registry.GetAvailable().Select(p => new AnalysisProviderDto
        {
            Key = p.Key,
            DisplayName = p.DisplayName,
            IsFree = p.IsFree,
            IsDefault = p.IsDefault,
            ContextWindow = p.ContextWindow,
        }).ToList());
    }
}
