using System.IO;
using System.Net;
using System.Text;

namespace RadioChatter.Speech
{
    /// <summary>Shared request plumbing for the localhost TTS/STT sidecar.</summary>
    internal static class SidecarHttp
    {
        public static string BuildUrl(Config config, string path)
        {
            return config.SidecarUrl.Value.TrimEnd('/') + path;
        }

        /// <summary>POST a JSON body and return the raw response bytes. Throws on HTTP or
        /// network errors, exactly like the underlying HttpWebRequest.</summary>
        public static byte[] PostJson(string url, string jsonBody, string accept, int timeoutMs)
        {
            byte[] payload = Encoding.UTF8.GetBytes(jsonBody);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = accept;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.ContentLength = payload.Length;

            using (Stream stream = request.GetRequestStream())
                stream.Write(payload, 0, payload.Length);

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream responseStream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                responseStream.CopyTo(memory);
                return memory.ToArray();
            }
        }
    }
}
