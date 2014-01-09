namespace OwinWorkerRole
open System
open System.Threading
open System.IO
open Microsoft.FSharp.Control.WebExtensions
open OwinWorkerRole.AgentState

/// These model the commands/actions/operations for agents
/// This is the real model of the functionality in the software
module BusinessLogicActions =

    /// Business-specific conditions to check after user action
    type ``Room state conditions for user`` =
    | UserLost
    | UserWon
    | DrawGame
    | NotEnded
 
    /// Some business-specific data transfer object
    type ``Current state of the room`` = {
        ``game history data``: int list;
        catUser: UserId;
        dogUser: UserId
    }

    /// Somekind of user action inside of a room, e.g. game actions for user
    type ``User business action inside of a room`` =
    | UserMovePieceInGame
    | UserSelectedItem
    //| EtcActionsHere //some actions and params

    /// Somekind of server actions to a room
    type ``Server business actions available in a room`` =
    | ServerGeneratedEvent of obj
    | RoomStateMessage of string
    | UserLostByTimeout

    // --------------------------------------------------------------

    /// User actions in a room
    type ``User actions available in a room`` =
    | RoomCreated of ``Current state of the room``
    | UserJoin
    | VoteToDeleteRoom
    | VisitorJoin
    | DoAction of ``User business action inside of a room`` * ``Current state of the room``
    | SendMsgToAll of string
    | UserWonTheGame
    | UserLostTheGame
    | DrawGameEnding
    | Leave

    /// User action or server action
    type ``Server and user actions available in a room`` = 
    | UserAction of ``User actions available in a room`` * UserId
    | ServerAction of ``Server business actions available in a room``

    // --------------------------------------------------------------

    /// Menu actions for user: Invitating some other user to a room
    type ``User invitation to other user`` =
    | SentInvitation
    | GotInvitation
    | AcceptedInvitation
    | RejectedInvitation
    | GotRejectedInvitation

    /// User actions: Menu and room actions
    type ``User actions available in a menu or a room`` =
    | LogOnToServer of string*int64*int64<points>*int<level>*obj //id, name, games, scores, ...
    | LoggedInMenu
    | LoggedOutMenu
    | Invitation of ``User invitation to other user``*RoomId*UserId//roomid*opponent
    | RoomAction of ``User actions available in a room``*RoomId//roomid

    // --------------------------------------------------------------

    /// Control agent for rooms
    let roomControl = new ControlAgent<``Server and user actions available in a room``>("room")
    // Note: If rooms would have more complex activities, there could be separate agents for each activity (e.g. chatControl).

    /// Control agent for users
    let userControl = new ControlAgent<``User actions available in a menu or a room``>("user")
    
    let fetchRoomIdentifier = roomControl.GiveIdentifierForGuid >> Async.RunSynchronously
    let fetchUserIdentifier = userControl.GiveIdentifierForGuid >> Async.RunSynchronously

    let NewUser name info = 
        let id = (Guid.NewGuid(),name) |> Id
        userControl.CreateNewItem id info

    let NewUserExistingId gid name info = 
        let id = (gid,name) |> Id
        userControl.CreateNewItem id info

    let NewGameRoom (roomName:string) user opponent =
        let players = 
            match System.Random().Next(2) with 
            | 1 -> { dogUser = user; catUser = opponent; ``game history data`` = [] }
            | _ -> { dogUser = opponent; catUser = user; ``game history data`` = [] }
        let id = Id(Guid.NewGuid(), roomName)
        roomControl.CreateNewItem id ((RoomCreated(players), user) |> UserAction)

    let ``post action to user and room`` userId (action:``User actions available in a room``) roomId=
        (action, userId) |> UserAction |> roomControl.AddAction roomId
        (action, roomId) |> RoomAction |> userControl.AddAction userId

    // --------------------------------------------------------------------------------
    // Just for testing:
(*
    //Add a player
    let name = "tuomas"
    let player1 = (name, 0L, 0L<points>, 0<level>, obj()) |> LogOnToServer |> NewUser name

    //Create new game
    let game1 = player1 |> NewGameRoom "my room"

    //Add another player    
    let name2 = "toka"
    let player2 = (name2, 0L, 0L<points>, 0<level>, obj()) |> LogOnToServer |> NewUser name2

    //AddAction will add any object to any actor.
    //There is no limit for objects, but it is easier to follow if 
    //objects are custom types like Actions or UserActions here

    //Adding info to player1:
    RoomAction(UserMovePieceInGame(0,1) |> DoAction, player2) |> userControl.AddAction player1 

    //Adding info to game1:
    UserAction(SendMsgToAll("hello"), player1) |> roomControl.AddAction game1 
    UserAction(UserMovePieceInGame(8, 3) |> DoAction, player1) |> roomControl.AddAction game1 
    UserAction(UserJoin, player2) |> roomControl.AddAction game1 

    let agentGame = roomControl.GetAgent game1 |> Async.RunSynchronously

    ``post action to user and room`` player2 UserJoin game1

    //Show item history/state:
    roomControl.ShowItemState game1 |> Async.RunSynchronously
    userControl.ShowItemState player1 |> Async.RunSynchronously


    let anotherGame = 
        let name3 = "kolmas"
        let newPlayer = (name3, 0L, 0L<points>, 0<level>, obj()) |> LogOnToServer |> NewUser name3
        let game2 = newPlayer |> NewGameRoom "new room"
        UserAction(SendMsgToAll("hello"), newPlayer) |> roomControl.AddAction game2 
    roomControl.ReturnAll() |> Async.RunSynchronously

    roomControl.DeleteItem game1
*)
