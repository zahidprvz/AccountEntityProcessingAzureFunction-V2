using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CsvHelper;
using CsvHelper.Configuration.Attributes;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Newtonsoft.Json.Linq;
using Polly;

namespace DynamicsAccountProcessor
{
    public static class ProcessAccountsFunction
    {
        private static readonly string ClientId = Environment.GetEnvironmentVariable("CLIENT_ID");
        private static readonly string ClientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
        private static readonly string TenantId = Environment.GetEnvironmentVariable("TENANT_ID");
        private static readonly string DynamicsUrl = Environment.GetEnvironmentVariable("DYNAMICS_URL");
        private static readonly string BlobConnectionString = Environment.GetEnvironmentVariable("BLOB_CONNECTION_STRING");
        private static readonly string ContainerName = Environment.GetEnvironmentVariable("BLOB_CONTAINER_NAME");

        const int BatchSize = 100;
        const int MaxConcurrentBatches = 5;
        const int RequestDelayMs = 1000;

        [Function("ProcessAccountsHttp")]
        public static async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req,
            FunctionContext context)
        {
            var logger = context.GetLogger("ProcessAccounts");
            var response = req.CreateResponse();

            try
            {
                var token = await GetAccessToken(logger);
                var httpClient = CreateHttpClient(token);
                var blobServiceClient = new BlobServiceClient(BlobConnectionString);

                logger.LogInformation("Fetching account IDs...");
                var allAccountIds = await GetAllAccountIds(httpClient, logger);
                logger.LogInformation($"Total accounts to process: {allAccountIds.Count}");

                var processedRecords = new ConcurrentBag<AccountRecord>();
                var batches = allAccountIds.Batch(BatchSize).ToList();
                var semaphore = new SemaphoreSlim(MaxConcurrentBatches);

                var tasks = batches.Select(async batch =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var records = await ProcessAccountBatchWithRetry(httpClient, batch.ToList(), logger);
                        foreach (var r in records)
                            processedRecords.Add(r);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                    await Task.Delay(RequestDelayMs);
                });

                await Task.WhenAll(tasks);
                await StreamCsvToBlob(processedRecords.ToList(), blobServiceClient, logger);

                response.StatusCode = System.Net.HttpStatusCode.OK;
                await response.WriteStringAsync($"✅ Successfully processed and exported {processedRecords.Count} records.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Processing failed.");
                response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                await response.WriteStringAsync($"Error: {ex.Message}");
            }

            return response;
        }

        private static async Task<string> GetAccessToken(ILogger logger)
        {
            try
            {
                var app = ConfidentialClientApplicationBuilder.Create(ClientId)
                    .WithClientSecret(ClientSecret)
                    .WithAuthority(new Uri($"https://login.microsoftonline.com/{TenantId}"))
                    .Build();

                var result = await app.AcquireTokenForClient(new[] { $"{DynamicsUrl}/.default" }).ExecuteAsync();
                return result.AccessToken;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Token acquisition failed.");
                throw;
            }
        }

        private static HttpClient CreateHttpClient(string token)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Prefer", "odata.maxpagesize=5000");
            return client;
        }

        private static async Task<List<string>> GetAllAccountIds(HttpClient client, ILogger logger)
        {
            var accountIds = new List<string>();
            var url = $"{DynamicsUrl}/api/data/v9.2/accounts?$select=accountid&$filter=cr356_processed eq false";

            while (!string.IsNullOrEmpty(url))
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);
                var records = (JArray)json["value"];
                accountIds.AddRange(records.Select(x => x["accountid"].ToString()));
                url = json["@odata.nextLink"]?.ToString();
            }

            return accountIds;
        }

        private static async Task<List<AccountRecord>> ProcessAccountBatchWithRetry(HttpClient client, List<string> accountIds, ILogger logger)
        {
            var retryPolicy = Policy
                .Handle<Exception>(ex =>
                    ex.Message.Contains("0x80072321") ||
                    ex.Message.Contains("429") ||
                    ex is HttpRequestException)
                .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    (exception, timeSpan, retryCount, context) =>
                    {
                        logger.LogWarning($"⏳ Retry {retryCount} after {timeSpan.TotalSeconds}s due to: {exception.Message}");
                    });

            return await retryPolicy.ExecuteAsync(() =>
                ProcessAccountBatch(client, accountIds, logger));
        }

        private static async Task<List<AccountRecord>> ProcessAccountBatch(HttpClient client, List<string> accountIds, ILogger logger)
        {
            var records = new List<AccountRecord>();
            var selectFields = "accountid,name,telephone1,fax,websiteurl,address1_composite,revenue,numberofemployees," +
                               "preferredcontactmethodcode,industrycode,sic,address1_longitude,address1_latitude," +
                               "customertypecode,cr356_duedate,cr356_processed";

            var filter = string.Join(" or ", accountIds.Select(id => $"accountid eq {id}"));
            var url = $"{DynamicsUrl}/api/data/v9.2/accounts?$select={selectFields}&$filter={filter}";

            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);
            var items = (JArray)json["value"];

            foreach (var item in items)
            {
                var record = new AccountRecord
                {
                    accountid = item["accountid"]?.ToString(),
                    name = item["name"]?.ToString(),
                    telephone1 = item["telephone1"]?.ToString(),
                    fax = item["fax"]?.ToString(),
                    websiteurl = item["websiteurl"]?.ToString(),
                    address1_composite = item["address1_composite"]?.ToString(),
                    revenue = item["revenue"]?.ToObject<decimal?>(),
                    numberofemployees = item["numberofemployees"]?.ToObject<int?>(),
                    preferredcontactmethodcode = item["preferredcontactmethodcode"]?.ToString(),
                    industrycode = item["industrycode"]?.ToString(),
                    sic = item["sic"]?.ToString(),
                    address1_longitude = item["address1_longitude"]?.ToObject<double?>(),
                    address1_latitude = item["address1_latitude"]?.ToObject<double?>(),
                    customertypecode = item["customertypecode"]?.ToString(),
                    cr356_duedate = item["cr356_duedate"]?.ToObject<DateTime?>(),
                    cr356_processed = "TRUE"
                };
                records.Add(record);
            }

            await UpdateProcessedFlagBatch(client, accountIds, logger);
            return records;
        }

        private static async Task UpdateProcessedFlagBatch(HttpClient client, List<string> accountIds, ILogger logger)
        {
            var batchBoundary = $"batch_{Guid.NewGuid()}";
            var changesetBoundary = $"changeset_{Guid.NewGuid()}";
            var batchBody = new StringBuilder();

            batchBody.AppendLine($"--{batchBoundary}");
            batchBody.AppendLine($"Content-Type: multipart/mixed; boundary={changesetBoundary}");
            batchBody.AppendLine();

            int contentId = 1;
            foreach (var accountId in accountIds)
            {
                batchBody.AppendLine($"--{changesetBoundary}");
                batchBody.AppendLine("Content-Type: application/http");
                batchBody.AppendLine("Content-Transfer-Encoding: binary");
                batchBody.AppendLine($"Content-ID: {contentId++}");
                batchBody.AppendLine();
                batchBody.AppendLine($"PATCH {DynamicsUrl}/api/data/v9.2/accounts({accountId}) HTTP/1.1");
                batchBody.AppendLine("Content-Type: application/json;type=entry");
                batchBody.AppendLine();
                batchBody.AppendLine("{\"cr356_processed\": true}");
            }

            batchBody.AppendLine($"--{changesetBoundary}--");
            batchBody.AppendLine($"--{batchBoundary}--");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{DynamicsUrl}/api/data/v9.2/$batch")
            {
                Content = new StringContent(batchBody.ToString())
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/mixed");
            request.Content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", batchBoundary));

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                logger.LogError($"⚠️ Batch update failed: {error}");
                throw new Exception(error);
            }
        }

        private static async Task StreamCsvToBlob(List<AccountRecord> records, BlobServiceClient blobServiceClient, ILogger logger)
        {
            var containerClient = blobServiceClient.GetBlobContainerClient(ContainerName);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

            var blobName = $"account-exports/accounts_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            var blobClient = containerClient.GetBlobClient(blobName);

            await using var stream = new MemoryStream();
            await using var writer = new StreamWriter(stream, leaveOpen: true);
            await using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                await csv.WriteRecordsAsync(records);
            }

            stream.Position = 0;
            await blobClient.UploadAsync(stream, new BlobHttpHeaders { ContentType = "text/csv" });

            logger.LogInformation($"✅ Exported CSV to blob: {blobName}");
        }

        public class AccountRecord
        {
            [Name("Account ID")] public string accountid { get; set; }
            [Name("Name")] public string name { get; set; }
            [Name("Phone")] public string telephone1 { get; set; }
            [Name("Fax")] public string fax { get; set; }
            [Name("Website")] public string websiteurl { get; set; }
            [Name("Address")] public string address1_composite { get; set; }
            [Name("Revenue")] public decimal? revenue { get; set; }
            [Name("Employees")] public int? numberofemployees { get; set; }
            [Name("Preferred Contact")] public string preferredcontactmethodcode { get; set; }
            [Name("Industry")] public string industrycode { get; set; }
            [Name("SIC")] public string sic { get; set; }
            [Name("Longitude")] public double? address1_longitude { get; set; }
            [Name("Latitude")] public double? address1_latitude { get; set; }
            [Name("Customer Type")] public string customertypecode { get; set; }
            [Name("Due Date")] public DateTime? cr356_duedate { get; set; }
            [Name("Processed")] public string cr356_processed { get; set; }
        }
    }

    public static class EnumerableExtensions
    {
        public static IEnumerable<List<T>> Batch<T>(this IEnumerable<T> source, int size)
        {
            var bucket = new List<T>(size);
            foreach (var item in source)
            {
                bucket.Add(item);
                if (bucket.Count == size)
                {
                    yield return bucket;
                    bucket = new List<T>(size);
                }
            }

            if (bucket.Any())
                yield return bucket;
        }
    }
}