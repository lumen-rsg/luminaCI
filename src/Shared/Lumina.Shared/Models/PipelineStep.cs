using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public class PipelineStep
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public int Order { get; set; }
    public StepType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Configuration { get; set; } = new();

    public Pipeline? Pipeline { get; set; }
}
