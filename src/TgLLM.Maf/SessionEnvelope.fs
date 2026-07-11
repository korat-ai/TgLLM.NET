namespace TgLLM.Maf

open System
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open TgLLM.Core

/// Why a persisted session record could not be turned back into a usable session. Surfaced through
/// `IMafSessionObserver.OnSessionRestoreFailed` — the durable-store wiring that produces these
/// values (reading a stored record, gating it against the running `Microsoft.Agents.AI` build)
/// lives elsewhere; this is only the vocabulary the restore path reports.
type SessionFailure =
    /// The stored bytes were not valid JSON, did not match `ConversationEnvelopeDto`'s shape, or
    /// decoded to a JSON `null` — a truncated write, a hand-edited record, or a store bug.
    | CorruptRecord of detail: string
    /// The record's `Format` marker does not match `SessionEnvelope.CurrentFormat` — the record was
    /// written by a build of this library whose on-disk envelope shape this build no longer (or not
    /// yet) agrees with.
    | IncompatibleFormat of found: int * expected: int
    /// The record's `MafVersion` marker's major.minor differs from the running `Microsoft.Agents.AI`
    /// build's own major.minor. `AIAgent`'s own session deserializer accepts whatever JSON it is
    /// handed without checking ITS OWN version drift, so this marker is the only signal that lets a
    /// restore refuse an incompatible record instead of silently mis-restoring it.
    | IncompatibleMafVersion of found: string * running: string
    /// The record's `MeaiVersion` marker's major.minor differs from the running
    /// `Microsoft.Extensions.AI.Abstractions` build's own major.minor. `Microsoft.Agents.AI` and
    /// `Microsoft.Extensions.AI.Abstractions` are two DIFFERENT assemblies that can float
    /// independently of each other (a host pins `Microsoft.Agents.AI` but lets
    /// `Microsoft.Extensions.AI.Abstractions` — the assembly `ToolApprovalRequestContent`/
    /// `FunctionCallContent` actually live in — drift upward via its own transitive resolution), so
    /// an identical `MafVersion` marker alone does not prove the content-type shapes a restored
    /// record's `Approvals` were serialized against still match this process's own build.
    | IncompatibleMeaiVersion of found: string * running: string
    /// The durable store itself could not be reached (connectivity, permissions) — distinct from a
    /// record that WAS read but failed to decode or validate.
    | StoreUnavailable of detail: string

/// The durable identity of one pending approval: enough to reconstruct, with NO live in-memory
/// state, the response the agent expects on resume. `RequestId` (`ToolApprovalRequestContent
/// .RequestId`) and `CallId` (`FunctionCallContent.CallId`, via `ToolCall.CallId`) are DIFFERENT
/// identifiers — the former correlates a decision tap back to its pending request, the latter names
/// the underlying tool call the agent is waiting on — so both are carried, never collapsed into one
/// field (the same distinction `ApprovalDetection.fs` draws between the two). STJ-friendly wire
/// shape, mirroring `ApprovalDescriptor`/`BindingDto`: `null`/`Nullable` stand in for an absent
/// optional, never a second case or union.
[<NoComparison>]
type PersistedApprovalDto =
    { RequestId: string
      CallId: string
      /// The MAF tool's own name, carried for a human-readable restore-failure report — mirrors
      /// `ApprovalDescriptor.Tool`.
      Tool: string
      /// The pending tool call's arguments, pre-rendered to JSON. `null` when the call carried none.
      ArgumentsJson: string | null
      /// `null` ⇒ `Anyone` may decide; a value ⇒ only that `UserId` may — mirrors
      /// `BindingDto.OwnerUserId`.
      OwnerUserId: Nullable<int64>
      /// The chat message the decision keyboard is attached to, needed to edit it in place once the
      /// decision resolves rather than re-sending it.
      MessageId: int64
      /// The decision keyboard's own expiry, if any — mirrors `BindingDto.ExpiresAt`.
      ExpiresAt: Nullable<DateTimeOffset> }

/// One chat's whole durable conversation record: the framework's own opaque serialized session
/// (an `AIAgent` session's own JSON rendering, merely carried here — never parsed or re-shaped by
/// this type), its still-pending approvals, and a format + framework-version marker. The framework's
/// own session deserializer accepts whatever JSON it is handed without complaint about its own
/// version drift, so these two markers are the only signal that lets a restore refuse a record from
/// an incompatible build instead of silently mis-restoring it.
[<NoComparison>]
type ConversationEnvelopeDto =
    { /// This DTO's own on-disk shape version — bumped only when `ConversationEnvelopeDto`'s SHAPE
      /// changes in a way `System.Text.Json` would otherwise misread, independent of `MafVersion`.
      Format: int
      /// The `Microsoft.Agents.AI` assembly version (`SessionEnvelope.currentMafVersion`) that
      /// produced `SessionJson` — compared by major.minor, never exactly, so a patch-level upgrade
      /// of the package does not itself invalidate every already-stored session.
      MafVersion: string
      /// The `Microsoft.Extensions.AI.Abstractions` assembly version
      /// (`SessionEnvelope.currentMeaiVersion`) that produced `Approvals` — the assembly
      /// `ToolApprovalRequestContent`/`FunctionCallContent` themselves live in, tracked separately
      /// from `MafVersion` because the two packages can float independently of each other (see
      /// `SessionFailure.IncompatibleMeaiVersion`'s own doc comment). Compared by major.minor, same
      /// convention as `MafVersion`.
      MeaiVersion: string
      SessionJson: string
      Approvals: PersistedApprovalDto[] }

/// Encodes/decodes `ConversationEnvelopeDto` to/from a durable store's own byte representation, and
/// gates a decoded record against the format/version this build actually understands. Every
/// function here is total: a store's bytes are untrusted input by the time they reach this module
/// (a hand-edited record, bytes from a different build, plain disk corruption), so nothing here ever
/// throws — every failure mode is a `SessionFailure` value.
module SessionEnvelope =

    /// `ConversationEnvelopeDto`'s own on-disk shape version. Bump only when the DTO's SHAPE
    /// changes — a field renamed, removed, or retyped in a way `System.Text.Json` would silently
    /// misread. An additive field does not need a bump: a reader on an older `Format` simply never
    /// sees it.
    [<Literal>]
    let CurrentFormat = 1

    /// `JsonFSharpConverter` is required for a round-trip through an F# record — mirrors
    /// `ApprovalDescriptor.jsonOptions`, with one addition: `allowNullFields = true`. Without it,
    /// FSharp.SystemTextJson's default (`false`) rejects a JSON `null` for any non-`option` field —
    /// exactly what `PersistedApprovalDto.ArgumentsJson` (`string | null`) needs to accept, since a
    /// nullable-annotated field, unlike an `option` field, does not get its own opt-in unwrap/rewrap
    /// handling from the converter.
    let private jsonOptions =
        let options = JsonSerializerOptions()
        options.Converters.Add(JsonFSharpConverter(allowNullFields = true))
        options

    /// The running `Microsoft.Agents.AI` build's own assembly version, e.g. `"1.13.0.0"` — the value
    /// every freshly-persisted `ConversationEnvelopeDto.MafVersion` is stamped with, and the value
    /// `validate` compares an existing record's `MafVersion` against. `""` on the (never observed in
    /// practice) case where the loaded assembly reports no version at all, rather than throwing —
    /// this is a diagnostic string, not a value parsed anywhere else.
    let currentMafVersion: string =
        match typeof<AIAgent>.Assembly.GetName().Version with
        | null -> ""
        | version -> version.ToString()

    /// The running `Microsoft.Extensions.AI.Abstractions` build's own assembly version, e.g.
    /// `"10.6.0.0"` — the value every freshly-persisted `ConversationEnvelopeDto.MeaiVersion` is
    /// stamped with, and the value `validate` compares an existing record's `MeaiVersion` against.
    /// Read off `ChatMessage` (rather than a content type like `ToolApprovalRequestContent`
    /// directly) purely so this module's own `open Microsoft.Extensions.AI` has an unambiguous,
    /// always-present type to anchor on; every type in that assembly shares its version. `""` on the
    /// same never-observed-in-practice null-version case `currentMafVersion` itself guards against.
    let currentMeaiVersion: string =
        match typeof<ChatMessage>.Assembly.GetName().Version with
        | null -> ""
        | version -> version.ToString()

    /// Encodes an envelope to UTF-8 JSON bytes — the shape a durable store persists.
    let encode (envelope: ConversationEnvelopeDto) : byte[] =
        JsonSerializer.SerializeToUtf8Bytes(envelope, jsonOptions)

    /// Decodes a durable store's own bytes back into an envelope. Total over ARBITRARY bytes:
    /// `null`/empty input, malformed JSON, a JSON shape `ConversationEnvelopeDto` cannot bind to, or
    /// a JSON literal `null` all yield `Error (CorruptRecord _)` — never a throw, mirroring
    /// `ApprovalDescriptor.tryParse`'s own totality contract.
    let decode (bytes: byte[] | null) : Result<ConversationEnvelopeDto, SessionFailure> =
        match Option.ofObj bytes with
        | None -> Error(CorruptRecord "no bytes to decode")
        | Some raw when raw.Length = 0 -> Error(CorruptRecord "no bytes to decode")
        | Some raw ->
            try
                match JsonSerializer.Deserialize<ConversationEnvelopeDto>(ReadOnlySpan raw, jsonOptions) with
                | null -> Error(CorruptRecord "decoded to a JSON null")
                | envelope -> Ok envelope
            with
            // DecoderFallbackException derives from ArgumentException, so it must be caught first —
            // an ArgumentException catch ahead of it would make this branch unreachable.
            | :? DecoderFallbackException as ex -> Error(CorruptRecord ex.Message)
            | :? JsonException as ex -> Error(CorruptRecord ex.Message)
            | :? NotSupportedException as ex -> Error(CorruptRecord ex.Message)
            | :? ArgumentException as ex -> Error(CorruptRecord ex.Message)

    /// Reduces a version string to its `"major.minor"` bucket, e.g. `"1.13.0.0"` -> `"1.13"` — the
    /// granularity `validate` compares at, so a patch-level package bump never invalidates an
    /// already-stored session. Total and null-safe: a `null`/unparsable string yields its own,
    /// distinguishable bucket rather than throwing or silently matching every other value.
    let private majorMinor (version: string | null) : string =
        match Option.ofObj version with
        | None -> "?"
        | Some raw ->
            match Version.TryParse raw with
            | true, parsed ->
                match Option.ofObj parsed with
                | Some v -> $"{v.Major}.{v.Minor}"
                | None -> raw
            | false, _ -> raw

    /// Whether `dto` carries every identifier `restoreOrCreate` (`Bridge.fs`) needs to reconstruct a
    /// `PendingApproval` WITHOUT throwing: `ToolApprovalRequestContent`'s and `FunctionCallContent`'s
    /// own constructors (the resolved 1.13.0/10.6.0 binaries) reject a null/whitespace `requestId`/
    /// `callId`/`name` — a persisted `PersistedApprovalDto` with one of those blank is otherwise
    /// well-formed JSON that would still throw deep inside rehydration, past the point this gate
    /// exists to catch it at.
    let private hasUsableIdentifiers (dto: PersistedApprovalDto) : bool =
        not (String.IsNullOrWhiteSpace dto.RequestId)
        && not (String.IsNullOrWhiteSpace dto.CallId)
        && not (String.IsNullOrWhiteSpace dto.Tool)

    /// Gates a decoded envelope against the format/framework-version this build actually
    /// understands, given the CALLER's own running `Microsoft.Agents.AI` version (`currentMafVersion`
    /// in production, an arbitrary value in a test). `Format` must match exactly; `MafVersion` only
    /// down to major.minor (see `majorMinor`). `MeaiVersion` is gated the same way, but against the
    /// ambient `currentMeaiVersion` directly rather than a second parameter: unlike `MafVersion`
    /// (whose whole point is a caller-supplied "running version" a test can vary independently of
    /// whatever `Microsoft.Agents.AI` build actually loaded), there is no equivalent need to fake a
    /// "running" `Microsoft.Extensions.AI.Abstractions` version — a mismatch test only needs to vary
    /// the RECORD's own `MeaiVersion`, never this process's.
    ///
    /// Also refuses the two `Approvals` shapes that are otherwise well-formed JSON but would THROW
    /// deep inside `Bridge.fs`'s own `rehydrate` (a null `Approvals` array itself — a bare
    /// `for dto in env.Approvals` loop over `null` throws `NullReferenceException` — or an approval
    /// missing one of `hasUsableIdentifiers`' three fields) rather than being caught by anything: a
    /// record that got this far already passed the two remove-on-failure gates around
    /// `decodeAndValidate` in `restoreOrCreate`, so a throw AFTER this point would never remove the
    /// poisoned record, bricking the chat for good (every later turn re-faulting the same way,
    /// forever, until the store's own idle eviction eventually catches up). Deliberately NOT
    /// exhaustive over every way `rehydrate` could still throw (e.g. `ArgumentsJson` holding valid
    /// but non-object JSON) — those residual cases are cheap to catch only by actually attempting the
    /// rehydration, which is `restoreOrCreate`'s own job, wrapped in the SAME try/with as
    /// `DeserializeSessionAsync`.
    let validate (runningVersion: string) (envelope: ConversationEnvelopeDto) : Result<ConversationEnvelopeDto, SessionFailure> =
        if envelope.Format <> CurrentFormat then
            Error(IncompatibleFormat(envelope.Format, CurrentFormat))
        elif majorMinor envelope.MafVersion <> majorMinor runningVersion then
            Error(IncompatibleMafVersion(envelope.MafVersion, runningVersion))
        elif majorMinor envelope.MeaiVersion <> majorMinor currentMeaiVersion then
            Error(IncompatibleMeaiVersion(envelope.MeaiVersion, currentMeaiVersion))
        elif isNull (box envelope.Approvals) then
            Error(CorruptRecord "Approvals is null")
        elif envelope.Approvals |> Array.exists (hasUsableIdentifiers >> not) then
            Error(CorruptRecord "an approval carries a null/empty RequestId, CallId, or Tool")
        else
            Ok envelope

    /// `decode` followed by `validate` against the running version — the one call a restore path
    /// actually needs.
    let decodeAndValidate (runningVersion: string) (bytes: byte[]) : Result<ConversationEnvelopeDto, SessionFailure> =
        decode bytes |> Result.bind (validate runningVersion)
