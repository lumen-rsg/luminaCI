using Lumina.Shared.DTOs;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.BuildService.Services;

public static class PipelineDefinitionValidator
{
    private static readonly IReadOnlyDictionary<StepType, int> CanonicalOrder =
        new Dictionary<StepType, int>
        {
            [StepType.Build] = 0,
            [StepType.Scan] = 1,
            [StepType.Sign] = 2,
            [StepType.Publish] = 3
        };

    public static void Validate(IReadOnlyCollection<CreatePipelineStepRequest> steps) =>
        ValidateCore(steps.Select(step => new StepDefinition(
            step.Type, step.Name, step.Order, step.Configuration)));

    public static void Validate(IReadOnlyCollection<PipelineStep> steps) =>
        ValidateCore(steps.Select(step => new StepDefinition(
            step.Type, step.Name, step.Order, step.Configuration)));

    private static void ValidateCore(IEnumerable<StepDefinition> definitions)
    {
        var steps = definitions.OrderBy(step => step.Order).ToList();
        if (steps.Count == 0)
        {
            throw new ValidationException("A pipeline must declare at least one Build step.");
        }

        if (steps.Count(step => step.Type == StepType.Build) != 1)
        {
            throw new ValidationException("A pipeline must declare exactly one Build step.");
        }

        if (steps.Any(step => !CanonicalOrder.ContainsKey(step.Type)))
        {
            throw new ValidationException("Pipeline contains an unknown step type.");
        }

        if (steps.Any(step => string.IsNullOrWhiteSpace(step.Name)))
        {
            throw new ValidationException("Every pipeline step requires a name.");
        }

        var duplicateType = steps.GroupBy(step => step.Type).FirstOrDefault(group => group.Count() > 1);
        if (duplicateType is not null)
        {
            throw new ValidationException($"Pipeline step '{duplicateType.Key}' may only be declared once.");
        }

        if (steps.Any(step => step.Order <= 0) ||
            steps.Select(step => step.Order).Distinct().Count() != steps.Count)
        {
            throw new ValidationException("Pipeline step orders must be unique positive integers.");
        }

        var ranks = steps.Select(step => CanonicalOrder[step.Type]).ToList();
        if (!ranks.SequenceEqual(ranks.Order()))
        {
            throw new ValidationException("Pipeline steps must follow Build, Scan, Sign, Publish order.");
        }

        if (steps[0].Type != StepType.Build)
        {
            throw new ValidationException("Build must be the first pipeline step.");
        }

        if (steps.Any(step => step.Type == StepType.Sign) &&
            steps.All(step => step.Type != StepType.Scan))
        {
            throw new ValidationException("A Sign step requires a preceding Scan step.");
        }

        var publish = steps.SingleOrDefault(step => step.Type == StepType.Publish);
        if (publish is not null)
        {
            if (steps.All(step => step.Type != StepType.Sign))
            {
                throw new ValidationException("A Publish step requires a preceding Sign step.");
            }

            if (publish.Configuration is null ||
                !publish.Configuration.TryGetValue("repositoryId", out var repositoryId) ||
                !Guid.TryParse(repositoryId, out _))
            {
                throw new ValidationException(
                    "A Publish step requires a valid repositoryId configuration value.");
            }
        }
    }

    private sealed record StepDefinition(
        StepType Type,
        string Name,
        int Order,
        Dictionary<string, string> Configuration);
}
