using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.Users;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;
[ApiController,
    Route("api/users"),
    Authorize(Roles = Roles.Administrator),
    Tags("Users")]
public sealed class UsersController(
    IUserService service,
    ICurrentUser me) : ControllerBase
{
    [HttpGet]
    [HttpGet("~/api/admin/users")]
    public Task<IReadOnlyList<UserDto>> List([FromQuery] UserRole? role, [FromQuery] bool? active, [FromQuery] string? search, CancellationToken ct)
        => service.ListAsync(role, active, search, ct);

    [HttpGet("{id:guid}")]
    public Task<UserDto> Get(Guid id, CancellationToken ct)
        => service.GetAsync(id, ct);

    [HttpPost]
    [HttpPost("~/api/admin/users")]
    public async Task<ActionResult<CreatedUserDto>> Create(CreateUserRequest r, CancellationToken ct)
    {
        var x = await service.CreateAsync(r, ct);
        return CreatedAtAction(nameof(Get), new
        {
            id = x.Id
        }, x);
    }

    [HttpPost("~/api/admin/users/{id:guid}/setup-link")]
    public async Task<IActionResult> SendSetupLink(Guid id, CancellationToken ct)
    {
        await service.ResendSetupEmailAsync(id, me.Id, ct);
        return Ok(new { setupEmailSent = true });
    }

    [HttpPut("{id:guid}")]
    public Task<UserDto> Update(Guid id, UpdateUserRequest r, CancellationToken ct)
        => service.UpdateAsync(id, r, ct);

    [HttpPatch("~/api/admin/users/{id:guid}/role")]
    public async Task<UserDto> ChangeRole(Guid id, PatchUserRoleRequest r, CancellationToken ct)
    {
        var current = await service.GetAsync(id, ct);
        var studentCode = r.StudentCode ??
            (r.Role == UserRole.Student && current.Role == UserRole.Student.ToString() ? current.StudentCode : null);
        var lecturerCode = r.LecturerCode ??
            (r.Role == UserRole.Lecturer && current.Role == UserRole.Lecturer.ToString() ? current.LecturerCode : null);
        return await service.UpdateAsync(id,
            new UpdateUserRequest(current.FullName!, studentCode, lecturerCode, current.IsActive, r.Role), ct);
    }

    [HttpPost("~/api/admin/users/{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var current = await service.GetAsync(id, ct);
        await service.UpdateAsync(id,
            new UpdateUserRequest(current.FullName!, current.StudentCode, current.LecturerCode, false), ct);
        return NoContent();
    }

}

public sealed record PatchUserRoleRequest(UserRole Role, string? StudentCode = null, string? LecturerCode = null);
