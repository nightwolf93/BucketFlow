using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json.Nodes;
using BucketFlow.Services;
using BucketFlow.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IApiKeyAuthenticationService, ApiKeyAuthenticationService>();
builder.Services.AddHostedService(sp => (ApiKeyAuthenticationService)sp.GetRequiredService<IApiKeyAuthenticationService>());
builder.Services.AddSingleton<IBucketService, BucketService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IReplicationService, ReplicationService>();

var app = builder.Build();

// Middleware
app.UseSwagger();
app.UseSwaggerUI();

// Auth Middleware
app.Use(async (context, next) =>
{
    // Skip auth for health check
    if (context.Request.Path == "/health")
    {
        await next();
        return;
    }

    var apiKeyService = context.RequestServices.GetRequiredService<IApiKeyAuthenticationService>();
    var apiKey = context.Request.Headers["X-API-Key"].FirstOrDefault();
    
    if (string.IsNullOrEmpty(apiKey) || !apiKeyService.ValidateApiKey(apiKey))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new ApiResponse<object> 
        { 
            Success = false, 
            Error = "Invalid API Key" 
        });
        return;
    }
    
    await next();
});

// Endpoints
app.MapPost("/api/buckets/{name}", async ([FromRoute] string name, [FromServices] IBucketService bucketService) =>
{
    var result = await bucketService.CreateBucketAsync(name);
    return Results.Ok(new ApiResponse<bool> { Success = result });
})
.WithName("CreateBucket")
.WithOpenApi();

app.MapGet("/api/buckets", async ([FromServices] IBucketService bucketService) =>
{
    var buckets = await bucketService.ListBucketsAsync();
    return Results.Ok(new ApiResponse<List<BucketConfiguration>> { Success = true, Data = buckets });
})
.WithName("ListBuckets")
.WithOpenApi();

app.MapDelete("/api/buckets/{name}", async ([FromRoute] string name, [FromServices] IBucketService bucketService) =>
{
    var result = await bucketService.DeleteBucketAsync(name);
    return Results.Ok(new ApiResponse<bool> { Success = result });
})
.WithName("DeleteBucket")
.WithOpenApi();

// Data Operations
app.MapPost("/api/buckets/{name}/data", async ([FromRoute] string name, [FromBody] JsonObject data, [FromServices] IBucketService bucketService) =>
{
    var result = await bucketService.AddDataAsync(name, data);
    return Results.Ok(new ApiResponse<bool> { Success = result });
})
.WithName("AddData")
.WithOpenApi();

app.MapGet("/api/buckets/{name}/data", async ([FromRoute] string name, 
    HttpContext context, [FromServices] IBucketService bucketService) =>
{
    try
    {
        var query = context.Request.Query;
        var jsonObject = new JsonObject();
        
        foreach (var param in query)
        {
            jsonObject.Add(param.Key, param.Value.ToString());
        }

        var parsedParams = SearchQueryParameters.FromJson(jsonObject);
        var data = await bucketService.QueryDataAsync(name, parsedParams);
        return Results.Ok(new ApiResponse<IEnumerable<JsonObject>> { Success = true, Data = data });
    }
    catch(Exception ex)
    {
        return Results.BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
    }
})
.WithName("QueryData")
.WithOpenApi();

app.MapDelete("/api/buckets/{name}/data", async ([FromRoute] string name, 
    [FromBody] JsonObject queryParams, 
    [FromQuery] bool isReplication,
    [FromServices] IBucketService bucketService) =>
{
    try 
    {
        var parsedParams = SearchQueryParameters.FromJson(queryParams);
        var result = await bucketService.DeleteDataAsync(name, parsedParams);
        return Results.Ok(new ApiResponse<bool> { Success = result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
    }
})
.WithName("DeleteData")
.WithOpenApi();

// Health Check endpoint
app.MapGet("/health", () =>
{
    return Results.Ok(new ApiResponse<object> 
    { 
        Success = true, 
        Data = new { Status = "Healthy", Timestamp = DateTime.UtcNow }
    });
})
.WithName("HealthCheck")
.WithOpenApi()
.AllowAnonymous(); // Permet l'accès sans API key

app.Run();
