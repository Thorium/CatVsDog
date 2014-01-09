namespace OwinWorkerRole

/// Actor (/Agent) -model to hold runtime process data
/// No real mutable state exposed to outside:
/// Every actor is like a small independent container of 
/// "event sourcing" / "transaction history" / "event loop" / "audit trail" / what ever you call it...
module AgentState =

    open System
    open System.Threading
    open System.IO
    open Microsoft.FSharp.Control.WebExtensions

    /// Identifier for a container (e.g. user/player or room/game)
    /// Id plus clear name
    type Identifier = 
    | Id of Guid * string
    with
        member x.AgentId = match x with | Id(g, n) -> g
        member x.AgentIdString = match x with | Id(g, n) -> g.ToString("N")
        member x.Name = match x with | Id(g, n) -> n

    type UserId = Identifier
    type RoomId = Identifier

    /// Single agent operators
    /// Single agent is a dynamically created agent for some data container
    /// e.g. an user, a room, etc.
    type 'T SingleAgent =
    | Set of 'T
    | Get of AsyncReplyChannel<List<DateTimeOffset*'T>>
    | Identify of AsyncReplyChannel<Identifier>
    | TimeCreated of AsyncReplyChannel<DateTimeOffset>

    /// Control agent manages single agents of certain type
    type private 'T ``Control agent operators`` =
    | Create of Identifier * Agent<'T>
    | Read of Identifier * AsyncReplyChannel<Agent<'T> option>
    | ReadByGuid of Guid * AsyncReplyChannel<Agent<'T> option>
    | Delete of Identifier
    | ReadAllIds of AsyncReplyChannel<Identifier list>
    | ReadAllIdsOf of (Identifier -> bool)*AsyncReplyChannel<Identifier list>
    | ReadAll of List<Identifier * Agent<'T>> AsyncReplyChannel
    | ReadAllOf of (Identifier -> bool)*AsyncReplyChannel<List<Identifier * Agent<'T>>>
    | Count of AsyncReplyChannel<int>
    
    let internal notifyError = Event<obj>()
    let public OnError (watch:Action<_>) =
        notifyError.Publish |> Observable.add(fun e -> watch.Invoke(e))

    /// Error queue agent
    let internal supervisor = 
        Agent<System.Exception>.Start(fun inbox ->
            async { while true do 
                    let! err = inbox.Receive()
                    notifyError.Trigger(err)
                    printfn "an error occurred in an agent: %A" err })

    /// Agent for storing other agents
    [<Sealed>]
    type 'T ControlAgent(controlagentName:string) =
        let control = new Agent<``Control agent operators``<SingleAgent<'T>>>(fun msg ->
            let rec msgPassing all =
                async { 
                    let! c = msg.Receive()
                    match c with
                    | Create(id,agent) ->
                        if all |> List.exists(fun (i:Identifier,a) -> i.AgentId = id.AgentId) then
                            return! msgPassing(all)
                        else
                            return! msgPassing((id,agent)::all)
                    | Read(id,reply) ->
                        let response = 
                            all 
                            |> List.tryFind (fun (i,a) -> i.AgentId = id.AgentId) 
                            |> Option.map snd
                        reply.Reply(response)
                        return! msgPassing(all)
                    | ReadByGuid(gid,reply) ->
                        let response = 
                            all |> List.tryFind (fun (i,a) -> i.AgentId = gid) 
                            |> Option.map snd
                        reply.Reply(response)
                        return! msgPassing(all)
                    | Delete(id) -> 
                        all |> List.tryFind (fun (i,a) -> i <> id)
                        |> Option.iter(fun (i, a) -> (a :> IDisposable).Dispose())
                        let removed = 
                            all |> List.filter (fun (i,a) -> i <> id) 
                        return! msgPassing(removed)
                    | ReadAllIds(reply) -> 
                        reply.Reply(all |> List.map fst)
                        return! msgPassing(all)
                    | ReadAllIdsOf(search, reply) -> 
                        let ids = all |> List.map fst |> List.filter search 
                        reply.Reply(ids)
                        return! msgPassing(all)
                    | ReadAllOf(search, reply) -> 
                        let agents = 
                            all |> List.filter(fst >> search) 
                        reply.Reply(agents)
                        return! msgPassing(all)
                    | ReadAll(reply) -> 
                        reply.Reply(all)
                        return! msgPassing(all)
                    | Count(reply) -> 
                        reply.Reply(all.Length)
                        return! msgPassing(all)
                }
            msgPassing [])
        /// Fetch agent form the control agent
        let fetchAgent id = control.PostAndAsyncReply(fun a -> Read(id, a))
        let fetchAgents ids = 
            let search id = List.exists (fun i -> i=id) ids
            control.PostAndAsyncReply(fun a -> ReadAllOf(search, a))
        do 
            control.Error.Add(fun error -> supervisor.Post error)
            control.Start()

        interface IDisposable with 
            override x.Dispose() = 
                control.PostAndReply(ReadAll) |> List.iter (fun (i, a) -> (a :> IDisposable).Dispose())
                (control :> IDisposable).Dispose()
                GC.SuppressFinalize(x)

        /// Create a new actor (like room or user)
        member x.CreateNewItem id initialState = 

            //DeleteItem(id)
            let myid = id
            let timecreated = DateTimeOffset.UtcNow
            let agent = new Agent<_>(fun msg ->
                let rec msgPassing all =
                    async { 
                        let! r = msg.Receive()
                        match r with
                        | Set(i) ->
                            //printf "%s" r
                            //let r = f(c)
                            return! msgPassing((DateTimeOffset.UtcNow,i)::all)
                        | Get(reply) ->
                            reply.Reply(all)
                            return! msgPassing(all)
                        | Identify(reply) ->
                            reply.Reply(myid)
                            return! msgPassing(all)
                        | TimeCreated(reply) ->
                            reply.Reply(timecreated)
                            return! msgPassing(all)
                    }
                msgPassing [])
            agent.Error.Add(fun error -> supervisor.Post error)
            agent.Post(Set(initialState))
            agent.Start()
            (id, agent) |> Create |> control.Post
            id //, { new IDisposable with member x.Dispose() = (agent :> IDisposable).Dispose()}
        
        /// Insert item state
        member x.AddAction id msg =
            let errorcancel = supervisor.Post
            Async.StartWithContinuations(
                fetchAgent id, 
                function
                    | Some agent -> 
                        let ms = msg
                        let a = agent
                        msg |> Set |> agent.Post
                    | None ->
                        let ag = id.AgentIdString + " - " + id.Name
                        //Agent does not exists.
                        supervisor.Post(ArgumentOutOfRangeException("id", "Agent " + ag))
                        // Maybe server has been reseted... Should we make a new one?
                        //let ag = CreateNewItem id (obj())
                ,  errorcancel, errorcancel)

        /// Insert item state
        member x.AddActionToAll ids msg =
            let errorcancel = supervisor.Post
            Async.StartWithContinuations(
                fetchAgents ids, 
                function
                    | [] ->
                        //Agent does not exists.
                        supervisor.Post(ArgumentOutOfRangeException("ids", "Agents: " + String.Join(", ", List.toArray ids)))
                        // Maybe server has been reseted... Should we make a new one?
                        //let ag = CreateNewItem id (obj())
                    | agents -> agents|> List.iter (fun a -> msg |> Set |> (snd a).Post)
                ,  errorcancel, errorcancel)

        /// Get item state
        member x.ShowItemState id =
            async{
                let! res = fetchAgent id
                match res with
                | Some agent -> 
                    let! res = agent.PostAndAsyncReply(Get)
                    return res |> List.toSeq
                | _ -> return Seq.empty
            }

        /// Name of the control agent, just for easier identification
        member x.Name = controlagentName
        /// This just (disposes the item and) removes the reference
        member x.DeleteItem = Delete >> control.Post
        /// Fetch the whole agent
        member x.GetAgent id = fetchAgent id
        /// Fetch all the agents
        member x.GetAgents = control.PostAndAsyncReply(ReadAll)
        /// Count items
        member x.GetCount = control.PostAndAsyncReply(Count)
        /// Return agent ids by some condition
        member x.ReturnAllIds() = control.PostAndAsyncReply(ReadAllIds) 
        /// Return agents by some condition
        member x.ReturnAllOf(filter) = control.PostAndAsyncReply(fun i -> ReadAllOf(filter, i))
        /// Get agent by guid
        member x.GiveAgentForGuid (id:Guid) = control.PostAndAsyncReply(fun a -> ReadByGuid(id, a)) 

        /// Return all states
        member x.ReturnAll() =
            let rec fetch (res:list<Identifier*Agent<_>>) (acc:list<Identifier*seq<_>>) =
                async{ 
                    match res with
                    | [] -> return acc
                    | (id,agent)::t -> 
                        let! msgs = agent.PostAndAsyncReply(Get)
                        let one = id, msgs |> List.toSeq
                        return! fetch t (one :: acc) 
                }
            async{ 
                let! result = control.PostAndAsyncReply(ReadAll) 
                let! all = fetch result []
                return (all |> List.toSeq)
            }

        member x.GiveIdentifierForGuid (id:Guid) = 
            async{
                let! list = control.PostAndAsyncReply(fun a -> ReadAllIdsOf((fun i -> (i.AgentId = id)), a)) 
                return list |> List.tryFind(fun _ -> true)
            }