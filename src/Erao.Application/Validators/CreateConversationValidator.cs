using FluentValidation;
using Erao.Core.DTOs.Chat;

namespace Erao.Application.Validators;

public class CreateConversationValidator : AbstractValidator<CreateConversationRequest>
{
    public CreateConversationValidator()
    {
        RuleFor(x => x.Title)
            .MaximumLength(500).WithMessage("Title must not exceed 500 characters")
            .When(x => !string.IsNullOrEmpty(x.Title));

        // Require either a database connection or a file document
        RuleFor(x => x)
            .Must(x => x.DatabaseConnectionId.HasValue || x.FileDocumentId.HasValue)
            .WithMessage("Either a database connection or a file document is required");
    }
}
