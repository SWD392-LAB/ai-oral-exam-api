using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Users.UpdateOwnProfile;

public sealed record UpdateOwnProfileRequest(
    [Required, StringLength(150)] string FullName);
