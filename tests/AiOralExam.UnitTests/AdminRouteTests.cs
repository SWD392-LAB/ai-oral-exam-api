using System.Reflection;
using AiOralExam.Modules.AccessConfig.Application.AIServices;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Controllers;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace AiOralExam.UnitTests;

public class AdminRouteTests
{
    [Fact]
    public void Required_admin_routes_are_registered_with_administrator_authorization()
    {
        AssertRoute(typeof(UsersController), nameof(UsersController.List), "GET", "api/admin/users");
        AssertRoute(typeof(UsersController), nameof(UsersController.Create), "POST", "api/admin/users");
        AssertRoute(typeof(UsersController), nameof(UsersController.SendSetupLink), "POST", "api/admin/users/{id:guid}/setup-link");
        AssertRoute(typeof(UsersController), nameof(UsersController.ChangeRole), "PATCH", "api/admin/users/{id:guid}/role");
        AssertRoute(typeof(UsersController), nameof(UsersController.Deactivate), "POST", "api/admin/users/{id:guid}/deactivate");
        AssertRoute(typeof(AdminCoursesController), nameof(AdminCoursesController.List), "GET", "api/admin/courses");
        AssertRoute(typeof(AdminCoursesController), nameof(AdminCoursesController.Create), "POST", "api/admin/courses");
        AssertRoute(typeof(AdminCoursesController), nameof(AdminCoursesController.Update), "PUT", "api/admin/courses/{id:guid}");
        AssertRoute(typeof(AiConfigsController), nameof(AiConfigsController.List), "GET", "api/admin/ai-settings");
        AssertRoute(typeof(AiConfigsController), nameof(AiConfigsController.SaveActive), "PUT", "api/admin/ai-settings");
        AssertRoute(typeof(AuditLogsController), nameof(AuditLogsController.List), "GET", "api/admin/audit-logs");

        AssertAdministrator(typeof(UsersController));
        AssertAdministrator(typeof(AdminCoursesController));
        AssertAdministrator(typeof(AiConfigsController));
        AssertAdministrator(typeof(AuditLogsController));
    }

    [Fact]
    public void Admin_course_list_is_not_anonymous()
    {
        var list = typeof(AdminCoursesController).GetMethod(nameof(AdminCoursesController.List))!;

        Assert.Empty(list.GetCustomAttributes<AllowAnonymousAttribute>(true));
        AssertAdministrator(typeof(AdminCoursesController));
    }

    [Fact]
    public void Ai_configuration_response_does_not_expose_secrets()
    {
        var propertyNames = typeof(AiServiceConfigDto).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain("ApiKey", propertyNames);
        Assert.DoesNotContain("ApiKeyEncrypted", propertyNames);
        Assert.Contains("HasApiKey", propertyNames);
    }

    [Fact]
    public void Public_course_dto_does_not_expose_lecturers()
    {
        Assert.Null(typeof(CourseDto).GetProperty("Lecturers"));
    }

    [Fact]
    public void Active_configuration_selection_rejects_ambiguous_rows()
    {
        Assert.Null(AiConfigService.ExistingActiveConfigurationId(Array.Empty<Guid>()));
        var id = Guid.NewGuid();
        Assert.Equal(id, AiConfigService.ExistingActiveConfigurationId([id]));
        Assert.Throws<ConflictException>(() =>
            AiConfigService.ExistingActiveConfigurationId([Guid.NewGuid(), Guid.NewGuid()]));
    }

    [Fact]
    public void Ai_configuration_service_validates_schema_enum_values()
    {
        var request = new SaveAiServiceConfigRequest(
            (AiServiceType)999, "Provider", "https://example.test", "key", SpeechLanguage.English);

        Assert.Throws<ValidationException>(() => AiConfigService.ValidateRequest(request));
    }

    private static void AssertAdministrator(Type controllerType)
    {
        var authorization = controllerType.GetCustomAttributes<AuthorizeAttribute>(true);
        Assert.Contains(authorization, attribute => attribute.Roles == Roles.Administrator);
    }

    private static void AssertRoute(Type controllerType, string actionName, string verb, string expectedRoute)
    {
        var controllerRoute = controllerType.GetCustomAttributes<RouteAttribute>(true)
            .FirstOrDefault()?.Template;
        var action = controllerType.GetMethod(actionName)!;
        var matches = action.GetCustomAttributes<HttpMethodAttribute>(true).Any(attribute =>
        {
            if (attribute.HttpMethods is null || !attribute.HttpMethods.Contains(verb, StringComparer.OrdinalIgnoreCase))
                return false;

            var template = attribute.Template;
            var route = template is null
                ? controllerRoute
                : template.StartsWith("~/", StringComparison.Ordinal)
                    ? template[2..]
                    : $"{controllerRoute}/{template.TrimStart('/')}";
            return string.Equals(route, expectedRoute, StringComparison.OrdinalIgnoreCase);
        });

        Assert.True(matches, $"{verb} /{expectedRoute} was not found on {controllerType.Name}.{actionName}.");
    }
}
