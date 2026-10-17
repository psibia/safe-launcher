// Запросы, как в ConnectionTester.RequestsAsync лаунчера: HttpClientHandler и WebProxy с именем и ключом (или без них).
// Для шагов check-via-core-* стенда. Запуск: Probe <http|http-pre|socks> <порт прокси> <имя|-> <ключ|-> <адрес> [адрес ...]
// http-pre — заголовок Proxy-Authorization сразу, без ожидания ответа 407.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

var mode = args[0];
var proxy = new WebProxy($"{(mode == "socks" ? "socks5" : "http")}://127.0.0.1:{args[1]}");
if (args[2] != "-")
    proxy.Credentials = new NetworkCredential(args[2], args[3]);
using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true, AllowAutoRedirect = false };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
client.DefaultRequestHeaders.UserAgent.ParseAdd("SafeLauncher/2.1");
if (mode == "http-pre")
    client.DefaultRequestHeaders.ProxyAuthorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(args[2] + ":" + args[3])));
foreach (var url in args.Skip(4))
{
    var watch = Stopwatch.StartNew();
    try
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"{url}: {(int)response.StatusCode} in {watch.ElapsedMilliseconds} ms: {body.Replace("\n", " ")[..Math.Min(70, body.Length)]}");
    }
    catch (Exception ex)
    {
        var chain = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            chain.Add(e.GetType().Name + ": " + e.Message);
        Console.WriteLine($"{url}: error in {watch.ElapsedMilliseconds} ms: {string.Join(" <- ", chain)[..Math.Min(300, string.Join(" <- ", chain).Length)]}");
    }
}
