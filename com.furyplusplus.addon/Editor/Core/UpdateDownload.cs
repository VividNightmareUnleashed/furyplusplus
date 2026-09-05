using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FuryPlusPlus {
    internal static class UpdateDownload {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) {
            Timeout = Timeout.InfiniteTimeSpan
        };

        internal static async Task<byte[]> Get(string url, int maxBytes, bool releaseAsset, CancellationToken cancellation) {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                timeout.CancelAfter(releaseAsset ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(15));
                var uri = new Uri(url);
                for (var redirects = 0; redirects <= 3; redirects++) {
                    using (var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false)) {
                        var code = (int)response.StatusCode;
                        if (code >= 300 && code < 400) {
                            var location = response.Headers.Location;
                            if (!releaseAsset || location == null || redirects == 3)
                                throw new IOException("Unexpected download redirect.");
                            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                            if (!IsAssetHost(uri)) throw new IOException("Untrusted release download redirect.");
                            continue;
                        }
                        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Download returned HTTP " + code + ".");
                        if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("Download exceeds the size limit.");
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var bytes = new MemoryStream()) {
                            var buffer = new byte[8192];
                            int count;
                            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0) {
                                if (bytes.Length + count > maxBytes) throw new IOException("Download exceeds the size limit.");
                                bytes.Write(buffer, 0, count);
                            }
                            return bytes.ToArray();
                        }
                    }
                }
                throw new IOException("Too many download redirects.");
            }
        }

        internal static bool IsAssetHost(Uri uri) {
            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
                   && (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com"
                       || uri.Host == "objects.githubusercontent.com");
        }
    }
}
