using k8s;
using k8s.Models;
using Lumina.BuildService.Services;
using Lumina.Shared.Models.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lumina.BuildService.Health;

internal interface IKubernetesReadinessProbe
{
    Task<IReadOnlyList<string>> FindDeniedPermissionsAsync(
        string buildNamespace,
        CancellationToken cancellationToken);
}

internal sealed class KubernetesReadinessProbe(k8s.Kubernetes client)
    : IKubernetesReadinessProbe
{
    private static readonly KubernetesPermission[] RequiredPermissions =
    [
        new("batch", "jobs", "create"),
        new("batch", "jobs", "get"),
        new("batch", "jobs", "list"),
        new("batch", "jobs", "update"),
        new("batch", "jobs", "delete"),
        new(string.Empty, "secrets", "create"),
        new(string.Empty, "secrets", "get"),
        new(string.Empty, "secrets", "delete"),
        new("networking.k8s.io", "networkpolicies", "create"),
        new("networking.k8s.io", "networkpolicies", "get"),
        new("networking.k8s.io", "networkpolicies", "update"),
        new("networking.k8s.io", "networkpolicies", "delete"),
        new(string.Empty, "pods", "list"),
        new(string.Empty, "pods/log", "get")
    ];

    public async Task<IReadOnlyList<string>> FindDeniedPermissionsAsync(
        string buildNamespace,
        CancellationToken cancellationToken)
    {
        var denied = new List<string>();
        foreach (var permission in RequiredPermissions)
        {
            var review = await client.CreateSelfSubjectAccessReviewAsync(
                new V1SelfSubjectAccessReview
                {
                    ApiVersion = "authorization.k8s.io/v1",
                    Kind = "SelfSubjectAccessReview",
                    Spec = new V1SelfSubjectAccessReviewSpec
                    {
                        ResourceAttributes = new V1ResourceAttributes
                        {
                            Group = permission.Group,
                            NamespaceProperty = buildNamespace,
                            Resource = permission.Resource,
                            Verb = permission.Verb
                        }
                    }
                },
                cancellationToken: cancellationToken);
            if (review.Status?.Allowed != true)
            {
                denied.Add(
                    $"{permission.Verb} {(string.IsNullOrEmpty(permission.Group) ? "core" : permission.Group)}/{permission.Resource}");
            }
        }

        // A successful namespaced request proves the namespace exists in
        // addition to the authorization server's policy evaluation.
        await client.ListNamespacedPodAsync(
            buildNamespace,
            labelSelector: "app.kubernetes.io/managed-by=lumina-ci",
            limit: 1,
            cancellationToken: cancellationToken);
        return denied;
    }

    private sealed record KubernetesPermission(string Group, string Resource, string Verb);
}

internal sealed class KubernetesReadinessHealthCheck(
    BuildExecutorSelection executorSelection,
    IKubernetesReadinessProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (executorSelection.Backend != BuildExecutorBackend.Kubernetes)
            return HealthCheckResult.Healthy("Kubernetes executor is not selected.");

        try
        {
            var denied = await probe.FindDeniedPermissionsAsync(
                executorSelection.KubernetesNamespace!,
                cancellationToken);
            return denied.Count == 0
                ? HealthCheckResult.Healthy("Kubernetes namespace and least-privilege executor permissions are ready.")
                : HealthCheckResult.Unhealthy(
                    "Kubernetes executor permissions are incomplete.",
                    data: new Dictionary<string, object> { ["denied"] = denied.ToArray() });
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "Kubernetes API or build namespace is unavailable.",
                exception);
        }
    }
}
