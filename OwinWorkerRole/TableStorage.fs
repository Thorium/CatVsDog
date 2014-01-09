namespace OwinWorkerRole

open System
open System.Data
open System.Linq
open Fog.Storage.Table
open System.Configuration
open System.Data.Services.Common
open Microsoft.WindowsAzure.StorageClient

/// Azure Table storage, Cloud-environment, NoSQL for permanent data
module TableStorage =

    let private ``Azure user table`` = ConfigurationManager.AppSettings.["AzureUserTable"]
    let private ``Azure agent table`` = ConfigurationManager.AppSettings.["AzureAgentTable"]
    let partition = "tprt"

    // Supported data types: http://msdn.microsoft.com/en-us/library/windowsazure/dd179338.aspx

    /// User data model for Windows Azure Table Storage 
    [<DataServiceKey("PartitionKey", "RowKey")>] 
    type UserData() = 
        member val PartitionKey = String.Empty with get, set
        member val RowKey = String.Empty with get, set
        member val Timestamp = DateTime.UtcNow with get, set
        member val Created = DateTimeOffset.Now.UtcTicks.ToString() with get, set
        member val ExternalIdFacebook = String.Empty with get, set
        member val ExternalIdGoogle = String.Empty with get, set
        member val Name = String.Empty with get, set
        member val Email = String.Empty with get, set
        member val Score = 0L<points> with get, set
        member val Level = 0<level> with get, set
        member val UsageCount = 0L with get, set
        member val Balance = 0L<coins> with get, set
        member val Info = "" with get, set
        member val Banned = false with get, set
        member val IPAddress = String.Empty with get, set

    // Fog example: https://github.com/dmohl/FsOnTheWeb-Workshop
    let addUser user = (``Azure user table``, user) ||> CreateEntity
    let updateUser user = (``Azure user table``, user) ||> UpdateEntity
    let deleteUser user = (``Azure user table``, user) ||> DeleteEntity

    let getUsers() = 
        let context = BuildTableClient().GetDataServiceContext()
        let query = 
            query { for item in context.CreateQuery<UserData>(``Azure user table``) do
                    select item }
        query.AsTableServiceQuery().Execute()

    /// Get user by some search condition. Condition is translated to Azure Storage query
    let ``get user by search condition`` partitionKey searchCondition = 
        let context = BuildTableClient().GetDataServiceContext()
        //let tablestoragePath = context.BaseUri
        let query = 
            let beginQuery = query { for item in context.CreateQuery<UserData>(``Azure user table``) do
                                     where (item.PartitionKey = partitionKey) }
            let filterQuery:IQueryable<UserData> = searchCondition(beginQuery);
            let selectQuery = query { for item in filterQuery do
                                         take 1
                                         select item}
            selectQuery
        query.AsTableServiceQuery().Execute()
        |> Seq.tryFind(fun _ -> true)

    let getUserById (uid:Guid) = 
        let id = uid.ToString("N")
        ``get user by search condition`` partition (fun iq -> iq.Where(fun (i:UserData) -> i.RowKey = id)) 
    let getUserByName name = ``get user by search condition`` partition (fun iq -> iq.Where(fun (i:UserData) -> i.Name = name))

    //Supported LINQ operations for Azure Table Storage: http://msdn.microsoft.com/en-us/library/windowsazure/dd135725.aspx
    let getUserByExternal eid = 
        let queryable:(IQueryable<UserData> -> IQueryable<UserData>) =
            match eid with
            |x, Facebook -> (fun iq -> iq.Where(fun (i:UserData) -> i.ExternalIdFacebook = x))
            |x, Google -> (fun iq -> iq.Where(fun (i:UserData) -> i.ExternalIdGoogle = x))
        ``get user by search condition`` partition queryable

    /// Overwrite / update existing user with new data
    /// Easy way to add new providers for existing user: Just login many times with different providers :)
    let update (existing:UserData) (user:UserData) =
        if String.notNullOrEmpty existing.ExternalIdFacebook then user.ExternalIdFacebook <- existing.ExternalIdFacebook
        if String.notNullOrEmpty existing.ExternalIdGoogle then user.ExternalIdGoogle <- existing.ExternalIdGoogle
        user.Created <- existing.Created 
        updateUser user
        user

    let externalAddOrUpdateUser (user:UserData) =
        let someEid = 
            if String.notNullOrEmpty user.ExternalIdFacebook then user.ExternalIdFacebook, Facebook
            elif String.notNullOrEmpty user.ExternalIdGoogle then user.ExternalIdGoogle, Google
            else failwith("Unknown id-provider")

        // Has user logged in earlier (etc. by different provider)?
        match getUserById (Guid(user.RowKey)) with
        | None -> 
            // Has already logged by this provider? (With different id)
            // (This causes internal full table scan...)
            let eid = getUserByExternal someEid
            match eid with
            | None -> 
                addUser user
                user
            | Some e when e.Banned -> user
            | Some existing ->
                user.RowKey <- existing.RowKey
                update existing user
        | Some e when e.Banned -> user
        | Some existing -> update existing user

    let addScore userId usage score =
        let context = BuildTableClient().GetDataServiceContext()
        let query = 
            query { for e in context.CreateQuery<UserData>(``Azure user table``) do
                        if e.PartitionKey = partition && e.RowKey = userId then
                            yield e }
        let item =
            query.AsTableServiceQuery().Execute()
            |> Seq.tryFind(fun _ -> true)
        match item with
        | None -> ()
        | Some(user) ->
            let longpoints = score |> toLongPoints
            let longUsage = int64(usage)
            let newScore = 
                let s = user.Score + longpoints // No negative scores:
                match s<1L<points> with false -> s | true -> 1L<points>
            let newUsage = user.UsageCount + longUsage
            user.Score <- newScore
            user.Level <- newScore |> toLevel
            user.UsageCount <- newUsage
            user.Timestamp <- DateTime.UtcNow
            UpdateEntity ``Azure user table`` user

    /// Create tables when software starts 
    let initTables() = 
        let tableClient = BuildTableClient()
        ``Azure user table`` |> tableClient.CreateTableIfNotExist |> ignore
        //``Azure agent table`` |> tableClient.CreateTableIfNotExist |> ignore