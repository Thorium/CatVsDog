namespace OwinWorkerRole

open System
open System.Collections.Generic
open System.Linq
open System.Net
open System.Threading
open System.Threading.Tasks
open Microsoft.WindowsAzure
open Microsoft.WindowsAzure.Diagnostics
open Microsoft.WindowsAzure.ServiceRuntime
open Microsoft.Owin.Hosting
open System.Security
open System.Globalization

/// Azure worker role host process
[<SecurityCritical>]
type WorkerRole() =
    inherit RoleEntryPoint() 

    // This is a sample worker implementation. Replace with your logic.
    let mutable server = Unchecked.defaultof<IDisposable>

    override wr.Run() =

        let assy = wr.GetType().Assembly.GetName().Name
        log (assy + " entry point called") "Information"
        while(true) do 
            Task.Delay(10<seconds> |> ``sec to ms``)
                // Azure doesn't support async here, so thread has to be blocked:
                .Wait()
            log "Working" "Information"
 
    override wr.OnStart() = 

        // Set the maximum number of concurrent connections 
        ServicePointManager.DefaultConnectionLimit <- (System.Configuration.ConfigurationManager.AppSettings.["WwwServerConcurrentConnectionLimit"], CultureInfo.InvariantCulture) |> Convert.ToInt32
       
        // For information on handling configuration changes
        // see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.

        let endpoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints.["WwwServer"]
        let baseUri = sprintf "%s://%A" endpoint.Protocol endpoint.IPEndpoint

        log ("Starting OWIN server at " + baseUri) "Information"

        let options = StartOptions()
        options.Urls.Add(baseUri)
        server <- WebApp.Start<OwinWorkerRole.MyServer.MyWebStartup>(options)

        base.OnStart()

    override wr.OnStop() =
        if server <> Unchecked.defaultof<IDisposable> then
            server.Dispose()
        base.OnStop()

module StartConsoleApp =
    open OwinWorkerRole.MyServer

    // If you want to run this as console application, then uncomment EntryPoint-attribute and
    // from this project properties change this application "Output Type" to: Console Application
    // and "Set as StartUp Project" this project, and remove the trace from app.config: Microsoft.WindowsAzure.Diagnostics.
    // (But then this will be .exe-file instead of dll-file)
    //[<EntryPoint>]
    let main argv = 
        let ``console app web server url`` = "http://localhost:8080"
        applicationDirectory <- @"C:\Users\tuomashie\Documents\GitHub\AzureOwinFSharp\OwinWorkerRole"
        use app =  WebApp.Start<MyWebStartup>(``console app web server url``) 
        Console.WriteLine "Server running... press enter to stop"
        Console.ReadLine() |> ignore
        app.Dispose()
        Console.WriteLine "Server closed."
        0