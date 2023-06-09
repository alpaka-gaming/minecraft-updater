using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Updater
{
    public static class Extensions
    {
        public static async Task<long> GetLengthAsync(this string url)
        {
            long length;
            using (var client = new HttpClient())
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    length = long.Parse(response.Content.Headers.First(h => h.Key.Equals("Content-Length")).Value.First());

            return length;
        }
    }
}