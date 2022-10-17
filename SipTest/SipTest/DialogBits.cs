using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SipTest
{
    internal class DialogBits
    {
        private HttpClient client;

        public DialogBits(string baseUri)
        {
            HttpClientHandler handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            client = new HttpClient(handler);
            client.BaseAddress = new Uri(baseUri);
        }

        public async Task<bool> Reachable()
        {
            HttpResponseMessage response = await client.GetAsync("");
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("No success");
                return false;
            }
            Responses.Reachable? reachable = JsonSerializer.Deserialize<Responses.Reachable>(await response.Content.ReadAsStringAsync());
            return reachable?.success ?? false;
        }

        public async Task<string> GetLanguage(string from)
        {
            var payload = new Requests.GetLanguage { from = from };
            HttpResponseMessage response = await client.PostAsJsonAsync("", payload);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Error while querying a language from Dialogbits for ${from}");
            }
            Responses.GetLanguage? reachable = JsonSerializer.Deserialize<Responses.GetLanguage>(await response.Content.ReadAsStringAsync());
            if (reachable == null || reachable.language?.Length == 0)
            {
                throw new Exception($"Did not get a language from Dialogbits for ${from}");
            }
            return reachable.language!;
        }

        public async Task<Responses.Actions?> OnCallInitiated(string sessionId, string from)
        {
            var payload = new Requests.CallInitiated { from = from, sessionId = sessionId };
            HttpResponseMessage response = await client.PostAsJsonAsync("", payload);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("Error while sending CallInitiated");
                return new Responses.Actions();
            }
            return JsonSerializer.Deserialize<Responses.Actions>(await response.Content.ReadAsStringAsync());
        }

        public async Task<Responses.Actions?> OnText(string sessionId, string text)
        {
            var payload = new Requests.Text { text = text, sessionId = sessionId };
            HttpResponseMessage response = await client.PostAsJsonAsync("", payload);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("Error while sending OnText");
                return new Responses.Actions();
            }
            return JsonSerializer.Deserialize<Responses.Actions>(await response.Content.ReadAsStringAsync());
        }

        public async Task<Responses.Actions?> OnDtmf(string sessionId, string touchTone)
        {
            var payload = new Requests.Dtmf { touchTone = touchTone, sessionId = sessionId };
            HttpResponseMessage response = await client.PostAsJsonAsync("", payload);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("Error while sending OnDtmf");
                return new Responses.Actions();
            }
            return JsonSerializer.Deserialize<Responses.Actions>(await response.Content.ReadAsStringAsync());
        }
    }

    namespace Responses
    {
        internal class Reachable
        {
            public bool success { get; set; }
        }

        internal class GetLanguage
        {
            public string? language { get; set; }
        }

        internal class Actions
        {
            public List<Action>? actions { get; set; }

            public string? getText()
            {
                if (actions == null)
                {
                    return null;
                }

                var text = "";
                foreach (var action in actions)
                {
                    if (action?.type == "text")
                    {
                        text += (action.text ?? "") + " ";
                    }
                }
                text = text.Trim();
                return text.Length == 0 ? null: text;
            }

            public bool hasHangup()
            {
                if (actions == null)
                {
                    return false;
                }

                foreach (var action in actions)
                {
                    if (action?.type == "hangup")
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        internal class Action
        {
            public string? type { get; set; }
            public string? text { get; set; }
        }
    }

    namespace Requests {
        internal abstract class BaseRequest
        {
            public string? sessionId { get; set;  }
        }

        internal class GetLanguage
        {
            public string? from { get; set; }
        }

        internal class CallInitiated : BaseRequest
        {
            public string? from { get; set; }
        }

        internal class Text : BaseRequest
        {
            public string? text { get; set; }
        }

        internal class Dtmf : BaseRequest
        {
            public string? touchTone { get; set; }
        }
    }
}
