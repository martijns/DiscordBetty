using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Betty.Bot.Services
{
    public interface ITwitchService
    {
        Task<JObject> GetStream(string user_login);
        Task<JObject> GetStream(int user_id);
        Task<JObject> GetUser(string login);
        Task<JObject> GetUser(int id);
        Task<JObject> GetGame(int id);
        Task Subscribe(int user_id, string callback_uri);
        Task Unubscribe(int user_id, string callback_uri);

    }

    public class TwitchService : ITwitchService
    {
        private readonly ILogger _logger;
        private readonly IKeyValueStore _kvstore;
        private readonly string _twitchAuthBase;
        private readonly string _twitchClientId;
        private readonly string _twitchClientSecret;

        private HttpClient _httpClient;
        private object _lock = 0;
        private string _token = null;

        public TwitchService(ILogger<TwitchService> logger, IKeyValueStore kvstore, IConfiguration config)
        {
            _logger = logger;
            _kvstore = kvstore;
            _twitchAuthBase = config["TwitchAuthBase"];
            _twitchClientId = config["TwitchClientId"];
            _twitchClientSecret = config["TwitchClientSecret"];
        }

        private async Task<HttpClient> GetClient()
        {
            // Fetch token from store
            if (_token == null)
            {
                _token = await _kvstore.GetValueAsync<string>("twitch", "apptoken");
            }

            // Make sure token is not expired (if we have any)
            if (_token != null)
            {
                var tokenobj = JsonConvert.DeserializeObject<JObject>(_token);
                if (DateTime.UtcNow.AddSeconds(300) > tokenobj["expires_at"].Value<DateTime>())
                {
                    _logger.LogDebug($"Twitch token expires (soon) at {tokenobj["expires_at"]}, will fetch new one...");
                    _token = null;
                    _httpClient = null;
                }
            }

            // Fetch token if we don't have one (or it was expired)
            if (_token == null)
            {
                _logger.LogDebug($"No authentication token, trying to fetch one...");
                using (var client = new HttpClient())
                {
                    var res = await client.PostAsync($"{_twitchAuthBase}/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>()
                    {
                        { "grant_type", "client_credentials" },
                        { "client_id", _twitchClientId },
                        { "client_secret", _twitchClientSecret },
                    }));
                    if (!res.IsSuccessStatusCode)
                        throw new ApplicationException($"Failed to obtain twitch access token: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
                    var tokenstr = await res.Content.ReadAsStringAsync();
                    // example: {"access_token":"ncv64caxtcy6oijq21oaqw1u20e43i","expires_in":5272455,"token_type":"bearer"}
                    var tokenobj = JsonConvert.DeserializeObject<JObject>(tokenstr);
                    tokenobj["expires_at"] = DateTime.UtcNow.AddSeconds(tokenobj["expires_in"].Value<int>()).ToString("o");
                    tokenstr = JsonConvert.SerializeObject(tokenobj);
                    await _kvstore.SetValueAsync("twitch", "apptoken", tokenstr);
                    _token = tokenstr;
                    _logger.LogDebug($"Authentication token acquired, expires at {tokenobj["expires_at"]}");
                }
            }

            // Extract token
            var access_token = JsonConvert.DeserializeObject<JObject>(_token)["access_token"].Value<string>();

            // Create client
            if (_httpClient == null)
                lock (_lock)
                    if (_httpClient == null)
                    {
                        _logger.LogDebug($"Creating an HttpClient with the current access_token");
                        _httpClient = new HttpClient();
                        _httpClient.BaseAddress = new Uri("https://api.twitch.tv/helix/");
                        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access_token);
                        _httpClient.DefaultRequestHeaders.Add("Client-ID", _twitchClientId);
                    }

            return _httpClient;
        }

        public async Task<JObject> GetStream(string user_login)
        {
            var client = await GetClient();
            var res = await client.GetAsync($"streams?user_login={user_login}");
            if (!res.IsSuccessStatusCode)
                throw new ApplicationException($"Failed to get stream for {user_login}: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
            return JsonConvert.DeserializeObject<JObject>(await res.Content.ReadAsStringAsync())["data"].FirstOrDefault() as JObject;
        }

        public async Task<JObject> GetStream(int user_id)
        {
            var client = await GetClient();
            var res = await client.GetAsync($"streams?user_id={user_id}");
            if (!res.IsSuccessStatusCode)
                throw new ApplicationException($"Failed to get stream for {user_id}: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
            return JsonConvert.DeserializeObject<JObject>(await res.Content.ReadAsStringAsync())["data"].FirstOrDefault() as JObject;
        }

        public async Task<JObject> GetUser(string login)
        {
            var client = await GetClient();
            var res = await client.GetAsync($"users?login={login}");
            if (!res.IsSuccessStatusCode)
                throw new ApplicationException($"Failed to get stream for {login}: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
            return JsonConvert.DeserializeObject<JObject>(await res.Content.ReadAsStringAsync())["data"].FirstOrDefault() as JObject;
        }

        public async Task<JObject> GetUser(int id)
        {
            var client = await GetClient();
            var res = await client.GetAsync($"users?id={id}");
            if (!res.IsSuccessStatusCode)
                throw new ApplicationException($"Failed to get stream for {id}: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
            return JsonConvert.DeserializeObject<JObject>(await res.Content.ReadAsStringAsync())["data"].FirstOrDefault() as JObject;
        }

        public async Task<JObject> GetGame(int id)
        {
            var client = await GetClient();
            var res = await client.GetAsync($"games?id={id}");
            if (!res.IsSuccessStatusCode)
                throw new ApplicationException($"Failed to get game for {id}: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
            return JsonConvert.DeserializeObject<JObject>(await res.Content.ReadAsStringAsync())["data"].FirstOrDefault() as JObject;
        }

        public async Task Subscribe(int user_id, string callback_uri)
        {
            var client = await GetClient();
            var jobj = new JObject
            {
                ["hub.callback"] = callback_uri,
                ["hub.mode"] = "subscribe",
                ["hub.topic"] = $"https://api.twitch.tv/helix/streams?user_id={user_id}",
                ["hub.lease_seconds"] = 864000,
                ["hub.secret"] = "jSX2vglfDpyNMu357lzNzeWvYDG3yXgp"
            };
            var res = await client.PostAsync("webhooks/hub", new StringContent(JsonConvert.SerializeObject(jobj), Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode)
                throw new ApplicationException($"Failed to subscribe for {user_id}: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
        }

        public async Task Unubscribe(int user_id, string callback_uri)
        {
            var client = await GetClient();
            var jobj = new JObject
            {
                ["hub.callback"] = callback_uri,
                ["hub.mode"] = "unsubscribe",
                ["hub.topic"] = $"https://api.twitch.tv/helix/streams?user_id={user_id}",
                ["hub.lease_seconds"] = 864000,
                ["hub.secret"] = "jSX2vglfDpyNMu357lzNzeWvYDG3yXgp"
            };
            var res = await client.PostAsync("webhooks/hub", new StringContent(JsonConvert.SerializeObject(jobj), Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode)
                throw new ApplicationException($"Failed to subscribe for {user_id}: {res.StatusCode} => {await res.Content.ReadAsStringAsync()}");
        }
    }
}
