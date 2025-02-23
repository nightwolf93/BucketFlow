using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BucketFlow.Models;
using System.Web;
using System.Collections.Generic;
using System.Threading;

namespace BucketFlow.Services;

public interface IReplicationService
{
    Task ReplicateCreateBucket(string name);
    Task ReplicateDeleteBucket(string name);
    Task ReplicateAddData(string bucketName, JsonObject data);
    Task ReplicateDeleteData(string bucketName, SearchQueryParameters parameters);
}

public class ReplicationService : IReplicationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ReplicationService> _logger;
    private readonly string _slaveUrl;
    private readonly string _replicationApiKey;
    private readonly bool _isMaster;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Queue<ReplicationRequest> _requestBuffer = new();
    private readonly Timer _healthCheckTimer;
    private readonly Timer _retryTimer;
    private bool _isSlaveAvailable;
    private const int HEALTH_CHECK_INTERVAL = 5000; // 5 secondes
    private const int RETRY_INTERVAL = 10000; // 10 secondes

    public class ReplicationRequest
    {
        public ReplicationType Type { get; set; }
        public string BucketName { get; set; }
        public object? Data { get; set; }
    }

    public enum ReplicationType
    {
        CreateBucket,
        DeleteBucket,
        AddData,
        DeleteData
    }

    public ReplicationService(
        IConfiguration configuration,
        ILogger<ReplicationService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _isMaster = configuration.GetValue<bool>("Replication:IsMaster");
        _slaveUrl = configuration.GetValue<string>("Replication:SlaveUrl") ?? "";
        _replicationApiKey = configuration.GetValue<string>("Replication:ApiKey") ?? "";
        
        _jsonOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
        };

        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _replicationApiKey);
        
        _healthCheckTimer = new Timer(_ => CheckSlaveHealth(), null, 0, HEALTH_CHECK_INTERVAL);
        _retryTimer = new Timer(_ => ProcessBuffer(), null, 0, RETRY_INTERVAL);
    }

    private async Task CheckSlaveHealth()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_slaveUrl}/health");
            _isSlaveAvailable = response.IsSuccessStatusCode;
        }
        catch
        {
            _isSlaveAvailable = false;
            _logger.LogWarning("Le slave n'est pas disponible");
        }
    }

    private async Task ProcessBuffer()
    {
        if (!_isSlaveAvailable || _requestBuffer.Count == 0) return;

        while (_requestBuffer.Count > 0 && _isSlaveAvailable)
        {
            var request = _requestBuffer.Peek();
            try
            {
                await ProcessRequest(request);
                _requestBuffer.Dequeue();
            }
            catch
            {
                break;
            }
        }
    }

    private async Task ProcessRequest(ReplicationRequest request)
    {
        switch (request.Type)
        {
            case ReplicationType.CreateBucket:
                await ReplicateCreateBucket(request.BucketName);
                break;
            case ReplicationType.AddData:
                await ReplicateAddData(request.BucketName, (JsonObject)request.Data!);
                break;
            case ReplicationType.DeleteBucket:
                await ReplicateDeleteBucket(request.BucketName);
                break;
            case ReplicationType.DeleteData:
                await ReplicateDeleteData(request.BucketName, (SearchQueryParameters)request.Data!);
                break;
        }
    }

    private void BufferRequest(ReplicationType type, string bucketName, object? data = null)
    {
        _requestBuffer.Enqueue(new ReplicationRequest
        {
            Type = type,
            BucketName = bucketName,
            Data = data
        });
        _logger.LogInformation("Requête de réplication mise en buffer: {Type} pour {BucketName}", type, bucketName);
    }

    public async Task ReplicateCreateBucket(string name)
    {
        if (!_isMaster) return;
        
        try
        {
            var response = await _httpClient.PostAsync(
                $"{_slaveUrl}/api/buckets/{name}?isReplication=true", 
                null);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Échec de la réplication de création du bucket {Name}", name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la réplication de création du bucket");
        }
    }

    public async Task ReplicateAddData(string bucketName, JsonObject data)
    {
        if (!_isMaster) return;

        if (!_isSlaveAvailable)
        {
            BufferRequest(ReplicationType.AddData, bucketName, data);
            return;
        }

        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(data, _jsonOptions),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(
                $"{_slaveUrl}/api/buckets/{Uri.EscapeDataString(bucketName)}/data?isReplication=true",
                content);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Échec de la réplication d'ajout de données. Status: {Status}, Error: {Error}", 
                    response.StatusCode, errorContent);
            }
        }
        catch
        {
            BufferRequest(ReplicationType.AddData, bucketName, data);
            _logger.LogInformation("Échec de réplication, requête mise en buffer");
        }
    }

    public async Task ReplicateDeleteData(string bucketName, SearchQueryParameters parameters)
    {
        if (!_isMaster) return;
        
        try
        {
            var queryParams = HttpUtility.ParseQueryString(string.Empty);
            queryParams["isReplication"] = "true";
            
            var url = $"{_slaveUrl}/api/buckets/{Uri.EscapeDataString(bucketName)}/data?{queryParams}";
            
            var request = new HttpRequestMessage(HttpMethod.Delete, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(parameters, _jsonOptions),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };

            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Échec de la réplication de suppression de données dans le bucket {BucketName}", bucketName);
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Réponse du serveur: {Error}", errorContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la réplication de suppression de données");
        }
    }

    public async Task ReplicateDeleteBucket(string name)
    {
        if (!_isMaster) return;
        
        try
        {
            var response = await _httpClient.DeleteAsync(
                $"{_slaveUrl}/api/buckets/{name}?isReplication=true");
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Échec de la réplication de suppression du bucket {Name}", name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la réplication de suppression du bucket");
        }
    }
} 