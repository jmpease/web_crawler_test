using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace Crawler.IntegrationTests;

public class ApiValidationTests(OfflineApiFactory factory) : IClassFixture<OfflineApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("ftp://example.com/")]
    [InlineData("mailto:someone@example.com")]
    public async Task Rejects_a_start_url_that_is_not_absolute_http(string? url)
    {
        var problem = await PostJobExpectingBadRequest(new { url, maxDepth = 1 });

        Assert.True(problem.Errors.ContainsKey("url"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public async Task Rejects_max_depth_outside_0_to_5(int maxDepth)
    {
        var problem = await PostJobExpectingBadRequest(new { url = "https://example.com/", maxDepth });

        Assert.Equal(["maxDepth"], problem.Errors.Keys);
    }

    [Fact]
    public async Task Rejects_an_overlong_url()
    {
        var problem = await PostJobExpectingBadRequest(new { url = "https://example.com/" + new string('a', 2100) });

        Assert.True(problem.Errors.ContainsKey("url"));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task Rejects_invalid_history_paging(string query)
    {
        var response = await _client.GetAsync($"/api/jobs?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Returns_problem_details_404_for_a_job_id_that_is_not_a_guid()
    {
        var response = await _client.GetAsync("/api/jobs/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<ValidationProblemDetails> PostJobExpectingBadRequest(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
