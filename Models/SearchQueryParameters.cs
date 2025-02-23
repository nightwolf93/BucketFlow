using System.Text.Json.Nodes;
using System.Text.Json;

namespace BucketFlow.Models;

public class SearchQueryParameters
{
    public Dictionary<string, string> Equality { get; set; } = new();
    public Dictionary<string, decimal> GreaterThanOrEqual { get; set; } = new();
    public Dictionary<string, List<string>> In { get; set; } = new();
    public Dictionary<string, (DateTime Start, DateTime End)> DateRanges { get; set; } = new();

    public static SearchQueryParameters FromJson(JsonObject json)
    {
        var parameters = new SearchQueryParameters();

        foreach (var property in json)
        {
            var key = property.Key;
            var value = property.Value?.ToString();

            if (key.EndsWith("[gte]"))
            {
                var fieldName = key.Replace("[gte]", "");
                if (decimal.TryParse(value, out var numValue))
                {
                    parameters.GreaterThanOrEqual[fieldName] = numValue;
                }
            }
            else if (key.EndsWith("[in]"))
            {
                var fieldName = key.Replace("[in]", "");
                parameters.In[fieldName] = value?.Split(',').ToList() ?? new List<string>();
            }
            else if (key.EndsWith("[between]"))
            {
                var fieldName = key.Replace("[between]", "");
                var dates = value?.Split(',');
                if (dates?.Length == 2 && 
                    DateTime.TryParse(dates[0], out var start) && 
                    DateTime.TryParse(dates[1], out var end))
                {
                    parameters.DateRanges[fieldName] = (start, end);
                }
            }
            else
            {
                parameters.Equality[key] = value ?? "";
            }
        }

        return parameters;
    }

    public bool Matches(JsonObject data)
    {
        foreach (var equal in Equality)
        {
            if (!data.TryGetPropertyValue(equal.Key, out var value) || 
                value?.ToString() != equal.Value)
                return false;
        }

        foreach (var gte in GreaterThanOrEqual)
        {
            if (!data.TryGetPropertyValue(gte.Key, out var value) || 
                !decimal.TryParse(value?.ToString(), out var numValue) || 
                numValue < gte.Value)
                return false;
        }

        foreach (var inClause in In)
        {
            if (!data.TryGetPropertyValue(inClause.Key, out var value) || 
                !inClause.Value.Contains(value?.ToString() ?? ""))
                return false;
        }

        foreach (var dateRange in DateRanges)
        {
            if (!data.TryGetPropertyValue(dateRange.Key, out var value) || 
                !DateTime.TryParse(value?.ToString(), out var date) ||
                date < dateRange.Value.Start || 
                date > dateRange.Value.End)
                return false;
        }

        return true;
    }

    public static bool TryParse(string? value, out SearchQueryParameters result)
    {
        try
        {
            result = new SearchQueryParameters();
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }
            
            var json = JsonNode.Parse(value) as JsonObject;
            if (json != null)
            {
                result = FromJson(json);
                return true;
            }
            
            return false;
        }
        catch
        {
            result = new SearchQueryParameters();
            return false;
        }
    }
} 