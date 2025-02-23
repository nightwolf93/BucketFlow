local function makeRequest(endpoint, method, data, cb)
    local headers = {
        ["X-API-Key"] = Config.apiKey,
        ["Content-Type"] = "application/json"
    }
    
    PerformHttpRequest(Config.baseUrl .. endpoint, function(statusCode, responseData, responseHeaders)
        if statusCode == 200 then
            local response = json.decode(responseData)
            if response.Success then
                if cb then cb(true, response.Data) end
                return
            else
                if cb then cb(false, response.Error) end
                return
            end
        else
            if cb then cb(false, "Erreur HTTP: " .. statusCode) end
        end
    end, method, data and json.encode(data) or "", headers)
end

-- Exports corrects
exports("CreateBucket", function(name, cb)
    makeRequest("/buckets/" .. name, "POST", nil, cb)
end)

exports("ListBuckets", function(cb)
    makeRequest("/buckets", "GET", nil, cb)
end)

exports("DeleteBucket", function(name, cb)
    makeRequest("/buckets/" .. name, "DELETE", nil, cb)
end)

exports("AddData", function(bucketName, data, cb)
    makeRequest("/buckets/" .. bucketName .. "/data", "POST", data, cb)
end)

exports("QueryData", function(bucketName, queryParams, cb)
    local endpoint = string.format("/buckets/%s/data?%s", 
        bucketName, 
        queryParams and ("query=" .. queryParams) or ""
    )
    makeRequest(endpoint, "GET", nil, cb)
end)

exports("DeleteData", function(bucketName, queryParams, cb)
    local endpoint = string.format("/buckets/%s/data?%s", 
        bucketName, 
        queryParams and ("query=" .. queryParams) or ""
    )
    makeRequest(endpoint, "DELETE", nil, cb)
end) 