using System.Reflection;
using AiOralExam.Modules.AccessConfig.Controllers;
using AiOralExam.Modules.Interview.Controllers;
using AiOralExam.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace AiOralExam.UnitTests;

public class Feature7RouteTests
{
    [Fact]
    public void Lecturer_exam_session_routes_use_the_expected_methods_and_templates()
    {
        AssertRoute(typeof(AuthController), nameof(AuthController.UpdateMe),
            "PATCH", "api/auth/me");
        AssertRoute(typeof(ExamSessionCommandsController), nameof(ExamSessionCommandsController.Create),
            "POST", "api/exam-sessions");
        AssertRoute(typeof(ExamSessionCommandsController), nameof(ExamSessionCommandsController.Update),
            "PUT", "api/exam-sessions/{id:guid}");
        AssertRoute(typeof(QuestionsController), nameof(QuestionsController.Import),
            "POST", "api/exam-sessions/{sessionId:guid}/questions/import");
        AssertRoute(typeof(RubricsController), nameof(RubricsController.Save),
            "PUT", "api/exam-sessions/{sessionId:guid}/questions/{questionId:guid}/rubric");
        AssertRoute(typeof(QuestionsController), nameof(QuestionsController.Delete),
            "DELETE", "api/exam-sessions/{sessionId:guid}/questions/{id:guid}");
        AssertRoute(typeof(ParticipantsController), nameof(ParticipantsController.Import),
            "POST", "api/exam-sessions/{sessionId:guid}/participants/import");
        AssertRoute(typeof(ExamSessionCommandsController), nameof(ExamSessionCommandsController.Publish),
            "POST", "api/exam-sessions/{id:guid}/publish");
    }

    [Fact]
    public void Lecturer_exam_session_controllers_require_lecturer_or_admin()
    {
        AssertRoles(typeof(ExamSessionCommandsController), Roles.LecturerOrAdmin);
        AssertRoles(typeof(QuestionsController), Roles.LecturerOrAdmin);
        AssertRoles(typeof(RubricsController), Roles.LecturerOrAdmin);
        AssertRoles(typeof(ParticipantsController), Roles.LecturerOrAdmin);
    }

    [Fact]
    public void Interview_attempt_review_routes_use_the_expected_methods_and_templates()
    {
        AssertRoute(typeof(InterviewAttemptReviewsController), nameof(InterviewAttemptReviewsController.GetReview),
            "GET", "api/interview-attempts/{id:guid}/review");
        AssertRoute(typeof(InterviewAttemptReviewsController), nameof(InterviewAttemptReviewsController.SubmitScoreReview),
            "PUT", "api/interview-attempts/{id:guid}/score-review");
    }

    [Fact]
    public void Interview_attempt_review_controller_requires_lecturer_role()
    {
        AssertRoles(typeof(InterviewAttemptReviewsController), Roles.Lecturer);
    }

    private static void AssertRoles(Type controllerType, string expectedRoles)
    {
        var authorization = controllerType.GetCustomAttributes<AuthorizeAttribute>(true);

        Assert.Contains(authorization, attribute => attribute.Roles == expectedRoles);
        Assert.Empty(controllerType.GetCustomAttributes<AllowAnonymousAttribute>(true));
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
