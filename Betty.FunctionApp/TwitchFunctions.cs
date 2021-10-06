using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage.Queue;
using Betty.Entities.Twitch;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Betty.FunctionApp
{
    public static class TwitchFunctions
    {
        [FunctionName("TwitchCallback")]
        public static async Task<IActionResult> TwitchCallback(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            [Queue("twitch", Connection = "AzureWebJobsStorage")] CloudQueue queue,
            ILogger log)
        {
            log.LogInformation("TwitchCallback function called");

            log.LogInformation($"Request headers: {JsonConvert.SerializeObject(req.Headers)}");
            log.LogInformation($"Request query: {req.QueryString}");
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            log.LogInformation($"Request body: {requestBody}");
            var jobj = JsonConvert.DeserializeObject<JObject>(requestBody);

            // If this is a challenge, acknowledge
            if (jobj.ContainsKey("challenge"))
            {
                string challenge = jobj["challenge"].Value<string>();
                log.LogInformation($"Received a challenge, replying with '{challenge}'");
                return new ContentResult
                {
                    StatusCode = 200,
                    Content = challenge,
                    ContentType = "text/plain"
                };
            }

            // Validate the signature
            var signingSecret = Environment.GetEnvironmentVariable("TwitchCallbackSigningSecret");
            if (!string.IsNullOrEmpty(signingSecret))
            {
                // We're expecting anything we get to be signed (https://dev.twitch.tv/docs/eventsub#verify-a-signature)
                var signature = req.Headers["Twitch-Eventsub-Message-Signature"].FirstOrDefault();
                if (string.IsNullOrEmpty(signature))
                    throw new ApplicationException($"The received message does not contain a signature. We won't accept its contents.");
                var hashType = signature.Split("=")[0];
                if (hashType != "sha256")
                    throw new ApplicationException($"The received hashtype ({hashType}) does not match what we expected (sha256). We cannot verify the contents.");
                var receivedHash = signature.Split("=")[1];
                using var sha256hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
                var twitchMessageId = req.Headers["Twitch-Eventsub-Message-Id"].FirstOrDefault();
                var twitchMessageTimestamp = req.Headers["Twitch-Eventsub-Message-Timestamp"].FirstOrDefault();
                var hash = sha256hmac.ComputeHash(Encoding.UTF8.GetBytes($"{twitchMessageId}{twitchMessageTimestamp}{requestBody}"));
                var calculatedHash = BitConverter.ToString(hash).Replace("-", "").ToLower();
                if (calculatedHash != receivedHash)
                    throw new ApplicationException($"The received hash ({receivedHash}) does not match the calculated hash ({calculatedHash}). We won't accept this message.");
                log.LogInformation($"The contents of this message have been successfully validated against the signing secret.");
            }

            // Add body to queue
            if (!string.IsNullOrEmpty(requestBody))
            {
                var json = JsonConvert.SerializeObject(new HttpCallbackMessage
                {
                    QueryItems = req.GetQueryParameterDictionary(),
                    RequestBody = requestBody
                });
                try
                {
                    req.GetQueryParameterDictionary();
                    await queue.AddMessageAsync(new CloudQueueMessage(json));
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, $"Error adding message, trying to create queue in case it didn't exist and retrying...");
                    await queue.CreateIfNotExistsAsync();
                    await queue.AddMessageAsync(new CloudQueueMessage(json));
                }
                log.LogInformation($"Successfully added message to queue: {json}");
            }

            return new OkResult();
        }
    }
}
