namespace OwinWorkerRole

open Owin
open Microsoft.Owin
open Microsoft.Owin.Security
open Microsoft.AspNet.SignalR
open Microsoft.AspNet.SignalR.Hubs
open System
open System.Configuration
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Web.Http
open System.Threading.Tasks
open System.Security.Claims
open OwinWorkerRole.HashCheck
open OwinWorkerRole.BusinessLogicActions

open Microsoft.Owin.Security.Cookies
open Microsoft.Owin.Security.Facebook
open Microsoft.Owin.Security.MicrosoftAccount
open Microsoft.Owin.Security.Google
open Microsoft.Owin.Hosting

open Fog.Storage.Blob
open Microsoft.WindowsAzure.StorageClient
open System.Diagnostics

/// Run Owin Server and configure authentications
/// Also deploy this server, e.g. www-root to Windows Azure Blob Storage.
module MyServer =

    let hubConfig = HubConfiguration(EnableDetailedErrors = true, EnableJavaScriptProxies = true)
    
    // WWW-root paths and files to deploy to www-root when starting server 
    let wwwPathAndZipFiles = [(ConfigurationManager.AppSettings.["StaticFilesPath"], "wwwroot.zip")]

    let schema = "http://www.w3.org/2001/XMLSchema#string"

    let private wwwDeployment = ConfigurationManager.AppSettings.["DeployStaticFilesTo"]

    // These comes from app.config
    let private fbAppId = ConfigurationManager.AppSettings.["FacebookAppId"]
    let private fbAppSecret = ConfigurationManager.AppSettings.["FacebookAppSecret"]

    let private ``internal authentication type`` = "ApplicationCookie"
    let private ``external authentication type`` = "External"

    let createClaims provider name eid id (authProps:AuthenticationProperties) =
            let providerTxt = 
                match provider with
                | Facebook  -> "Facebook"
                | Google    -> "Google"
                
            let time, hash = eid + "|" + id |> generateStamp
            let claims = [|
                "ClientUserId", authProps.Dictionary.["ClientUserId"];
                "ClientHash", hash;
                "KnownId", id
                "LoginTime", time.Ticks.ToString();
                "ExternalId", eid;
                "IdProvider", providerTxt;
                "UserName", name |] |> Array.map (fun (k, v) -> Claim(k,v))
            let identity = ClaimsIdentity(claims, ``internal authentication type``)
            claims

    let setUser provider name eid mail (authProps:AuthenticationProperties) ip =
        match authProps=null with
        | true -> Array.empty
        | false -> 
            let idOk, tempId = authProps.Dictionary.["ClientUserId"] |> Guid.TryParse
            let timeOk, itime = authProps.Dictionary.["ClientStamp"] |> Int64.TryParse
            let hash = authProps.Dictionary.["ClientHash"]
            if (timeOk && idOk && checkHash (tempId.ToString("N")) (DateTimeOffset(DateTime(itime))) hash) then

                let fb, g = 
                    match provider with
                    | Facebook  -> eid, ""
                    | Google    -> "", eid
                let user = 
                    TableStorage.UserData(
                        PartitionKey = TableStorage.partition,
                        RowKey = tempId.ToString("N"),
                        Created = itime.ToString(),
                        ExternalIdFacebook = fb,
                        ExternalIdGoogle = g,
                        Name = name,
                        Email = mail,
                        IPAddress = ip
                    ) |> TableStorage.externalAddOrUpdateUser
                match user.Banned with
                | true -> Array.empty
                | false -> 
                    let userid = user.RowKey |> parseGuid
                    (name, user.UsageCount, user.Score, user.Level, obj()) |> LogOnToServer |> NewUserExistingId userid name |> ignore
                    let claims = createClaims provider name eid user.RowKey authProps
                    claims
            else
                Array.empty

    let fetchExternalClaim claimName (identitityClaims:ClaimsIdentity) =
        let h = identitityClaims.Claims 
                |> Seq.filter(fun i -> i.Type.Contains(claimName))
                |> Seq.map(fun i -> i.Value)
                |> Seq.head
        h

    let fetchIP (req:IOwinRequest) = 
        let okIp, ipAddr = req.Environment.TryGetValue("server.RemoteIpAddress");
        match okIp with |true -> ipAddr.ToString() | false -> String.Empty


    let mutable applicationDirectory = AppDomain.CurrentDomain.BaseDirectory 

    type MyWebStartup() =

        member x.Configuration(app:Owin.IAppBuilder) =

            // For debug:
            Owin.ErrorPageExtensions.UseErrorPage(app) |> ignore

            // Auth cookies
            app.UseCookieAuthentication(
                CookieAuthenticationOptions(
                    AuthenticationType = ``internal authentication type``,
                    AuthenticationMode = AuthenticationMode.Active,
                    LoginPath = PathString("/login.html"),
                    LogoutPath = PathString("/currentUser/logout"),
                    CookieSecure = CookieSecureOption.SameAsRequest,
                    CookieName = CookieAuthenticationDefaults.CookiePrefix + ``internal authentication type``,
                    ExpireTimeSpan = TimeSpan.FromMinutes(30.0),
                    SlidingExpiration = true
            )) |> ignore

            app.SetDefaultSignInAsAuthenticationType(``external authentication type``) |> ignore

            app.UseCookieAuthentication(
                CookieAuthenticationOptions(
                    AuthenticationType = ``external authentication type``,
                    AuthenticationMode = AuthenticationMode.Passive,
                    CookieSecure = CookieSecureOption.SameAsRequest,
                    CookieName = CookieAuthenticationDefaults.CookiePrefix + ``external authentication type``,
                    ExpireTimeSpan = TimeSpan.FromMinutes(30.0)
            )) |> ignore

            // SignalR
            app.MapSignalR(hubConfig) |> ignore
            
            // Facebook account
            // https://developers.facebook.com/apps/
            app.UseFacebookAuthentication(
                FacebookAuthenticationOptions(
                    AppId = fbAppId,
                    AppSecret = fbAppSecret,
                    Provider = FacebookAuthenticationProvider(
                        OnAuthenticated = 
                            fun contextf ->
                                let name = fetchExternalClaim "facebook:name" contextf.Identity
                                let eid = fetchExternalClaim "identifier" contextf.Identity 
                                let ip = fetchIP contextf.Request
                                let userlink = 
                                    match String.IsNullOrEmpty contextf.Email with
                                    | true -> fetchExternalClaim "link" contextf.Identity 
                                    | false -> contextf.Email
                                setUser Facebook name eid userlink contextf.Properties ip
                                |> contextf.Identity.AddClaims
                                Task.FromResult(0) :> Task
                        ))) |> ignore

            // Google account
            app.UseGoogleAuthentication(
                GoogleAuthenticationOptions(
                    Provider = GoogleAuthenticationProvider(
                        OnAuthenticated = 
                            fun contextg ->
                                let name = contextg.Identity.Name
                                let eid = fetchExternalClaim "identifier" contextg.Identity 
                                let email = fetchExternalClaim "email" contextg.Identity 
                                let ip = fetchIP contextg.Request
                                setUser Google name eid email contextg.Properties ip
                                |> contextg.Identity.AddClaims
                                Task.FromResult(0) :> Task
                            ))
                        ) |> ignore

            // REST Web Api
            use httpConfig = new HttpConfiguration()
            OwinWorkerRole.WebApiConfig.Register httpConfig
            app.UseWebApi(httpConfig) |> ignore

            let deployZip www zip =
                // File server, deploy www
                let zipFile = applicationDirectory + @"\wwwroot\" + zip
                let deploymentDir = applicationDirectory + @"\" + www

                if Directory.Exists(deploymentDir) then
                    Directory.Delete(deploymentDir, true)
                do ZipFile.ExtractToDirectory(zipFile, deploymentDir)
                          
            let deployToBlobStorage(www,zip) =
                deployZip www zip

                let selectMimeType (file:string) =
                    let dot = file.LastIndexOf(".", StringComparison.Ordinal)
                    if dot = -1 then 
                        ""
                    else
                        match dot |> file.Substring with
                        | ".html" | ".htm" -> "text/html"
                        | ".js" -> "application/javascript"
                        | ".css" -> "text/css"
                        | ".jpg" | ".jpeg" -> "image/jpeg"
                        | ".png" -> "image/png"
                        | ".gif" -> "image/gif"
                        | ".ico" -> "image/x-icon"
                        | ".txt" -> "text/plain"
                        | _ -> ""

                // Deploy to Azure Blob:
                let containerName = www
                let deploymentDir = applicationDirectory + @"\" + www
                Directory.EnumerateFiles(deploymentDir,"*.*",SearchOption.AllDirectories) 
                |> Seq.iter(fun filename -> 

                    let fileWithoutPath = filename.Replace(deploymentDir + @"\", "")
                    let blob = GetBlobReference containerName fileWithoutPath
                    blob.DeleteIfExists() |> ignore
                    blob.Properties.ContentType <- selectMimeType fileWithoutPath
                    blob.UploadFile(filename)
                )

                // Use OWIN middleware to route requests to Azure Blob Storage
                // You could also load static files directly from Azure Blob Storage
                // but then the WorkerRole would be in different web-site and then you
                // would have to enable CORS to get JavaScript Ajax requests from Blob to Worker role.
                app.Use(fun (context:IOwinContext) (next:Func<Task>) ->

                    let path =
                        match context.Request.Path = PathString.Empty || context.Request.Path.Value = "/" with
                        | true -> "login.html"
                        | false -> context.Request.Path.Value.Substring(1)

                    match String.IsNullOrEmpty (selectMimeType path) with 
                    | true -> next.Invoke()
                    | false -> 
                        async {
                            
                            let blob = GetBlobReference containerName path
                            
                            context.Response.StatusCode <- (int)HttpStatusCode.OK
                            context.Response.ContentType <- selectMimeType path

                            let filename = blob.Name

                            let task = 
                                try
                                    blob.DownloadByteArray()
                                with
                                    | :? StorageClientException as se when se.ErrorCode = StorageErrorCode.ResourceNotFound || se.ErrorCode = StorageErrorCode.BlobNotFound -> 
                                        log ("File not found: " + filename) "Information"
                                        context.Response.StatusCode <- (int)HttpStatusCode.NotFound
                                        Array.empty                               
                                    | e ->
                                        log ("Error loading " + filename + ": " + e.ToString()) "Error"
                                        context.Response.StatusCode <- (int)HttpStatusCode.InternalServerError
                                        //failwith(e.Message)
                                        Array.empty                               
                                |> context.Response.WriteAsync

                            return task
                        } |> Async.StartAsTask :> Task
                ) |> ignore
                ()

            match wwwDeployment with
            |"BLOB" -> wwwPathAndZipFiles |> List.iter deployToBlobStorage
            |"NONE" -> ()
            |_ -> failwith("app.config: configuration/appSettings: DeployStaticFilesTo not set")

            OwinWorkerRole.TableStorage.initTables() |> ignore
            ()

    [<assembly: Microsoft.Owin.OwinStartup(typeof<MyWebStartup>)>]
    do()