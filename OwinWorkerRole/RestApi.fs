namespace OwinWorkerRole

open System

/// REST / HATEOAS -api for web 
/// These will correspond the user interface (/ view model) data on some level
/// So these objects are converted to JSON in both SignalR and HTTP WebAPI.
module RestApi =

    type ApiUser(id, link, name, score, timestamp, created) = 
        new (id, link, name) = ApiUser(id, link, name, 0L<points>, DateTime.MinValue, DateTimeOffset.Now.UtcTicks.ToString())
        member x.id = id
        member x.link = link
        member x.name = name
        member x.score = score
        member x.timestamp = timestamp
        member x.created = created
        override x.Equals(yobj) =
            match yobj with
            | :? ApiUser as y -> x.id = y.id
            | _ -> false
        override x.GetHashCode () = hash (x.id)

    type ApiInvitation(roomId, myRoom, user) = 
        member x.roomId = roomId
        member x.myRoom = myRoom
        member x.notMyRoom = not myRoom
        member x.user = user

    type ApiRoom(id, link, name, gameData, lastActionTime, gameStateMessage, lastTurn, chatMessages, catUser, dogUser) = 
        new (id, link, name) = ApiRoom(id, link, name, Array.empty, DateTimeOffset.Now, String.Empty, 0, Array.empty<string>, Unchecked.defaultof<ApiUser>, Unchecked.defaultof<ApiUser>)
        member x.id = id
        member x.link = link
        member x.name = name
        member x.gameData = gameData
        member x.lastActionTime = lastActionTime
        member x.gameStateMessage = gameStateMessage
        member x.lastTurn = lastTurn
        member x.chatMessages = chatMessages
        member x.allowedMoveTimeInMinutes = moveTimeAllowed
        member x.dogUser = dogUser
        member x.catUser = catUser

    /// Statistics of an user
    type ApiStatistics(link, user, created, lastLogin, level, score, usageCount, actionsOnThisServer, 
                        lastRoomId, isCurrentUser, firstEventOnline, serverRoomCount, serverUserCount) = 
        member x.link = link
        member x.user = user
        member x.created = created
        member x.lastLogin = lastLogin
        member x.level = level
        member x.score = score
        member x.usageCount = usageCount
        member x.actionsOnThisServer = actionsOnThisServer
        member x.lastRoomId = lastRoomId
        member x.isCurrentUser = isCurrentUser
        member x.firstEventOnline = firstEventOnline
        member x.serverRoomCount = serverRoomCount
        member x.serverUserCount = serverUserCount

    type ApiTournament(link, name, endDate, startDate, users, rooms) = 
        member x.link = link
        member x.name = name
        member x.endDate = endDate
        member x.startDate = startDate
        member x.users = users // ApiUser array
        member x.rooms = rooms // ApiRoom array

    type ApiSettings(theme, visualType, computerLevel) = 
        member x.theme = theme
        member x.visualType = visualType
        member x.computerLevel = computerLevel

    type ApiMenu(availableRooms, availableUsers, invitations, userInfo, link) = 
        member x.link = link
        member x.availableRooms = availableRooms
        member x.availableUsers = availableUsers
        member x.invitations = invitations
        member x.userInfo = userInfo

    //Converting real data to this data model
    
    let usersPath = "users"
    let tournamentPath = "tournament"

    let userPath (userId: Guid) = "user/" + userId.ToString("N")
    let roomPath (roomId: Guid) = "room/" + roomId.ToString("N")

    let toApiUser baseuri (user: TableStorage.UserData) =
        ApiUser(
            id = user.RowKey, 
            name = user.Name, 
            link = getUri baseuri ((userPath << parseGuid) user.RowKey),
            score = user.Score,
            timestamp = user.Timestamp,
            created = user.Created)

    let toApiUserPlain baseuri (userId: Guid) (userName:string) =
        ApiUser(
            id = userId.ToString("N"), 
            name = userName, 
            link = getUri baseuri (userPath userId))

    open System.Threading.Tasks
    open OwinWorkerRole.AgentState
    open OwinWorkerRole.BusinessLogicActions
    open OwinWorkerRole.BusinessLogicValidation

    let toApiRoom baseuri (roomAgent:Agent<SingleAgent<_>>) =
        async {
            let! rid = roomAgent.PostAndAsyncReply(Identify)
            let! lst = roomAgent.PostAndAsyncReply(Get)

            // Actions in room
            let actions = lst |> List.toArray
            let actionlist = 
                actions 
                |> fetchRoomStateActions
                |> Array.map (fun (d,i,a) -> 
                    match i with
                    |Some (Id(id, n)) -> d,Some((id, n) ||> toApiUserPlain baseuri), a
                    | _ -> d,None,a)

            // The room/game state
            let roomstate = 
                let state = lst |> fetchRoomRecentState
                match state with Some x -> x |> List.toArray | None -> Array.empty

            // Game state message
            let gameMessage = 
                let msg = 
                    actions |> Array.tryPick (
                        function 
                            | _,UserAction(UserWonTheGame,Id(i, n)) -> Some("Game over: Won by " + n)
                            | _,UserAction(UserLostTheGame,Id(i, n))-> Some("Game over: Lost by " + n)
                            | _,UserAction(DrawGameEnding,_)    -> Some("Game over: Draw game")
                            | _,_ -> None)
                match msg with None -> "" | Some(m) -> m

            // Chat history
            let messages = 
                actions |> Array.choose (
                    function 
                        | dt,UserAction(RoomCreated(_),Id(i, n))        -> showtime(dt) + "User " + n + " joined." |> Some
                        | dt,UserAction(UserJoin,Id(i, n))        -> showtime(dt) + "User " + n + " joined." |> Some
                        | dt,UserAction(VisitorJoin,Id(i, n))     -> showtime(dt) + "Visitor " + n + " joined." |> Some
                        | dt,UserAction(Leave,Id(i, n))           -> showtime(dt) + n + " left the room." |> Some
                        | dt,UserAction(SendMsgToAll(m),Id(i, n)) -> showtime(dt) + "<" + n + ">" + m |> Some
                        | _ -> None)

            (* Testing:
            let actions = [|
                    UserJoin(UserId(Guid.Empty, "a"));
                    Leave(UserId(Guid.Empty, "a"));
                    UserJoin(UserId(Guid.NewGuid(), "b"))|]
            *)

            let toApiUsr i n = toApiUserPlain baseuri i n

            let dogUser, catUser =
                let users = 
                    actions |> Array.tryPick(
                        function
                            | _, UserAction(RoomCreated(s), id) -> Some(s.dogUser, s.catUser)
                            | _,_ -> None
                    ) 
                match users with
                | Some(Id(di, dn), Id(ci, cn)) -> toApiUsr di dn, toApiUsr ci cn
                | None -> Unchecked.defaultof<ApiUser>, Unchecked.defaultof<ApiUser>

            let dt = 
                match actionlist.Length with
                | 0 -> actions |> (Array.toSeq >> Seq.head >> fst)
                | _ -> actionlist |> Array.map (fun(d,_,_)->d) |> Array.toSeq |> Seq.head

            return
                ApiRoom(
                        id = rid.AgentId,
                        gameData = Array.rev roomstate,
                        gameStateMessage = gameMessage,
                        lastActionTime = dt,
                        lastTurn = actionlist.Length,
                        chatMessages = Array.rev messages,
                        name = rid.Name,
                        link = getUri baseuri (roomPath rid.AgentId),
                        dogUser = dogUser,
                        catUser = catUser
                    )
        }

    let toApiMenu baseuri (user: TableStorage.UserData) uid =

        let toUser = function |Id(i, n) -> (baseuri,i,n) |||> toApiUserPlain
        let toRoom = function |Id(i, n) -> ApiRoom(id=i, name=n, link = getUri baseuri (roomPath i))
        let toInvite = function |Id(ri, rn), usr -> ApiInvitation(roomId=ri, user = toUser usr, myRoom = (uid = usr.AgentId))

        //Todo: Remove this combinating from agent history and move it to AgentState

        // Invites
        let invites = 
            async{
                let! id = user.RowKey |> parseGuid
                            |> userControl.GiveIdentifierForGuid
                match id with
                | None -> return Array.empty<RoomId*UserId>
                | Some i -> 
                    let! invitations = i |> userControl.ShowItemState
                    let result = 
                        invitations |> Seq.toArray
                        |> Array.choose (function 
                            | _,Invitation(GotInvitation, r,u)
                            | _,Invitation(SentInvitation, r,u)        
                                -> Some((r,u),+1) 
                            | _,Invitation(AcceptedInvitation, r,u)
                            | _,Invitation(RejectedInvitation, r,u)
                            | _,Invitation(GotRejectedInvitation, r,u) 
                                -> Some((r,u),-1) 
                            | _,_ -> None)
                        |> filterNonPositives fst
                    return result
            } |> Async.StartAsTask

        let userApis, roomApis =
            async {
                let! ru = //F# can handle keeping the indexes:
                    [|roomControl.ReturnAllIds(); userControl.ReturnAllIds()|] |> Async.Parallel
                let rooms = ru.[0] |> List.map toRoom
                let users = ru.[1] |> List.map toUser
                return users, rooms
            } |> Async.RunSynchronously
        
        invites.Wait()

        ApiMenu(
            link = getUri baseuri "currentUser/menu",
            availableRooms = roomApis, //ApiRooms
            availableUsers = userApis, //ApiUser
            invitations = Seq.map toInvite invites.Result, //user.RowKey
            userInfo = toApiUser baseuri user //ApiUser
            )

    let toApiStatistics baseuri (tableData:TableStorage.UserData) (agent:Async<Agent<SingleAgent<_>> option>) isCurrent =
        let usage =
            async {
                let! a = agent
                return a |> Option.map(fun (userAgent:Agent<SingleAgent<_>>) ->
                    let lst = userAgent.PostAndReply(Get)
                    let createtime = userAgent.PostAndReply(TimeCreated)
                    let actions = lst |> List.toArray
                    let actionlist = actions |> Array.choose (function 
                        | dt,LogOnToServer(_) -> Some(showtime(dt) + "Logged on to this server.", None)
                        | dt,RoomAction(RoomCreated(_),room) -> Some(showtime(dt) + "Joined room: " + room.Name, Some(room.AgentId)) 
                        | dt,RoomAction(UserJoin,room) -> Some(showtime(dt) + "Joined room: " + room.Name, Some(room.AgentId)) 
                        | dt,RoomAction(VisitorJoin,room) -> Some(showtime(dt) + "Visited room: " + room.Name, Some(room.AgentId)) 
                        | dt,RoomAction(Leave,room) -> Some(showtime(dt) + "Left room: " + room.Name, None) 
                        | _ -> None)
                    actionlist,createtime)
            } |> Async.RunSynchronously

        let uactions, lastroom, timeAgentCreated =
            match usage with
            | None -> [||], None, DateTimeOffset.Now
            | Some(actionlist, time) ->
                let lastRoomId = 
                    let guids = actionlist |> Array.map snd
                    let roomId = Array.FindLast(guids, fun rid -> rid<>None)
                    match roomId with
                    | None -> None
                    | Some n when n = Unchecked.defaultof<Guid> -> None
                    | Some x -> x.ToString("N") |> Some
                actionlist,lastRoomId,time

        let serverRooms, serverUsers = 
            async{
                let! ru = [|roomControl.GetCount;userControl.GetCount|] |> Async.Parallel
                return ru.[0], ru.[1]
            } |> Async.RunSynchronously

        ApiStatistics(
            link = getUri baseuri ("user/" + tableData.RowKey + "/statistics"),
            user = toApiUserPlain baseuri (tableData.RowKey |> parseGuid) tableData.Name,
            created = DateTimeOffset(DateTime(int64(tableData.Created))),
            lastLogin = DateTimeOffset(tableData.Timestamp),
            level = tableData.Level,
            score = tableData.Score,
            usageCount = tableData.UsageCount,
            actionsOnThisServer = Array.map fst uactions,
            isCurrentUser = isCurrent,
            firstEventOnline = timeAgentCreated,
            serverRoomCount = serverRooms,
            serverUserCount = serverUsers,

            lastRoomId = match lastroom with | None -> "" | Some x -> x
        )
