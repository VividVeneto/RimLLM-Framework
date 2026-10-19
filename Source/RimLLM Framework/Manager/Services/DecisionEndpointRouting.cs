using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 決策端點的傳輸層路由。
///
/// Jev SDK 只接受 HTTPS origin（<c>ClientSettings.ValidateBaseAddress</c> 會拒絕其他 scheme），
/// 但本機架設的決策服務（Ollama、Unsloth 等）只提供明文 HTTP。這個類別把兩件事分開處理：
/// 給 SDK 一個通過驗證的 https origin，並在請求真正送上線前把 URI 改寫回玩家設定的實際端點。
///
/// 框架的對話與 embedding 供應商本來就允許明文本機端點（Ollama、LM Studio），
/// 決策這裡沿用同一套規則：玩家自己填的端點就是權威，框架只負責讓 SDK 能跟它說話。
/// </summary>
internal static class DecisionEndpointRouting
{
    /// <summary>
    /// 明文端點沒有金鑰時送出的佔位值。SDK 拒絕空金鑰（<c>TextRules.IsUsableApiKey</c>），
    /// 但本機服務多半不驗證金鑰，所以送一個固定的佔位字串過去。
    /// </summary>
    public const string PlaintextPlaceholderKey = "local";

    /// <summary>端點是否走明文 HTTP。明文端點視為玩家自架的服務，可以不填金鑰。</summary>
    public static bool IsPlaintext(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri)
               && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 給 SDK 的 base address。明文端點換成同 host／port 的 https origin，
    /// 這樣 SDK 的驗證會通過；實際連線仍由 <see cref="PlaintextEndpointRewriteHandler"/> 改寫回明文。
    /// </summary>
    public static Uri GetSdkBaseAddress(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri))
        {
            throw new UriFormatException($"The decision endpoint '{endpoint}' is not an absolute URI.");
        }

        if (!IsPlaintext(endpoint) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new UriFormatException(
                $"The decision endpoint '{endpoint}' must use http:// or https://.");
        }

        if (!IsPlaintext(endpoint))
        {
            return uri;
        }

        // SDK 要求 base address 是沒有路徑的 origin；路徑交給 SDK 自己去接 /v1/systemone。
        return new UriBuilder(Uri.UriSchemeHttps, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }

    /// <summary>實際要連的 origin（明文端點保留原本的 http scheme 與 host／port）。</summary>
    public static Uri GetTransportOrigin(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri))
        {
            throw new UriFormatException($"The decision endpoint '{endpoint}' is not an absolute URI.");
        }

        return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }
}

/// <summary>
/// 把 SDK 產生的 https 請求改寫成玩家設定的明文端點。
///
/// SDK 只會用自己的 base address 組出 <c>/v1/systemone</c> 與 <c>/v1/models</c> 兩個路徑，
/// 因此改寫只需要換掉 scheme 與 host／port，路徑原封不動。
/// </summary>
internal sealed class PlaintextEndpointRewriteHandler : DelegatingHandler
{
    private readonly Uri _transportOrigin;

    public PlaintextEndpointRewriteHandler(Uri transportOrigin)
    {
        _transportOrigin = transportOrigin ?? throw new ArgumentNullException(nameof(transportOrigin));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request?.RequestUri != null)
        {
            request.RequestUri = new Uri(
                $"{_transportOrigin.Scheme}://{_transportOrigin.Authority}{request.RequestUri.PathAndQuery}",
                UriKind.Absolute);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
