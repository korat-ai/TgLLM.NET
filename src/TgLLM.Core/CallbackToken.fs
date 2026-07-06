namespace TgLLM.Core

open System

/// The opaque value the library writes into `callback_data` to identify a button. 16 random bytes
/// (a `Guid`'s worth) base64url-encoded, well under the Bot API's 1–64 BYTE `callback_data` limit.
/// The codec is pure and total, feeding the unknown/stale-press path.
[<Struct>]
type CallbackToken = private CallbackToken of string

module CallbackToken =

    /// Base64url alphabet (RFC 4648 §5), unpadded — `callback_data` has no reason to waste bytes
    /// on `=` padding, and unpadded base64url is what most Bot API client libraries expect.
    let private encode (bytes: byte[]) : string =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private tryDecode (s: string) : byte[] option =
        try
            let standard = s.Replace('-', '+').Replace('_', '/')
            let pad = (4 - standard.Length % 4) % 4
            Some(Convert.FromBase64String(standard + String('=', pad)))
        with _ ->
            None

    /// Deterministic — the same `Guid` always yields the same token (FsCheck-drivable).
    let ofGuid (guid: Guid) : CallbackToken = CallbackToken(encode (guid.ToByteArray()))

    let generate () : CallbackToken = ofGuid (Guid.NewGuid())

    /// Total: any string, including `null`, garbage, or non-canonical base64url, returns a
    /// `voption` rather than throwing. Only strings that are the canonical encoding of exactly
    /// 16 bytes (i.e. exactly what `ofGuid`/`value` would produce) parse to `ValueSome`.
    /// `s` is annotated nullable because this is a public API boundary — the whole point of
    /// totality here is that even a caller passing raw, untrusted, possibly-null input gets a
    /// `voption` back, never an exception (Always-Rule 5).
    let tryParse (s: string | null) : CallbackToken voption =
        match s |> Option.ofObj with
        | None -> ValueNone
        | Some s when s.Length = 0 -> ValueNone
        | Some s ->
            match tryDecode s with
            | Some bytes when bytes.Length = 16 ->
                let canonical = encode bytes
                if canonical = s then ValueSome(CallbackToken s) else ValueNone
            | _ -> ValueNone

    let value (CallbackToken s) : string = s
