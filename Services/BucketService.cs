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
using System.Linq;

namespace BucketFlow.Services;

public interface IBucketService
{
    Task<bool> CreateBucketAsync(string name);
    Task<List<BucketConfiguration>> ListBucketsAsync();
    Task<bool> DeleteBucketAsync(string name);
    Task<bool> AddDataAsync(string bucketName, JsonObject data);
    Task<PaginatedResult<JsonObject>> QueryDataAsync(string bucketName, SearchQueryParameters parameters);
    Task<bool> DeleteDataAsync(string bucketName, SearchQueryParameters parameters);
    Task<bool> FlushBucketAsync(string bucketName);
    Task<bool> SetDataAsync(string bucketName, JsonObject data, string keyField);
}

public class BucketService : IBucketService
{
    private Dictionary<string, List<JsonObject>> _buckets = new();
    private Dictionary<string, BucketConfiguration> _configurations = new();
    private readonly string _bucketsPath;
    private readonly string _configFilePath;
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
        _bucketsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "buckets");
        _configFilePath = Path.Combine(_bucketsPath, "config.json");
        _cache = cache;
        _logger = logger;
        _replicationService = replicationService;
        _isReplicationRequest = httpContextAccessor.HttpContext?.Request.Query["isReplication"].ToString() == "true";
        InitializeBuckets();
    }

    private void InitializeBuckets()
    {
        if (!Directory.Exists(_bucketsPath))
        {
            Directory.CreateDirectory(_bucketsPath);
            MigrateExistingBuckets();
        }
        LoadConfigurations();
        LoadAllBuckets();
    }

    private void MigrateExistingBuckets()
    {
        if (File.Exists("buckets.json"))
        {
            _logger.LogInformation("Migration des anciens buckets vers le nouveau format");
            var oldJson = File.ReadAllText("buckets.json");
            var oldStorage = JsonSerializer.Deserialize<BucketStorage>(oldJson);
            
            if (oldStorage != null)
            {
                _configurations = oldStorage.Configurations;
                SaveConfigurations();

                foreach (var (bucketName, data) in oldStorage.Buckets)
                {
                    SaveBucketToFile(bucketName, data);
                }
            }

            // Backup de l'ancien fichier
            File.Move("buckets.json", "buckets.json.bak");
            _logger.LogInformation("Migration terminée");
        }
    }

    private void LoadConfigurations()
    {
        if (File.Exists(_configFilePath))
        {
            var json = File.ReadAllText(_configFilePath);
            _configurations = JsonSerializer.Deserialize<Dictionary<string, BucketConfiguration>>(json) ?? new();
        }
    }

    private void SaveConfigurations()
    {
        var json = JsonSerializer.Serialize(_configurations);
        File.WriteAllText(_configFilePath, json);
    }

    private void LoadAllBuckets()
    {
        _buckets.Clear();
        foreach (var config in _configurations)
        {
            var bucketPath = GetBucketPath(config.Key);
            if (File.Exists(bucketPath))
            {
                var json = File.ReadAllText(bucketPath);
                _buckets[config.Key] = JsonSerializer.Deserialize<List<JsonObject>>(json) ?? new();
            }
            else
            {
                _buckets[config.Key] = new();
            }
        }
    }

    private string GetBucketPath(string bucketName)
    {
        return Path.Combine(_bucketsPath, $"{bucketName}.bucket");
    }

    private void SaveBucketToFile(string bucketName, List<JsonObject> data)
    {
        var bucketPath = GetBucketPath(bucketName);
        var json = JsonSerializer.Serialize(data);
        File.WriteAllText(bucketPath, json);
    }

    private void SaveToFile()
    {
        lock (_lockObject)
        {
            SaveConfigurations();
            foreach (var (bucketName, data) in _buckets)
            {
                SaveBucketToFile(bucketName, data);
            }
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

        if (!data.ContainsKey("timestamp"))
        {
            data["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

    public Task<PaginatedResult<JsonObject>> QueryDataAsync(string bucketName, SearchQueryParameters parameters)
    {
        _logger.LogInformation("Recherche dans le bucket: {BucketName} avec paramètres: {@Parameters}", bucketName, parameters);
        
        if (!_buckets.ContainsKey(bucketName))
        {
            _logger.LogWarning("Bucket {BucketName} non trouvé pour la recherche", bucketName);
            return Task.FromResult(new PaginatedResult<JsonObject>());
        }

        // Appliquer les filtres
        var query = _buckets[bucketName].AsQueryable().Where(data => parameters.Matches(data));

        // Appliquer le tri
        if (!string.IsNullOrEmpty(parameters.SortBy))
        {
            query = parameters.SortDescending
                ? query.OrderByDescending(x => GetSortValue(x, parameters.SortBy))
                : query.OrderBy(x => GetSortValue(x, parameters.SortBy));
        }
        else
        {
            // Tri par défaut sur timestamp en ordre décroissant
            query = query.OrderByDescending(x => GetTimestampValue(x));
        }

        // Calculer la pagination
        var totalItems = query.Count();
        var totalPages = (int)Math.Ceiling(totalItems / (double)parameters.Limit);
        var skip = (parameters.Page - 1) * parameters.Limit;

        // Appliquer la pagination
        var items = query
            .Skip(skip)
            .Take(parameters.Limit)
            .ToList();

        return Task.FromResult(new PaginatedResult<JsonObject>
        {
            Items = items,
            TotalItems = totalItems,
            Page = parameters.Page,
            TotalPages = totalPages,
            Limit = parameters.Limit
        });
    }

    private string GetSortValue(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var value))
        {
            return value?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    private long GetTimestampValue(JsonObject obj)
    {
        if (obj.TryGetPropertyValue("timestamp", out var timestamp))
        {
            return timestamp.GetValue<long>();
        }
        return 0;
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

    public async Task<bool> FlushBucketAsync(string bucketName)
    {
        _logger.LogInformation("Vidage du bucket: {BucketName}", bucketName);
        if (!_buckets.ContainsKey(bucketName))
        {
            _logger.LogWarning("Bucket {BucketName} non trouvé pour le vidage", bucketName);
            return false;
        }

        _buckets[bucketName].Clear();
        SaveToFile();
        _logger.LogInformation("Bucket {BucketName} vidé avec succès", bucketName);

        if (!_isReplicationRequest)
        {
            await _replicationService.ReplicateFlushBucket(bucketName);
        }

        return true;
    }

    public async Task<bool> SetDataAsync(string bucketName, JsonObject data, string keyField)
    {
        _logger.LogInformation("Set de données dans le bucket: {BucketName} avec keyField: {KeyField}", bucketName, keyField);
        
        if (!data.ContainsKey(keyField))
        {
            _logger.LogWarning("Le champ clé {KeyField} n'est pas présent dans les données", keyField);
            return false;
        }

        if (!_buckets.ContainsKey(bucketName))
        {
            _logger.LogInformation("Bucket {BucketName} n'existe pas, création automatique", bucketName);
            await CreateBucketAsync(bucketName);
        }

        // Ajouter/Mettre à jour le timestamp
        data["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var keyValue = data[keyField]?.ToString();
        var existingIndex = _buckets[bucketName].FindIndex(item => 
            item.ContainsKey(keyField) && 
            item[keyField]?.ToString() == keyValue);

        if (existingIndex != -1)
        {
            _logger.LogInformation("Mise à jour des données existantes avec {KeyField}={KeyValue}", keyField, keyValue);
            _buckets[bucketName][existingIndex] = data;
        }
        else
        {
            _logger.LogInformation("Ajout de nouvelles données avec {KeyField}={KeyValue}", keyField, keyValue);
            _buckets[bucketName].Add(data);
        }

        SaveToFile();

        if (!_isReplicationRequest)
        {
            await _replicationService.ReplicateSetData(bucketName, data, keyField);
        }

        return true;
    }
}

public class BucketStorage
{
    public Dictionary<string, List<JsonObject>> Buckets { get; set; } = new();
    public Dictionary<string, BucketConfiguration> Configurations { get; set; } = new();
} 