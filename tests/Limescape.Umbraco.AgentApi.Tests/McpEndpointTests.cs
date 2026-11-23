using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Limescape.Umbraco.AgentApi.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Xunit;

namespace Limescape.Umbraco.AgentApi.Tests;

/// <summary>
/// Transport and authentication of the MCP endpoint, through the same Umbraco pipeline filter the package registers,
/// without a CMS boot. Tool behaviour against a real Umbraco is tested in the Limescape content layer host.
/// </summary>
public sealed class McpEndpointTests
{
    private static readonly string[] ExpectedTools = ["add_element_from_session", "add_elements", "add_relation", "add_to_group", "approve_material", "archive_material", "clear_legal_hold", "create_material", "get_detail_link", "get_material", "list_collections", "list_groups", "list_retention", "list_review_queue", "propose_metadata_context", "reject_material", "remove_element", "remove_from_group", "remove_relation", "set_legal_hold", "set_metadata_field_state", "set_owners", "set_retention_class", "submit_material", "update_material"];
    private static readonly string[] WriteTools = ["add_element_from_session", "add_elements", "add_relation", "add_to_group", "approve_material", "archive_material", "clear_legal_hold", "create_material", "reject_material", "remove_element", "remove_from_group", "remove_relation", "set_legal_hold", "set_metadata_field_state", "set_owners", "set_retention_class", "submit_material", "update_material"];
    private static readonly string[] DestructiveTools = ["archive_material", "clear_legal_hold", "remove_element", "remove_from_group", "remove_relation", "set_owners", "set_retention_class", "update_material"];

    [Theory]
    [InlineData(null, "/mcp")]
    [InlineData("/agents/materials", "/agents/materials")]
    public async Task Tools_are_listed_for_a_valid_user_token(string? configuredPath, string requestPath)
    {
        await using var app = await StartAsync(true, configuredPath);
        using var httpClient = app.GetTestClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Shared.Create(roles: ["Materials.Developer"]));
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, requestPath),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, httpClient);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedTools, tools.Select(t => t.Name).Order());
        foreach (var tool in tools)
        {
            var isWrite = WriteTools.Contains(tool.Name);
            Assert.Equal(!isWrite, tool.ProtocolTool.Annotations?.ReadOnlyHint);
            // update_material overwrites values, set_owners can take access away, the remove tools remove a membership or link (old values stay in the audit log),
            // archive_material takes a material out of publication for good, set_retention_class replaces a class and
            // clear_legal_hold lifts a deletion block.
            Assert.Equal(DestructiveTools.Contains(tool.Name), tool.ProtocolTool.Annotations?.DestructiveHint);
            // Human-in-the-loop is configured in the agent; the description tells any client the same.
            Assert.Equal(isWrite, tool.Description.Contains("REQUIRES CONFIRMATION", StringComparison.Ordinal));
        }

        // There is no tool that publishes without a review or deletes: approve_material publishes only through the
        // publication gate, after the reviewer's own approval record.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.CallToolAsync("publish_material", cancellationToken: TestContext.Current.CancellationToken));

        if (configuredPath is not null)
        {
            using var wrongPath = await httpClient.PostAsJsonAsync("/mcp", new { }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);
        }
    }

    public static TheoryData<string?> RejectedTokens => new()
    {
        { (string?)null },
        { "not-a-token" },
        { TestTokens.Shared.Create(audience: "api://someone-else") },
        { TestTokens.Shared.Create(issuer: "https://evil.example.test") },
        { TestTokens.Shared.Create(lifetime: TimeSpan.FromMinutes(-5)) },
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task Requests_without_a_valid_user_token_get_401(string? token)
    {
        await using var app = await StartAsync(true, null);
        using var httpClient = app.GetTestClient();
        if (token is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await PostToolsListAsync(httpClient);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task A_token_signed_with_another_key_gets_401()
    {
        await using var app = await StartAsync(true, null);
        using var httpClient = app.GetTestClient();
        using var otherIdp = new TestTokens();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherIdp.Create());

        using var response = await PostToolsListAsync(httpClient);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Without_configured_identity_provider_every_request_is_refused()
    {
        await using var app = await StartAsync(true, null, trustTestIdp: false);
        using var httpClient = app.GetTestClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Shared.Create());

        using var response = await PostToolsListAsync(httpClient);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Direct_json_rpc_tools_list_works_with_a_token()
    {
        await using var app = await StartAsync(true, null);
        using var httpClient = app.GetTestClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Shared.Create());

        using var response = await PostToolsListAsync(httpClient);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Streamable HTTP permits JSON or an SSE response.
        var json = body.StartsWith("event:", StringComparison.Ordinal) || body.StartsWith("data:", StringComparison.Ordinal)
            ? body.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim()
            : body;
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(ExpectedTools.Length, document.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Disabled_by_default_does_not_expose_endpoints()
    {
        await using var app = await StartAsync(null, null);
        using var httpClient = app.GetTestClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Shared.Create());

        using var mcp = await httpClient.PostAsJsonAsync("/mcp", new { }, TestContext.Current.CancellationToken);
        using var rest = await httpClient.GetAsync("/api/learning-materials/collections", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, mcp.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rest.StatusCode);
    }

    [Fact]
    public async Task Rest_endpoints_require_a_user_token()
    {
        await using var app = await StartAsync(true, null);
        using var httpClient = app.GetTestClient();

        using var response = await httpClient.GetAsync("/api/learning-materials/collections", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("/")]
    [InlineData("/mcp/{tool}")]
    [InlineData("/mcp?token=x")]
    [InlineData("//mcp")]
    public async Task Invalid_paths_fail_at_startup(string path)
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(true, path));
    }

    [Theory]
    [InlineData("https://navigator.example.test/materiaal/")]
    [InlineData("http://navigator.example.test/materiaal/{key}")]
    [InlineData("/materiaal/{key}")]
    public async Task Invalid_detail_link_templates_fail_at_startup(string template)
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(true, null, detailLinkTemplate: template));
    }

    internal static async Task<HttpResponseMessage> PostToolsListAsync(HttpClient httpClient)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        return await httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal static async Task<WebApplication> StartAsync(bool? enabled, string? path, bool trustTestIdp = true, string? detailLinkTemplate = null, IDictionary<string, string?>? extraSettings = null, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var settings = new Dictionary<string, string?>
        {
            [$"{AgentApiOptions.SectionName}:Authentication:Audiences:0"] = TestTokens.Audience,
            [$"{AgentApiOptions.SectionName}:Authentication:RoleMapping:developer:0"] = "Materials.Developer",
        };
        if (enabled.HasValue)
        {
            settings[$"{AgentApiOptions.SectionName}:Enabled"] = enabled.Value.ToString();
        }

        if (path is not null)
        {
            settings[$"{AgentApiOptions.SectionName}:EndpointPath"] = path;
        }

        if (detailLinkTemplate is not null)
        {
            settings[$"{AgentApiOptions.SectionName}:DetailLinkTemplate"] = detailLinkTemplate;
        }

        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddAgentApi(builder.Configuration);
        configureServices?.Invoke(builder.Services);
        if (trustTestIdp)
        {
            // Stands in for OIDC discovery: the test identity provider's issuer and signing key.
            builder.Services.PostConfigure<JwtBearerOptions>(UserTokenAuthentication.Scheme, options =>
            {
                options.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                options.TokenValidationParameters.IssuerSigningKey = TestTokens.Shared.SigningKey;
            });
        }

        var app = builder.Build();
        try
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            // Exercise the same filter that Umbraco executes, without a database or CMS boot.
            foreach (var filter in app.Services.GetRequiredService<IOptions<UmbracoPipelineOptions>>().Value.PipelineFilters)
            {
                filter.OnEndpoints(app);
            }

            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
