/// ASP.NET Core minimal-API glue for the webhook transport, and the ONLY project that references
/// `Microsoft.AspNetCore.App`. `MapTelegramWebhook` is a
/// `[<Extension>]` method so BOTH C# and F# callers can write `app.MapTelegramWebhook(...)`. It
/// follows Microsoft's library-author guidance (extends `IEndpointRouteBuilder`, returns the mapped
/// endpoint's convention builder). The handler verifies the secret-token header, parses the body,
/// and hands the update to the source without waiting for processing: accepted updates return 200,
/// while a saturated bounded queue returns 503 so Telegram can retry.
namespace TgLLM.AspNetCore

open System
open System.IO
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open TgLLM.Webhooks

[<Extension>]
type TelegramWebhookEndpointExtensions =

    /// Map `POST {pattern}` to receive Telegram webhook updates for `source`. `secretToken` must
    /// be non-empty and match the value passed to `setWebhook`.
    [<Extension>]
    static member MapTelegramWebhook
        (endpoints: IEndpointRouteBuilder, pattern: string, source: WebhookUpdateSource, secretToken: string)
        : IEndpointConventionBuilder =
        if String.IsNullOrWhiteSpace secretToken then
            invalidArg (nameof secretToken) "a webhook secret token is required"

        let expected = Some secretToken

        endpoints.MapPost(
            pattern,
            Func<HttpContext, Task>(fun ctx ->
                task {
                    let actual =
                        match ctx.Request.Headers.TryGetValue "X-Telegram-Bot-Api-Secret-Token" with
                        | true, values -> Some(values.ToString())
                        | _ -> None

                    if not (Webhook.verifySecretToken expected actual) then
                        ctx.Response.StatusCode <- StatusCodes.Status401Unauthorized
                    else
                        use reader = new StreamReader(ctx.Request.Body)
                        let! body = reader.ReadToEndAsync ctx.RequestAborted
                        let update = Webhook.parseUpdate body
                        if source.TryIngest update then
                            ctx.Response.StatusCode <- StatusCodes.Status200OK
                        else
                            ctx.Response.StatusCode <- StatusCodes.Status503ServiceUnavailable
                }
                :> Task)
        )
        :> IEndpointConventionBuilder
