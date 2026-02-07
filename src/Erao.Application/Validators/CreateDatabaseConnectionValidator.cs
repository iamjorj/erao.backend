using FluentValidation;
using Erao.Core.DTOs.Database;

namespace Erao.Application.Validators;

public class CreateDatabaseConnectionValidator : AbstractValidator<CreateDatabaseConnectionRequest>
{
    public CreateDatabaseConnectionValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Connection name is required")
            .MaximumLength(255).WithMessage("Connection name must not exceed 255 characters");

        RuleFor(x => x.DatabaseType)
            .IsInEnum().WithMessage("Invalid database type");

        RuleFor(x => x.Host)
            .NotEmpty().WithMessage("Host is required")
            .MaximumLength(255).WithMessage("Host must not exceed 255 characters");

        RuleFor(x => x.Port)
            .GreaterThan(0).WithMessage("Port must be greater than 0")
            .LessThanOrEqualTo(65535).WithMessage("Port must not exceed 65535");

        RuleFor(x => x.DatabaseName)
            .NotEmpty().WithMessage("Database name is required")
            .MaximumLength(255).WithMessage("Database name must not exceed 255 characters");

        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required")
            .MaximumLength(255).WithMessage("Username must not exceed 255 characters");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required");
    }
}
