namespace OwinWorkerRole

open System
open System.Net
open System.Net.Http
open System.Linq
open System.Security.Claims
open System.Web.Http
open System.Web.Http.HttpResource
open Microsoft.Owin
open Microsoft.Owin.Security
open OwinWorkerRole.HashCheck
open OwinWorkerRole.AgentState
open OwinWorkerRole.BusinessLogicActions
open OwinWorkerRole.RestApi

/// REST based interface for communication with Javascript
module WebApi = 

    let createRedirectResponse (request: HttpRequestMessage) relativeUrl =
        let response = request.CreateResponse(HttpStatusCode.Redirect)
        let fullyQualifiedUrl = request.RequestUri.GetLeftPart(UriPartial.Authority)
        response.Headers.Location <- Uri(fullyQualifiedUrl + relativeUrl, UriKind.Absolute)
        response


    // Agent
    let (|RoomAgent|NotFound|) ``request data from agents`` =
        let result =
            getParam<string> ``request data from agents`` "item"
            |> Option.bind(fun agId ->
                    async{
                        return! System.Uri.UnescapeDataString agId |> parseGuid |> roomControl.GiveAgentForGuid
                    } |> Async.RunSynchronously
                )
        match result with
        | None -> NotFound 
        | Some agent -> RoomAgent agent


    /// GET User
    [<Authorize>]
    let getUser (request: HttpRequestMessage) = async {
        match request.GetOwinContext().Authentication with
        | Denied -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | AuthenticatedRequest(_) ->
            let result =
                getParam<string> request "user"
                |> Option.bind (fun id ->
                    let parsed = System.Uri.UnescapeDataString id 
                    match parsed with
                    |IsGuid id -> TableStorage.getUserById id
                    |NotGuid -> TableStorage.getUserByName parsed )
            match result with
            | Some user -> return request.CreateResponse(HttpStatusCode.OK, user |> toApiUser request.RequestUri)
            | None -> return request.CreateResponse(HttpStatusCode.NotFound)
    }

    /// GET Statistics
    [<Authorize>]
    let getStatistics (request: HttpRequestMessage) = async {
        match request.GetOwinContext().Authentication with
        | Denied -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | AuthenticatedRequest(i, _) ->
            let result =
                getParam<string> request "user"
                |> Option.bind(fun agId ->
                        let parsed = System.Uri.UnescapeDataString agId
                        match parsed with
                        |IsGuid id -> TableStorage.getUserById id
                        |NotGuid -> TableStorage.getUserByName parsed )
            match result with
            | Some t -> 
                let isCurrent = t.RowKey = i.ToString("N")
                let agentData = t.RowKey |> parseGuid |> userControl.GiveAgentForGuid
                return request.CreateResponse(HttpStatusCode.OK, (t, agentData, isCurrent) |||> toApiStatistics request.RequestUri)
            | None -> return request.CreateResponse(HttpStatusCode.NotFound)
    }

    /// GET Settings
    [<Authorize>]
    let getSettings (request: HttpRequestMessage) = async {
        match request.GetOwinContext().Authentication with
        | Denied -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | AuthenticatedRequest(uid, uname) ->
            //Fetch some settings. Currently we don't have any... 
            let settings = Some(true)
            match settings with
            | Some value -> return request.CreateResponse(HttpStatusCode.OK, ApiSettings(theme="Default", visualType="Default", computerLevel=1))
            | None -> return request.CreateResponse(HttpStatusCode.NotFound)
    }

    /// GET Room
    [<Authorize>]
    let getRoom (request: HttpRequestMessage) = async {
        match request.GetOwinContext().Authentication, request with
        | Denied, _ -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | _, RoomAgent(agent) -> 
            let room = agent |> toApiRoom request.RequestUri |> Async.RunSynchronously
            return request.CreateResponse(HttpStatusCode.OK, room)
        | _, NotFound -> return request.CreateResponse(HttpStatusCode.NotFound)
    }

    /// DELETE Room
    [<Authorize>]
    let deleteRoom (request: HttpRequestMessage) = async {
        match request.GetOwinContext().Authentication, request with
        | Denied, _ -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | _, RoomAgent agent ->
            async {
                let! res = agent.PostAndAsyncReply(Identify)
                res |> roomControl.DeleteItem
            } |> Async.Start
            return request.CreateResponse(HttpStatusCode.NoContent)
        | _, NotFound -> return request.CreateResponse(HttpStatusCode.NotFound)
    }

    /// GET Users
    [<Authorize>]
    let getUsers (request: HttpRequestMessage) = async {
        match request.GetOwinContext().Authentication with
        | Denied -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | AuthenticatedRequest(_) ->
            let users =
                TableStorage.getUsers() |> Seq.map (toApiUser request.RequestUri)
            return request.CreateResponse(HttpStatusCode.OK, users)

    }

    let (|ContainsItem|MissingItem|) (content: HttpContent) =
        if content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > 0L then
            ContainsItem(content.AsyncReadAs<TableStorage.UserData>())
        else MissingItem
 
    /// POST User
    [<Authorize>]
    let postUser (request: HttpRequestMessage) = async {
        match request.GetOwinContext().Authentication, request.Content with
        | Denied, _ -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | _,ContainsItem(content) ->
            let! user = content
            TableStorage.addUser user |> ignore
            let response = request.CreateResponse(HttpStatusCode.Created, user)
            response.Headers.Location <- user.RowKey |> parseGuid |> userPath |> getUri request.RequestUri
            return response
        | _,MissingItem -> return request.CreateResponse(HttpStatusCode.BadRequest)
    }

    /// GET Login
    [<AllowAnonymous>]
    let getLogin (request: HttpRequestMessage) = async {

        let tempId = 
            //Has user already logged in?
            // If user has already logged-in with a different provider, then add this provider to current user id
            match request.GetOwinContext().Authentication with
            | AuthenticatedRequest(i, n) -> i.ToString("N")
            | Denied -> Guid.NewGuid().ToString("N")

        let (time, hash) = generateStamp(tempId)
        let providers = request.GetQueryNameValuePairs() 
                        |> Seq.filter(fun f -> f.Key = "Provider")
                        |> Seq.map(fun f -> f.Value)
                        |> Seq.toArray
        if providers = Array.empty || providers.Length = 0 then
            let available = request.GetOwinContext().Authentication.GetAuthenticationTypes(fun _ -> true)
                            |> Seq.map(fun c -> c.Caption) 
                            |> Seq.filter(fun c -> not <| String.IsNullOrWhiteSpace(c))
                            |> Seq.toArray
            return request.CreateResponse(HttpStatusCode.OK, "Please give Provider in querystring: " + String.Join(", ", available))
        else
            let provider = Seq.head providers
            let auth = request.GetOwinContext().Authentication
            let authtype = auth.GetAuthenticationTypes(fun p -> p.Caption = provider) |> Seq.head
            let authProps = AuthenticationProperties(RedirectUri = "currentUser/welcome", IsPersistent = true, IssuedUtc = Nullable(time))
            authProps.Dictionary.Add("ClientStamp", time.Ticks.ToString())
            authProps.Dictionary.Add("ClientUserId", tempId)
            authProps.Dictionary.Add("ClientHash", hash)
            auth.Challenge(authProps, authtype.AuthenticationType)
            return request.CreateErrorResponse(HttpStatusCode.Unauthorized, authProps.RedirectUri)
    }

    // GET LOGON: After login
    [<AllowAnonymous>]
    let didLogon (request: HttpRequestMessage) = async {
        let context = request.GetOwinContext()
        let auth = context.Authentication
        let! claims = auth.GetExternalIdentityAsync("External") |> Async.AwaitTask
        
        match claims = null with
        | true -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | false -> 
            let internalClaims = claims.Claims 
                                 |> Seq.filter(fun c -> not <| c.Type.StartsWith("http://schemas.xmlsoap.org/ws/2005/05/identity/claims", StringComparison.Ordinal))
            if not <| internalClaims.Any() then 
                return request.CreateErrorResponse(HttpStatusCode.BadRequest, "Claims not found")
            else
                let identity = ClaimsIdentity(internalClaims, "ApplicationCookie")
                context.Authentication.SignIn(AuthenticationProperties(IsPersistent = true),identity)
            
                let auth = context.Authentication

                let! a = auth.AuthenticateAsync("ApplicationCookie") |> Async.AwaitTask
                return createRedirectResponse request "/menu.html"
     }

    /// GET User
    [<Authorize>]
    let getUserdata (request: HttpRequestMessage) = async {
        match request.GetOwinContext().Authentication with
        | Denied -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | AuthenticatedRequest(uid, uname) -> 
            let userData = TableStorage.getUserById uid
            match userData with
            | Some value -> return request.CreateResponse(HttpStatusCode.OK, value |> toApiUser request.RequestUri)
            | None -> return request.CreateResponse(HttpStatusCode.NotFound)
    }

    /// GET Menu
    [<Authorize>]
    let getMenu (request: HttpRequestMessage) = async {
        match request.GetOwinContext().Authentication with
        | Denied -> return request.CreateErrorResponse(HttpStatusCode.Unauthorized, "Login required")
        | AuthenticatedRequest(uid, uname) -> 
            let userData = TableStorage.getUserByName uname
            match userData with
            | Some value -> return request.CreateResponse(HttpStatusCode.OK, toApiMenu request.RequestUri value uid)
            | None -> return request.CreateResponse(HttpStatusCode.NotFound)
    }

    /// GET Logout
    [<AllowAnonymous>]
    let getLogout (request: HttpRequestMessage) = async {
            request.GetOwinContext().Authentication.SignOut([|"Exteranl";"ApplicationCookie"|])
            return createRedirectResponse request "/login.html"
    }

    let resources = [
            routeResource ("user/{user}") [ get getUser ];
            routeResource ("room/{item}") [ get getRoom; delete deleteRoom ];
            routeResource ("user/{user}/statistics") [ get getStatistics ];

            route usersPath (get getUsers <|> post postUser);
            //Current user login can't be directly updated, but Facebook uses POST-verb
            //when logging to Facebook-application, so GET or POST allowed:
            route "currentUser/login" ( get getLogin <|> post getLogin);
            route "currentUser/welcome" ( get didLogon );
            route "currentUser/logout" ( get getLogout );
            route "currentUser/userdata" ( get getUserdata );
            route "currentUser/menu" ( get getMenu )
            route "currentUser/settings" ( get getSettings )
        ] 

open System.Web.Http
type WebApiConfig() =
    static member Register(config: HttpConfiguration) =
        config
        |> HttpResource.register WebApi.resources
        |> ignore

        config.Formatters.JsonFormatter.SerializerSettings.ContractResolver <-
            Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
