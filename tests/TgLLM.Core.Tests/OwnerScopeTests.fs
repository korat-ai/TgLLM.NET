/// Tests for `OwnerScope` (US1 foundation) and its pure "is this presser allowed?" decision:
/// `Anyone` always allows; `User uid` allows only that exact user and refuses every other
/// (including a missing/anonymous presser). Enforcement of this decision at resolve/dispatch time
/// is US1 (out of scope here) — this file covers only the pure kernel function.
module TgLLM.Core.Tests.OwnerScopeTests

open Expecto
open FsCheck
open TgLLM.Core
open FSharp.UMX

[<Tests>]
let ownerScopeTests =
    testList "OwnerScope" [

        testCase "Anyone allows a normal presser" <| fun _ ->
            Expect.isTrue (OwnerScope.isAllowed Anyone (Some(UMX.tag<userId> 1L))) "Anyone always allows"

        testCase "Anyone allows a missing/anonymous presser too" <| fun _ ->
            Expect.isTrue (OwnerScope.isAllowed Anyone None) "Anyone allows even an unidentifiable presser"

        testCase "User uid allows that exact user" <| fun _ ->
            let uid = UMX.tag<userId> 42L
            Expect.isTrue (OwnerScope.isAllowed (User uid) (Some uid)) "the scoped user is allowed"

        testCase "User uid refuses a different user" <| fun _ ->
            let uid = UMX.tag<userId> 42L
            let other = UMX.tag<userId> 43L
            Expect.isFalse (OwnerScope.isAllowed (User uid) (Some other)) "a different presser is refused"

        testCase "User uid refuses a missing/anonymous presser" <| fun _ ->
            let uid = UMX.tag<userId> 42L
            Expect.isFalse (OwnerScope.isAllowed (User uid) None) "an unidentifiable presser is refused, not allowed by default"

        testProperty "User uid allows exactly that uid and refuses every other uid" <| fun (a: int64, b: int64) ->
            if a = b then
                true // FsCheck happened to draw equal values — nothing to refuse, vacuously true
            else
                let uidA = UMX.tag<userId> a
                let uidB = UMX.tag<userId> b
                OwnerScope.isAllowed (User uidA) (Some uidA) && not (OwnerScope.isAllowed (User uidA) (Some uidB))

        testProperty "Anyone allows any presser, identified or not" <| fun (id: int64) ->
            let presser = Some(UMX.tag<userId> id)
            OwnerScope.isAllowed Anyone presser && OwnerScope.isAllowed Anyone None
    ]
