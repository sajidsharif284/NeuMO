using System;
using System.Configuration;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static NeuMo.Controllers.AssemblyController;

public class OdooService
{
    private readonly HttpClient _client;

    public OdooService()
    {
        _client = new HttpClient();
    }

    public string GetStockLotId(string productId)
    {
        var apiUrl = ConfigurationManager.AppSettings["OdooApiUrl"];
        var dbName = ConfigurationManager.AppSettings["OdooDatabase"];
        var userId = int.Parse(ConfigurationManager.AppSettings["OdooUserId"]);
        var apiKey = ConfigurationManager.AppSettings["OdooApiKey"];

        var requestBody = new
        {
            jsonrpc = "2.0",
            method = "call",
            @params = new
            {
                service = "object",
                method = "execute_kw",
                args = new object[]
                {
                   dbName, // From config
                     userId, // From config
                   apiKey, // From config
                    "stock.lot",
                    "search_read",
                    new object[]
                    {
                        new object[]
                        {
                            new object[] { "name", "=", productId }
                        },
                        new string[] { "id", "name", "product_id", "status", "frame_number" }
                    },
                    new { }
                }
            },
            id = 1
        };

        var content = new StringContent(
            JsonConvert.SerializeObject(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        try
        {
            var response = _client.PostAsync(apiUrl, content).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP Error: {(int)response.StatusCode} - {response.ReasonPhrase}");
            }

            var responseString = response.Content.ReadAsStringAsync().Result;
            var rpcResponse = JsonConvert.DeserializeObject<RpcResponse>(responseString);

            if (rpcResponse.Error != null && rpcResponse.Error.HasValues)
            {
                throw new Exception($"RPC Error: {rpcResponse.Error}");
            }

            if (rpcResponse.Result != null)
            {
                var resultArray = rpcResponse.Result as JArray;
                if (resultArray != null && resultArray.Count > 0)
                {
                    var firstItem = resultArray[0];

                    // get product_id array
                    var productIdArray = firstItem["product_id"] as JArray;
                    if (productIdArray != null && productIdArray.Count > 1)
                    {
                        var productString = productIdArray[1]?.ToString();
                        // productString = "[E212154] HAIBIKE Lyke CF SE BLAU"

                        // extract E212154 using regex
                        var match = System.Text.RegularExpressions.Regex.Match(productString, @"\[(.*?)\]");
                        if (match.Success)
                        {
                            return match.Groups[1].Value; // "E212154"
                        }
                    }

                    return firstItem["id"]?.ToString(); // fallback → return id
                }
            }

            return null; // no result found
        }
        catch (Exception ex)
        {
            throw new Exception($"Exception: {ex.Message}");
        }
    }
    public string GetLotId(string bikeId)
    {
        try
        {
            // Read config values
            var apiUrl = ConfigurationManager.AppSettings["OdooApiUrl"];
            var dbName = ConfigurationManager.AppSettings["OdooDatabase"];
            var userId = int.Parse(ConfigurationManager.AppSettings["OdooUserId"]);
            var apiKey = ConfigurationManager.AppSettings["OdooApiKey"];

            // Build request body
            var requestBody = new
            {
                jsonrpc = "2.0",
                method = "call",
                @params = new
                {
                    service = "object",
                    method = "execute_kw",
                    args = new object[]
                    {
                            dbName,
                            userId,
                            apiKey,
                            "stock.lot",
                            "search_read",
                            new object[]
                            {
                                new object[]
                                {
                                    new object[] { "name", "=", bikeId }
                                },
                                new string[] { "id", "name", "product_id", "status", "frame_number" }
                            },
                            new { }
                    }
                }
            };

            var client = new HttpClient();
            var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
            var response = client.PostAsync(apiUrl, content).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP Error: {(int)response.StatusCode} - {response.ReasonPhrase}");
            }

            var responseString = response.Content.ReadAsStringAsync().Result;
            var rpcResponse = JsonConvert.DeserializeObject<RpcResponse>(responseString);

            // Handle RPC error
            if (rpcResponse.Error != null && rpcResponse.Error.HasValues)
            {
                throw new Exception($"RPC Error: {rpcResponse.Error}");
            }

            // Extract ID
            if (rpcResponse.Result is JArray resultArray && resultArray.Count > 0)
            {
                return resultArray[0]["id"]?.ToString();
            }

            return null; // No result found
        }
        catch (Exception ex)
        {
            throw new Exception($"Odoo API Exception: {ex.Message}", ex);
        }
    }

    public bool UpdateLotStatus(string lotId)
    {
        var apiUrl = ConfigurationManager.AppSettings["OdooApiUrl"];
        var dbName = ConfigurationManager.AppSettings["OdooDatabase"];
        var userId = int.Parse(ConfigurationManager.AppSettings["OdooUserId"]);
        var apiKey = ConfigurationManager.AppSettings["OdooApiKey"];

        var client = new HttpClient();

        var requestBody = new
        {
            jsonrpc = "2.0",
            method = "call",
            @params = new
            {
                service = "object",
                method = "execute_kw",
                args = new object[]
                {
                        dbName,
                        userId,
                        apiKey,
                        "stock.lot",
                        "write",
                        new object[]
                        {
                            new object[] { Convert.ToInt32(lotId) },
                            new
                            {
                                status = "unpacked"
                            }
                        }
                }
            },
            id = 2
        };

        var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

        var response = client.PostAsync(apiUrl, content).Result;
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"HTTP Error: {(int)response.StatusCode} - {response.ReasonPhrase}");
        }

        var responseString = response.Content.ReadAsStringAsync().Result;
        var rpcResponse = JsonConvert.DeserializeObject<RpcResponse>(responseString);

        if (rpcResponse.Error != null && rpcResponse.Error.HasValues)
        {
            throw new Exception($"RPC Error: {rpcResponse.Error}");
        }

        // If the result is true, the update succeeded
        return rpcResponse.Result != null && rpcResponse.Result.ToString().ToLower() == "true";
    }
}
