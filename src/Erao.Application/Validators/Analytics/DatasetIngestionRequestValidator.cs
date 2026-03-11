using FluentValidation;
using Erao.Core.DTOs.Analytics;

namespace Erao.Application.Validators.Analytics;

public class DatasetIngestionRequestValidator : AbstractValidator<DatasetIngestionRequest>
{
    public DatasetIngestionRequestValidator()
    {
        RuleFor(x => x.DatasetName)
            .NotEmpty().WithMessage("Dataset name is required")
            .MaximumLength(200).WithMessage("Dataset name must not exceed 200 characters")
            .Matches(@"^[a-zA-Z0-9_\-\.]+$").WithMessage("Dataset name can only contain letters, numbers, underscores, hyphens, and dots");

        RuleFor(x => x.Source)
            .NotEmpty().WithMessage("Source is required")
            .MaximumLength(200).WithMessage("Source must not exceed 200 characters");

        RuleFor(x => x.Records)
            .NotEmpty().WithMessage("At least one record is required")
            .Must(r => r.Count <= 10000).WithMessage("Maximum 10,000 records per ingestion request");

        RuleForEach(x => x.Records).ChildRules(record =>
        {
            record.RuleFor(r => r.OccurredAt)
                .NotEmpty().WithMessage("OccurredAt is required for each record");

            record.RuleFor(r => r.Measures)
                .NotEmpty().WithMessage("At least one measure is required per record");
        });
    }
}

public class AnalyticsQueryRequestValidator : AbstractValidator<AnalyticsQueryRequest>
{
    public AnalyticsQueryRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0).WithMessage("Page must be greater than 0");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 500).WithMessage("PageSize must be between 1 and 500");
        RuleFor(x => x.SortDirection)
            .Must(d => d is "asc" or "desc").WithMessage("SortDirection must be 'asc' or 'desc'");
    }
}

public class MetricsRequestValidator : AbstractValidator<MetricsRequest>
{
    public MetricsRequestValidator()
    {
        RuleFor(x => x.DatasetName)
            .NotEmpty().WithMessage("Dataset name is required");

        RuleForEach(x => x.Metrics).ChildRules(m =>
        {
            m.RuleFor(x => x.Name).NotEmpty().WithMessage("Metric name is required");
            m.RuleFor(x => x.MetricType).IsInEnum().WithMessage("Invalid metric type");
        });
    }
}

public class AggregationRequestValidator : AbstractValidator<AggregationRequest>
{
    public AggregationRequestValidator()
    {
        RuleFor(x => x.DatasetName)
            .NotEmpty().WithMessage("Dataset name is required");

        RuleFor(x => x.Metrics)
            .NotEmpty().WithMessage("At least one aggregation metric is required");

        RuleFor(x => x)
            .Must(x => x.Period.HasValue || (x.GroupByDimensions != null && x.GroupByDimensions.Count > 0))
            .WithMessage("Either a time period or at least one group-by dimension is required");

        RuleForEach(x => x.Metrics).ChildRules(m =>
        {
            m.RuleFor(x => x.Name).NotEmpty().WithMessage("Metric name is required");
            m.RuleFor(x => x.MetricType).IsInEnum().WithMessage("Invalid metric type");
        });
    }
}

public class CreateMetricDefinitionRequestValidator : AbstractValidator<CreateMetricDefinitionRequest>
{
    public CreateMetricDefinitionRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required").MaximumLength(200);
        RuleFor(x => x.DatasetName).NotEmpty().WithMessage("Dataset name is required").MaximumLength(200);
        RuleFor(x => x.MetricType).IsInEnum().WithMessage("Invalid metric type");
        RuleFor(x => x.MeasureField)
            .NotEmpty().When(x => x.MetricType != Core.Enums.MetricType.Count)
            .WithMessage("MeasureField is required for non-Count metrics");
    }
}
