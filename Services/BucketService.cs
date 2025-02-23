using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using BucketFlow.Models;
using System.Text.Json;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using BucketFlow.Services;

namespace BucketFlow.Services;

public interface IBucketService
{
    Task<bool> CreateBucketAsync(string name);
    Task<List<BucketConfiguration>> ListBucketsAsync();
    Task<bool> DeleteBucketAsync(string name);
    Task<bool> AddDataAsync(string bucketName, JsonObject data);
    Task<IEnumerable<JsonObject>> QueryDataAsync(string bucketName, SearchQueryParameters parameters);
    Task<bool> DeleteDataAsync(string bucketName, SearchQueryParameters parameters);
}

public class BucketService : IBucketService
{
    private Dictionary<string, List<JsonObject>> _buckets = new();
    private Dictionary<string, BucketConfiguration> _configurations = new();
    private readonly string _storageFilePath = "buckets.json";
    private readonly IMemoryCache _cache;
    private readonly ILogger<BucketService> _logger;
    private readonly object _lockObject = new();
    private readonly IReplicationService _replicationService;
    private readonly bool _isReplicationRequest;

    public BucketService(
        IMemoryCache cache, 
        ILogger<BucketService> logger,
        IReplicationService replicationService,
        IHttpContextAccessor httpContextAccessor)
    {
        _cache = cache;
        _logger = logger;
        _replicationService = replicationService;
        _isReplicationRequest = httpContextAccessor.HttpContext?.Request.Query["isReplication"].ToString() == "true";
        LoadFromFile();
    }

    private void LoadFromFile()
    {
        _logger.LogInformation("Chargement des buckets depuis {FilePath}", _storageFilePath);
        if (File.Exists(_storageFilePath))
        {
            var json = File.ReadAllText(_storageFilePath);
            var storage = JsonSerializer.Deserialize<BucketStorage>(json) ?? new BucketStorage();
            _buckets = storage.Buckets;
            _configurations = storage.Configurations;
            _logger.LogInformation("Chargement terminé: {BucketCount} buckets chargés", _buckets.Count);
        }
        else
        {
            _logger.LogWarning("Fichier de stockage non trouvé: {FilePath}", _storageFilePath);
        }
    }

    private void SaveToFile()
    {
        lock (_lockObject)
        {
            _logger.LogInformation("Sauvegarde des buckets vers {FilePath}", _storageFilePath);
            var storage = new BucketStorage
            {
                Buckets = _buckets,
                Configurations = _configurations
            };
            var json = JsonSerializer.Serialize(storage);
            File.WriteAllText(_storageFilePath, json);
            _logger.LogInformation("Sauvegarde terminée");
        }
    }

    public async Task<bool> CreateBucketAsync(string name)
    {
        _logger.LogInformation("Création du bucket: {BucketName}", name);
        if (_buckets.ContainsKey(name))
        {
            _logger.LogWarning("Bucket {BucketName} existe déjà", name);
            return false;
        }
            
        _buckets[name] = new List<JsonObject>();
        _configurations[name] = new BucketConfiguration { Name = name };
        SaveToFile();
        _logger.LogInformation("Bucket {BucketName} créé avec succès", name);

        if (!_isReplicationRequest)
        {
            await _replicationService.ReplicateCreateBucket(name);
        }

        return true;
    }

    public Task<List<BucketConfiguration>> ListBucketsAsync()
    {
        _logger.LogInformation("Listage des buckets");
        var bucketList = _configurations.Values.ToList();
        _logger.LogInformation("{Count} buckets trouvés", bucketList.Count);
        return Task.FromResult(bucketList);
    }

    public async Task<bool> DeleteBucketAsync(string name)
    {
        _logger.LogInformation("Suppression du bucket: {BucketName}", name);
        var removed = _buckets.Remove(name);
        if (removed)
        {
            _configurations.Remove(name);
            SaveToFile();
            _logger.LogInformation("Bucket {BucketName} supprimé avec succès", name);

            if (!_isReplicationRequest)
            {
                await _replicationService.ReplicateDeleteBucket(name);
            }
        }
        else
        {
            _logger.LogWarning("Bucket {BucketName} non trouvé pour la suppression", name);
        }
        return removed;
    }

    public async Task<bool> AddDataAsync(string bucketName, JsonObject data)
    {
        _logger.LogInformation("Ajout de données dans le bucket: {BucketName}", bucketName);
        
        if (!_buckets.ContainsKey(bucketName))
        {
            _logger.LogInformation("Bucket {BucketName} n'existe pas, création automatique", bucketName);
            await CreateBucketAsync(bucketName);
        }

        _buckets[bucketName].Add(data);
        SaveToFile();
        _logger.LogInformation("Données ajoutées avec succès dans {BucketName}", bucketName);

        if (!_isReplicationRequest)
        {
            await _replicationService.ReplicateAddData(bucketName, data);
        }

        return true;
    }

    public Task<IEnumerable<JsonObject>> QueryDataAsync(string bucketName, SearchQueryParameters parameters)
    {
        _logger.LogInformation("Recherche dans le bucket: {BucketName} avec paramètres: {@Parameters}", bucketName, parameters);
        if (!_buckets.ContainsKey(bucketName))
        {
            _logger.LogWarning("Bucket {BucketName} non trouvé pour la recherche", bucketName);
            return Task.FromResult(Enumerable.Empty<JsonObject>());
        }

        var results = _buckets[bucketName]
            .Where(data => parameters.Matches(data));
        
        _logger.LogInformation("{Count} résultats trouvés dans {BucketName}", results.Count(), bucketName);
        return Task.FromResult(results);
    }

    public async Task<bool> DeleteDataAsync(string bucketName, SearchQueryParameters parameters)
    {
        _logger.LogInformation("Suppression de données dans le bucket: {BucketName} avec paramètres: {@Parameters}", bucketName, parameters);
        if (!_buckets.ContainsKey(bucketName))
        {
            _logger.LogWarning("Bucket {BucketName} non trouvé pour la suppression de données", bucketName);
            return false;
        }

        var itemsToRemove = _buckets[bucketName]
            .Where(data => parameters.Matches(data))
            .ToList();

        foreach (var item in itemsToRemove)
        {
            _buckets[bucketName].Remove(item);
        }

        SaveToFile();
        _logger.LogInformation("{Count} éléments supprimés dans {BucketName}", itemsToRemove.Count, bucketName);

        if (!_isReplicationRequest)
        {
            await _replicationService.ReplicateDeleteData(bucketName, parameters);
        }

        return itemsToRemove.Any();
    }
}

public class BucketStorage
{
    public Dictionary<string, List<JsonObject>> Buckets { get; set; } = new();
    public Dictionary<string, BucketConfiguration> Configurations { get; set; } = new();
} 