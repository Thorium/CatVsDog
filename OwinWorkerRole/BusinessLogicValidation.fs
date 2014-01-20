namespace OwinWorkerRole

open System
open OwinWorkerRole.AgentState
open OwinWorkerRole.BusinessLogicActions

///Todo: Some business-logic server action here :-)
module BusinessLogicValidation =


    let fetchRoomStateActions actions =
        // Actions in room
        actions |> Array.choose (
            function 
                | dt,UserAction(a, id) -> Some(dt,Some(id), UserAction(a, id)) 
                | dt,ServerAction(a) -> Some(dt, None, ServerAction(a)) 
                )

    let fetchRoomRecentState actions =
        // Actions in room
        actions |> List.tryPick (
            function 
                | dt,UserAction(DoAction(ba,state), id) -> Some(state.``game history data``) 
                | _ -> None
                )

    type ValidationState = 
    | InvalidReason of string
    | ValidFirstAction of ``Current state of the room``
    | ValidLastAction of ``User business action inside of a room`` * ``Current state of the room`` * Identifier
    | NoActions of string

    let (|DogsTurn|CatsTurn|) i = if i % 2 = 1 then DogsTurn else CatsTurn

    /// Validate the user action request based on action history
    let validateAction (roomAgent:Agent<SingleAgent<_>>) user actionParameter : Async<ValidationState> =
        async{
            let! agentState = roomAgent.PostAndAsyncReply(Get)

            //Test here if the user performed action is valid!

            let okAction = [1..9] |> List.exists (fun i -> i=actionParameter)

            if not okAction then return InvalidReason("Unknown action.") else

            let gameIsOver = 
                let hasEndAction =
                    agentState |> List.tryFind(function 
                    |dt, UserAction(UserWonTheGame(_),id) -> true 
                    |dt, UserAction(UserLostTheGame(_),id) -> true 
                    |dt, ServerAction(UserLostByTimeout) -> true
                    | _ -> false)
                match hasEndAction with | None -> false | Some _ -> true
            if gameIsOver then return InvalidReason("The game is over.") else

            let lastAction = agentState |> List.tryFind(function |dt, UserAction(DoAction(_),_) -> true 
                                                                 |dt, UserAction(RoomCreated(_),_) -> true 
                                                                 | _ -> false)
            let ``Is user's turn`` state = 
                match state.``game history data``.Length+1 with
                | CatsTurn -> state.catUser = user
                | DogsTurn -> state.dogUser = user

            match lastAction with
            | Some(dt, UserAction(DoAction(action, state),id)) when state.``game history data`` |> List.exists(fun i -> i=actionParameter) -> return InvalidReason("Position already occupied.")
            | Some(dt, UserAction(RoomCreated(state),id)) when ``Is user's turn`` state
                     -> return state |> ValidFirstAction // Ok to do action
            | Some(dt, UserAction(DoAction(action, state),id)) when ``Is user's turn`` state
                     -> return (action, state, id) |> ValidLastAction // Ok to do action
            | Some(dt, UserAction(DoAction(action, state),id)) -> return InvalidReason("Not your turn!")
            | Some(dt, UserAction(RoomCreated(state),id)) -> return InvalidReason("Not your turn!")
            | _ -> return NoActions "Room is not ready yet..."
        
        }
    /// Calculate the game state based on action history
    let checkConditions (userId:UserId) (``suggested next move``:``Current state of the room``) =
        
        //Test here if the user caused some server action (like end-condition like end-of-game)
        //...
        let history = ``suggested next move``.``game history data``


        let partitionByIndex = function
            | CatsTurn -> (fun i (l,r) -> i::r, l)
            | DogsTurn -> (fun i (l,r) -> r, i::l)
         
        let turn = history.Length

        let partition = turn |> partitionByIndex
        
        let cats, dogs = List.foldBack partition history ([],[])

        let findVictory arr =
            let findItems(pos1,pos2,pos3) = 
                let find i = List.exists(fun item -> item = i)
                find pos1 arr && find pos2 arr && find pos3 arr
            let victories = [(1,2,3);(4,5,6);(7,8,9);(1,4,7);(2,5,8);(3,6,9);(1,5,9);(3,5,7)]
            let found = victories |> List.exists(findItems)
            found

        let (|CatsWon|_|)() = match findVictory cats with | true -> Some() | _ -> None
        let (|DogsWon|_|)() = match findVictory dogs with | true -> Some() | _ -> None

        //Scores assume that dogs usually wins...

        if findVictory dogs then
            TableStorage.addScore  ``suggested next move``.dogUser.AgentIdString 1 10<points>
            TableStorage.addScore  ``suggested next move``.catUser.AgentIdString 1 -1<points>
            if ``suggested next move``.dogUser = userId then
                UserWon
            else
                UserLost               
        elif findVictory cats then
            TableStorage.addScore  ``suggested next move``.catUser.AgentIdString 1 20<points>
            TableStorage.addScore  ``suggested next move``.dogUser.AgentIdString 1 -5<points>
            if ``suggested next move``.catUser = userId then
                UserWon
            else
                UserLost               
        elif turn > 8 then
            TableStorage.addScore  ``suggested next move``.dogUser.AgentIdString 1 2<points>
            TableStorage.addScore  ``suggested next move``.catUser.AgentIdString 1 4<points>
            DrawGame
        else
        let endconditions = NotEnded

        endconditions

    /// Send server event after some time has passed
    let sendServerEvent() =
        let doSomeMaintenance (agentData:RoomId*Agent<SingleAgent<_>>) =
            let id, agent = agentData
            let startTime = agent.PostAndReply(TimeCreated)
            if(startTime.AddMonths(3) < DateTimeOffset.UtcNow) then
                roomControl.DeleteItem id
        async {
            while true do
                do! oncePerFiveMinutes |> ``min to ms`` |> Async.Sleep
                //Let's delete all over three months old agents:
                let! allAgents = roomControl.GetAgents
                allAgents |> List.iter doSomeMaintenance
        } |> Async.Start
        

    /// Some maintenance events (like clear un-used rooms or something)
    let maintenanceEvent() =
        let doSomeMaintenance (agentData:RoomId*Agent<SingleAgent<_>>) =
            let id, agent = agentData
            let startTime = agent.PostAndReply(TimeCreated)
            if(startTime.AddMonths(3) < DateTimeOffset.UtcNow) then
                roomControl.DeleteItem id
        async {
            while true do
                do! oncePerDay |> ``min to ms`` |> Async.Sleep
                //Let's delete all over three months old agents:
                let! allAgents = roomControl.GetAgents
                allAgents |> List.iter doSomeMaintenance
        } |> Async.Start
    maintenanceEvent()
