namespace OwinWorkerRole

open Microsoft.Owin.Security
open System
open System.Linq
open System.Security.Principal
open System.Security.Cryptography
open System.Security.Claims
open System.Text

/// Optional module just to make NSA busy. ;-)
/// Can you trust any build-in-security without any extra-modifications anymore?
module HashCheck =

    let private ``app specific configured hash secret key`` = System.Configuration.ConfigurationManager.AppSettings.["HashSecretBegin"]
    let private ``app spesific amount of random chars to insert before hash`` = System.Configuration.ConfigurationManager.AppSettings.["RandomHashKeyBeginLength"] |> int
    let private ``app spesific amount of random chars to insert after hash`` = System.Configuration.ConfigurationManager.AppSettings.["RandomHashKeyEndLength"] |> int

    /// Random string with characters [0-9][a-z][A-Z]
    let private randomString length =
        //Like System.Random() but more random. Just for fun. :)
        use provider = new RNGCryptoServiceProvider()
        let randomArray maxValue =
            let data = Array.zeroCreate length
            do provider.GetBytes data
            let modulo i = i % (maxValue+1)
            data |> Array.map (Convert.ToInt32 >> modulo)

        let fourArrays = [3; 10; 25; 25] |> List.map randomArray
        let chars = Array.init length (fun i -> 
            match fourArrays.[0].[i] with
            | 0 -> 48 + fourArrays.[1].[i] //0-9
            | 1 -> 65 + fourArrays.[2].[i] //A-Z
            | _ -> 97 + fourArrays.[3].[i] //a-z
            |> (char))
        new string(chars)

    let private ``calculate SHA384 hash`` (bstr:string) =
        let sha = SHA384Managed.Create()
        let res = bstr 
                  |> Encoding.UTF8.GetBytes 
                  |> sha.ComputeHash
                  |> Convert.ToBase64String
        randomString(``app spesific amount of random chars to insert before hash``)+res+randomString(``app spesific amount of random chars to insert after hash``)

    let ``second part of hash secret`` = 
            Seq.unfold (fun (state1, state2) ->
                    Some(state1 + state2, (state2, 0.8 * state1 - 0.6 * state2))) (4.0,-1.0)
                |> Seq.map(fun i -> (i*12.0 |> Math.Floor |> int)+48 |> char)
                |> Seq.take 6
                |> Array.ofSeq
                |> String.Concat

    let generateStamp clientUserId =
        let time = System.DateTimeOffset.Now
        time, ``calculate SHA384 hash`` ((``app specific configured hash secret key`` + ``second part of hash secret`` + clientUserId + "-" + time.UtcTicks.ToString()).Trim())

    let checkHash someId (time:DateTimeOffset) (hash1:string) =
        let ripHash (h:string) = h.Substring(``app spesific amount of random chars to insert before hash``, h.Length-``app spesific amount of random chars to insert before hash``-``app spesific amount of random chars to insert after hash``)
        let ok =
            time.UtcTicks < DateTimeOffset.Now.AddHours(5.0).UtcTicks 
            && time.UtcTicks > DateTimeOffset.Now.AddHours(-5.0).UtcTicks
            && ripHash(hash1) = ripHash(``calculate SHA384 hash`` ((``app specific configured hash secret key`` + ``second part of hash secret`` + someId + "-" + time.UtcTicks.ToString()).Trim()))
        ok

    let (|AuthClaims|NotFound|) (userClaims : Claim seq) =
        if userClaims = null || not <| userClaims.Any() then
            NotFound
        else
            let findClaimValue v = 
                let cl = userClaims.FirstOrDefault(fun f -> f.Type = v)
                if cl = null then String.Empty else cl.Value
            let id = findClaimValue "ClientUserId"
            let timeOk, itime = findClaimValue "LoginTime" |> Int64.TryParse
            let hash = findClaimValue "ClientHash"
            let name = findClaimValue "UserName"
            let eidvalue = findClaimValue "ExternalId"
            let knownId = findClaimValue "KnownId"
            if(not timeOk) then
                NotFound
            else
                let time = DateTimeOffset(DateTime(itime))
                let idval = eidvalue + "|" + knownId
                match (checkHash idval time hash) && String.notNullOrEmpty id with
                | false -> NotFound
                | true -> AuthClaims (knownId, name)


    let (|Denied|AuthenticatedRequest|) (auth:IAuthenticationManager) =
        if (auth = null || auth.User = null) then Denied
        else
            match auth.User.Claims with
            | AuthClaims(id, name) -> AuthenticatedRequest(id|>parseGuid, name)
            | NotFound -> Denied

    let checkAuthConn (principal:IPrincipal) =
        let claims = principal.Identity :?> ClaimsIdentity
        if(claims = null) then failwith("Authentication failed. ")
        match claims.Claims with
        | AuthClaims(id, name) -> id, name
        | _ -> failwith("Authentication failed. ")
