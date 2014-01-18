namespace OwinWorkerRole

open System
open Microsoft.AspNet.SignalR
open Microsoft.AspNet.SignalR.Hubs
open System.Security.Principal
open System.Security.Claims
open Dynamic
open OwinWorkerRole.AgentState
open OwinWorkerRole.BusinessLogicActions
open OwinWorkerRole.BusinessLogicValidation
open OwinWorkerRole.HashCheck
open OwinWorkerRole.RestApi
open System.Runtime.InteropServices

// http://www.asp.net/signalr/overview/signalr-20/hubs-api/working-with-groups

/// SignalR for real-time bi-directional communication over WebSockets (etc.)
/// Here is one hub per one interactive HTML-page (and methods for possible actions in that page)
/// for each pages that have some kind of real-time updating collections
/// This also shows F# in object-oriented programming...
module SignalRHub =

    let defaultRoomName user = user + "'s room"

    type HubBase() as this =
        inherit Hub()

        let getSignalRUrl path = 
            match this.Path with
            | Some baseuri -> getUri baseuri path
            | None -> Uri("", UriKind.Relative)

        let checkPrincipal (principal:IPrincipal) =
            if(principal <> null) then 
                let id, name = checkAuthConn principal
                let userid = Id(id |> parseGuid, name)
                this.UserId <- userid
                this.ApiUser <- ApiUser(id=id, link=(userid.AgentId |> userPath |> getSignalRUrl), name=name)
                this.Groups.Add(this.Context.ConnectionId, id) |> doTaskAsync
                true
            else false

        member val internal UserId = Unchecked.defaultof<UserId> with get, set
        member val internal ApiUser = ApiUser(String.Empty,null,String.Empty) with get, set
        member val private Path = None with get, set

        member internal this.checkPrincipals() = checkPrincipal this.Context.User
        member internal this.checkAuthPrincipals() = String.notNullOrEmpty this.ApiUser.name || checkPrincipal this.Context.User
        member internal this.getSignalRUri path = getSignalRUrl path

        member private this.Connect() =
            let conn = this.Context.ConnectionId
            if this.checkPrincipals() then 
                // All connections of a single user (id) is managed by one group named after the user id 
                this.Groups.Add(conn, this.UserId.AgentIdString) |> doTaskAsync

        abstract member Disconnect : unit -> unit
        default this.Disconnect() =
            if this.checkAuthPrincipals() then
                this.Groups.Remove(this.Context.ConnectionId, this.UserId.AgentIdString) |> doTask

        override this.OnConnected() =
            this.Path <- try Some(this.Context.Request.Url) with | :? ObjectDisposedException -> None
            this.Connect()
            base.OnConnected()

        override this.OnReconnected() =
            this.Connect()
            base.OnReconnected()

        override this.OnDisconnected() =
            // We have to disconnect manually as SignalR seems to auto-disconnect all the time
            //this.Disconnect()
            base.OnDisconnected()

    /// RoomHub for room-page
    type RoomHub() as this = 
        inherit HubBase()

        let fetchIds() = 
            let qs = this.Context.QueryString.["rid"]
            this.Context.ConnectionId,             
            let room = 
                match String.IsNullOrEmpty qs with
                | true -> qs
                | false -> qs.Replace("\"", "").Replace("-", "")
            if String.notNullOrEmpty room then 
                this.RoomId <- Id(room |> parseGuid, (this.ApiUser:ApiUser).name |> defaultRoomName) |> Some
            room

        let ``do if connected`` func =
            if this.checkPrincipals() then 
                let ids = fetchIds()
                this.RoomId |> Option.iter(ids |> func)

        let connect() = 
            ``do if connected`` (fun (connId, roomId) room ->
                this.Clients.OthersInGroup(roomId)?visitorJoin(this.ApiUser) |> doTaskAsync
                ``post action to user and room`` this.UserId UserJoin room
                this.Groups.Add(connId, roomId) |> doTaskAsync)

        let leave() =
            ``do if connected`` (fun (connId, roomId) room ->
                ``post action to user and room`` this.UserId Leave room
                this.Groups.Remove(connId, roomId) |> doTask
                this.Clients.OthersInGroup(roomId)?visitorPart(this.ApiUser) |> doTaskAsync)

        member val private RoomId :RoomId option = None with get, set

        override this.Disconnect() =
            leave()
            base.Disconnect()

        override this.OnConnected() =
            connect()
            base.OnConnected()

        override this.OnReconnected() = base.OnReconnected()

        member this.SendMessage(roomId : string, message : string) : unit =
            ``do if connected`` (fun (connId, roomId) room ->
                ``post action to user and room`` this.UserId (SendMsgToAll(message)) room
                let time = showtime DateTimeOffset.Now
                this.Clients.Group(roomId)?someoneSentMessage(time + "<" + this.ApiUser.name + "> " + message) |> doTaskAsync)

        member x.DoAction(actionParam : string) : unit =
            //actionParam: rock paper scissors
            ``do if connected`` (fun (connId, roomId) room ->
                roomControl.GetAgent room |> Async.RunSynchronously
                |> Option.iter(fun roomAgent ->

                    let actionParamParsed = (int)actionParam //actionParam.ToUpperInvariant()

                    let isNotValid = BusinessLogicValidation.validateAction roomAgent this.UserId actionParamParsed |> Async.RunSynchronously

                    match isNotValid with
                    | InvalidReason reason | NoActions reason ->
                        this.Clients.Group(this.UserId.AgentIdString)?userInvalidAction(reason) |> doTaskAsync
                    | ValidFirstAction _ | ValidLastAction _ ->

                        let nextMove = 
                            match isNotValid with
                            | ValidFirstAction firstAction ->
                                //Cancel old task to not cause server action
                                OwinWorkerRole.TaskCancellationAgent.cancelAction room |> ignore
                                { ``game history data`` = [actionParamParsed]; dogUser = firstAction.dogUser; catUser = firstAction.catUser }
                            | ValidLastAction(action, state, id) ->
                                //Cancel old task to not cause server action
                                OwinWorkerRole.TaskCancellationAgent.cancelAction room |> ignore

                                //Some domain specific logics:
                                { ``game history data`` = actionParamParsed :: state.``game history data``; 
                                    dogUser = state.dogUser; catUser = state.catUser
                                }
                            | InvalidReason reason | NoActions reason -> failwith("Invalid move. " + reason)

                        //Notify this move to users
                        let state = UserSelectedItem, nextMove
                        ``post action to user and room`` this.UserId (state |> DoAction) room
                        this.Clients.Group(roomId)?userDidAction(this.ApiUser.name, actionParam) |> doTaskAsync

                        //Calculate turn, calculate business rules, check for some conditions like game ending, etc.
                        let check = BusinessLogicValidation.checkConditions this.UserId nextMove

                        match check with
                        | UserWon ->
                            ``post action to user and room`` this.UserId UserWonTheGame room
                            this.Clients.Group(roomId)?serverNotification(this.ApiUser.name, "User won") |> doTaskAsync
                            ()
                        | UserLost ->
                            ``post action to user and room`` this.UserId UserLostTheGame room
                            this.Clients.Group(roomId)?serverNotification(this.ApiUser.name, "User lost") |> doTaskAsync
                            ()
                        | DrawGame ->
                            ``post action to user and room`` this.UserId DrawGameEnding room
                            this.Clients.Group(roomId)?serverNotification(this.ApiUser.name, "Draw game") |> doTaskAsync
                            ()
                        | NotEnded -> 

                            // else: Add timeout of 5 minutes to next user!
                            let interval = moveTimeAllowed
                            
                            let username = this.ApiUser.name
                            let userid = this.ApiUser.id
                            let notifyTimeout() =
                                let evt = UserLostByTimeout |> ServerAction |> SingleAgent.Set
                                roomAgent.Post(evt) 
                                GlobalHost.ConnectionManager.GetHubContext<RoomHub>().Clients.Group(roomId)?serverNotification(username, "Timeout-victory")
                                |> doTaskAsync
                                //Just give points to user that did won by timeout. Other player deserves nothing.
                                TableStorage.addScore userid 1 20<points>


                            this.Clients.Group(roomId)?resetTimer(interval) |> doTaskAsync
                            let ``timeout plus loadtime`` = (interval |> ``min to sec``) + 5<seconds>
                            let token = scheduleAction ``timeout plus loadtime`` notifyTimeout
                            
                            //Post cancellation token to token agent...
                            OwinWorkerRole.TaskCancellationAgent.postToken(room,token)
                            ()
                ))

    /// MenuHub for current user menu
    type MenuHub() as this = 
        inherit HubBase()

        let connect() = 
            if this.checkPrincipals() then 
                LoggedInMenu |> userControl.AddAction this.UserId
                this.Clients.All?informUserInMenuPage(this.ApiUser) |> doTaskAsync

        let leave() =
            if this.checkAuthPrincipals() then
                LoggedOutMenu |> userControl.AddAction this.UserId
                this.Clients.Others?informUserOutMenuPage(this.ApiUser) |> doTaskAsync

        override this.Disconnect() =
            leave()
            base.Disconnect()

        override this.OnConnected() =
            connect()
            base.OnConnected()

        member x.CreateNewRoom(invitedIdr:string) : unit =
            if String.IsNullOrEmpty invitedIdr then
                ()
            else
                let invitedId = invitedIdr.Replace("\"", "").Replace("-", "")
                if String.IsNullOrEmpty this.ApiUser.name && not (this.checkPrincipals()) || String.IsNullOrEmpty invitedId then
                    ()
                else
                    let invited = invitedId |> parseGuid |> fetchUserIdentifier
                    invited |> Option.iter(function 
                        |Id(gid, name) ->
                            let roomName = showtime(DateTimeOffset.Now) + this.ApiUser.name + " vs. " + name
                            let roomIdf = NewGameRoom roomName this.UserId invited.Value

                            Invitation(SentInvitation, roomIdf, invited.Value) |> userControl.AddAction this.UserId
                            Invitation(GotInvitation, roomIdf, this.UserId) |> userControl.AddAction invited.Value
                            //To self:
                            this.Clients.Group(this.UserId.AgentIdString)?addInvitation(
                                ApiInvitation(roomId=roomIdf.AgentId, myRoom=true, user = ApiUser(id=invited.Value.AgentIdString, link=(invited.Value.AgentId |> userPath |> this.getSignalRUri), name=invited.Value.Name))) |> doTaskAsync
                
                            //To other: Todo: How to get Id??
                            this.Clients.OthersInGroup(gid.ToString("N"))?addInvitation(
                                ApiInvitation(roomId=roomIdf.AgentId, myRoom=false, user = this.ApiUser)) |> doTaskAsync
                        )


        member x.RejectInvite(inviterUser:string, inviterId:string, roomId:string) : unit =
            if this.checkAuthPrincipals() then
                match inviterId |> parseGuid |> fetchUserIdentifier, roomId |> parseGuid |> fetchRoomIdentifier with
                | None, _ | _, None -> ()
                | Some inviter, Some room ->

                    Invitation(RejectedInvitation, room, this.UserId) |> userControl.AddAction this.UserId
                    Invitation(GotRejectedInvitation, room, this.UserId) |> userControl.AddAction inviter
                    (VoteToDeleteRoom, this.UserId) |> UserAction |> roomControl.AddAction room

                    //To other:
                    this.Clients.OthersInGroup(inviterUser)?removeInvitation(
                         ApiInvitation(roomId=room.AgentId, user = this.ApiUser, myRoom=false)) |> doTaskAsync

                    //To self:
                    this.Clients.Group(this.UserId.AgentIdString)?removeInvitation(
                         ApiInvitation(roomId=room.AgentId, user = ApiUser(id=inviter.AgentIdString, link=(inviter.AgentId |> userPath |> this.getSignalRUri), name=inviter.Name), myRoom=true)) |> doTaskAsync

        member x.UserJoinRoom(acceptedUser : string, acceptedId:Guid, roomid : Guid) : unit = 
            if this.checkAuthPrincipals() then
                match acceptedId |> fetchUserIdentifier, roomid |> fetchRoomIdentifier with
                | None, _  | _, None -> ()
                | Some acUser, Some room ->
                    (UserJoin, this.UserId) |> UserAction |> roomControl.AddAction room
                    Invitation(AcceptedInvitation, room, acUser) |> userControl.AddAction this.UserId

                    this.Clients.Others?informNewRoom(ApiRoom(id = room.AgentId, name=room.Name, link = (room.AgentId |> roomPath |> this.getSignalRUri))) |> doTaskAsync
