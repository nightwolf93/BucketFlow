using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BucketFlow.Services;

public interface IApiKeyAuthenticationService
{
    bool ValidateApiKey(string apiKey);
}

public class ApiKeyAuthenticationService : IApiKeyAuthenticationService, IHostedService
{
    private HashSet<string> _validApiKeys = new();
    private readonly ILogger<ApiKeyAuthenticationService> _logger;
    private readonly string _keysPath = "keys.json";
    private Timer? _timer;
    private const int RELOAD_INTERVAL_MS = 30000; // 30 secondes

    public ApiKeyAuthenticationService(ILogger<ApiKeyAuthenticationService> logger)
    {
        _logger = logger;
    }

    public bool ValidateApiKey(string apiKey)
    {
        return _validApiKeys.Contains(apiKey);
    }

    private void LoadApiKeys()
    {
        try
        {
            if (File.Exists(_keysPath))
            {
                var jsonContent = File.ReadAllText(_keysPath);
                var options = new JsonSerializerOptions
                {
                    TypeInfoResolver = null,
                    IncludeFields = true
                };
                var keys = JsonSerializer.Deserialize<string[]>(jsonContent, options);
                _validApiKeys = new HashSet<string>(keys ?? Array.Empty<string>());
                _logger.LogInformation("API keys rechargées: {count} clés trouvées", _validApiKeys.Count);
            }
            else
            {
                _logger.LogWarning("Fichier keys.json non trouvé");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors du chargement des API keys");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadApiKeys(); // Chargement initial
        _timer = new Timer(state => LoadApiKeys(), null, TimeSpan.Zero, 
            TimeSpan.FromMilliseconds(RELOAD_INTERVAL_MS));
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }
} 