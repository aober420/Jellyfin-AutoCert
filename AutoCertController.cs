using System.Text.Json;
using Jellyfin.Plugin.AutoCert.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Jellyfin.Plugin.AutoCert;
public sealed class SettingsRequest
{
    public PluginConfiguration Settings { get; set; } = new();
    public string? Token { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
}
[ApiController]
[Route("AutoCert")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class AutoCertController(CertificateManager manager, ITaskManager tasks) : ControllerBase
{
    private ContentResult Json(object data) => Content(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), "application/json");
    [HttpGet("settings")]
    public IActionResult Settings() => Json(Plugin.Instance.Configuration);
    [HttpGet("status")]
    public IActionResult Status() => Json(manager.Status());
    [HttpPost("settings")]
    public async Task<IActionResult> Save([FromBody] SettingsRequest request, CancellationToken ct)
    {
        try { await manager.Save(request.Settings, request.Token, request.Password, ct, request.ApiKey, request.ApiSecret); return NoContent(); }
        catch (Exception ex) { return BadRequest(new { message = CertificateManager.SafeMessage(ex) }); }
    }
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        try { await manager.Test(ct); return Json(new { message = "DNS read access succeeded. Use staging issuance to test write access and domain verification." }); }
        catch (Exception ex) { return BadRequest(new { message = CertificateManager.SafeMessage(ex) }); }
    }
    [HttpPost("check")]
    public IActionResult Check() { tasks.QueueIfNotRunning<RenewalTask>(); return Accepted(); }
    [HttpPost("issue-now")]
    public IActionResult IssueNow() { tasks.QueueIfNotRunning<ManualIssuanceTask>(); return Accepted(); }
    [HttpPost("rollback")]
    public async Task<IActionResult> Rollback(CancellationToken ct)
    {
        try { await manager.Rollback(ct); return NoContent(); }
        catch (Exception ex) { return BadRequest(new { message = CertificateManager.SafeMessage(ex) }); }
    }
}
