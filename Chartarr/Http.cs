using System;
using System.Net.Http;
using System.Reflection;

namespace Chartarr
{
    // one http client for the whole plugin. musicbrainz requires a
    // descriptive user-agent; nothing else we talk to minds one.
    internal static class PluginHttp
    {
        public static readonly HttpClient Client = Create();

        private static HttpClient Create()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60),
                MaxResponseContentBufferSize = 32 * 1024 * 1024,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"Lidarr.Plugin.Chartarr/{version} (+https://github.com/alperien/chartarr.plugin)");
            return client;
        }
    }
}
