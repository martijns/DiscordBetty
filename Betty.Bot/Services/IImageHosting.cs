using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace Betty.Bot.Services
{
    public interface IImageHosting
    {
        Task<string> UploadImageFromUrl(string url);
        Task<string> UploadImageFromFile(string path);
    }

    public class ImgbbImageHosting : IImageHosting
    {
        private readonly ILogger<ImgbbImageHosting> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _imgbbApiKey;

        public ImgbbImageHosting(ILogger<ImgbbImageHosting> logger, IConfiguration config)
        {
            _logger = logger;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.imgbb.com/1/")
            };
            _imgbbApiKey = config["ImgbbApiKey"];
        }

        public async Task<string> UploadImageFromFile(string path)
        {
            string uploadRequestString = "image=" + HttpUtility.UrlEncode(System.Convert.ToBase64String(File.ReadAllBytes(path))) + "&name=" + HttpUtility.UrlEncode(Path.GetFileName(path));
            var res = await _httpClient.PostAsync($"upload?key={_imgbbApiKey}", new StringContent(uploadRequestString, Encoding.UTF8, "application/x-www-form-urlencoded"));
            if (!res.IsSuccessStatusCode)
                throw new ApplicationException($"Failed to call imgbb api: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
            var content = await res.Content.ReadAsStringAsync();
            var jobj = JsonConvert.DeserializeObject<JObject>(content);
            var newurl = jobj["data"]["url"].Value<string>();
            _logger.LogDebug($"Uploaded {path} (~{uploadRequestString.Length} bytes) => {newurl}");
            return newurl;
        }

        public async Task<string> UploadImageFromUrl(string url)
        {
            var res = await _httpClient.PostAsync($"upload?key={_imgbbApiKey}", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                {"image", url}
            }));
            if (!res.IsSuccessStatusCode)
                throw new ApplicationException($"Failed to call imgbb api: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
            var content = await res.Content.ReadAsStringAsync();
            var jobj = JsonConvert.DeserializeObject<JObject>(content);
            var newurl = jobj["data"]["url"].Value<string>();
            _logger.LogDebug($"Uploaded {url} => {newurl}");
            return newurl;
        }

    }

    public class AzureStorageImageHosting : IImageHosting
    {

        private readonly ILogger _logger;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly HttpClient _httpClient;
        private BlobContainerClient _containerClient;

        public AzureStorageImageHosting(ILogger<AzureStorageImageHosting> logger, BlobServiceClient blobServiceClient)
        {
            _blobServiceClient = blobServiceClient;
            _logger = logger;
            _httpClient = new HttpClient();
        }

        private async Task<BlobContainerClient> GetContainer()
        {
            if (_containerClient == null)
            {
                // Update service if on an old version. We'll need "2013-08-15" at least but we'll set it to a more recent version.
                var servicePropertiesResp = await _blobServiceClient.GetPropertiesAsync();
                if (servicePropertiesResp.Value.DefaultServiceVersion != "2019-07-07")
                {
                    _logger.LogInformation($"Updating DefaultServiceVersion for storage account {_blobServiceClient.AccountName} to 2019-07-07");
                    servicePropertiesResp.Value.DefaultServiceVersion = "2019-07-07";
                    await _blobServiceClient.SetPropertiesAsync(servicePropertiesResp.Value);
                }

                // Get and create the container
                var container = _blobServiceClient.GetBlobContainerClient("images");
                await container.CreateIfNotExistsAsync(PublicAccessType.Blob);
                _containerClient = container;
            }
            return _containerClient;
        }

        public async Task<string> UploadImageFromFile(string path)
        {
            var filename = Guid.NewGuid().ToString("n") + "/" + Path.GetFileName(path);
            var info = new FileInfo(path);

            var container = await GetContainer();
            var blob = container.GetBlobClient(filename);
            await blob.UploadAsync(path, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = GetContentTypeForFile(filename)
                }
            });
            _logger.LogDebug($"Uploaded {path} ({info.Length} bytes) => {blob.Uri}");
            return blob.Uri.ToString();
        }

        public async Task<string> UploadImageFromUrl(string url)
        {
            var uri = new Uri(url);
            var filename = Guid.NewGuid().ToString("n") + "/" + Path.GetFileName(uri.AbsolutePath);

            using var stream = await _httpClient.GetStreamAsync(url);

            var container = await GetContainer();
            var blob = container.GetBlobClient(filename);
            await blob.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = GetContentTypeForFile(filename)
                }
            });
            _logger.LogDebug($"Uploaded {url} => {blob.Uri}");
            return blob.Uri.ToString();
        }

        private string GetContentTypeForFile(string filename)
        {
            var ext = Path.GetExtension(filename);
            switch (ext.ToLower())
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".gif":
                    return "image/gif";
                case ".png":
                    return "image/png";
                case ".svg":
                    return "image/svg";
                case ".apng":
                    return "image/apng";
                case ".bmp":
                    return "image/bmp";
                case ".ico":
                case ".cur":
                    return "image/x-icon";
                case ".tif":
                case ".tiff":
                    return "image/tiff";
                case ".webp":
                    return "image/webp";
                case ".webm":
                    return "video/webm";
                case ".wav":
                    return "audio/wave";
                default:
                    return "image/jpeg"; // we are working with images here so lets default back to an image. Even when wrong, the browser should adjust.
            }
        }
    }
}
