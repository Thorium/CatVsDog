namespace OwinWorkerRole

open System
open System.Threading
open OwinWorkerRole.AgentState

module TaskCancellationAgent = 

    // Basic idea from: http://msdn.microsoft.com/en-us/library/ee370246.aspx

    /// Agent to maintain a queue of scheduled tasks that can be canceled.
    /// It never runs its processor function, so it doesn't do anything.
    let scheduleAgent = new Agent<Identifier * CancellationTokenSource>(fun _ -> async { () })

    let postToken = scheduleAgent.Post

    let cancelAction(agentId) =
        scheduleAgent.TryScan((fun (aId, source) ->
            let action =
                async {
                    source.Cancel()
                    return agentId
                }
            if (agentId = aId) then
                Some(action)
            else
                None), 100) // timeout: if queue is empty, wait 100ms to get a value.
        |> Async.RunSynchronously

// Testing:
(*
    let id = Id(Guid.NewGuid(), "test room")
    let task = id, new CancellationTokenSource()
    scheduleAgent.Post(task)
    cancelAction(id)
*)