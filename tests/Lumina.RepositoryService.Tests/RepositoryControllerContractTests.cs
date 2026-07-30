using Lumina.RepositoryService.Controllers;
using Lumina.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Lumina.RepositoryService.Tests;

public class RepositoryControllerContractTests
{
    [Fact]
    public void ListRepositories_returns_the_shared_list_response_contract()
    {
        var method = typeof(RepositoryController).GetMethod(
            nameof(RepositoryController.ListRepositories));

        Assert.NotNull(method);
        Assert.Equal(
            typeof(Task<ActionResult<ApiResponse<RepositoryListResponse>>>),
            method.ReturnType);
    }
}
