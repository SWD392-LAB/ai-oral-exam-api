using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiOralExam.Modules.AccessConfig.Controllers;
[ApiController, Route("api/audit-logs"),
    Authorize(Roles = Roles.Administrator),
    Tags("Audit Logs")]

public sealed class AuditLogsController(IAuditLogService service) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AuditLogDto>> List(Guid? userId, string? action, string? targetEntity,
        DateTime? from, DateTime? to, [FromQuery] int take = 100, CancellationToken ct = default)
        => service.ListAsync(userId, action, targetEntity, from, to, take, ct);
}
