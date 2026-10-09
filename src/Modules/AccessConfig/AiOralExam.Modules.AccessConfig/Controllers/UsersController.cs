using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
public sealed class UsersController(IUserService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<UserDto>> List([FromQuery] UserRole? role, [FromQuery] bool? active, [FromQuery] string? search, CancellationToken ct)
        => service.ListAsync(role, active, search, ct);

    [HttpGet("{id:guid}")]
    public Task<UserDto> Get(Guid id, CancellationToken ct)
        => service.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest r, CancellationToken ct)
    {
        var x = await service.CreateAsync(r, ct);
        return CreatedAtAction(nameof(Get), new
        {
            id = x.Id
        }, x);
    }

    [HttpPut("{id:guid}")]
    public Task<UserDto> Update(Guid id, UpdateUserRequest r, CancellationToken ct)
        => service.UpdateAsync(id, r, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return NoContent();
    }
}
