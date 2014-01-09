namespace OwinWorkerRole

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open System.Configuration
open TaskHelper

/// Some small generic functions
/// Just tools, no state, independent
/// Also unit of measures to help type-driven development
[<AutoOpen>]
module Tools =

    /// String.notNullOrEmpty extension to strings
    module String = let notNullOrEmpty = not << String.IsNullOrEmpty

    /// Log message to error console
    let log message (kind : string) = Trace.TraceInformation(message, kind)

    /// Time as chat-style timestamp: [hh:mm]
    let showtime (dt:DateTimeOffset) = "[" + dt.Hour.ToString("00") + ":" + dt.Minute.ToString("00") + "] "

    /// String to Guid
    let (|IsGuid|NotGuid|) (gid:String) = 
        if String.IsNullOrEmpty gid then NotGuid else
        let ok, id = gid.Replace("\"", "") |> Guid.TryParse
        match ok with
        | true -> IsGuid(id)
        | false -> NotGuid

    /// String to Guid, explicit
    let parseGuid (gid:String) = 
        if String.IsNullOrEmpty gid then failwith("Empty guid given.")
        match gid with
        |IsGuid id -> id
        |NotGuid -> failwith("Not a guid: " + gid)

    /// Creates absolute url from relative: e.g. /room -> http://...:80/room
    let getUri (baseurl: Uri) (item: string) =
        let baseUrl = Uri(baseurl.GetLeftPart(System.UriPartial.Authority), UriKind.Absolute)
        Uri(baseUrl, item)

    /// For instances of operations and negative operations, takes positive ones, e.g.: 
    /// Input: [("a",+1);("a",-1);("a",+1);("b",1);("c",1);("c",-1);("b",1)]
    /// Output: ["a","b"]
    let filterNonPositives (byFirst : 'a*int -> 'a) =
        Array.toSeq
        >> Seq.groupBy(fun u -> byFirst(u))
        >> Seq.map(fun (key, values) -> (key, values |> Seq.sumBy snd))
        >> Seq.choose(
            function 
            | key, values when values>0 -> key |> Some
            | _ -> None)
        >> Seq.toArray


    /// The usual alias for mailbox
    type Agent<'T> = MailboxProcessor<'T>

// ----------- Types ----------

    type ExternalIdTypes =
    | Facebook
    | Google

    /// The most interesting game currency could be mBTC (millibitcoin)
    [<Measure>]
    type coins

    [<Measure>]
    type level

    [<Measure>]
    type points

    let toLongPoints (p:int<points>) =
        int64(p/1<points>)*1L<points>

    //Usage: 1000<points> |> toLevel
    let toLevel (v:int64<points>) = 
        let dividedBy100 = float(v/1L<points>) *1.0/100.0
        let logarithmicScale = 
            Math.Log(dividedBy100+1.0, 2.0) |> Math.Round |> int
        logarithmicScale * 1<level>
    
    [<Measure>]
    type seconds

    [<Measure>]
    type minutes

    let ``min to sec`` (s:int<minutes>) =  s * 60<seconds> / 1<minutes>
    let ``sec to ms`` (s:int<seconds>) =  s * 1000 / 1<seconds>
    let ``min to ms`` (s:int<minutes>) =  s * 60 * 1000 / 1<minutes>

    //How often server time based events may happen
    let oncePerMinute = 1<minutes>
    let oncePerFiveMinutes = 5<minutes>
    let oncePerDay = 24 * 60<minutes>

    let moveTimeAllowed = (ConfigurationManager.AppSettings.["AllowedMoveTimeInMinutes"] |> Convert.ToInt32) * 1<minutes>

// ---------------------

    /// Async timer to perform actions
    let timer interval scheduledAction = async {
        do! interval |> ``sec to ms`` |> Async.Sleep
        scheduledAction()
    }

    /// Add action to timer, return cancellation-token to cancel the action
    let scheduleAction interval scheduledAction =
        let cancel = new CancellationTokenSource()
        Async.Start (timer interval scheduledAction, cancel.Token)
        cancel

    /// Start non-generic task but don't wait 
    let doTaskAsync:(Task -> unit) = awaitPlainTask >> Async.Start
    /// Start non-generic task and wait
    let doTask:(Task -> unit) = awaitPlainTask >> Async.RunSynchronously
